namespace GraphQLite.Storage;

/// <summary>
/// Reader/writer lock optimised for many concurrent readers.
/// <list type="bullet">
/// <item>Readers are counted on per-processor counters, each on its own cache line: entering and leaving is one
/// uncontended atomic increment/decrement, so readers on different cores never fight over the same cache line.</item>
/// <item>Writer-preferring: once a writer announces itself new readers wait, so writers never starve.</item>
/// <item>Not thread-affine: a transaction may be released from a different thread (or core) than the one that opened
/// it. A reader may decrement a different counter than the one it incremented, so single counters can go negative:
/// only their sum (the number of readers inside) is meaningful.</item>
/// </list>
/// Used to give checkpoints exclusive access: transactions themselves never take it exclusively.
/// </summary>
/// <remarks>
/// Correctness (Dekker): a reader increments its counter and then reads the writer flag; the writer sets the flag and
/// then sums the counters. Both steps are full fences, so either the reader sees the flag (and backs off) or the writer
/// sees the increment. Every reader inside incremented before the flag was set, so the writer sees all of their
/// increments and at most the decrements of readers that already left: a sum of zero means nobody is inside.
/// </remarks>
internal sealed class RwLock : IDisposable
{
    /// <summary>Distance between two counters, in longs: 128 bytes, so adjacent counters never share a cache line
    /// (nor the pair of lines the hardware prefetcher fetches together).</summary>
    private const int Stride = 16;

    private readonly long[] _counts;
    private readonly int _mask;
    private int _writer;
    private readonly ManualResetEventSlim _readersGo = new(true);
    private readonly ManualResetEventSlim _writerGo = new(false);

    public RwLock()
    {
        int slots = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, Environment.ProcessorCount));
        _mask = slots - 1;
        _counts = new long[(slots + 1) * Stride]; // slot 0 starts one stride in: away from the array header
    }

    private ref long Counter() => ref _counts[((Thread.GetCurrentProcessorId() & _mask) + 1) * Stride];

    public void EnterRead()
    {
        while (true)
        {
            ref long c = ref Counter();
            Interlocked.Increment(ref c);
            if (Volatile.Read(ref _writer) == 0) return;
            // a writer is waiting or active: back off (it may be waiting for this very increment) and wait for it
            Interlocked.Decrement(ref c);
            _writerGo.Set();
            _readersGo.Wait();
        }
    }

    public void ExitRead()
    {
        Interlocked.Decrement(ref Counter());
        if (Volatile.Read(ref _writer) != 0) _writerGo.Set(); // possibly the last reader out while a writer waits
    }

    private long Readers()
    {
        long sum = 0;
        for (int i = 0; i <= _mask; i++) sum += Volatile.Read(ref _counts[(i + 1) * Stride]);
        return sum;
    }

    private void Announce()
    {
        _readersGo.Reset();
        Interlocked.Exchange(ref _writer, 1);
    }

    /// <summary>Caller must already hold the writer gate.</summary>
    public void EnterWrite()
    {
        Announce();
        while (true)
        {
            _writerGo.Reset();
            if (Readers() == 0) return;
            _writerGo.Wait();
        }
    }

    /// <summary>Like <see cref="EnterWrite"/> but gives up (and lets readers in again) after the timeout.</summary>
    public bool TryEnterWrite(int timeoutMs)
    {
        Announce();
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            _writerGo.Reset();
            if (Readers() == 0) return true;
            long left = deadline - Environment.TickCount64;
            if (left <= 0 || !_writerGo.Wait((int)left))
            {
                if (Readers() == 0) return true;
                ExitWrite();
                return false;
            }
        }
    }

    public void ExitWrite()
    {
        Volatile.Write(ref _writer, 0);
        _readersGo.Set();
    }

    public void Dispose()
    {
        _readersGo.Dispose();
        _writerGo.Dispose();
    }
}
