namespace GraphQLite.Storage;

/// <summary>
/// Reader/writer lock optimised for many concurrent readers.
/// <list type="bullet">
/// <item>Readers enter with a single CAS when no writer is active or waiting (no kernel transition).</item>
/// <item>Writer-preferring: once a writer announces itself new readers wait, so writers never starve.</item>
/// <item>Not thread-affine: a transaction may be released from a different thread than the one that opened it.</item>
/// </list>
/// Used to give checkpoints exclusive access: transactions themselves never take it exclusively.
/// </summary>
internal sealed class RwLock : IDisposable
{
    private const int WriterBit = 1 << 30;
    private const int ReaderMask = WriterBit - 1;

    private int _state;
    private readonly ManualResetEventSlim _readersGo = new(true);
    private readonly ManualResetEventSlim _writerGo = new(false);

    public void EnterRead()
    {
        var spin = new SpinWait();
        while (true)
        {
            int s = Volatile.Read(ref _state);
            if ((s & WriterBit) == 0)
            {
                if (Interlocked.CompareExchange(ref _state, s + 1, s) == s) return;
                spin.SpinOnce(-1);
                continue;
            }
            _readersGo.Wait();
        }
    }

    public void ExitRead()
    {
        int s = Interlocked.Decrement(ref _state);
        if (s == WriterBit) _writerGo.Set(); // last reader out while a writer waits
    }

    /// <summary>Caller must already hold the writer gate.</summary>
    public void EnterWrite()
    {
        _readersGo.Reset();
        Interlocked.Or(ref _state, WriterBit);
        while (true)
        {
            _writerGo.Reset();
            if ((Volatile.Read(ref _state) & ReaderMask) == 0) return;
            _writerGo.Wait();
        }
    }

    /// <summary>Like <see cref="EnterWrite"/> but gives up (and lets readers in again) after the timeout.</summary>
    public bool TryEnterWrite(int timeoutMs)
    {
        _readersGo.Reset();
        Interlocked.Or(ref _state, WriterBit);
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            _writerGo.Reset();
            if ((Volatile.Read(ref _state) & ReaderMask) == 0) return true;
            long left = deadline - Environment.TickCount64;
            if (left <= 0 || !_writerGo.Wait((int)left))
            {
                if ((Volatile.Read(ref _state) & ReaderMask) == 0) return true;
                ExitWrite();
                return false;
            }
        }
    }

    public void ExitWrite()
    {
        Interlocked.And(ref _state, ~WriterBit);
        _readersGo.Set();
    }

    public void Dispose()
    {
        _readersGo.Dispose();
        _writerGo.Dispose();
    }
}
