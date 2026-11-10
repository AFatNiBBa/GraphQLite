using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GraphQLite.Storage;

internal enum Space : byte { Data = 0, Nodes = 1, Rels = 2 }

internal static unsafe class Pages
{
    public const int Size = 8192;
    public const int Shift = 13;
    public const long OffsetMask = Size - 1;

    /// <summary>A permanently zeroed page, returned for pages that do not exist yet.</summary>
    public static readonly byte* Zero = (byte*)NativeMemory.AlignedAlloc(Size, 64);

    static Pages() => NativeMemory.Clear(Zero, Size);

    /// <summary>
    /// Pool of free page buffers. Copy-on-write allocates a page for every page a transaction touches and the
    /// checkpoint frees them all at once: recycling them avoids the native heap, which dominated bulk loads.
    /// </summary>
    private static readonly nint[] _pool = new nint[PoolCapacity];
    private static int _pooled;
    private static readonly Lock _poolLock = new();
    private const int PoolCapacity = 16384; // 128 MB

    public static byte* Alloc()
    {
        lock (_poolLock)
        {
            if (_pooled > 0) return (byte*)_pool[--_pooled];
        }
        return (byte*)NativeMemory.AlignedAlloc(Size, 64);
    }

    public static void Free(byte* p)
    {
        lock (_poolLock)
        {
            if (_pooled < PoolCapacity)
            {
                _pool[_pooled++] = (nint)p;
                return;
            }
        }
        NativeMemory.AlignedFree(p);
    }
    public static void Copy(byte* src, byte* dst) => Buffer.MemoryCopy(src, dst, Size, Size);
}

/// <summary>
/// The checkpointed ("base") image of one space: a sequence of segments that are either views of a
/// memory-mapped file or native memory (in-memory databases). Segments grow geometrically
/// (1, 1, 2, 4 … 32 MB, then 64 MB each) and are never remapped, so page pointers stay valid.
/// </summary>
internal sealed unsafe class SegmentedSpace : IDisposable
{
    private const int MaxGeometric = 7;
    private readonly FileStream? _file;
    private readonly List<MemoryMappedFile> _maps = new();
    private readonly List<MemoryMappedViewAccessor> _views = new();
    private byte*[] _bases = new byte*[64];
    private int _count;
    private long _capacityPages;

    public long CapacityPages => Volatile.Read(ref _capacityPages);

    private SegmentedSpace(FileStream? file) => _file = file;

    public static SegmentedSpace InMemory() => new(null);

    public static SegmentedSpace OpenFile(string path)
    {
        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess);
        }
        catch (IOException ex)
        {
            throw new GraphQLiteException($"Database file '{path}' is locked by another process", ex);
        }
        var s = new SegmentedSpace(fs);
        long pages = fs.Length >> Pages.Shift;
        if (pages > 0) s.EnsurePages(pages);
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SegStart(int k) => k == 0 ? 0 : k < MaxGeometric ? 128L << (k - 1) : 8192L + (k - MaxGeometric) * 8192L;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SegPages(int k) => k == 0 ? 128 : k < MaxGeometric ? 128L << (k - 1) : 8192;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SegOf(long page) =>
        page < 8192 ? (page < 128 ? 0 : BitOperations.Log2((ulong)(page >> 7)) + 1) : MaxGeometric + (int)((page - 8192) >> 13);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* PagePtr(long page)
    {
        if ((ulong)page >= (ulong)_capacityPages) return Pages.Zero;
        int k = SegOf(page);
        return _bases[k] + ((page - SegStart(k)) << Pages.Shift);
    }

    /// <summary>Grows the space so that it holds at least <paramref name="pages"/> pages.</summary>
    public void EnsurePages(long pages)
    {
        while (_capacityPages < pages)
        {
            int k = _count;
            long startBytes = SegStart(k) << Pages.Shift;
            long sizeBytes = SegPages(k) << Pages.Shift;
            byte* p;
            if (_file is null)
            {
                p = (byte*)NativeMemory.AlignedAlloc((nuint)sizeBytes, 4096);
                NativeMemory.Clear(p, (nuint)sizeBytes);
            }
            else
            {
                long end = startBytes + sizeBytes;
                if (_file.Length < end) _file.SetLength(end);
                var map = MemoryMappedFile.CreateFromFile(_file, null, Math.Max(end, _file.Length), MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                var view = map.CreateViewAccessor(startBytes, sizeBytes, MemoryMappedFileAccess.ReadWrite);
                p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                p += view.PointerOffset;
                _maps.Add(map);
                _views.Add(view);
            }
            if (k >= _bases.Length)
            {
                var nb = new byte*[_bases.Length * 2];
                for (int i = 0; i < _bases.Length; i++) nb[i] = _bases[i];
                _bases = nb;
            }
            _bases[k] = p;
            _count++;
            Volatile.Write(ref _capacityPages, SegStart(k) + SegPages(k));
        }
    }

    public void Flush()
    {
        if (_file is null) return;
        foreach (var v in _views) v.Flush();
        _file.Flush(true);
    }

    public void Dispose()
    {
        if (_file is null)
        {
            for (int i = 0; i < _count; i++) NativeMemory.AlignedFree(_bases[i]);
        }
        else
        {
            foreach (var v in _views)
            {
                v.SafeMemoryMappedViewHandle.ReleasePointer();
                v.Dispose();
            }
            foreach (var m in _maps) m.Dispose();
            _file.Dispose();
        }
        _count = 0;
        _capacityPages = 0;
    }
}

/// <summary>A committed page image. Versions of the same page form a newest-first chain.</summary>
internal sealed unsafe class PageVersion(long seq, byte* ptr, PageVersion? older)
{
    public readonly long Seq = seq;
    public readonly byte* Ptr = ptr;
    public readonly PageVersion? Older = older;
}

/// <summary>Sparse array indexed by page number (chunks of 4096 entries). Lock-free reads, single writer.</summary>
internal sealed class PageTable<T>
{
    private const int Shift = 12, ChunkSize = 1 << Shift, Mask = ChunkSize - 1;
    private T[]?[] _chunks = new T[]?[8];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? Get(long page)
    {
        var ch = Volatile.Read(ref _chunks);
        long c = page >> Shift;
        if ((ulong)c >= (ulong)ch.Length) return default;
        var a = ch[c];
        return a is null ? default : a[page & Mask];
    }

    public void Set(long page, T value)
    {
        long c = page >> Shift;
        var ch = _chunks;
        if (c >= ch.Length)
        {
            var n = new T[]?[Math.Max(ch.Length * 2, c + 1)];
            Array.Copy(ch, n, ch.Length);
            Volatile.Write(ref _chunks, n);
            ch = n;
        }
        var a = ch[c];
        if (a is null)
        {
            a = new T[ChunkSize];
            Volatile.Write(ref ch[c], a);
        }
        a[page & Mask] = value;
    }

    public void Clear() => Volatile.Write(ref _chunks, new T[]?[8]);
}

/// <summary>
/// Page manager with multi-version concurrency:
/// <list type="bullet">
/// <item>a write transaction modifies private copies of pages (<see cref="WriteTx"/>);</item>
/// <item>commit appends the page images to the WAL and publishes them as new versions in an overlay;</item>
/// <item>readers see the newest version not newer than their snapshot, falling back to the base image;</item>
/// <item>checkpoint copies the newest versions into the base (memory-mapped files) and truncates the WAL.</item>
/// </list>
/// </summary>
internal sealed unsafe class Pager : IDisposable
{
    public readonly SegmentedSpace[] Spaces;
    private readonly PageTable<PageVersion>[] _overlay = [new(), new(), new()];
    private readonly List<long> _overlayKeys = new();
    private long _committedSeq;
    private long _overlayBytes;
    private readonly Wal? _wal;

    public long CommittedSeq => Volatile.Read(ref _committedSeq);
    public long OverlayBytes => Volatile.Read(ref _overlayBytes);
    public long WalSize => _wal?.Length ?? 0;
    public bool Durable => _wal != null;

    private Pager(SegmentedSpace[] spaces, Wal? wal)
    {
        Spaces = spaces;
        _wal = wal;
    }

    public static Pager InMemory() => new([SegmentedSpace.InMemory(), SegmentedSpace.InMemory(), SegmentedSpace.InMemory()], null);

    /// <summary>Opens a database directory: <c>data</c>, <c>nodes</c>, <c>rels</c> (memory-mapped) and <c>wal</c>.</summary>
    public static Pager OpenFiles(string directory, SyncMode sync)
    {
        var dir = System.IO.Path.GetFullPath(directory);
        if (File.Exists(dir)) throw new GraphQLiteException($"'{directory}' is a file: a GraphQLite database is a directory");
        Directory.CreateDirectory(dir);
        var opened = new List<IDisposable>();
        try
        {
            var wal = Wal.Open(System.IO.Path.Combine(dir, "wal"), sync);
            opened.Add(wal);
            var data = SegmentedSpace.OpenFile(System.IO.Path.Combine(dir, "data"));
            opened.Add(data);
            var nodes = SegmentedSpace.OpenFile(System.IO.Path.Combine(dir, "nodes"));
            opened.Add(nodes);
            var rels = SegmentedSpace.OpenFile(System.IO.Path.Combine(dir, "rels"));
            opened.Add(rels);
            var pager = new Pager([data, nodes, rels], wal);
            pager.Recover();
            return pager;
        }
        catch
        {
            foreach (var d in opened) d.Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Key(Space s, long page) => ((long)s << 56) | page;

    private const long PageMask = 0x00FF_FFFF_FFFF_FFFF;

    /// <summary>Reads a committed page as seen by a snapshot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* Read(Space s, long page, long snapshot)
    {
        var v = _overlay[(int)s].Get(page);
        if (v != null)
        {
            for (; v != null; v = v.Older)
                if (v.Seq <= snapshot) return v.Ptr;
        }
        return Spaces[(int)s].PagePtr(page);
    }

    private void Publish(long seq, long key, nint ptr)
    {
        var table = _overlay[(int)(key >> 56)];
        long page = key & PageMask;
        var older = table.Get(page);
        if (older is null) _overlayKeys.Add(key);
        table.Set(page, new PageVersion(seq, (byte*)ptr, older));
    }

    /// <summary>Publishes the dirty pages of a transaction. Caller holds the writer gate.</summary>
    public void Commit(List<(long Key, nint Ptr)> dirty)
    {
        if (dirty.Count == 0) return;
        long seq = _committedSeq + 1;
        _wal?.Append(seq, dirty, key => (nint)Read((Space)(key >> 56), key & PageMask, long.MaxValue));
        foreach (var (key, ptr) in dirty) Publish(seq, key, ptr);
        Interlocked.Add(ref _overlayBytes, (long)dirty.Count * Pages.Size);
        Volatile.Write(ref _committedSeq, seq);
    }

    /// <summary>Copies the newest page versions into the base image and frees the overlay.
    /// Caller guarantees exclusivity (no readers, no active write transaction).</summary>
    public void MergeOverlay()
    {
        if (_overlayKeys.Count == 0) return;
        _wal?.Sync();
        foreach (var key in _overlayKeys)
        {
            var space = Spaces[(int)(key >> 56)];
            long page = key & PageMask;
            var v = _overlay[(int)(key >> 56)].Get(page)!;
            space.EnsurePages(page + 1);
            Pages.Copy(v.Ptr, space.PagePtr(page));
            for (var x = v; x != null; x = x.Older) Pages.Free(x.Ptr);
        }
        _overlayKeys.Clear();
        foreach (var t in _overlay) t.Clear();
        Volatile.Write(ref _overlayBytes, 0);
    }

    /// <summary>Makes the base image durable and truncates the WAL. Caller excludes writers.</summary>
    public void FlushAndTruncateWal()
    {
        if (_wal is null) return;
        foreach (var s in Spaces) s.Flush();
        _wal.Truncate();
    }

    private void Recover()
    {
        long last = 0;
        _wal!.Recover(key => (nint)Read((Space)(key >> 56), key & PageMask, long.MaxValue), (seq, pages) =>
        {
            foreach (var (key, ptr) in pages) Publish(seq, key, ptr);
            last = seq;
        });
        _committedSeq = last;
        if (_overlayKeys.Count > 0)
        {
            MergeOverlay();
            FlushAndTruncateWal();
        }
    }

    public void Dispose()
    {
        foreach (var key in _overlayKeys)
            for (var x = _overlay[(int)(key >> 56)].Get(key & PageMask); x != null; x = x.Older) Pages.Free(x.Ptr);
        _overlayKeys.Clear();
        foreach (var s in Spaces) s.Dispose();
        _wal?.Dispose();
    }
}

/// <summary>
/// Private page copies of a write transaction, with statement savepoints. The exclusive writer reads the latest
/// committed pages; a concurrent (optimistic) writer reads the snapshot it started from.
/// </summary>
internal sealed unsafe class WriteTx
{
    private readonly Pager _pager;
    private readonly long _snapshot;
    private readonly PageTable<nint>[] _dirty = [new(), new(), new()];
    private List<(long Key, nint Ptr)> _dirtyList = new();
    private Dictionary<long, nint>? _spBackup;
    private HashSet<long>? _spCreated;
    // single-entry cache of the last written page
    private long _lastKey = -1;
    private byte* _lastPtr;

    public WriteTx(Pager pager, long snapshot = long.MaxValue)
    {
        _pager = pager;
        _snapshot = snapshot;
    }

    public int DirtyCount => _dirtyList.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* Read(Space s, long page)
    {
        nint p = _dirty[(int)s].Get(page);
        return p != 0 ? (byte*)p : _pager.Read(s, page, _snapshot);
    }

    /// <summary>Returns a writable private copy of the page.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* Write(Space s, long page)
    {
        long key = Pager.Key(s, page);
        if (key == _lastKey) return _lastPtr;
        return WriteSlow(s, page, key, false);
    }

    /// <summary>Returns a writable zeroed page (the page is known to be new).</summary>
    public byte* WriteFresh(Space s, long page) => WriteSlow(s, page, Pager.Key(s, page), true);

    private byte* WriteSlow(Space s, long page, long key, bool fresh)
    {
        byte* p = (byte*)_dirty[(int)s].Get(page);
        if (p != null)
        {
            if (_spBackup != null && !_spCreated!.Contains(key) && !_spBackup.ContainsKey(key))
            {
                var b = Pages.Alloc();
                Pages.Copy(p, b);
                _spBackup[key] = (nint)b;
            }
            if (fresh) NativeMemory.Clear(p, Pages.Size);
        }
        else
        {
            p = Pages.Alloc();
            if (fresh) NativeMemory.Clear(p, Pages.Size);
            else Pages.Copy(_pager.Read(s, page, _snapshot), p);
            _dirty[(int)s].Set(page, (nint)p);
            _dirtyList.Add((key, (nint)p));
            _spCreated?.Add(key);
        }
        _lastKey = key;
        _lastPtr = p;
        return p;
    }

    // ------------------------------------------------------------------ savepoints (statement atomicity)

    public void BeginSavepoint()
    {
        ReleaseSavepoint();
        _spBackup = new Dictionary<long, nint>();
        _spCreated = new HashSet<long>();
        _lastKey = -1;
    }

    public void ReleaseSavepoint()
    {
        if (_spBackup is null) return;
        foreach (var (_, b) in _spBackup) Pages.Free((byte*)b);
        _spBackup = null;
        _spCreated = null;
    }

    public void RollbackToSavepoint()
    {
        if (_spBackup is null) return;
        if (_spCreated!.Count > 0)
        {
            var keep = new List<(long, nint)>(_dirtyList.Count);
            foreach (var (key, p) in _dirtyList)
            {
                if (_spCreated.Contains(key))
                {
                    Pages.Free((byte*)p);
                    _dirty[(int)(key >> 56)].Set(key & 0x00FF_FFFF_FFFF_FFFF, 0);
                }
                else keep.Add((key, p));
            }
            _dirtyList = keep;
        }
        foreach (var (key, b) in _spBackup)
        {
            Pages.Copy((byte*)b, (byte*)_dirty[(int)(key >> 56)].Get(key & 0x00FF_FFFF_FFFF_FFFF));
            Pages.Free((byte*)b);
        }
        _spBackup = null;
        _spCreated = null;
        _lastKey = -1;
    }

    // ------------------------------------------------------------------ end

    /// <summary>Publishes the pages; the transaction can continue with an empty change set.</summary>
    public void Commit()
    {
        ReleaseSavepoint();
        _pager.Commit(_dirtyList);
        ResetDirty();
    }

    public void Abort()
    {
        ReleaseSavepoint();
        foreach (var (_, p) in _dirtyList) Pages.Free((byte*)p);
        ResetDirty();
    }

    private void ResetDirty()
    {
        _dirtyList = new List<(long, nint)>();
        foreach (var t in _dirty) t.Clear();
        _lastKey = -1;
    }
}
/// <summary>
/// Write-ahead log. One checksummed frame per commit; each page is logged as the byte ranges that differ from
/// its previous committed image (or as a full image when most of it changed). Replaying the deltas in order is
/// idempotent byte by byte, so recovery is correct even if a checkpoint was interrupted half way.
/// </summary>
internal sealed unsafe class Wal : IDisposable
{
    private const uint Magic = 0x334C4157; // "WAL3"
    private const int HeaderSize = 24;     // magic, page count, seq, crc, body length
    private const ushort FullPage = 0xFFFF;
    private const int MergeGap = 16;
    private readonly FileStream _fs;
    private readonly SyncMode _sync;
    private readonly BinWriter _body = new(1 << 16);

    private Wal(FileStream fs, SyncMode sync)
    {
        _fs = fs;
        _sync = sync;
    }

    public long Length => _fs.Length;

    public static Wal Open(string path, SyncMode sync)
    {
        try
        {
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            return new Wal(fs, sync);
        }
        catch (IOException ex)
        {
            throw new GraphQLiteException($"Database '{path}' is locked by another process", ex);
        }
    }

    /// <summary>Appends the delta of every dirty page against its previous committed image.</summary>
    public void Append(long seq, List<(long Key, nint Ptr)> pages, Func<long, nint> previous)
    {
        var w = _body;
        w.Reset();
        Span<(int Off, int Len)> segs = stackalloc (int, int)[64];
        foreach (var (key, ptr) in pages)
        {
            var cur = new ReadOnlySpan<byte>((byte*)ptr, Pages.Size);
            var old = new ReadOnlySpan<byte>((byte*)previous(key), Pages.Size);
            int n = 0, total = 0;
            bool full = false;
            int i = 0;
            while (i < Pages.Size)
            {
                // skip equal blocks quickly (vectorized)
                int blk = Math.Min(64, Pages.Size - i);
                if (cur.Slice(i, blk).SequenceEqual(old.Slice(i, blk))) { i += blk; continue; }
                while (cur[i] == old[i]) i++;
                int start = i, end = i;
                int j = i;
                while (j < Pages.Size)
                {
                    if (cur[j] != old[j]) { end = j + 1; j++; continue; }
                    if (j - end >= MergeGap) break;
                    j++;
                }
                if (n > 0 && start - (segs[n - 1].Off + segs[n - 1].Len) < MergeGap)
                {
                    segs[n - 1].Len = end - segs[n - 1].Off;
                }
                else
                {
                    if (n == segs.Length) { full = true; break; }
                    segs[n++] = (start, end - start);
                }
                total += end - start;
                i = j; // bytes in [end, j) are known to be equal
                if (total > Pages.Size / 2) { full = true; break; }
            }
            w.WriteInt64(key);
            if (full)
            {
                BinaryPrimitivesWriteU16(w, FullPage);
                w.WriteBytes(cur);
                continue;
            }
            BinaryPrimitivesWriteU16(w, (ushort)n);
            for (int k = 0; k < n; k++)
            {
                BinaryPrimitivesWriteU16(w, (ushort)segs[k].Off);
                BinaryPrimitivesWriteU16(w, (ushort)segs[k].Len);
                w.WriteBytes(cur.Slice(segs[k].Off, segs[k].Len));
            }
        }
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], pages.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], seq);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], w.Length);
        uint crc = CrcStream.Update(0xFFFFFFFFu, header.Slice(8, 8));
        crc = CrcStream.Update(crc, header.Slice(20, 4));
        crc = CrcStream.Update(crc, w.Written);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], ~crc);
        long pos = _fs.Position;
        try
        {
            _fs.Write(header);
            _fs.Write(w.Written);
            if (_sync != SyncMode.Off) _fs.Flush(_sync == SyncMode.Full);
        }
        catch
        {
            try { _fs.SetLength(pos); _fs.Position = pos; } catch { }
            throw;
        }
        if (w.Length > 1 << 22) _body.Reset();
    }

    private static void BinaryPrimitivesWriteU16(BinWriter w, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(w.Reserve(2), v);

    /// <summary>Replays valid frames; <paramref name="current"/> returns the latest image of a page (recovered so far or base).</summary>
    public void Recover(Func<long, nint> current, Action<long, List<(long Key, nint Ptr)>> apply)
    {
        _fs.Position = 0;
        long good = 0, len = _fs.Length;
        Span<byte> header = stackalloc byte[HeaderSize];
        byte[] body = [];
        while (good + HeaderSize <= len)
        {
            _fs.Position = good;
            if (_fs.Read(header) != HeaderSize) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) break;
            int count = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            long seq = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            int bodyLen = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
            if (count <= 0 || bodyLen < 0 || good + HeaderSize + bodyLen > len) break;
            if (body.Length < bodyLen) body = new byte[bodyLen];
            _fs.ReadExactly(body, 0, bodyLen);
            uint state = CrcStream.Update(0xFFFFFFFFu, header.Slice(8, 8));
            state = CrcStream.Update(state, header.Slice(20, 4));
            state = CrcStream.Update(state, body.AsSpan(0, bodyLen));
            if (~state != crc) break;
            var pages = new List<(long, nint)>(count);
            int p = 0;
            for (int i = 0; i < count; i++)
            {
                long key = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(p));
                ushort n = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(p + 8));
                p += 10;
                var img = Pages.Alloc();
                var dst = new Span<byte>(img, Pages.Size);
                if (n == FullPage)
                {
                    body.AsSpan(p, Pages.Size).CopyTo(dst);
                    p += Pages.Size;
                }
                else
                {
                    Pages.Copy((byte*)current(key), img);
                    for (int k = 0; k < n; k++)
                    {
                        int off = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(p));
                        int l = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(p + 2));
                        body.AsSpan(p + 4, l).CopyTo(dst[off..]);
                        p += 4 + l;
                    }
                }
                pages.Add((key, (nint)img));
            }
            apply(seq, pages);
            good += HeaderSize + bodyLen;
        }
        if (good != len)
        {
            _fs.SetLength(good);
            _fs.Flush(true);
        }
        _fs.Position = good;
    }

    public void Sync()
    {
        if (_fs.Length > 0) _fs.Flush(true);
    }

    public void Truncate()
    {
        _fs.SetLength(0);
        _fs.Position = 0;
        _fs.Flush(true);
    }

    public void Dispose() => _fs.Dispose();
}