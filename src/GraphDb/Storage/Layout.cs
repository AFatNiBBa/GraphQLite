using System.Runtime.InteropServices;

namespace GraphDb.Storage;

/// <summary>Database header, stored at the beginning of page 0 of the data space.</summary>
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal unsafe struct Header
{
    public const ulong MagicValue = 0x3342444750524700UL; // "\0GRPGDB3"
    public const int CurrentVersion = 3;

    [FieldOffset(0)] public ulong Magic;
    [FieldOffset(8)] public int Version;
    [FieldOffset(12)] public int PageSize;
    [FieldOffset(16)] public long NodeHigh;
    [FieldOffset(24)] public long NodeCount;
    [FieldOffset(32)] public long RelHigh;
    [FieldOffset(40)] public long RelCount;
    /// <summary>Next never-used page of the data space.</summary>
    [FieldOffset(48)] public long DataPageHigh;
    /// <summary>Head of the list of free data pages (page number).</summary>
    [FieldOffset(56)] public long FreePageHead;
    /// <summary>Heads of the free-slot lists, one per size class (blob addresses).</summary>
    [FieldOffset(64)] public fixed long SlotFree[Blobs.SlotClasses];
    /// <summary>Page vector: [2*label] = node count, [2*label+1] = root of the label's bitmap directory.</summary>
    [FieldOffset(128)] public long LabelTable;
    /// <summary>Page vector: relationship count per type.</summary>
    [FieldOffset(136)] public long RelTypeTable;
    /// <summary>Page vector: B+tree root page per index slot.</summary>
    [FieldOffset(144)] public long IndexRoots;
    /// <summary>Blob holding the schema catalog.</summary>
    [FieldOffset(152)] public long Catalog;
    [FieldOffset(160)] public long SchemaVersion;
    /// <summary>First / last page of the token log.</summary>
    [FieldOffset(168)] public long TokenHead;
    [FieldOffset(176)] public long TokenTail;
    [FieldOffset(184)] public int TokenTailUsed;
    [FieldOffset(188)] public int NextIndexSlot;
    [FieldOffset(192)] public fixed int TokenCounts[3];
}

/// <summary>Fixed-size node record (48 bytes, 170 per page).</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal unsafe struct NodeRec
{
    public const int Size = 48;
    public const int PerPage = Pages.Size / Size;
    public const int InlineLabels = 4;
    public const byte InUse = 1, LabelOverflow = 2;

    [FieldOffset(0)] public byte Flags;
    [FieldOffset(1)] public byte LabelCount;
    [FieldOffset(4)] public int OutDegree;
    [FieldOffset(8)] public long Props;
    [FieldOffset(16)] public long OutHead;
    [FieldOffset(24)] public long InHead;
    [FieldOffset(32)] public int InDegree;
    /// <summary>Up to 4 inline label ids, or (with <see cref="LabelOverflow"/>) a blob address.</summary>
    [FieldOffset(36)] public fixed ushort Labels[InlineLabels];
    [FieldOffset(36)] public long LabelBlob;
    [FieldOffset(44)] public int LabelBlobCount;
}

/// <summary>Fixed-size relationship record (32 bytes, 256 per page).</summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct RelRec
{
    public const int Size = 32;
    public const int PerPage = Pages.Size / Size;
    public const byte InUse = 1;

    [FieldOffset(0)] public byte Flags;
    [FieldOffset(4)] public int Type;
    [FieldOffset(8)] public long Src;
    [FieldOffset(16)] public long Dst;
    [FieldOffset(24)] public long Props;
}

/// <summary>Header of an adjacency block; entries follow.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct AdjBlock
{
    public const int HeaderSize = 16;
    public const int EntrySize = 20;
    [FieldOffset(0)] public int Count;
    [FieldOffset(4)] public int Capacity;
    [FieldOffset(8)] public long Next;

    public static int CapacityFor(int bytes) => (bytes - HeaderSize) / EntrySize;
}

/// <summary>Blob addressing and size classes of the data space.</summary>
internal static class Blobs
{
    /// <summary>Slot classes: 32, 64, 128, 256, 512, 1024, 2048, 4096 bytes. Larger objects use whole pages.</summary>
    public const int SlotClasses = 8;
    public const int MaxSlot = 4096;
    /// <summary>Address flag: the property blob is stored in a chain of overflow pages.</summary>
    public const long ChainFlag = 1L << 62;
    public const int ChainHeader = 12; // [long next][int len]

    public static int ClassOf(int size) => size <= 32 ? 0 : BitUtil.Log2Ceil(size) - 5;
    public static int ClassSize(int c) => 32 << c;

    public static long Addr(long page, int offset) => (page << Pages.Shift) | (uint)offset;
    public static long PageOf(long addr) => (addr & ~ChainFlag) >> Pages.Shift;
    public static int OffsetOf(long addr) => (int)(addr & Pages.OffsetMask);
}
