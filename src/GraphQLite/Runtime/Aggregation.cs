using System.Runtime.CompilerServices;

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

/// <summary>
/// Hash aggregation. When later expressions can read the variables of the input rows, each group keeps a snapshot
/// of its first row.
/// </summary>
internal sealed class AggregateOp(Operator input, int[] keySlots, Eval[] keyEvals, AggSpec[] aggs, bool snapshot, int[]? keyDeps)
    : UnaryOperator(input)
{
    private readonly int[] keySlots = keySlots;
    private readonly Eval[] keyEvals = keyEvals;
    private readonly AggSpec[] aggs = aggs;
    private readonly bool snapshot = snapshot;
    public override string Name => keySlots.Length == 0 ? "Aggregation" : "EagerAggregation";
    public override string Details => string.Join(", ", aggs.Select(a => a.Text));
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    /// <summary>
    /// A group, kept small because there can be millions: one grouping key is stored inline, a single count() needs
    /// no aggregator and a single aggregate no array.
    /// </summary>
    private sealed class Group
    {
        /// <summary>The grouping key, when there is only one.</summary>
        public Value Key;
        /// <summary>The grouping keys, when there are several.</summary>
        public Value[]? Keys;
        public Value[]? Snapshot;
        /// <summary>Result of the only aggregate when it is a plain count() (<see cref="_countOnly"/>).</summary>
        public long Count;
        /// <summary>The aggregate, when there is only one.</summary>
        public Aggregator? Agg;
        /// <summary>The aggregates, when there are several.</summary>
        public Aggregator[]? Aggs;
    }

    /// <summary>Groups compare by their keys. Hash sets hold 16 bytes per group (a dictionary 24): smaller arrays stay off the large object heap.</summary>
    private sealed class KeyComparer : IEqualityComparer<Group>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(Group? x, Group? y) => x!.Keys is null
            ? ValueOps.Equivalent(x.Key, y!.Key)
            : ValueEquivalenceComparer.Instance.Equals(x.Keys, y!.Keys);

        public int GetHashCode(Group g) => g.Keys is null ? ValueOps.Hash(g.Key) : ValueEquivalenceComparer.Instance.GetHashCode(g.Keys);
    }

    /// <summary>The only aggregate is count(x) or count(*) without DISTINCT: groups just count.</summary>
    private readonly bool _countOnly = aggs is [{ Kind: AggKind.Count or AggKind.CountStar, Distinct: false }];

    /// <summary>
    /// Scan at the bottom of the input when the input is a chain of row-local operators: its id space can then
    /// be split into ranges aggregated in parallel and merged.
    /// </summary>
    private readonly Operator? _parallelLeaf = Partitioning.FindLeaf(input);

    /// <summary>
    /// Slots read by the grouping keys, when the keys are deterministic functions of them and the input performs no
    /// writes: consecutive rows with the same values there (an expansion emits the rows of one node together)
    /// belong to the same group, found without evaluating the keys or hashing them.
    /// </summary>
    private readonly int[]? _keyDeps = keyDeps is not null && !Writes(input) ? keyDeps : null;

    private static bool Writes(Operator op) =>
        op is CreateOp or MergeOp or SetOp or DeleteOp or ForeachOp or ProcedureCallOp || op.Children.Any(Writes);

    /// <summary>
    /// Groups computed over (a part of) the input. A hash set without removals enumerates in insertion order,
    /// so <see cref="Groups"/> lists the groups in order of first appearance.
    /// </summary>
    private sealed class Partial
    {
        public HashSet<Group>? Groups;
        public Group? Single;
    }

    /// <summary>A group with the key of <paramref name="probe"/>.</summary>
    private Group NewGroup(Group probe, ExecContext ctx)
    {
        var g = new Group
        {
            Key = probe.Key,
            Keys = probe.Keys is null ? null : (Value[])probe.Keys.Clone(),
            Snapshot = snapshot ? RowOps.Snapshot(ctx.Row) : null,
        };
        if (_countOnly) return g;
        if (aggs.Length == 1) g.Agg = aggs[0].Create(ctx);
        else
        {
            var a = g.Aggs = new Aggregator[aggs.Length];
            for (int i = 0; i < a.Length; i++) a[i] = aggs[i].Create(ctx);
        }
        return g;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value ArgOf(AggSpec spec, ExecContext ctx) => spec.Kind == AggKind.CountStar ? Value.True : spec.Arg!(ctx);

    private void Feed(Group g, ExecContext ctx)
    {
        if (_countOnly)
        {
            var spec = aggs[0];
            if (spec.Kind == AggKind.CountStar || !spec.Arg!(ctx).IsNull) g.Count++;
            return;
        }
        if (g.Agg is { } single)
        {
            single.Add(ArgOf(aggs[0], ctx));
            return;
        }
        var a = g.Aggs!;
        for (int i = 0; i < a.Length; i++) a[i].Add(ArgOf(aggs[i], ctx));
    }

    private static void MergeInto(Group target, Group source)
    {
        target.Count += source.Count;
        if (target.Agg is { } single) single.Merge(source.Agg!);
        else if (target.Aggs is { } a)
        {
            for (int i = 0; i < a.Length; i++) a[i].Merge(source.Aggs![i]);
        }
    }

    /// <param name="capacity">Expected number of groups.</param>
    private Partial Aggregate(Cursor input, ExecContext ctx, int capacity = 0)
    {
        var part = new Partial();
        int nk = keyEvals.Length;
        var probe = new Group { Keys = nk > 1 ? new Value[nk] : null };
        if (nk == 0)
        {
            while (input.Next())
            {
                part.Single ??= NewGroup(probe, ctx);
                Feed(part.Single, ctx);
            }
            return part;
        }
        var groups = part.Groups = new HashSet<Group>(capacity, KeyComparer.Instance);
        var deps = _keyDeps;
        var last = deps is null ? null : new Value[deps.Length];
        Group? lastGroup = null;
        while (input.Next())
        {
            var row = ctx.Row;
            Group? g = lastGroup;
            if (g is null || !SameDeps(row, deps!, last!))
            {
                if (nk == 1) probe.Key = keyEvals[0](ctx);
                else
                {
                    for (int i = 0; i < nk; i++) probe.Keys![i] = keyEvals[i](ctx);
                }
                if (!groups.TryGetValue(probe, out g)) groups.Add(g = NewGroup(probe, ctx));
                if (deps != null)
                {
                    for (int i = 0; i < deps.Length; i++) last![i] = row[deps[i]];
                    lastGroup = g;
                }
            }
            Feed(g, ctx);
        }
        return part;
    }

    /// <summary>Identity (not equivalence) of the slot values: a false negative only costs a regular lookup.</summary>
    private static bool SameDeps(Value[] row, int[] deps, Value[] last)
    {
        for (int i = 0; i < deps.Length; i++)
        {
            ref readonly var a = ref row[deps[i]];
            ref readonly var b = ref last[i];
            if (a.Kind != b.Kind || a.Num != b.Num) return false;
            if (!ReferenceEquals(a.Obj, b.Obj) && a.Kind is not (ValueKind.Node or ValueKind.Relationship)) return false;
        }
        return true;
    }

    /// <summary>
    /// Aggregates contiguous node id ranges in parallel and merges them in range order, so groups, first-row
    /// snapshots and collect() order are the same as in a sequential run.
    /// </summary>
    private Partial AggregateParallel(ExecContext ctx, int workers)
    {
        var (size, chunks) = Partitioning.Ranges(ctx, workers);
        var parts = new Partial[chunks];
        var leaf = _parallelLeaf!;
        int hint = 0; // groups of the last chunk aggregated: ranges of the same size tend to have as many
        Partitioning.For(chunks, workers, i =>
        {
            var w = ctx.ForPartition(leaf, i * size, (i + 1) * size);
            var p = parts[i] = Aggregate(Input.Open(w), w, Volatile.Read(ref hint));
            if (p.Groups != null) Volatile.Write(ref hint, p.Groups.Count);
        });
        var result = new Partial();
        if (keyEvals.Length == 0)
        {
            foreach (var p in parts)
            {
                if (p.Single is null) continue;
                if (result.Single is null) result.Single = p.Single;
                else MergeInto(result.Single, p.Single);
            }
            return result;
        }
        // the sum of the partial counts bounds the result (no resizing), and the input rows bound the sum
        int total = 0;
        foreach (var p in parts) total += p.Groups!.Count;
        var groups = result.Groups = new HashSet<Group>(total, KeyComparer.Instance);
        foreach (var p in parts)
        {
            foreach (var g in p.Groups!)
            {
                if (!groups.Add(g) && groups.TryGetValue(g, out var existing)) MergeInto(existing, g);
            }
        }
        return result;
    }

    private sealed class C(AggregateOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private IEnumerator<Group>? _out;

        private void Run()
        {
            int workers = Partitioning.Workers(ctx, op._parallelLeaf);
            var part = workers > 1 ? op.AggregateParallel(ctx, workers) : op.Aggregate(input, ctx);
            _out = op.keyEvals.Length == 0
                ? Enumerable.Repeat(part.Single ?? op.NewGroup(new Group(), ctx), 1).GetEnumerator()
                : part.Groups!.GetEnumerator();
        }

        public override bool Next()
        {
            if (_out is null) Run();
            if (!_out!.MoveNext()) return false;
            var g = _out.Current;
            var row = ctx.Row;
            if (g.Snapshot != null) RowOps.Restore(g.Snapshot, row);
            if (g.Keys is { } keys)
            {
                for (int i = 0; i < keys.Length; i++) row[op.keySlots[i]] = keys[i];
            }
            else if (op.keySlots.Length == 1) row[op.keySlots[0]] = g.Key;
            if (op._countOnly) row[op.aggs[0].Slot] = Value.FromInt(g.Count);
            else if (g.Agg is { } single) row[op.aggs[0].Slot] = single.Result();
            else
            {
                for (int i = 0; i < op.aggs.Length; i++) row[op.aggs[i].Slot] = g.Aggs![i].Result();
            }
            return true;
        }
    }
}
