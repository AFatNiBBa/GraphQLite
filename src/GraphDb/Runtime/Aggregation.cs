namespace GraphDb.Runtime;

internal abstract class Aggregator
{
    public abstract void Add(in Value v);
    public abstract Value Result();
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
    }

    private sealed class CollectAgg : Aggregator
    {
        private readonly List<Value> _items = new();
        public override void Add(in Value v) { if (!v.IsNull) _items.Add(v); }
        public override Value Result() => Value.FromList(_items.ToArray());
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

    private sealed class C(AggregateOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private List<(Value[] Key, Group G)>? _out;
        private int _pos;

        private Aggregator[] NewAggs()
        {
            var a = new Aggregator[op.aggs.Length];
            for (int i = 0; i < a.Length; i++) a[i] = op.aggs[i].Create(ctx);
            return a;
        }

        private void Feed(Group g)
        {
            var aggs = op.aggs;
            for (int i = 0; i < aggs.Length; i++)
            {
                var spec = aggs[i];
                g.Aggs[i].Add(spec.Kind == AggKind.CountStar ? Value.True : spec.Arg!(ctx));
            }
        }

        private void Run()
        {
            _out = new List<(Value[], Group)>();
            int nk = op.keyEvals.Length;
            if (nk == 0)
            {
                Group? single = null;
                while (input.Next())
                {
                    single ??= new Group(RowOps.Snapshot(ctx.Row), NewAggs());
                    Feed(single);
                }
                single ??= new Group(null!, NewAggs());
                _out.Add(([], single));
                return;
            }
            var groups = new Dictionary<Value[], Group>(ValueEquivalenceComparer.Instance);
            var probe = new Value[nk];
            while (input.Next())
            {
                for (int i = 0; i < nk; i++) probe[i] = op.keyEvals[i](ctx);
                if (!groups.TryGetValue(probe, out var g))
                {
                    var key = (Value[])probe.Clone();
                    g = new Group(RowOps.Snapshot(ctx.Row), NewAggs());
                    groups[key] = g;
                    _out.Add((key, g));
                }
                Feed(g);
            }
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
