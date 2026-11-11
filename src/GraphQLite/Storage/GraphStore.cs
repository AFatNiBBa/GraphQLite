using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace GraphQLite.Storage;

internal sealed class IndexDef(string name, int label, int key, bool unique, int slot, int hashSlot = -1)
{
    public readonly string Name = name;
    public readonly int Label = label;
    public readonly int Key = key;
    public readonly bool Unique = unique;
    /// <summary>Slot of the B+tree root in the index-root vector.</summary>
    public readonly int Slot = slot;
    /// <summary>Slot of the <see cref="HashIndex"/> meta page, or -1 (only unique indexes have one).</summary>
    public readonly int HashSlot = hashSlot;
}

/// <summary>Immutable schema catalog (index / constraint definitions).</summary>
internal sealed class Catalog
{
    public static readonly Catalog Empty = new([]);
    public readonly IndexDef[] Indexes;
    private readonly Dictionary<int, IndexDef[]> _byLabel;

    public Catalog(IndexDef[] indexes)
    {
        Indexes = indexes;
        _byLabel = indexes.GroupBy(i => i.Label).ToDictionary(g => g.Key, g => g.ToArray());
    }

    public IndexDef[] ForLabel(int label) => _byLabel.TryGetValue(label, out var a) ? a : [];

    public IndexDef? Find(int label, int key)
    {
        foreach (var ix in ForLabel(label)) if (ix.Key == key) return ix;
        return null;
    }

    public IndexDef? ByName(string name) => Indexes.FirstOrDefault(i => i.Name == name);

    private const int FlagUnique = 1, FlagHash = 2;

    public byte[] Encode()
    {
        var w = new BinWriter(64);
        w.WriteInt32(0); // length placeholder
        w.WriteVarUInt((ulong)Indexes.Length);
        foreach (var ix in Indexes)
        {
            w.WriteString(ix.Name);
            w.WriteVarUInt((ulong)ix.Label);
            w.WriteVarUInt((ulong)ix.Key);
            w.WriteByte((byte)((ix.Unique ? FlagUnique : 0) | (ix.HashSlot >= 0 ? FlagHash : 0)));
            w.WriteVarUInt((ulong)ix.Slot);
            if (ix.HashSlot >= 0) w.WriteVarUInt((ulong)ix.HashSlot);
        }
        var bytes = w.Written.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, bytes.Length);
        return bytes;
    }

    public static Catalog Decode(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data, 4);
        int n = r.ReadVarInt32();
        var list = new IndexDef[n];
        for (int i = 0; i < n; i++)
        {
            var name = r.ReadString();
            int label = r.ReadVarInt32();
            int key = r.ReadVarInt32();
            int flags = r.ReadByte();
            int slot = r.ReadVarInt32();
            int hashSlot = (flags & FlagHash) != 0 ? r.ReadVarInt32() : -1;
            list[i] = new IndexDef(name, label, key, (flags & FlagUnique) != 0, slot, hashSlot);
        }
        return new Catalog(list);
    }
}

/// <summary>Shared state of an open database: pager, tokens, catalog cache.</summary>
internal sealed unsafe class GraphStore : IDisposable
{
    public readonly Pager Pager;
    public readonly TokenTable Labels = new();
    public readonly TokenTable RelTypes = new();
    public readonly TokenTable PropKeys = new();
    private readonly ConcurrentDictionary<long, Catalog> _catalogs = new();

    /// <summary>
    /// Next node / relationship id. Ids are handed out here rather than from the header page, so that concurrent
    /// transactions never pick the same id and a replayed transaction can reuse the ids it reserved.
    /// </summary>
    internal long NextNodeId, NextRelId;

    private GraphStore(Pager pager) => Pager = pager;

    public static GraphStore Open(string? path, SyncMode sync)
    {
        var pager = path is null ? Pager.InMemory() : Pager.OpenFiles(path, sync);
        var store = new GraphStore(pager);
        try
        {
            store.Initialize();
            return store;
        }
        catch
        {
            pager.Dispose();
            throw;
        }
    }

    public TokenTable TokensOf(TokenKind k) => k switch
    {
        TokenKind.Label => Labels,
        TokenKind.RelType => RelTypes,
        _ => PropKeys,
    };

    private void Initialize()
    {
        var tx = BeginRead();
        var h = tx.H;
        if (h->Magic == 0)
        {
            var w = BeginWrite();
            w.Format();
            w.Commit();
            Pager.MergeOverlay();
            Pager.FlushAndTruncateWal();
            return;
        }
        NextNodeId = h->NodeHigh;
        NextRelId = h->RelHigh;
        if (h->Magic != Header.MagicValue) throw new GraphQLiteException("Not a GraphQLite database (or an incompatible format version)");
        if (h->Version != Header.CurrentVersion) throw new GraphQLiteException($"Unsupported database format version {h->Version}");
        if (h->PageSize != Pages.Size) throw new GraphQLiteException("Unsupported page size");
        tx.LoadTokens();
    }

    public StoreTx BeginRead() => new(this, null, Pager.VisibleSeq);

    /// <summary>Caller holds the writer gate.</summary>
    public StoreTx BeginWrite() => new(this, new WriteTx(Pager), long.MaxValue);

    /// <summary>Optimistic writer on the current snapshot: needs no gate until it commits.</summary>
    public StoreTx BeginConcurrentWrite()
    {
        long seq = Pager.VisibleSeq; // its rows reach the client before it commits: only durable data
        return new(this, new WriteTx(Pager, seq), seq) { ReservedNodes = new(), ReservedRels = new() };
    }

    internal Catalog GetCommittedCatalog(long addr, StoreTx tx)
    {
        if (_catalogs.TryGetValue(addr, out var c)) return c;
        c = Catalog.Decode(tx.BlobSpan(addr));
        _catalogs[addr] = c;
        return c;
    }

    public void Dispose() => Pager.Dispose();
}

/// <summary>
/// View of the graph for one transaction. Readers see an immutable snapshot; the writer sees its own
/// private page copies. All record access goes through page lookups: callers must not keep raw
/// pointers across calls that may commit (see <see cref="Pager"/>).
/// </summary>
internal sealed unsafe class StoreTx
{
    public readonly GraphStore G;
    public readonly WriteTx? W;
    public readonly long Snapshot;
    private Catalog? _ownCatalog;
    private long _ownCatalogAddr = -1;
    /// <summary>True when this transaction changed the schema (plans must not be cached).</summary>
    public bool SchemaChanged;

    /// <summary>Ids reserved by a concurrent transaction (null otherwise), reused when it is replayed.</summary>
    internal List<long>? ReservedNodes, ReservedRels;
    /// <summary>Ids to reuse, in order, while replaying a concurrent transaction.</summary>
    internal Queue<long>? ReplayNodes, ReplayRels;
    private IdRun _nodeIds = IdRun.Empty, _relIds = IdRun.Empty;

    /// <summary>
    /// Ids reserved by an exclusive transaction since its last commit, while contiguous: a rolled back statement or
    /// transaction gives them back if nobody reserved ids after it, so a single writer keeps ids dense.
    /// </summary>
    private struct IdRun
    {
        public long Start, End, StatementStart;
        public static IdRun Empty => new() { Start = -1, End = -1, StatementStart = -1 };

        public void Reserved(long id)
        {
            if (id != End) Start = StatementStart = id;
            End = id + 1;
        }

        public void BeginStatement() => StatementStart = End;

        public void RollbackStatement(ref long counter)
        {
            if (StatementStart >= 0 && End > StatementStart && Interlocked.CompareExchange(ref counter, StatementStart, End) == End)
                End = StatementStart;
        }

        public void Abort(ref long counter)
        {
            if (Start >= 0 && End > Start) Interlocked.CompareExchange(ref counter, Start, End);
            this = Empty;
        }

        public void Commit() => Start = StatementStart = End;
    }

    private long NewNodeId()
    {
        if (ReplayNodes != null && ReplayNodes.TryDequeue(out long id)) return id;
        id = Interlocked.Increment(ref G.NextNodeId) - 1;
        if (ReservedNodes != null) ReservedNodes.Add(id);
        else _nodeIds.Reserved(id);
        return id;
    }

    private long NewRelId()
    {
        if (ReplayRels != null && ReplayRels.TryDequeue(out long id)) return id;
        id = Interlocked.Increment(ref G.NextRelId) - 1;
        if (ReservedRels != null) ReservedRels.Add(id);
        else _relIds.Reserved(id);
        return id;
    }

    internal StoreTx(GraphStore g, WriteTx? w, long snapshot)
    {
        G = g;
        W = w;
        Snapshot = snapshot;
    }

    public bool IsWriter => W != null;
    public TokenTable Labels => G.Labels;
    public TokenTable RelTypes => G.RelTypes;
    public TokenTable PropKeys => G.PropKeys;

    // ================================================================== page access

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* Page(Space s, long page) => W != null ? W.Read(s, page) : G.Pager.Read(s, page, Snapshot);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* ReadPage(long dataPage) => Page(Space.Data, dataPage);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* WritePage(long dataPage) => Writer.Write(Space.Data, dataPage);

    public byte* WritePageFresh(long dataPage) => Writer.WriteFresh(Space.Data, dataPage);

    private WriteTx Writer => W ?? throw new GraphQLiteException("Cannot perform updates in a read-only transaction");

    public Header* H
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Header*)Page(Space.Data, 0);
    }

    private Header* HW => (Header*)Writer.Write(Space.Data, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* R(long addr) => Page(Space.Data, Blobs.PageOf(addr)) + Blobs.OffsetOf(addr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* Wr(long addr) => Writer.Write(Space.Data, Blobs.PageOf(addr)) + Blobs.OffsetOf(addr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NodeRec* NR(long id) => (NodeRec*)(Page(Space.Nodes, id / NodeRec.PerPage) + (int)(id % NodeRec.PerPage) * NodeRec.Size);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NodeRec* NW(long id) => (NodeRec*)(Writer.Write(Space.Nodes, id / NodeRec.PerPage) + (int)(id % NodeRec.PerPage) * NodeRec.Size);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RelRec* RR(long id) => (RelRec*)(Page(Space.Rels, id / RelRec.PerPage) + (int)(id % RelRec.PerPage) * RelRec.Size);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RelRec* RW(long id) => (RelRec*)(Writer.Write(Space.Rels, id / RelRec.PerPage) + (int)(id % RelRec.PerPage) * RelRec.Size);

    // ================================================================== counters

    public long NodeHigh => H->NodeHigh;
    public long NodeCount => H->NodeCount;
    public long RelHigh => H->RelHigh;
    public long RelCount => H->RelCount;
    public long SchemaVersion => H->SchemaVersion;
    public long CatalogAddress => H->Catalog;
    public long DataPages => H->DataPageHigh;

    // ================================================================== allocation (data space)

    public long AllocPage()
    {
        var h = HW;
        long page = h->FreePageHead;
        if (page != 0)
        {
            h->FreePageHead = *(long*)ReadPage(page);
            WritePageFresh(page);
            return page;
        }
        page = h->DataPageHigh++;
        WritePageFresh(page);
        return page;
    }

    public void FreePage(long page)
    {
        var h = HW;
        *(long*)WritePage(page) = h->FreePageHead;
        h->FreePageHead = page;
    }

    /// <summary>Allocates a blob of at least <paramref name="size"/> bytes (whole page above 4 KB).</summary>
    public long AllocBlob(int size)
    {
        if (size > Blobs.MaxSlot) return Blobs.Addr(AllocPage(), 0);
        int c = Blobs.ClassOf(size);
        var h = HW;
        long head = h->SlotFree[c];
        if (head != 0)
        {
            h->SlotFree[c] = *(long*)R(head);
            return head;
        }
        long page = AllocPage();
        byte* p = WritePage(page);
        int ss = Blobs.ClassSize(c), n = Pages.Size / ss;
        for (int i = 1; i < n; i++) *(long*)(p + i * ss) = i + 1 < n ? Blobs.Addr(page, (i + 1) * ss) : 0;
        HW->SlotFree[c] = n > 1 ? Blobs.Addr(page, ss) : 0;
        return Blobs.Addr(page, 0);
    }

    public void FreeBlob(long addr, int size)
    {
        if (size > Blobs.MaxSlot)
        {
            FreePage(Blobs.PageOf(addr));
            return;
        }
        int c = Blobs.ClassOf(size);
        var h = HW;
        *(long*)Wr(addr) = h->SlotFree[c];
        h->SlotFree[c] = addr;
    }

    /// <summary>Stores a self-describing blob (first int = total length), spilling to a page chain if large.</summary>
    public long StoreBlob(ReadOnlySpan<byte> data)
    {
        if (data.Length <= Blobs.MaxSlot)
        {
            long a = AllocBlob(data.Length);
            data.CopyTo(new Span<byte>(Wr(a), data.Length));
            return a;
        }
        const int cap = Pages.Size - Blobs.ChainHeader;
        long first = 0, prev = 0;
        for (int off = 0; off < data.Length; off += cap)
        {
            long page = AllocPage();
            byte* p = WritePage(page);
            int len = Math.Min(cap, data.Length - off);
            *(long*)p = 0;
            *(int*)(p + 8) = len;
            data.Slice(off, len).CopyTo(new Span<byte>(p + Blobs.ChainHeader, len));
            if (prev != 0) *(long*)WritePage(prev) = page;
            else first = page;
            prev = page;
        }
        return Blobs.Addr(first, 0) | Blobs.ChainFlag;
    }

    public void FreeStoredBlob(long addr)
    {
        if (addr == 0) return;
        if ((addr & Blobs.ChainFlag) != 0)
        {
            long page = Blobs.PageOf(addr);
            while (page != 0)
            {
                long next = *(long*)ReadPage(page);
                FreePage(page);
                page = next;
            }
            return;
        }
        FreeBlob(addr, *(int*)R(addr));
    }

    /// <summary>Returns the bytes of a stored blob (pointing into page memory unless chained).</summary>
    public ReadOnlySpan<byte> BlobSpan(long addr)
    {
        if ((addr & Blobs.ChainFlag) == 0) return new ReadOnlySpan<byte>(R(addr), *(int*)R(addr));
        var ms = new MemoryStream();
        long page = Blobs.PageOf(addr);
        while (page != 0)
        {
            byte* p = ReadPage(page);
            ms.Write(new ReadOnlySpan<byte>(p + Blobs.ChainHeader, *(int*)(p + 8)));
            page = *(long*)p;
        }
        return ms.ToArray();
    }

    // ================================================================== page vectors (2-level: 1024 x 1024 slots)

    public long VecGet(long root, long i)
    {
        if (root == 0 || (ulong)i >= 1 << 20) return 0;
        long leaf = ((long*)ReadPage(root))[i >> 10];
        return leaf == 0 ? 0 : ((long*)ReadPage(leaf))[i & 1023];
    }

    /// <summary>Frees the pages of a page vector (not the pages it points to).</summary>
    public void FreeVec(long root)
    {
        var leaves = new ReadOnlySpan<long>(ReadPage(root), 1024).ToArray();
        foreach (long leaf in leaves) if (leaf != 0) FreePage(leaf);
        FreePage(root);
    }

    public void VecSet(long root, long i, long value)
    {
        if ((ulong)i >= 1 << 20) throw new GraphQLiteException("Storage limit exceeded (page vector index)");
        long leaf = ((long*)ReadPage(root))[i >> 10];
        if (leaf == 0)
        {
            if (value == 0) return;
            leaf = AllocPage();
            ((long*)WritePage(root))[i >> 10] = leaf;
        }
        ((long*)WritePage(leaf))[i & 1023] = value;
    }

    // ================================================================== tokens

    internal void LoadTokens()
    {
        long page = H->TokenHead;
        while (page != 0)
        {
            byte* p = ReadPage(page);
            int used = *(int*)(p + 8);
            int off = 12;
            while (off < used)
            {
                var kind = (TokenKind)p[off];
                int id = *(int*)(p + off + 1);
                int len = *(ushort*)(p + off + 5);
                var name = Encoding.UTF8.GetString(p + off + 7, len);
                G.TokensOf(kind).DefineAt(id, name);
                off += 7 + len;
            }
            page = *(long*)p;
        }
        foreach (var k in new[] { TokenKind.Label, TokenKind.RelType, TokenKind.PropKey })
            G.TokensOf(k).PersistedCount = G.TokensOf(k).Count;
    }

    private void PersistTokens()
    {
        foreach (var k in new[] { TokenKind.Label, TokenKind.RelType, TokenKind.PropKey })
        {
            var t = G.TokensOf(k);
            int count = t.Count;
            for (int id = t.PersistedCount; id < count; id++)
            {
                var bytes = Encoding.UTF8.GetBytes(t.Name(id));
                if (bytes.Length > 4000) throw new GraphQLiteException("Token name too long");
                int need = 7 + bytes.Length;
                var h = HW;
                long tail = h->TokenTail;
                if (tail == 0 || h->TokenTailUsed + need > Pages.Size)
                {
                    long np = AllocPage();
                    byte* npp = WritePage(np);
                    *(long*)npp = 0;
                    *(int*)(npp + 8) = 12;
                    h = HW;
                    if (tail != 0) *(long*)WritePage(tail) = np;
                    else h->TokenHead = np;
                    h->TokenTail = np;
                    h->TokenTailUsed = 12;
                    tail = np;
                }
                byte* p = WritePage(tail);
                int off = HW->TokenTailUsed;
                p[off] = (byte)k;
                *(int*)(p + off + 1) = id;
                *(ushort*)(p + off + 5) = (ushort)bytes.Length;
                bytes.CopyTo(new Span<byte>(p + off + 7, bytes.Length));
                HW->TokenTailUsed = off + need;
                *(int*)(p + 8) = off + need;
            }
        }
    }

    // ================================================================== commit / rollback

    public void Commit()
    {
        if (W is null) return;
        if (W.DirtyCount > 0) PersistTokens();
        W.Commit();
        foreach (var k in new[] { TokenKind.Label, TokenKind.RelType, TokenKind.PropKey })
            G.TokensOf(k).PersistedCount = G.TokensOf(k).Count;
        _nodeIds.Commit();
        _relIds.Commit();
        _ownCatalog = null;
        _ownCatalogAddr = -1;
    }

    public void Abort()
    {
        W?.Abort();
        _nodeIds.Abort(ref G.NextNodeId);
        _relIds.Abort(ref G.NextRelId);
        _ownCatalog = null;
        _ownCatalogAddr = -1;
    }

    public void BeginStatement()
    {
        if (W is null) return;
        W.BeginSavepoint();
        _nodeIds.BeginStatement();
        _relIds.BeginStatement();
    }

    public void EndStatement() => W?.ReleaseSavepoint();

    public void RollbackStatement()
    {
        W?.RollbackToSavepoint();
        _nodeIds.RollbackStatement(ref G.NextNodeId);
        _relIds.RollbackStatement(ref G.NextRelId);
        _ownCatalog = null;
        _ownCatalogAddr = -1;
    }

    internal void Format()
    {
        var h = HW;
        h->Magic = Header.MagicValue;
        h->Version = Header.CurrentVersion;
        h->PageSize = Pages.Size;
        h->DataPageHigh = 1;
        long lt = AllocPage(), rt = AllocPage(), ir = AllocPage();
        h = HW;
        h->LabelTable = lt;
        h->RelTypeTable = rt;
        h->IndexRoots = ir;
        h->Catalog = StoreBlob(Catalog.Empty.Encode());
    }

    // ================================================================== catalog

    public Catalog Catalog
    {
        get
        {
            long addr = H->Catalog;
            if (W is null) return G.GetCommittedCatalog(addr, this);
            if (addr == _ownCatalogAddr && _ownCatalog != null) return _ownCatalog;
            _ownCatalog = Catalog.Decode(BlobSpan(addr));
            _ownCatalogAddr = addr;
            return _ownCatalog;
        }
    }

    private void WriteCatalog(Catalog c)
    {
        long addr = StoreBlob(c.Encode()); // old catalog blobs are never freed: addresses stay unique
        var h = HW;
        h->Catalog = addr;
        h->SchemaVersion++;
        _ownCatalog = c;
        _ownCatalogAddr = addr;
        SchemaChanged = true;
    }

    public long IndexRoot(IndexDef ix) => VecGet(H->IndexRoots, ix.Slot);

    public void CreateIndex(string name, int label, int key, bool unique)
    {
        var cat = Catalog;
        if (cat.ByName(name) != null) throw new GraphQLiteException($"An index named '{name}' already exists");
        if (cat.Find(label, key) != null) throw new GraphQLiteException($"An index on :{Labels.Name(label)}({PropKeys.Name(key)}) already exists");
        int slot = HW->NextIndexSlot++;
        int hashSlot = unique ? HW->NextIndexSlot++ : -1;
        var def = new IndexDef(name, label, key, unique, slot, hashSlot);
        long root = BTree.NewTree(this);
        VecSet(H->IndexRoots, slot, root);
        if (unique) VecSet(H->IndexRoots, hashSlot, HashIndex.Create(this));
        // backfill from the label bitmap
        var w = new BinWriter(64);
        long high = NodeHigh;
        long dir = LabelDir(label);
        for (long group = 0; dir != 0 && group <= (high >> 16); group++)
        {
            long bpage = VecGet(dir, group);
            if (bpage == 0) continue;
            for (int wi = 0; wi < 1024; wi++)
            {
                ulong word = ((ulong*)ReadPage(bpage))[wi];
                while (word != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                    word &= word - 1;
                    long id = (group << 16) | ((long)wi << 6) | (uint)bit;
                    var v = NodeProp(id, key);
                    if (v.IsNull) continue;
                    if (unique && FindConflict(def, v, id) is { } other)
                        throw new ConstraintViolationException(
                            $"Cannot create unique constraint: nodes {other} and {id} share the value {v} for :{Labels.Name(label)}({PropKeys.Name(key)})");
                    IndexAdd(def, v, id, w);
                }
            }
        }
        WriteCatalog(new Catalog([.. cat.Indexes, def]));
    }

    public bool DropIndex(string name)
    {
        var cat = Catalog;
        var def = cat.ByName(name);
        if (def is null) return false;
        long root = IndexRoot(def);
        if (root != 0) BTree.FreeTree(this, root);
        VecSet(H->IndexRoots, def.Slot, 0);
        if (HashMeta(def) is var meta and not 0)
        {
            HashIndex.Free(this, meta);
            VecSet(H->IndexRoots, def.HashSlot, 0);
        }
        WriteCatalog(new Catalog(cat.Indexes.Where(i => i != def).ToArray()));
        return true;
    }

    /// <summary>Meta page of the hash index of <paramref name="ix"/>, or 0.</summary>
    private long HashMeta(IndexDef ix) => ix.HashSlot < 0 ? 0 : VecGet(H->IndexRoots, ix.HashSlot);

    public bool HasHashIndex(IndexDef ix) => HashMeta(ix) != 0;

    /// <summary>Encodes the value part of an index key into <paramref name="w"/>; returns its hash (0 if not indexable).</summary>
    private static bool EncodeKey(in Value v, BinWriter w, out ulong hash)
    {
        w.Reset();
        hash = 0;
        if (!IndexKeys.EncodeValue(v, w)) return false;
        hash = HashIndex.HashOf(w.Written);
        return true;
    }

    private void IndexAdd(IndexDef ix, in Value v, long id, BinWriter w)
    {
        if (!EncodeKey(v, w, out ulong hash)) return;
        IndexKeys.AppendId(w, id);
        long root = IndexRoot(ix);
        long nr = BTree.Insert(this, root, w.Written);
        if (nr != root) VecSet(H->IndexRoots, ix.Slot, nr);
        if (HashMeta(ix) is var meta and not 0) HashIndex.Insert(this, meta, hash, id);
    }

    private void IndexRemove(IndexDef ix, in Value v, long id, BinWriter w)
    {
        if (!EncodeKey(v, w, out ulong hash)) return;
        IndexKeys.AppendId(w, id);
        BTree.Delete(this, IndexRoot(ix), w.Written);
        if (HashMeta(ix) is var meta and not 0) HashIndex.Remove(this, meta, hash, id);
    }

    /// <summary>
    /// Candidate node ids for <c>n.key = v</c> through the hash index of a unique index. Returns false when
    /// there is no such index (the caller falls back to the B+tree). Candidates must be verified by the caller.
    /// </summary>
    public bool IndexLookup(IndexDef ix, in Value v, BinWriter w, List<long> output)
    {
        long meta = HashMeta(ix);
        if (meta == 0) return false;
        if (EncodeKey(v, w, out ulong hash)) HashIndex.Lookup(this, meta, hash, output);
        return true;
    }

    /// <summary>Returns another node holding the same value in a unique index, or null.</summary>
    private long? FindConflict(IndexDef ix, in Value v, long self)
    {
        var w = new BinWriter(64);
        var candidates = new List<long>();
        if (IndexLookup(ix, v, w, candidates))
        {
            foreach (long id in candidates)
            {
                if (id == self || !NodeAlive(id)) continue;
                if (ValueOps.Equivalent(NodeProp(id, ix.Key), v)) return id;
            }
            return null;
        }
        if (!IndexKeys.EncodeValue(v, w)) return null;
        var prefix = w.Written.ToArray();
        var cur = new IndexRangeCursor(this, IndexRoot(ix), prefix, IndexKeys.Successor(prefix));
        while (cur.Next(out long id))
        {
            if (id == self || !NodeAlive(id)) continue;
            if (ValueOps.Equivalent(NodeProp(id, ix.Key), v)) return id;
        }
        return null;
    }

    private void CheckUnique(IndexDef ix, in Value v, long self)
    {
        if (!ix.Unique || v.IsNull) return;
        if (FindConflict(ix, v, self) is { } other)
            throw new ConstraintViolationException(
                $"Node({other}) already exists with label `{Labels.Name(ix.Label)}` and property `{PropKeys.Name(ix.Key)}` = {v}");
    }

    /// <summary>Node ids matching an index range [low, high) (keys including the type tag).</summary>
    public IndexRangeCursor? IndexScan(int label, int key, ReadOnlySpan<byte> low, byte[]? high)
    {
        var ix = Catalog.Find(label, key);
        return ix is null ? null : IndexScan(ix, low, high);
    }

    /// <summary>Node ids matching a range [low, high) of <paramref name="ix"/>.</summary>
    public IndexRangeCursor? IndexScan(IndexDef ix, ReadOnlySpan<byte> low, byte[]? high)
    {
        long root = IndexRoot(ix);
        return root == 0 ? null : new IndexRangeCursor(this, root, low, high);
    }

    // ================================================================== labels

    public bool NodeAlive(long id) => (ulong)id < (ulong)H->NodeHigh && (NR(id)->Flags & NodeRec.InUse) != 0;

    public int[] NodeLabels(long id)
    {
        if (!NodeAlive(id)) return [];
        var n = NR(id);
        if ((n->Flags & NodeRec.LabelOverflow) != 0)
        {
            int cnt = n->LabelBlobCount;
            var arr = new int[cnt];
            new ReadOnlySpan<int>(R(n->LabelBlob) + 4, cnt).CopyTo(arr);
            return arr;
        }
        var res = new int[n->LabelCount];
        for (int i = 0; i < res.Length; i++) res[i] = n->Labels[i];
        return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasLabel(long id, int label)
    {
        var n = NR(id);
        if ((n->Flags & NodeRec.LabelOverflow) == 0)
        {
            int c = n->LabelCount;
            for (int i = 0; i < c; i++) if (n->Labels[i] == label) return true;
            return false;
        }
        return new ReadOnlySpan<int>(R(n->LabelBlob) + 4, n->LabelBlobCount).Contains(label);
    }

    private void WriteLabels(long id, int[] labels)
    {
        var n = NR(id);
        if ((n->Flags & NodeRec.LabelOverflow) != 0) FreeBlob(n->LabelBlob, 4 + 4 * n->LabelBlobCount);
        bool inline = labels.Length <= NodeRec.InlineLabels && labels.All(l => l < ushort.MaxValue);
        if (inline)
        {
            var w = NW(id);
            w->Flags = (byte)(w->Flags & ~NodeRec.LabelOverflow);
            w->LabelCount = (byte)labels.Length;
            w->LabelBlob = 0;
            for (int i = 0; i < labels.Length; i++) w->Labels[i] = (ushort)labels[i];
            w->LabelBlobCount = 0;
        }
        else
        {
            long blob = AllocBlob(4 + 4 * labels.Length);
            byte* b = Wr(blob);
            *(int*)b = 4 + 4 * labels.Length;
            labels.AsSpan().CopyTo(new Span<int>(b + 4, labels.Length));
            var w = NW(id);
            w->Flags |= NodeRec.LabelOverflow;
            w->LabelCount = 0;
            w->LabelBlob = blob;
            w->LabelBlobCount = labels.Length;
        }
    }

    public long LabelCount(int label) => (uint)label < 1 << 19 ? VecGet(H->LabelTable, 2L * label) : 0;

    public long LabelDir(int label) => (uint)label < 1 << 19 ? VecGet(H->LabelTable, 2L * label + 1) : 0;

    /// <summary>Page holding the label bitmap for nodes [group*65536, (group+1)*65536), or 0.</summary>
    public long LabelBitmapPage(long dir, long group) => VecGet(dir, group);

    private void SetLabelBit(int label, long node, bool on)
    {
        if ((uint)label >= 1 << 19) throw new GraphQLiteException("Too many labels");
        long lt = H->LabelTable;
        long dir = VecGet(lt, 2L * label + 1);
        if (dir == 0)
        {
            if (!on) return;
            dir = AllocPage();
            VecSet(lt, 2L * label + 1, dir);
        }
        long group = node >> 16;
        long bpage = VecGet(dir, group);
        if (bpage == 0)
        {
            if (!on) return;
            bpage = AllocPage();
            VecSet(dir, group, bpage);
        }
        int bit = (int)(node & 65535);
        ulong mask = 1UL << (bit & 63);
        ulong* word = (ulong*)ReadPage(bpage) + (bit >> 6);
        bool isSet = (*word & mask) != 0;
        if (isSet == on) return;
        word = (ulong*)WritePage(bpage) + (bit >> 6);
        if (on) *word |= mask; else *word &= ~mask;
        VecSet(lt, 2L * label, VecGet(lt, 2L * label) + (on ? 1 : -1));
    }

    // ================================================================== properties

    /// <summary>Blob format: [int len][int count][int keys[count]][int offsets[count]][values...].</summary>
    private static byte[]? EncodeProps(PropertyMap? m)
    {
        if (m is null || m.Count == 0) return null;
        int c = m.Count;
        var w = new BinWriter(32 + 16 * c);
        w.WriteInt32(0);
        w.WriteInt32(c);
        var keys = m.Keys;
        foreach (var k in keys) w.WriteInt32(k);
        int offPos = w.Length;
        w.Reserve(4 * c);
        var offsets = new int[c];
        var vals = m.Values;
        for (int i = 0; i < c; i++)
        {
            offsets[i] = w.Length;
            w.WriteValue(vals[i]);
        }
        var bytes = w.WrittenMutable;
        for (int i = 0; i < c; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes[(offPos + 4 * i)..], offsets[i]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, bytes.Length);
        return bytes.ToArray();
    }

    private static Value GetProp(ReadOnlySpan<byte> blob, int key)
    {
        int c = BinaryPrimitives.ReadInt32LittleEndian(blob[4..]);
        var keys = MemoryMarshal.Cast<byte, int>(blob.Slice(8, 4 * c));
        int i = keys.IndexOf(key);
        if (i < 0) return Value.Null;
        int off = BinaryPrimitives.ReadInt32LittleEndian(blob[(8 + 4 * c + 4 * i)..]);
        return new SpanReader(blob, off).ReadValue();
    }

    public PropertyMap? DecodeProps(long addr)
    {
        if (addr == 0) return null;
        var blob = BlobSpan(addr);
        int c = BinaryPrimitives.ReadInt32LittleEndian(blob[4..]);
        var m = new PropertyMap(c);
        var keys = MemoryMarshal.Cast<byte, int>(blob.Slice(8, 4 * c));
        for (int i = 0; i < c; i++)
        {
            int off = BinaryPrimitives.ReadInt32LittleEndian(blob[(8 + 4 * c + 4 * i)..]);
            m.Set(keys[i], new SpanReader(blob, off).ReadValue());
        }
        return m;
    }

    public int[] PropKeysOf(long addr)
    {
        if (addr == 0) return [];
        var blob = BlobSpan(addr);
        int c = BinaryPrimitives.ReadInt32LittleEndian(blob[4..]);
        return MemoryMarshal.Cast<byte, int>(blob.Slice(8, 4 * c)).ToArray();
    }

    public Dictionary<string, Value> PropsDict(long addr)
    {
        var m = DecodeProps(addr);
        if (m is null) return new Dictionary<string, Value>(0);
        var d = new Dictionary<string, Value>(m.Count);
        var keys = m.Keys;
        var vals = m.Values;
        for (int i = 0; i < keys.Length; i++) d[PropKeys.Name(keys[i])] = vals[i];
        return d;
    }

    private long ReplaceProps(long oldAddr, PropertyMap? m)
    {
        var data = EncodeProps(m);
        if (data is null)
        {
            FreeStoredBlob(oldAddr);
            return 0;
        }
        if (oldAddr != 0 && (oldAddr & Blobs.ChainFlag) == 0 && data.Length <= Blobs.MaxSlot)
        {
            int oldLen = *(int*)R(oldAddr);
            if (Blobs.ClassOf(oldLen) == Blobs.ClassOf(data.Length))
            {
                data.CopyTo(new Span<byte>(Wr(oldAddr), data.Length));
                return oldAddr;
            }
        }
        FreeStoredBlob(oldAddr);
        return StoreBlob(data);
    }

    public long NodePropsAddr(long id) => NodeAlive(id) ? NR(id)->Props : 0;

    public Value NodeProp(long id, int key)
    {
        if (key < 0 || (ulong)id >= (ulong)H->NodeHigh) return Value.Null;
        var n = NR(id);
        if ((n->Flags & NodeRec.InUse) == 0 || n->Props == 0) return Value.Null;
        long a = n->Props;
        return GetProp((a & Blobs.ChainFlag) == 0 ? new ReadOnlySpan<byte>(R(a), *(int*)R(a)) : BlobSpan(a), key);
    }

    public bool RelAlive(long id) => (ulong)id < (ulong)H->RelHigh && (RR(id)->Flags & RelRec.InUse) != 0;

    public long RelPropsAddr(long id) => RelAlive(id) ? RR(id)->Props : 0;

    public Value RelProp(long id, int key)
    {
        if (key < 0 || (ulong)id >= (ulong)H->RelHigh) return Value.Null;
        var r = RR(id);
        if ((r->Flags & RelRec.InUse) == 0 || r->Props == 0) return Value.Null;
        long a = r->Props;
        return GetProp((a & Blobs.ChainFlag) == 0 ? new ReadOnlySpan<byte>(R(a), *(int*)R(a)) : BlobSpan(a), key);
    }

    public (long Src, long Dst, int Type) RelInfo(long id)
    {
        var r = RR(id);
        return (r->Src, r->Dst, r->Type);
    }

    public long RelTypeCount(int type) => type < 0 ? 0 : VecGet(H->RelTypeTable, type);

    // ================================================================== node mutations

    internal static void ValidatePropertyValue(in Value v)
    {
        switch (v.Kind)
        {
            case ValueKind.Node:
            case ValueKind.Relationship:
            case ValueKind.Path:
                throw new CypherRuntimeException($"Property values cannot be {v.Kind.ToString().ToLowerInvariant()}s");
            case ValueKind.List:
                foreach (var x in v.ListItems) ValidatePropertyValue(x);
                return;
            case ValueKind.Map:
                foreach (var x in v.MapItems.Values) ValidatePropertyValue(x);
                return;
        }
    }

    public long CreateNode(int[]? labels, PropertyMap? props)
    {
        long id = NewNodeId();
        labels = labels is { Length: > 0 } ? labels.Distinct().Order().ToArray() : null;
        var cat = Catalog;
        if (labels != null && props != null && cat.Indexes.Length > 0)
            foreach (var l in labels)
                foreach (var ix in cat.ForLabel(l))
                    if (props.TryGet(ix.Key, out var v)) CheckUnique(ix, v, id);
        var h = HW;
        if (id >= h->NodeHigh) h->NodeHigh = id + 1;
        h->NodeCount++;
        var n = NW(id);
        *n = default;
        n->Flags = NodeRec.InUse;
        if (labels != null) WriteLabels(id, labels);
        var data = EncodeProps(props);
        if (data != null)
        {
            long pa = StoreBlob(data);
            NW(id)->Props = pa;
        }
        if (labels != null)
        {
            BinWriter? w = null;
            foreach (var l in labels)
            {
                SetLabelBit(l, id, true);
                if (props is null) continue;
                foreach (var ix in cat.ForLabel(l))
                    if (props.TryGet(ix.Key, out var v)) IndexAdd(ix, v, id, w ??= new BinWriter(64));
            }
        }
        return id;
    }

    /// <summary>Marks a node deleted. Adjacency is left untouched (callers verify or detach first).</summary>
    public void DeleteNode(long id)
    {
        if (!NodeAlive(id)) return;
        var labels = NodeLabels(id);
        var cat = Catalog;
        long props = NR(id)->Props;
        if (props != 0 && cat.Indexes.Length > 0)
        {
            var map = DecodeProps(props)!;
            var w = new BinWriter(64);
            foreach (var l in labels)
                foreach (var ix in cat.ForLabel(l))
                    if (map.TryGet(ix.Key, out var v)) IndexRemove(ix, v, id, w);
        }
        foreach (var l in labels) SetLabelBit(l, id, false);
        FreeStoredBlob(props);
        var n = NR(id);
        if ((n->Flags & NodeRec.LabelOverflow) != 0) FreeBlob(n->LabelBlob, 4 + 4 * n->LabelBlobCount);
        var nw = NW(id);
        nw->Flags = 0;
        nw->Props = 0;
        nw->LabelCount = 0;
        nw->LabelBlob = 0;
        nw->LabelBlobCount = 0;
        HW->NodeCount--;
    }

    public void SetNodeProp(long id, int key, in Value v)
    {
        if (!NodeAlive(id)) throw new CypherRuntimeException($"Node {id} has been deleted");
        ValidatePropertyValue(v);
        long addr = NR(id)->Props;
        var map = DecodeProps(addr) ?? new PropertyMap();
        var old = v.IsNull ? map.Remove(key) : map.Set(key, v);
        if (v.IsNull && old.IsNull) return;
        var cat = Catalog;
        if (cat.Indexes.Length > 0)
        {
            var labels = NodeLabels(id);
            BinWriter? w = null;
            foreach (var l in labels)
                foreach (var ix in cat.ForLabel(l))
                {
                    if (ix.Key != key) continue;
                    CheckUnique(ix, v, id);
                    w ??= new BinWriter(64);
                    IndexRemove(ix, old, id, w);
                    IndexAdd(ix, v, id, w);
                }
        }
        long na = ReplaceProps(addr, map);
        if (na != addr) NW(id)->Props = na;
    }

    public bool AddLabel(long id, int label)
    {
        if (!NodeAlive(id)) throw new CypherRuntimeException($"Node {id} has been deleted");
        var labels = NodeLabels(id);
        if (labels.Contains(label)) return false;
        var ixs = Catalog.ForLabel(label);
        PropertyMap? props = ixs.Length > 0 ? DecodeProps(NR(id)->Props) : null;
        if (props != null)
            foreach (var ix in ixs)
                if (props.TryGet(ix.Key, out var v)) CheckUnique(ix, v, id);
        WriteLabels(id, [.. labels, label]);
        SetLabelBit(label, id, true);
        if (props != null)
        {
            var w = new BinWriter(64);
            foreach (var ix in ixs)
                if (props.TryGet(ix.Key, out var v)) IndexAdd(ix, v, id, w);
        }
        return true;
    }

    public bool RemoveLabel(long id, int label)
    {
        if (!NodeAlive(id)) throw new CypherRuntimeException($"Node {id} has been deleted");
        var labels = NodeLabels(id);
        if (!labels.Contains(label)) return false;
        var ixs = Catalog.ForLabel(label);
        if (ixs.Length > 0 && DecodeProps(NR(id)->Props) is { } props)
        {
            var w = new BinWriter(64);
            foreach (var ix in ixs)
                if (props.TryGet(ix.Key, out var v)) IndexRemove(ix, v, id, w);
        }
        WriteLabels(id, labels.Where(l => l != label).ToArray());
        SetLabelBit(label, id, false);
        return true;
    }

    // ================================================================== relationships & adjacency

    private static int BlockBytes(int capacity) => capacity switch
    {
        <= 5 => 128,
        <= 24 => 512,
        <= 101 => 2048,
        _ => Pages.Size,
    };

    private static int NextBlockBytes(int capacity) => capacity switch
    {
        < 5 => 128,
        < 24 => 512,
        < 101 => 2048,
        _ => Pages.Size,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AdjEntry* Entries(byte* block) => (AdjEntry*)(block + AdjBlock.HeaderSize);

    public long AdjHead(long node, bool outgoing)
    {
        var n = NR(node);
        return outgoing ? n->OutHead : n->InHead;
    }

    public (int Out, int In) Degree(long node)
    {
        var n = NR(node);
        return (n->OutDegree, n->InDegree);
    }

    private void AdjAdd(long node, bool outgoing, AdjEntry e)
    {
        long head = AdjHead(node, outgoing);
        if (head != 0)
        {
            var b = (AdjBlock*)R(head);
            if (b->Count < b->Capacity)
            {
                b = (AdjBlock*)Wr(head);
                Entries((byte*)b)[b->Count++] = e;
                var nn = NW(node);
                if (outgoing) nn->OutDegree++; else nn->InDegree++;
                return;
            }
        }
        int cap = head == 0 ? 0 : ((AdjBlock*)R(head))->Capacity;
        int bytes = NextBlockBytes(cap);
        long nb = AllocBlob(bytes);
        var blk = (AdjBlock*)Wr(nb);
        blk->Count = 1;
        blk->Capacity = AdjBlock.CapacityFor(bytes);
        blk->Next = head;
        Entries((byte*)blk)[0] = e;
        var n = NW(node);
        if (outgoing)
        {
            n->OutHead = nb;
            n->OutDegree++;
        }
        else
        {
            n->InHead = nb;
            n->InDegree++;
        }
    }

    private void AdjRemove(long node, bool outgoing, long rel)
    {
        long head = AdjHead(node, outgoing);
        long found = 0;
        int foundIdx = -1;
        for (long a = head; a != 0;)
        {
            var b = (AdjBlock*)R(a);
            var es = Entries((byte*)b);
            for (int i = 0; i < b->Count; i++)
                if (es[i].Rel == rel) { found = a; foundIdx = i; break; }
            if (found != 0) break;
            a = b->Next;
        }
        if (found == 0) return;
        var hb = (AdjBlock*)Wr(head);
        var last = Entries((byte*)hb)[hb->Count - 1];
        var fb = (AdjBlock*)Wr(found);
        Entries((byte*)fb)[foundIdx] = last;
        hb = (AdjBlock*)Wr(head);
        hb->Count--;
        var n = NW(node);
        if (outgoing) n->OutDegree--; else n->InDegree--;
        if (hb->Count == 0)
        {
            long next = hb->Next;
            int bytes = BlockBytes(hb->Capacity);
            FreeBlob(head, bytes);
            n = NW(node);
            if (outgoing) n->OutHead = next; else n->InHead = next;
        }
    }

    public long CreateRel(long src, long dst, int type, PropertyMap? props)
    {
        if (!NodeAlive(src) || !NodeAlive(dst)) throw new CypherRuntimeException("Cannot create a relationship to a deleted node");
        long id = NewRelId();
        var h = HW;
        if (id >= h->RelHigh) h->RelHigh = id + 1;
        h->RelCount++;
        var data = EncodeProps(props);
        long pa = data is null ? 0 : StoreBlob(data);
        var r = RW(id);
        r->Flags = RelRec.InUse;
        r->Type = type;
        r->Src = src;
        r->Dst = dst;
        r->Props = pa;
        AdjAdd(src, true, new AdjEntry(id, dst, type));
        AdjAdd(dst, false, new AdjEntry(id, src, type));
        long rt = H->RelTypeTable;
        VecSet(rt, type, VecGet(rt, type) + 1);
        return id;
    }

    public void DeleteRel(long id)
    {
        if (!RelAlive(id)) return;
        var (src, dst, type) = RelInfo(id);
        AdjRemove(src, true, id);
        AdjRemove(dst, false, id);
        FreeStoredBlob(RR(id)->Props);
        var r = RW(id);
        r->Flags = 0;
        r->Props = 0;
        HW->RelCount--;
        long rt = H->RelTypeTable;
        VecSet(rt, type, VecGet(rt, type) - 1);
    }

    /// <summary>
    /// Distinct (start label, type, end label) combinations of the relationships: the graph schema. Reads every
    /// node and its outgoing adjacency, so it costs a scan of the relationships.
    /// </summary>
    public HashSet<(int From, int Type, int To)> SchemaTriples(CancellationToken ct)
    {
        var set = new HashSet<(int, int, int)>();
        var labelCache = new Dictionary<long, int[]>();
        int[] LabelsOf(long id)
        {
            if (labelCache.TryGetValue(id, out var l)) return l;
            if (labelCache.Count > 1 << 16) labelCache.Clear();
            return labelCache[id] = NodeLabels(id);
        }
        long high = NodeHigh;
        for (long n = 0; n < high; n++)
        {
            if ((n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (!NodeAlive(n) || NR(n)->OutDegree == 0) continue;
            var from = LabelsOf(n);
            if (from.Length == 0) continue;
            for (long a = AdjHead(n, true); a != 0;)
            {
                var b = (AdjBlock*)R(a);
                var es = Entries((byte*)b);
                for (int i = 0; i < b->Count; i++)
                {
                    var e = es[i];
                    foreach (int to in LabelsOf(e.Other))
                        foreach (int f in from) set.Add((f, e.Type, to));
                }
                a = b->Next;
            }
        }
        return set;
    }

    /// <summary>Relationship ids attached to a node (both directions, self loops once).</summary>
    public List<long> NodeRels(long node)
    {
        var list = new List<long>();
        foreach (var outgoing in new[] { true, false })
        {
            for (long a = AdjHead(node, outgoing); a != 0;)
            {
                var b = (AdjBlock*)R(a);
                var es = Entries((byte*)b);
                for (int i = 0; i < b->Count; i++)
                    if (outgoing || es[i].Other != node) list.Add(es[i].Rel);
                a = b->Next;
            }
        }
        return list;
    }

    public void SetRelProp(long id, int key, in Value v)
    {
        if (!RelAlive(id)) throw new CypherRuntimeException($"Relationship {id} has been deleted");
        ValidatePropertyValue(v);
        long addr = RR(id)->Props;
        var map = DecodeProps(addr) ?? new PropertyMap();
        var old = v.IsNull ? map.Remove(key) : map.Set(key, v);
        if (v.IsNull && old.IsNull) return;
        long na = ReplaceProps(addr, map);
        if (na != addr) RW(id)->Props = na;
    }

    // ================================================================== materialization

    public Node MaterializeNode(long id)
    {
        if (!NodeAlive(id)) return new Node(id, Array.Empty<string>(), new Dictionary<string, Value>());
        var labels = NodeLabels(id);
        return new Node(id, Array.ConvertAll(labels, Labels.Name), PropsDict(NR(id)->Props));
    }

    public Relationship MaterializeRel(long id)
    {
        if ((ulong)id >= (ulong)RelHigh) return new Relationship(id, "", -1, -1, new Dictionary<string, Value>());
        var r = RR(id);
        return new Relationship(id, RelTypes.Name(r->Type), r->Src, r->Dst, (r->Flags & RelRec.InUse) != 0 ? PropsDict(r->Props) : new());
    }
}

