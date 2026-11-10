using System.Runtime.CompilerServices;

namespace GraphDb;

/// <summary>Cypher comparison / equality / equivalence semantics.</summary>
internal static class ValueOps
{
    // ----- equivalence (DISTINCT, grouping, index keys): null ~ null, 1 ~ 1.0, NaN ~ NaN -----
    public static bool Equivalent(in Value a, in Value b)
    {
        if (a.Kind == b.Kind)
        {
            switch (a.Kind)
            {
                case ValueKind.Null: return true;
                case ValueKind.Boolean:
                case ValueKind.Integer:
                case ValueKind.Node:
                case ValueKind.Relationship:
                    return a.Num == b.Num;
                case ValueKind.Float:
                    {
                        double x = a.FloatBits, y = b.FloatBits;
                        return x == y || (double.IsNaN(x) && double.IsNaN(y));
                    }
                case ValueKind.String: return string.Equals(a.Str, b.Str, StringComparison.Ordinal);
                case ValueKind.List:
                    {
                        var x = a.ListItems; var y = b.ListItems;
                        if (x.Length != y.Length) return false;
                        for (int i = 0; i < x.Length; i++) if (!Equivalent(x[i], y[i])) return false;
                        return true;
                    }
                case ValueKind.Map:
                    {
                        var x = a.MapItems; var y = b.MapItems;
                        if (x.Count != y.Count) return false;
                        foreach (var kv in x)
                        {
                            if (!y.TryGetValue(kv.Key, out var o) || !Equivalent(kv.Value, o)) return false;
                        }
                        return true;
                    }
                case ValueKind.Path: return ((GraphPath)a.Obj!).SameAs((GraphPath)b.Obj!);
                case ValueKind.Date:
                case ValueKind.LocalTime:
                case ValueKind.LocalDateTime:
                case ValueKind.DateTime:
                    return a.Num == b.Num;
                case ValueKind.Time: return Temporal.TimeUtcTicks(a) == Temporal.TimeUtcTicks(b);
                case ValueKind.Duration:
                case ValueKind.Point:
                    return a.Obj!.Equals(b.Obj);
            }
            return false;
        }
        if (a.IsNumber && b.IsNumber) return a.AsDouble() == b.AsDouble() && NumEqExact(a, b);
        return false;
    }

    private static bool NumEqExact(in Value a, in Value b)
    {
        // int vs float: compare exactly
        if (a.Kind == ValueKind.Integer) return (double)a.Num == b.FloatBits && (long)b.FloatBits == a.Num;
        return (double)b.Num == a.FloatBits && (long)a.FloatBits == b.Num;
    }

    public static int Hash(in Value v)
    {
        switch (v.Kind)
        {
            case ValueKind.Null: return 0;
            case ValueKind.Boolean: return v.Num != 0 ? 1231 : 1237;
            case ValueKind.Integer: return v.Num.GetHashCode();
            case ValueKind.Float:
                {
                    double d = v.FloatBits;
                    if (d >= long.MinValue && d <= long.MaxValue && Math.Floor(d) == d) return ((long)d).GetHashCode();
                    return double.IsNaN(d) ? 0x7ff8 : d.GetHashCode();
                }
            case ValueKind.String: return v.Str.GetHashCode();
            case ValueKind.List:
                {
                    var h = new HashCode();
                    foreach (var x in v.ListItems) h.Add(Hash(x));
                    return h.ToHashCode();
                }
            case ValueKind.Map:
                {
                    int h = 17;
                    foreach (var kv in v.MapItems) h ^= HashCode.Combine(kv.Key, Hash(kv.Value));
                    return h;
                }
            case ValueKind.Node: return HashCode.Combine(1, v.Num);
            case ValueKind.Relationship: return HashCode.Combine(2, v.Num);
            case ValueKind.Date:
            case ValueKind.LocalTime:
            case ValueKind.LocalDateTime:
            case ValueKind.DateTime:
                return HashCode.Combine((int)v.Kind, v.Num);
            case ValueKind.Time: return HashCode.Combine(9, Temporal.TimeUtcTicks(v));
            case ValueKind.Duration:
            case ValueKind.Point:
                return v.Obj!.GetHashCode();
            case ValueKind.Path:
                {
                    var p = (GraphPath)v.Obj!;
                    var h = new HashCode();
                    foreach (var n in p.NodeIds) h.Add(n);
                    foreach (var r in p.RelIds) h.Add(r);
                    return h.ToHashCode();
                }
        }
        return 0;
    }

    // ----- ternary equality (=) -----
    /// <summary>Returns true/false or null (unknown).</summary>
    public static Value Equal(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Null || b.Kind == ValueKind.Null) return Value.Null;
        if (a.Kind == b.Kind)
        {
            switch (a.Kind)
            {
                case ValueKind.Integer:
                case ValueKind.Boolean:
                case ValueKind.Node:
                case ValueKind.Relationship:
                    return Value.FromBool(a.Num == b.Num);
                case ValueKind.Float: return Value.FromBool(a.FloatBits == b.FloatBits);
                case ValueKind.String: return Value.FromBool(string.Equals(a.Str, b.Str, StringComparison.Ordinal));
                case ValueKind.List:
                    {
                        var x = a.ListItems; var y = b.ListItems;
                        if (x.Length != y.Length) return Value.False;
                        bool unknown = false;
                        for (int i = 0; i < x.Length; i++)
                        {
                            var r = Equal(x[i], y[i]);
                            if (r.IsNull) unknown = true;
                            else if (r.Num == 0) return Value.False;
                        }
                        return unknown ? Value.Null : Value.True;
                    }
                case ValueKind.Map:
                    {
                        var x = a.MapItems; var y = b.MapItems;
                        if (x.Count != y.Count) return Value.False;
                        bool unknown = false;
                        foreach (var kv in x)
                        {
                            if (!y.TryGetValue(kv.Key, out var o)) return Value.False;
                            var r = Equal(kv.Value, o);
                            if (r.IsNull) unknown = true;
                            else if (r.Num == 0) return Value.False;
                        }
                        return unknown ? Value.Null : Value.True;
                    }
                case ValueKind.Path: return Value.FromBool(((GraphPath)a.Obj!).SameAs((GraphPath)b.Obj!));
                default: return Value.FromBool(Equivalent(a, b));
            }
        }
        if (a.IsNumber && b.IsNumber) return Value.FromBool(a.AsDouble() == b.AsDouble());
        return Value.False;
    }

    // ----- ternary ordering comparison (<, >, <=, >=) -----
    /// <summary>Returns comparison result or null when values are not comparable.</summary>
    public static int? Compare(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Null || b.Kind == ValueKind.Null) return null;
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return a.Num.CompareTo(b.Num);
        if (a.IsNumber && b.IsNumber)
        {
            double x = a.AsDouble(), y = b.AsDouble();
            if (double.IsNaN(x) || double.IsNaN(y)) return null;
            return x.CompareTo(y);
        }
        if (a.Kind != b.Kind) return null;
        switch (a.Kind)
        {
            case ValueKind.String: return string.CompareOrdinal(a.Str, b.Str);
            case ValueKind.Boolean: return a.Num.CompareTo(b.Num);
            case ValueKind.Date:
            case ValueKind.LocalTime:
            case ValueKind.LocalDateTime:
            case ValueKind.DateTime:
                return a.Num.CompareTo(b.Num);
            case ValueKind.Time: return Temporal.TimeUtcTicks(a).CompareTo(Temporal.TimeUtcTicks(b));
            case ValueKind.List:
                {
                    var x = a.ListItems; var y = b.ListItems;
                    int n = Math.Min(x.Length, y.Length);
                    for (int i = 0; i < n; i++)
                    {
                        var c = Compare(x[i], y[i]);
                        if (c is null) return null;
                        if (c != 0) return c;
                    }
                    return x.Length.CompareTo(y.Length);
                }
        }
        return null;
    }

    // ----- total ordering for ORDER BY -----
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OrderRank(ValueKind k) => k switch
    {
        ValueKind.Map => 0,
        ValueKind.Node => 1,
        ValueKind.Relationship => 2,
        ValueKind.List => 3,
        ValueKind.Path => 4,
        ValueKind.Point => 5,
        ValueKind.DateTime => 6,
        ValueKind.LocalDateTime => 7,
        ValueKind.Date => 8,
        ValueKind.Time => 9,
        ValueKind.LocalTime => 10,
        ValueKind.Duration => 11,
        ValueKind.String => 12,
        ValueKind.Boolean => 13,
        ValueKind.Integer or ValueKind.Float => 14,
        _ => 15, // null last
    };

    public static int OrderCompare(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return a.Num.CompareTo(b.Num);
        int ra = OrderRank(a.Kind), rb = OrderRank(b.Kind);
        if (ra != rb) return ra.CompareTo(rb);
        switch (a.Kind)
        {
            case ValueKind.Null: return 0;
            case ValueKind.Integer:
            case ValueKind.Float:
                {
                    if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Float || a.Kind == ValueKind.Float && b.Kind == ValueKind.Integer || a.Kind == ValueKind.Float)
                    {
                        double x = a.AsDouble(), y = b.AsDouble();
                        bool nx = double.IsNaN(x), ny = double.IsNaN(y);
                        if (nx || ny) return nx.CompareTo(ny);
                        int c = x.CompareTo(y);
                        if (c != 0) return c;
                        return a.Kind == b.Kind ? 0 : (a.Kind == ValueKind.Integer ? -1 : 1);
                    }
                    return a.Num.CompareTo(b.Num);
                }
            case ValueKind.String: return string.CompareOrdinal(a.Str, b.Str);
            case ValueKind.Boolean: return a.Num.CompareTo(b.Num);
            case ValueKind.Node:
            case ValueKind.Relationship:
            case ValueKind.Date:
            case ValueKind.LocalTime:
            case ValueKind.LocalDateTime:
            case ValueKind.DateTime:
                return a.Num.CompareTo(b.Num);
            case ValueKind.Time: return Temporal.TimeUtcTicks(a).CompareTo(Temporal.TimeUtcTicks(b));
            case ValueKind.Duration:
                {
                    var x = a.AsDuration(); var y = b.AsDuration();
                    int c = x.ApproxSeconds.CompareTo(y.ApproxSeconds);
                    return c != 0 ? c : (x.Months, x.Days, x.Seconds, x.Nanoseconds).CompareTo((y.Months, y.Days, y.Seconds, y.Nanoseconds));
                }
            case ValueKind.Point:
                {
                    var x = a.AsPoint(); var y = b.AsPoint();
                    return (x.Srid, x.X, x.Y, x.Z ?? 0).CompareTo((y.Srid, y.X, y.Y, y.Z ?? 0));
                }
            case ValueKind.List:
                {
                    var x = a.ListItems; var y = b.ListItems;
                    int n = Math.Min(x.Length, y.Length);
                    for (int i = 0; i < n; i++)
                    {
                        int c = OrderCompare(x[i], y[i]);
                        if (c != 0) return c;
                    }
                    return x.Length.CompareTo(y.Length);
                }
            case ValueKind.Map:
                {
                    var x = a.MapItems; var y = b.MapItems;
                    var kx = x.Keys.Order(StringComparer.Ordinal).ToArray();
                    var ky = y.Keys.Order(StringComparer.Ordinal).ToArray();
                    int n = Math.Min(kx.Length, ky.Length);
                    for (int i = 0; i < n; i++)
                    {
                        int c = string.CompareOrdinal(kx[i], ky[i]);
                        if (c != 0) return c;
                        c = OrderCompare(x[kx[i]], y[ky[i]]);
                        if (c != 0) return c;
                    }
                    return kx.Length.CompareTo(ky.Length);
                }
            case ValueKind.Path:
                {
                    var x = (GraphPath)a.Obj!; var y = (GraphPath)b.Obj!;
                    int c = x.NodeIds.AsSpan().SequenceCompareTo(y.NodeIds);
                    return c != 0 ? c : x.RelIds.AsSpan().SequenceCompareTo(y.RelIds);
                }
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTrue(in Value v) => v.Kind == ValueKind.Boolean && v.Num != 0;
}

/// <summary>Equality comparer using Cypher equivalence semantics.</summary>
internal sealed class ValueEquivalenceComparer : IEqualityComparer<Value>, IEqualityComparer<Value[]>
{
    public static readonly ValueEquivalenceComparer Instance = new();
    public bool Equals(Value x, Value y) => ValueOps.Equivalent(x, y);
    public int GetHashCode(Value obj) => ValueOps.Hash(obj);

    public bool Equals(Value[]? x, Value[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++) if (!ValueOps.Equivalent(x[i], y[i])) return false;
        return true;
    }

    public int GetHashCode(Value[] obj)
    {
        int h = obj.Length;
        foreach (var v in obj) h = h * 31 + ValueOps.Hash(v);
        return h;
    }
}
