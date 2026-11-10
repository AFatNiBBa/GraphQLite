namespace GraphQLite.Runtime;

internal abstract class Aggregator
{
    public abstract void Add(in Value v);
    public abstract Value Result();

    /// <summary>Adds the state of <paramref name="other"/> (same kind), as if its inputs had been added after ours.</summary>
    public abstract void Merge(Aggregator other);
}

internal enum AggKind { Count, CountStar, Sum, Avg, Min, Max, Collect, StDev, StDevP, PercentileCont, PercentileDisc }

internal sealed class AggSpec(AggKind kind, Eval? arg, Eval? param, bool distinct, int slot, string text)
{
    public readonly AggKind Kind = kind;
    public readonly Eval? Arg = arg;
    public readonly Eval? Param = param;
    public readonly bool Distinct = distinct;
    public readonly int Slot = slot;
    public readonly string Text = text;

    public static AggKind? Parse(string name) => name switch
    {
        "count" => AggKind.Count,
        "sum" => AggKind.Sum,
        "avg" => AggKind.Avg,
        "min" => AggKind.Min,
        "max" => AggKind.Max,
        "collect" => AggKind.Collect,
        "stdev" => AggKind.StDev,
        "stdevp" => AggKind.StDevP,
        "percentilecont" => AggKind.PercentileCont,
        "percentiledisc" => AggKind.PercentileDisc,
        _ => null,
    };

    public Aggregator Create(ExecContext ctx)
    {
        Aggregator a = Kind switch
        {
            AggKind.Count or AggKind.CountStar => new CountAgg(),
            AggKind.Sum => new SumAgg(),
            AggKind.Avg => new AvgAgg(),
            AggKind.Min => new MinMaxAgg(false),
            AggKind.Max => new MinMaxAgg(true),
            AggKind.Collect => new CollectAgg(),
            AggKind.StDev => new StDevAgg(true),
            AggKind.StDevP => new StDevAgg(false),
            AggKind.PercentileCont => new PercentileAgg(true, Param!(ctx)),
            AggKind.PercentileDisc => new PercentileAgg(false, Param!(ctx)),
            _ => throw new InvalidOperationException(),
        };
        return Distinct ? new DistinctAgg(a) : a;
    }

    private sealed class CountAgg : Aggregator
    {
        private long _n;
        public override void Add(in Value v) { if (!v.IsNull) _n++; }
        public override Value Result() => Value.FromInt(_n);
        public override void Merge(Aggregator other) => _n += ((CountAgg)other)._n;
    }

    private sealed class SumAgg : Aggregator
    {
        private long _l;
        private double _d;
        private bool _float;

        public override void Add(in Value v)
        {
            switch (v.Kind)
            {
                case ValueKind.Null: return;
                case ValueKind.Integer:
                    if (_float) _d += v.Num;
                    else
                    {
                        try { _l = checked(_l + v.Num); }
                        catch (OverflowException) { throw new CypherRuntimeException("Integer overflow in sum()"); }
                    }
                    return;
                case ValueKind.Float:
                    if (!_float) { _float = true; _d = _l; }
                    _d += v.FloatBits;
                    return;
                default: throw new CypherRuntimeException($"sum() expects numbers, got {v.Kind}");
            }
        }

        public override Value Result() => _float ? Value.FromFloat(_d) : Value.FromInt(_l);

        public override void Merge(Aggregator other)
        {
            var o = (SumAgg)other;
            if (_float || o._float)
            {
                _d = (_float ? _d : _l) + (o._float ? o._d : o._l);
                _float = true;
                return;
            }
            try { _l = checked(_l + o._l); }
            catch (OverflowException) { throw new CypherRuntimeException("Integer overflow in sum()"); }
        }
    }

    private sealed class AvgAgg : Aggregator
    {
        private double _sum;
        private long _n;

        public override void Add(in Value v)
        {
            if (v.IsNull) return;
            if (!v.IsNumber) throw new CypherRuntimeException($"avg() expects numbers, got {v.Kind}");
            _sum += v.AsDouble();
            _n++;
        }

        public override Value Result() => _n == 0 ? Value.Null : Value.FromFloat(_sum / _n);

        public override void Merge(Aggregator other)
        {
            var o = (AvgAgg)other;
            _sum += o._sum;
            _n += o._n;
        }
    }

    private sealed class MinMaxAgg(bool max) : Aggregator
    {
        private Value _best;
        private bool _has;

        public override void Add(in Value v)
        {
            if (v.IsNull) return;
            if (!_has) { _best = v; _has = true; return; }
            int c = ValueOps.OrderCompare(v, _best);
            if (max ? c > 0 : c < 0) _best = v;
        }

        public override Value Result() => _has ? _best : Value.Null;

        public override void Merge(Aggregator other)
        {
            var o = (MinMaxAgg)other;
            if (o._has) Add(o._best);
        }
    }

    private sealed class CollectAgg : Aggregator
    {
        private readonly List<Value> _items = new();
        public override void Add(in Value v) { if (!v.IsNull) _items.Add(v); }
        public override Value Result() => Value.FromList(_items.ToArray());
        public override void Merge(Aggregator other) => _items.AddRange(((CollectAgg)other)._items);
    }

    private sealed class StDevAgg(bool sample) : Aggregator
    {
        private long _n;
        private double _mean, _m2;

        public override void Add(in Value v)
        {
            if (v.IsNull) return;
            double x = v.AsDouble();
            _n++;
            double d = x - _mean;
            _mean += d / _n;
            _m2 += d * (x - _mean);
        }

        public override Value Result()
        {
            if (_n == 0) return Value.FromFloat(0);
            if (sample) return Value.FromFloat(_n < 2 ? 0 : Math.Sqrt(_m2 / (_n - 1)));
            return Value.FromFloat(Math.Sqrt(_m2 / _n));
        }

        public override void Merge(Aggregator other)
        {
            // Chan et al. parallel variance
            var o = (StDevAgg)other;
            if (o._n == 0) return;
            if (_n == 0)
            {
                (_n, _mean, _m2) = (o._n, o._mean, o._m2);
                return;
            }
            long n = _n + o._n;
            double d = o._mean - _mean;
            _mean += d * o._n / n;
            _m2 += o._m2 + d * d * ((double)_n * o._n / n);
            _n = n;
        }
    }

    private sealed class PercentileAgg(bool cont, Value p) : Aggregator
    {
        private readonly List<double> _vals = new();
        private readonly List<Value> _raw = new();

        public override void Add(in Value v)
        {
            if (v.IsNull) return;
            _vals.Add(v.AsDouble());
            _raw.Add(v);
        }

        public override Value Result()
        {
            double pc = p.AsDouble();
            if (pc < 0 || pc > 1) throw new CypherRuntimeException("Percentile must be between 0.0 and 1.0");
            if (_vals.Count == 0) return Value.Null;
            var idx = Enumerable.Range(0, _vals.Count).OrderBy(i => _vals[i]).ToArray();
            if (cont)
            {
                double pos = pc * (idx.Length - 1);
                int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
                double frac = pos - lo;
                return Value.FromFloat(_vals[idx[lo]] + (_vals[idx[hi]] - _vals[idx[lo]]) * frac);
            }
            int k = pc == 0 ? 0 : (int)Math.Ceiling(pc * idx.Length) - 1;
            return _raw[idx[Math.Clamp(k, 0, idx.Length - 1)]];
        }

        public override void Merge(Aggregator other)
        {
            var o = (PercentileAgg)other;
            _vals.AddRange(o._vals);
            _raw.AddRange(o._raw);
        }
    }

    private sealed class DistinctAgg(Aggregator inner) : Aggregator
    {
        private readonly HashSet<Value> _seen = new(ValueEquivalenceComparer.Instance);

        public override void Add(in Value v)
        {
            if (v.IsNull || !_seen.Add(v)) return;
            inner.Add(v);
        }

        public override Value Result() => inner.Result();

        // the other side's inner state may count values we already have: replay its distinct values instead.
        // A HashSet without removals enumerates in insertion order, so collect(DISTINCT) keeps its order.
        public override void Merge(Aggregator other)
        {
            foreach (var v in ((DistinctAgg)other)._seen) Add(v);
        }
    }
}

/// <summary>Hash aggregation. Each group keeps a snapshot of its first row so that grouping
/// variables remain accessible to later expressions.</summary>
internal sealed class AggregateOp(Operator input, int[] keySlots, Eval[] keyEvals, AggSpec[] aggs) : UnaryOperator(input)
{
    private readonly int[] keySlots = keySlots;
    private readonly Eval[] keyEvals = keyEvals;
    private readonly AggSpec[] aggs = aggs;
    public override string Name => keySlots.Length == 0 ? "Aggregation" : "EagerAggregation";
    public override string Details => string.Join(", ", aggs.Select(a => a.Text));
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class Group(Value[] snapshot, Aggregator[] aggs)
    {
        public readonly Value[] Snapshot = snapshot;
        public readonly Aggregator[] Aggs = aggs;
    }

    /// <summary>
    /// Scan at the bottom of the input when the input is a chain of row-local operators: its id space can then
    /// be split into ranges aggregated in parallel and merged.
    /// </summary>
    private readonly Operator? _parallelLeaf = FindParallelLeaf(input);

    /// <summary>Minimum estimated input rows per worker for a parallel aggregation.</summary>
    internal const long ParallelRowsPerWorker = 16_384;

    private static Operator? FindParallelLeaf(Operator op)
    {
        while (op.RowLocal && op is UnaryOperator u)
        {
            if (op is IPartitionedScan && u.Input is StartOp) return op;
            op = u.Input;
        }
        return null;
    }

    /// <summary>Groups computed over (a part of) the input, in order of first appearance.</summary>
    private sealed class Partial
    {
        public readonly List<(Value[] Key, Group G)> Order = new();
        public Dictionary<Value[], Group>? Groups;
        public Group? Single;
    }

    private Aggregator[] NewAggs(ExecContext ctx)
    {
        var a = new Aggregator[aggs.Length];
        for (int i = 0; i < a.Length; i++) a[i] = aggs[i].Create(ctx);
        return a;
    }

    private void Feed(Group g, ExecContext ctx)
    {
        for (int i = 0; i < aggs.Length; i++)
        {
            var spec = aggs[i];
            g.Aggs[i].Add(spec.Kind == AggKind.CountStar ? Value.True : spec.Arg!(ctx));
        }
    }

    private Partial Aggregate(Cursor input, ExecContext ctx)
    {
        var part = new Partial();
        int nk = keyEvals.Length;
        if (nk == 0)
        {
            while (input.Next())
            {
                part.Single ??= new Group(RowOps.Snapshot(ctx.Row), NewAggs(ctx));
                Feed(part.Single, ctx);
            }
            return part;
        }
        var groups = part.Groups = new Dictionary<Value[], Group>(ValueEquivalenceComparer.Instance);
        var probe = new Value[nk];
        while (input.Next())
        {
            for (int i = 0; i < nk; i++) probe[i] = keyEvals[i](ctx);
            if (!groups.TryGetValue(probe, out var g))
            {
                var key = (Value[])probe.Clone();
                g = new Group(RowOps.Snapshot(ctx.Row), NewAggs(ctx));
                groups[key] = g;
                part.Order.Add((key, g));
            }
            Feed(g, ctx);
        }
        return part;
    }

    /// <summary>Number of workers worth using for this execution (1 = sequential).</summary>
    private int Workers(ExecContext ctx)
    {
        if (_parallelLeaf is null || ctx.Store.IsWriter) return 1;
        int max = ctx.Options.MaxDegreeOfParallelism;
        if (max <= 1) return 1;
        long rows = ((IPartitionedScan)_parallelLeaf).EstimateRows(ctx.Store);
        return (int)Math.Min(max, rows / ParallelRowsPerWorker);
    }

    /// <summary>
    /// Aggregates contiguous node id ranges in parallel and merges them in range order, so groups, first-row
    /// snapshots and collect() order are the same as in a sequential run.
    /// </summary>
    private Partial AggregateParallel(ExecContext ctx, int workers)
    {
        long high = ctx.Store.NodeHigh;
        long size = Math.Max(4096, ((high + workers * 8 - 1) / (workers * 8) + 63) & ~63L);
        int chunks = (int)((high + size - 1) / size);
        var parts = new Partial[chunks];
        var leaf = _parallelLeaf!;
        try
        {
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
            {
                var w = ctx.ForPartition(leaf, i * size, (i + 1) * size);
                parts[i] = Aggregate(Input.Open(w), w);
            });
        }
        catch (AggregateException ae)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }
        var result = new Partial { Groups = keyEvals.Length == 0 ? null : new Dictionary<Value[], Group>(ValueEquivalenceComparer.Instance) };
        foreach (var p in parts)
        {
            if (p.Single != null)
            {
                if (result.Single is null) result.Single = p.Single;
                else MergeInto(result.Single, p.Single);
            }
            foreach (var (key, g) in p.Order)
            {
                if (result.Groups!.TryGetValue(key, out var existing)) MergeInto(existing, g);
                else
                {
                    result.Groups[key] = g;
                    result.Order.Add((key, g));
                }
            }
        }
        return result;
    }

    private static void MergeInto(Group target, Group source)
    {
        for (int i = 0; i < target.Aggs.Length; i++) target.Aggs[i].Merge(source.Aggs[i]);
    }

    private sealed class C(AggregateOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private List<(Value[] Key, Group G)>? _out;
        private int _pos;

        private void Run()
        {
            int workers = op.Workers(ctx);
            var part = workers > 1 ? op.AggregateParallel(ctx, workers) : op.Aggregate(input, ctx);
            _out = part.Order;
            if (op.keyEvals.Length == 0) _out.Add(([], part.Single ?? new Group(null!, op.NewAggs(ctx))));
        }

        public override bool Next()
        {
            if (_out is null) Run();
            if (_pos >= _out!.Count) return false;
            var (key, g) = _out[_pos];
            _out[_pos++] = default;
            var row = ctx.Row;
            if (g.Snapshot != null) RowOps.Restore(g.Snapshot, row);
            for (int i = 0; i < key.Length; i++) row[op.keySlots[i]] = key[i];
            for (int i = 0; i < op.aggs.Length; i++) row[op.aggs[i].Slot] = g.Aggs[i].Result();
            return true;
        }
    }
}
