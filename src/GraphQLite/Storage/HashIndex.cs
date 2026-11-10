using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace GraphQLite.Storage;

/// <summary>
/// Hash index used next to the B+tree of unique indexes, for equality lookups (linear hashing).
/// <para>
/// Entries are (64-bit hash of the encoded value, node id); values themselves are not stored, so a lookup
/// returns candidates that the caller must verify (the planner always keeps the predicate as a filter, and
/// constraint checks compare the actual property). A hash collision only costs one extra check.
/// </para>
/// <para>
/// Layout: a meta page, a page vector of bucket pages (8 buckets of 1 KB per page) and overflow buckets
/// allocated as 1 KB blobs. A bucket is [int count][int pad][long next overflow][ulong hash × 63][long id × 63].
/// Buckets split one at a time (round robin) when the average load exceeds 75%, so the structure grows
/// without ever being rebuilt. Everything lives in ordinary data pages, so MVCC, WAL and recovery apply unchanged.
/// </para>
/// </summary>
internal static unsafe class HashIndex
{
    private const int BucketSize = 1024;
    private const int PerPageShift = 3; // 8 buckets per page
    private const int Capacity = 63;
    private const int HashesOff = 16;
    private const int IdsOff = HashesOff + Capacity * 8;
    private const int SplitLoad = 47;
    private const int InitialLevel = PerPageShift;
    /// <summary>The page vector addresses 2^20 bucket pages; past that buckets stop splitting (chains grow).</summary>
    private const long MaxBuckets = (1L << 20) << PerPageShift;

    [StructLayout(LayoutKind.Sequential)]
    private struct Meta
    {
        public long Dir;
        public int Level;
        public int Pad;
        public long Split;
        public long Entries;
    }

    /// <summary>Deterministic 64-bit hash (persisted: must never change between versions).</summary>
    public static ulong HashOf(ReadOnlySpan<byte> s)
    {
        const ulong M1 = 0x87C37B91114253D5, M2 = 0x4CF5AD432745937F;
        ulong h = 0x9E3779B97F4A7C15 ^ ((ulong)s.Length * 0xFF51AFD7ED558CCD);
        int i = 0;
        for (; i + 8 <= s.Length; i += 8)
        {
            h ^= BitOperations.RotateLeft(BinaryPrimitives.ReadUInt64LittleEndian(s[i..]) * M1, 31) * M2;
            h = BitOperations.RotateLeft(h, 27) * 5 + 0x52DCE729;
        }
        if (i < s.Length)
        {
            ulong t = 0;
            for (int k = 0; i < s.Length; i++, k += 8) t |= (ulong)s[i] << k;
            h ^= BitOperations.RotateLeft(t * M1, 31) * M2;
        }
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCD;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53;
        h ^= h >> 33;
        return h;
    }

    /// <summary>Creates an empty hash index and returns its meta page.</summary>
    public static long Create(StoreTx tx)
    {
        long meta = tx.AllocPage(), dir = tx.AllocPage(), first = tx.AllocPage();
        tx.VecSet(dir, 0, first);
        var m = (Meta*)tx.WritePage(meta);
        m->Dir = dir;
        m->Level = InitialLevel;
        m->Split = 0;
        m->Entries = 0;
        return meta;
    }

    /// <summary>Frees every page and blob of the index.</summary>
    public static void Free(StoreTx tx, long meta)
    {
        var m = *(Meta*)tx.ReadPage(meta);
        long buckets = (1L << m.Level) + m.Split;
        for (long b = 0; b < buckets; b++)
        {
            long next = *(long*)(tx.R(Primary(tx, m.Dir, b)) + 8);
            while (next != 0)
            {
                long after = *(long*)(tx.R(next) + 8);
                tx.FreeBlob(next, BucketSize);
                next = after;
            }
        }
        long pages = (buckets + (1 << PerPageShift) - 1) >> PerPageShift;
        for (long g = 0; g < pages; g++) tx.FreePage(tx.VecGet(m.Dir, g));
        tx.FreeVec(m.Dir);
        tx.FreePage(meta);
    }

    private static long Primary(StoreTx tx, long dir, long bucket) =>
        Blobs.Addr(tx.VecGet(dir, bucket >> PerPageShift), (int)(bucket & ((1 << PerPageShift) - 1)) * BucketSize);

    private static long BucketOf(in Meta m, ulong h)
    {
        long b = (long)(h & ((1UL << m.Level) - 1));
        return b < m.Split ? (long)(h & ((1UL << (m.Level + 1)) - 1)) : b;
    }

    /// <summary>Appends the ids stored with hash <paramref name="h"/> (candidates: verify them).</summary>
    public static void Lookup(StoreTx tx, long meta, ulong h, List<long> output)
    {
        var m = (Meta*)tx.ReadPage(meta);
        long addr = Primary(tx, m->Dir, BucketOf(*m, h));
        while (addr != 0)
        {
            byte* p = tx.R(addr);
            int count = *(int*)p;
            var hashes = new ReadOnlySpan<ulong>(p + HashesOff, count);
            int from = 0;
            while (true)
            {
                int i = hashes[from..].IndexOf(h);
                if (i < 0) break;
                output.Add(((long*)(p + IdsOff))[from + i]);
                from += i + 1;
            }
            addr = *(long*)(p + 8);
        }
    }

    public static void Insert(StoreTx tx, long meta, ulong h, long id)
    {
        var m = *(Meta*)tx.ReadPage(meta);
        Put(tx, Primary(tx, m.Dir, BucketOf(m, h)), h, id);
        m.Entries++;
        long buckets = (1L << m.Level) + m.Split;
        if (m.Entries > buckets * SplitLoad && buckets < MaxBuckets) Split(tx, ref m);
        *(Meta*)tx.WritePage(meta) = m;
    }

    /// <summary>Removes the entry (h, id) if present.</summary>
    public static void Remove(StoreTx tx, long meta, ulong h, long id)
    {
        var m = *(Meta*)tx.ReadPage(meta);
        for (long addr = Primary(tx, m.Dir, BucketOf(m, h)); addr != 0;)
        {
            byte* p = tx.R(addr);
            int count = *(int*)p;
            var hashes = (ulong*)(p + HashesOff);
            var ids = (long*)(p + IdsOff);
            for (int i = 0; i < count; i++)
            {
                if (hashes[i] != h || ids[i] != id) continue;
                byte* w = tx.Wr(addr);
                int last = count - 1;
                ((ulong*)(w + HashesOff))[i] = ((ulong*)(w + HashesOff))[last];
                ((long*)(w + IdsOff))[i] = ((long*)(w + IdsOff))[last];
                *(int*)w = last;
                ((Meta*)tx.WritePage(meta))->Entries--;
                return;
            }
            addr = *(long*)(p + 8);
        }
    }

    /// <summary>Adds an entry to the first bucket of the chain with free room, chaining a new overflow bucket if needed.</summary>
    private static void Put(StoreTx tx, long addr, ulong h, long id)
    {
        while (true)
        {
            byte* p = tx.R(addr);
            int count = *(int*)p;
            if (count < Capacity)
            {
                byte* w = tx.Wr(addr);
                ((ulong*)(w + HashesOff))[count] = h;
                ((long*)(w + IdsOff))[count] = id;
                *(int*)w = count + 1;
                return;
            }
            long next = *(long*)(p + 8);
            if (next == 0)
            {
                next = tx.AllocBlob(BucketSize);
                byte* nb = tx.Wr(next);
                *(int*)nb = 0;
                *(long*)(nb + 8) = 0;
                *(long*)(tx.Wr(addr) + 8) = next;
            }
            addr = next;
        }
    }

    /// <summary>Splits the bucket at the split pointer between itself and its new image (linear hashing).</summary>
    private static void Split(StoreTx tx, ref Meta m)
    {
        long src = m.Split, dst = (1L << m.Level) + m.Split;
        if ((dst & ((1 << PerPageShift) - 1)) == 0) tx.VecSet(m.Dir, dst >> PerPageShift, tx.AllocPage());

        // collect the whole chain, then reset it to an empty primary bucket
        var entries = new List<(ulong H, long Id)>(Capacity * 2);
        long primary = Primary(tx, m.Dir, src);
        for (long addr = primary; addr != 0;)
        {
            byte* p = tx.R(addr);
            int count = *(int*)p;
            for (int i = 0; i < count; i++) entries.Add((((ulong*)(p + HashesOff))[i], ((long*)(p + IdsOff))[i]));
            long next = *(long*)(p + 8);
            if (addr != primary) tx.FreeBlob(addr, BucketSize);
            addr = next;
        }
        byte* pw = tx.Wr(primary);
        *(int*)pw = 0;
        *(long*)(pw + 8) = 0;

        ulong bit = 1UL << m.Level;
        long target = Primary(tx, m.Dir, dst);
        foreach (var (h, id) in entries) Put(tx, (h & bit) != 0 ? target : primary, h, id);

        if (++m.Split == 1L << m.Level)
        {
            m.Level++;
            m.Split = 0;
        }
    }
}
