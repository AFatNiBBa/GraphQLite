using System.Buffers;

namespace GraphQLite.Runtime;

/// <summary>
/// Runs its input, a chain of row-local operators over a partitioned scan (<see cref="Partitioning.FindLeaf"/>), on
/// several threads and returns the rows in the order of a sequential run: the node id ranges are computed by workers
/// into buffers and consumed in range order. Workers stay at most a window of ranges ahead of the consumer, so a
/// LIMIT or a slow reader does not make them compute (and buffer) the whole result. Small inputs and write
/// transactions run sequentially.
/// </summary>
internal sealed class GatherOp(Operator input, Operator leaf, int[] output) : UnaryOperator(input)
{
    private readonly Operator leaf = leaf;
    /// <summary>Slots of the result columns: the workers materialize the entities there (<see cref="QueryResult.Materializer"/>).</summary>
    private readonly int[] output = output;
    public override string Name => "Gather";
    public override Cursor Open(ExecContext ctx) => new C(this, ctx);

    private const int Waiting = 0, Running = 1, Done = 2, Cancelled = 3;

    /// <summary>Rows of one range: <see cref="Count"/> rows of the full row width, one after the other.</summary>
    private sealed class Chunk
    {
        public int State;
        public Value[]? Rows;
        public int Count;
        public Exception? Error;
    }

    private sealed class C(GatherOp op, ExecContext ctx) : Cursor
    {
        private Cursor? _sequential;
        private Chunk[]? _chunks;
        private long _size;
        private int _workers, _scheduled, _current = -1, _pos;
        private Value[] _initial = [];
        private CancellationTokenSource? _stop;

        public override bool Next()
        {
            if (_sequential != null) return _sequential.Next();
            if (_chunks is null && !Start()) return _sequential!.Next();
            if (_current >= _chunks!.Length) return false;
            int width = ctx.Row.Length;
            while (true)
            {
                if (_current >= 0)
                {
                    var c = _chunks![_current];
                    if (_pos < c.Count)
                    {
                        c.Rows.AsSpan(_pos++ * width, width).CopyTo(ctx.Row);
                        return true;
                    }
                    Recycle(c);
                }
                if (++_current >= _chunks!.Length) return false;
                Schedule(_current + _workers * 2);
                var chunk = _chunks[_current];
                Run(chunk, _current); // nobody took it yet: the consumer computes it itself
                lock (chunk)
                {
                    while (chunk.State != Done) Monitor.Wait(chunk);
                }
                if (chunk.Error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(chunk.Error);
                _pos = 0;
            }
        }

        /// <summary>Decides between a sequential and a parallel run; false for sequential.</summary>
        private bool Start()
        {
            _workers = Partitioning.Workers(ctx, op.leaf);
            if (_workers <= 1)
            {
                _sequential = op.Input.Open(ctx);
                return false;
            }
            (_size, int count) = Partitioning.Ranges(ctx, _workers);
            _chunks = new Chunk[count];
            for (int i = 0; i < count; i++) _chunks[i] = new Chunk();
            _initial = RowOps.Snapshot(ctx.Row);
            _ = ctx.Now; // the clock and the seed are read lazily: fix them before the workers copy them
            _ = ctx.Seed;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation);
            ctx.OnStop(Stop);
            return true;
        }

        /// <summary>Queues the ranges below <paramref name="end"/> (exclusive) not queued yet.</summary>
        private void Schedule(int end)
        {
            end = Math.Min(end, _chunks!.Length);
            for (; _scheduled < end; _scheduled++)
            {
                int i = _scheduled;
                ThreadPool.UnsafeQueueUserWorkItem(static s => s.Self.Run(s.Self._chunks![s.Index], s.Index), (Self: this, Index: i), false);
            }
        }

        /// <summary>Computes range <paramref name="index"/>, unless another thread took it or the statement ended.</summary>
        private void Run(Chunk chunk, int index)
        {
            if (Interlocked.CompareExchange(ref chunk.State, Running, Waiting) != Waiting) return;
            try
            {
                var w = ctx.ForPartition(op.leaf, index * _size, (index + 1) * _size, _initial, _stop!.Token);
                var cursor = op.Input.Open(w);
                int width = w.Row.Length;
                var rows = ArrayPool<Value>.Shared.Rent(Math.Max(64, width * 64));
                int n = 0;
                var mat = new QueryResult.Materializer(w.Store);
                try
                {
                    while (cursor.Next())
                    {
                        foreach (int s in op.output) w.Row[s] = mat.Convert(w.Row[s]);
                        if ((n + 1) * width > rows.Length)
                        {
                            var bigger = ArrayPool<Value>.Shared.Rent(rows.Length * 2);
                            rows.AsSpan(0, n * width).CopyTo(bigger);
                            ArrayPool<Value>.Shared.Return(rows, true);
                            rows = bigger;
                        }
                        w.Row.AsSpan().CopyTo(rows.AsSpan(n++ * width));
                    }
                }
                catch
                {
                    ArrayPool<Value>.Shared.Return(rows, true);
                    throw;
                }
                chunk.Rows = rows;
                chunk.Count = n;
            }
            catch (Exception ex)
            {
                chunk.Error = ex;
            }
            lock (chunk)
            {
                chunk.State = Done;
                Monitor.PulseAll(chunk);
            }
        }

        private static void Recycle(Chunk c)
        {
            if (c.Rows != null) ArrayPool<Value>.Shared.Return(c.Rows, true);
            c.Rows = null;
            c.Count = 0;
        }

        /// <summary>
        /// End of the statement: ranges not started are cancelled, running ones are stopped and waited for (they read
        /// the snapshot that the transaction is about to release).
        /// </summary>
        private void Stop()
        {
            _stop!.Cancel();
            foreach (var c in _chunks!)
            {
                if (Interlocked.CompareExchange(ref c.State, Cancelled, Waiting) == Waiting) continue;
                lock (c)
                {
                    while (c.State == Running) Monitor.Wait(c);
                }
                Recycle(c);
            }
            _stop.Dispose();
        }
    }
}
