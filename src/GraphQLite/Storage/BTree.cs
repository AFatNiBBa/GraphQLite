using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace GraphQLite.Storage;

/// <summary>
/// Order-preserving binary encoding of indexable values: comparing encoded keys bytewise gives the
/// Cypher ordering within a type. A key is [tag][value bytes][8-byte big-endian node id].
/// </summary>
internal static class IndexKeys
{
    public const byte TBool = 0x10, TNumber = 0x20, TString = 0x30, TDate = 0x40, TLocalTime = 0x41, TTime = 0x42, TLocalDateTime = 0x43, TDateTime = 0x44;
    public const int MaxValueBytes = 900;

    public static byte TagOf(ValueKind k) => k switch
    {
        ValueKind.Boolean => TBool,
        ValueKind.Integer or ValueKind.Float => TNumber,
        ValueKind.String => TString,
        ValueKind.Date => TDate,
        ValueKind.LocalTime => TLocalTime,
        ValueKind.Time => TTime,
        ValueKind.LocalDateTime => TLocalDateTime,
        ValueKind.DateTime => TDateTime,
        _ => 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSortableLong(BinWriter w, long v) => BinaryPrimitives.WriteUInt64BigEndian(w.Reserve(8), (ulong)(v ^ long.MinValue));

    /// <summary>Encodes the value part. Returns false for values that are not indexed (null, NaN, lists, maps...).</summary>
    public static bool EncodeValue(in Value v, BinWriter w)
    {
        switch (v.Kind)
        {
            case ValueKind.Boolean:
                w.WriteByte(TBool);
                w.WriteByte((byte)v.Num);
                return true;
            case ValueKind.Integer:
            case ValueKind.Float:
                {
                    double d = v.AsDouble();
                    if (double.IsNaN(d)) return false;
                    if (d == 0) d = 0; // fold -0.0
                    long bits = BitConverter.DoubleToInt64Bits(d);
                    ulong u = bits < 0 ? ~(ulong)bits : (ulong)bits ^ 0x8000_0000_0000_0000UL;
                    w.WriteByte(TNumber);
                    BinaryPrimitives.WriteUInt64BigEndian(w.Reserve(8), u);
                    return true;
                }
            case ValueKind.String:
                w.WriteByte(TString);
                WriteEscaped(w, v.Str, terminate: true);
                return true;
            case ValueKind.Date:
            case ValueKind.LocalTime:
            case ValueKind.LocalDateTime:
            case ValueKind.DateTime:
                w.WriteByte(TagOf(v.Kind));
                WriteSortableLong(w, v.Num);
                return true;
            case ValueKind.Time:
                w.WriteByte(TTime);
                WriteSortableLong(w, Temporal.TimeUtcTicks(v));
                return true;
            default:
                return false;
        }
    }

    /// <summary>UTF-8 with 0x00 escaped as 0x00 0xFF, terminated by 0x00 0x00; truncated (unterminated) if too long.</summary>
    public static void WriteEscaped(BinWriter w, string s, bool terminate)
    {
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        Span<byte> tmp = max <= 2048 ? stackalloc byte[max] : new byte[max];
        int n = Encoding.UTF8.GetBytes(s, tmp);
        int written = 0;
        for (int i = 0; i < n; i++)
        {
            if (written >= MaxValueBytes) return; // truncated key: never terminated
            byte b = tmp[i];
            w.WriteByte(b);
            written++;
            if (b == 0) { w.WriteByte(0xFF); written++; }
        }
        if (terminate)
        {
            w.WriteByte(0);
            w.WriteByte(0);
        }
    }

    public static void AppendId(BinWriter w, long id) => BinaryPrimitives.WriteInt64BigEndian(w.Reserve(8), id);

    public static long IdOf(ReadOnlySpan<byte> key) => BinaryPrimitives.ReadInt64BigEndian(key[^8..]);

    /// <summary>Smallest byte string greater than every string having <paramref name="prefix"/> as prefix (null = unbounded).</summary>
    public static byte[]? Successor(ReadOnlySpan<byte> prefix)
    {
        var b = prefix.ToArray();
        for (int i = b.Length - 1; i >= 0; i--)
        {
            if (b[i] != 0xFF)
            {
                b[i]++;
                return b[..(i + 1)];
            }
        }
        return null;
    }
}

/// <summary>
/// B+tree over data-space pages with variable-length keys (no values: keys embed the node id).
/// Page: [kind:1][pad:1][count:2][cellStart:2][pad:2][link:8][slots:2*count] ... cells at the end.
/// Leaf cell: [len:2][key]. Internal cell: [len:2][key][child:8]; link = leftmost child (internal) or next leaf.
/// </summary>
internal static unsafe class BTree
{
    private const byte Leaf = 1, Internal = 2;
    private const int Hdr = 16;
    public const int MaxKey = 1024;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Count(byte* p) => *(ushort*)(p + 2);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ushort CountRef(byte* p) => ref *(ushort*)(p + 2);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ushort CellStart(byte* p) => ref *(ushort*)(p + 4);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref long Link(byte* p) => ref *(long*)(p + 8);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort* Slots(byte* p) => (ushort*)(p + Hdr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> KeyAt(byte* p, int i)
    {
        byte* c = p + Slots(p)[i];
        return new ReadOnlySpan<byte>(c + 2, *(ushort*)c);
    }

    private static long ChildAt(byte* p, int i)
    {
        byte* c = p + Slots(p)[i];
        return *(long*)(c + 2 + *(ushort*)c);
    }

    private static int CellSize(byte* p, int i, bool internalPage)
    {
        byte* c = p + Slots(p)[i];
        return 2 + *(ushort*)c + (internalPage ? 8 : 0);
    }

    public static long NewTree(StoreTx tx)
    {
        long page = tx.AllocPage();
        InitPage(tx.WritePageFresh(page), Leaf);
        return page;
    }

    private static void InitPage(byte* p, byte kind)
    {
        p[0] = kind;
        CountRef(p) = 0;
        CellStart(p) = Pages.Size;
        Link(p) = 0;
    }

    /// <summary>Index of the first slot with key &gt;= <paramref name="key"/>.</summary>
    private static int LowerBound(byte* p, ReadOnlySpan<byte> key)
    {
        int lo = 0, hi = Count(p);
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (KeyAt(p, mid).SequenceCompareTo(key) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Child page to descend into for <paramref name="key"/>.</summary>
    private static long ChildFor(byte* p, ReadOnlySpan<byte> key)
    {
        // last slot with key <= search key
        int lo = 0, hi = Count(p);
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (KeyAt(p, mid).SequenceCompareTo(key) <= 0) lo = mid + 1;
            else hi = mid;
        }
        return lo == 0 ? Link(p) : ChildAt(p, lo - 1);
    }

    private static int FreeSpace(byte* p) => CellStart(p) - (Hdr + 2 * Count(p));

    private static void InsertCell(byte* p, int pos, ReadOnlySpan<byte> key, long child, bool internalPage)
    {
        int size = 2 + key.Length + (internalPage ? 8 : 0);
        ushort start = (ushort)(CellStart(p) - size);
        byte* c = p + start;
        *(ushort*)c = (ushort)key.Length;
        key.CopyTo(new Span<byte>(c + 2, key.Length));
        if (internalPage) *(long*)(c + 2 + key.Length) = child;
        CellStart(p) = start;
        int n = Count(p);
        ushort* s = Slots(p);
        for (int i = n; i > pos; i--) s[i] = s[i - 1];
        s[pos] = start;
        CountRef(p) = (ushort)(n + 1);
    }

    /// <summary>Rewrites the page keeping only live cells (reclaims space of deleted cells).</summary>
    private static void Compact(byte* p, bool internalPage)
    {
        int n = Count(p);
        var cells = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            int size = CellSize(p, i, internalPage);
            cells[i] = new ReadOnlySpan<byte>(p + Slots(p)[i], size).ToArray();
        }
        ushort start = Pages.Size;
        for (int i = 0; i < n; i++)
        {
            start = (ushort)(start - cells[i].Length);
            cells[i].CopyTo(new Span<byte>(p + start, cells[i].Length));
            Slots(p)[i] = start;
        }
        CellStart(p) = start;
    }

    private static List<byte[]> ReadCells(byte* p, bool internalPage)
    {
        int n = Count(p);
        var list = new List<byte[]>(n + 1);
        for (int i = 0; i < n; i++) list.Add(new ReadOnlySpan<byte>(p + Slots(p)[i], CellSize(p, i, internalPage)).ToArray());
        return list;
    }

    private static void WriteCells(byte* p, List<byte[]> cells, int from, int to, byte kind, long link)
    {
        InitPage(p, kind);
        Link(p) = link;
        ushort start = Pages.Size;
        for (int i = from; i < to; i++)
        {
            start = (ushort)(start - cells[i].Length);
            cells[i].CopyTo(new Span<byte>(p + start, cells[i].Length));
            Slots(p)[i - from] = start;
        }
        CellStart(p) = start;
        CountRef(p) = (ushort)(to - from);
    }

    private static byte[] MakeCell(ReadOnlySpan<byte> key, long child, bool internalPage)
    {
        var c = new byte[2 + key.Length + (internalPage ? 8 : 0)];
        BinaryPrimitives.WriteUInt16LittleEndian(c, (ushort)key.Length);
        key.CopyTo(c.AsSpan(2));
        if (internalPage) BinaryPrimitives.WriteInt64LittleEndian(c.AsSpan(2 + key.Length), child);
        return c;
    }

    private static ReadOnlySpan<byte> CellKey(byte[] cell) => cell.AsSpan(2, BinaryPrimitives.ReadUInt16LittleEndian(cell));

    /// <summary>Inserts a key; returns the (possibly new) root page.</summary>
    public static long Insert(StoreTx tx, long root, ReadOnlySpan<byte> key)
    {
        if (key.Length > MaxKey) throw new GraphQLiteException("Index key too long");
        var split = InsertRec(tx, root, key);
        if (split is null) return root;
        long newRoot = tx.AllocPage();
        byte* p = tx.WritePageFresh(newRoot);
        InitPage(p, Internal);
        Link(p) = root;
        InsertCell(p, 0, split.Value.Sep, split.Value.Right, true);
        return newRoot;
    }

    private static (byte[] Sep, long Right)? InsertRec(StoreTx tx, long page, ReadOnlySpan<byte> key)
    {
        byte* p = tx.ReadPage(page);
        if (p[0] == Leaf)
        {
            int pos = LowerBound(p, key);
            if (pos < Count(p) && KeyAt(p, pos).SequenceEqual(key)) return null;
            p = tx.WritePage(page);
            return InsertInto(tx, page, p, pos, key, 0, false);
        }
        long child = ChildFor(p, key);
        var split = InsertRec(tx, child, key);
        if (split is null) return null;
        p = tx.WritePage(page);
        int ipos = LowerBound(p, split.Value.Sep);
        return InsertInto(tx, page, p, ipos, split.Value.Sep, split.Value.Right, true);
    }

    private static (byte[] Sep, long Right)? InsertInto(StoreTx tx, long page, byte* p, int pos, ReadOnlySpan<byte> key, long child, bool internalPage)
    {
        int need = 2 + key.Length + (internalPage ? 8 : 0) + 2;
        if (FreeSpace(p) < need) Compact(p, internalPage);
        if (FreeSpace(p) >= need)
        {
            InsertCell(p, pos, key, child, internalPage);
            return null;
        }
        // split
        var cells = ReadCells(p, internalPage);
        cells.Insert(pos, MakeCell(key, child, internalPage));
        int total = 0;
        foreach (var c in cells) total += c.Length + 2;
        int acc = 0, mid = 0;
        while (mid < cells.Count - 1 && acc + cells[mid].Length + 2 < total / 2)
        {
            acc += cells[mid].Length + 2;
            mid++;
        }
        mid = Math.Clamp(mid, 1, cells.Count - 1);
        long right = tx.AllocPage();
        byte* rp = tx.WritePageFresh(right);
        p = tx.WritePage(page);
        if (!internalPage)
        {
            long oldLink = Link(p);
            WriteCells(rp, cells, mid, cells.Count, Leaf, oldLink);
            WriteCells(p, cells, 0, mid, Leaf, right);
            return (CellKey(cells[mid]).ToArray(), right);
        }
        else
        {
            // separator moves up; its child becomes the leftmost child of the right page
            var sepCell = cells[mid];
            var sep = CellKey(sepCell).ToArray();
            long sepChild = BinaryPrimitives.ReadInt64LittleEndian(sepCell.AsSpan(sepCell.Length - 8));
            long leftLink = Link(p);
            WriteCells(rp, cells, mid + 1, cells.Count, Internal, sepChild);
            WriteCells(p, cells, 0, mid, Internal, leftLink);
            return (sep, right);
        }
    }

    /// <summary>Removes an exact key. Pages are never merged (empty leaves stay linked).</summary>
    public static bool Delete(StoreTx tx, long root, ReadOnlySpan<byte> key)
    {
        long page = root;
        while (true)
        {
            byte* p = tx.ReadPage(page);
            if (p[0] == Internal)
            {
                page = ChildFor(p, key);
                continue;
            }
            int pos = LowerBound(p, key);
            if (pos >= Count(p) || !KeyAt(p, pos).SequenceEqual(key)) return false;
            p = tx.WritePage(page);
            int n = Count(p);
            ushort* s = Slots(p);
            for (int i = pos; i < n - 1; i++) s[i] = s[i + 1];
            CountRef(p) = (ushort)(n - 1);
            return true;
        }
    }

    public static void FreeTree(StoreTx tx, long page)
    {
        byte* p = tx.ReadPage(page);
        if (p[0] == Internal)
        {
            int n = Count(p);
            var children = new List<long> { Link(p) };
            for (int i = 0; i < n; i++) children.Add(ChildAt(p, i));
            foreach (var c in children) FreeTree(tx, c);
        }
        tx.FreePage(page);
    }

    /// <summary>Positions on the first key &gt;= low.</summary>
    public static (long Page, int Slot) Seek(StoreTx tx, long root, ReadOnlySpan<byte> low)
    {
        long page = root;
        while (true)
        {
            byte* p = tx.ReadPage(page);
            if (p[0] == Internal)
            {
                page = ChildFor(p, low);
                continue;
            }
            return (page, LowerBound(p, low));
        }
    }

    /// <summary>Copies the key at the cursor into <paramref name="buf"/> and advances. Returns the key length or -1 at the end.</summary>
    public static int Next(StoreTx tx, ref long page, ref int slot, ref byte[] buf)
    {
        while (page != 0)
        {
            byte* p = tx.ReadPage(page);
            if (slot < Count(p))
            {
                var k = KeyAt(p, slot++);
                if (k.Length > buf.Length) buf = new byte[Math.Max(k.Length, buf.Length * 2)];
                k.CopyTo(buf);
                return k.Length;
            }
            page = Link(p);
            slot = 0;
        }
        return -1;
    }
}

/// <summary>Range scan over an index: yields node ids whose key lies in [low, high).</summary>
internal sealed class IndexRangeCursor
{
    private readonly StoreTx _tx;
    private readonly byte[]? _high;
    private byte[] _buf = new byte[64];
    private long _page;
    private int _slot;

    public IndexRangeCursor(StoreTx tx, long root, ReadOnlySpan<byte> low, byte[]? high)
    {
        _tx = tx;
        _high = high;
        (_page, _slot) = BTree.Seek(tx, root, low);
    }

    public bool Next(out long nodeId)
    {
        int n = BTree.Next(_tx, ref _page, ref _slot, ref _buf);
        if (n < 0 || (_high != null && _buf.AsSpan(0, n).SequenceCompareTo(_high) >= 0))
        {
            _page = 0;
            nodeId = -1;
            return false;
        }
        nodeId = IndexKeys.IdOf(_buf.AsSpan(0, n));
        return true;
    }
}
