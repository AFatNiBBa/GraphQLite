using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace GraphDb.Runtime;

/// <summary>Runtime semantics of Cypher operators (null propagation, numeric promotion, ...).</summary>
internal static class ExprOps
{
    private static CypherRuntimeException TypeErr(string op, in Value a, in Value b) =>
        new($"Cannot apply '{op}' to {a.Kind} and {b.Kind}");

    private static CypherRuntimeException Overflow() => new("Integer overflow");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Add(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer)
        {
            long r = a.Num + b.Num;
            if (((a.Num ^ r) & (b.Num ^ r)) < 0) throw Overflow();
            return Value.FromInt(r);
        }
        return AddSlow(a, b);
    }

    private static Value AddSlow(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.List || b.Kind == ValueKind.List)
        {
            if (a.Kind == ValueKind.List && b.Kind == ValueKind.List)
            {
                var x = a.ListItems; var y = b.ListItems;
                var r = new Value[x.Length + y.Length];
                x.CopyTo(r, 0);
                y.CopyTo(r, x.Length);
                return Value.FromList(r);
            }
            if (a.Kind == ValueKind.List)
            {
                var x = a.ListItems;
                var r = new Value[x.Length + 1];
                x.CopyTo(r, 0);
                r[^1] = b;
                return Value.FromList(r);
            }
            else
            {
                var y = b.ListItems;
                var r = new Value[y.Length + 1];
                r[0] = a;
                y.CopyTo(r, 1);
                return Value.FromList(r);
            }
        }
        if (a.IsNull || b.IsNull) return Value.Null;
        if (a.IsNumber && b.IsNumber) return Value.FromFloat(a.AsDouble() + b.AsDouble());
        if (a.Kind == ValueKind.String && b.Kind == ValueKind.String) return Value.FromString(string.Concat(a.Str, b.Str));
        if (a.Kind == ValueKind.String && b.IsNumber) return Value.FromString(a.Str + b.ToDisplayString());
        if (b.Kind == ValueKind.String && a.IsNumber) return Value.FromString(a.ToDisplayString() + b.Str);
        throw TypeErr("+", a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Sub(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer)
        {
            long r = a.Num - b.Num;
            if (((a.Num ^ b.Num) & (a.Num ^ r)) < 0) throw Overflow();
            return Value.FromInt(r);
        }
        if (a.IsNull || b.IsNull) return Value.Null;
        if (a.IsNumber && b.IsNumber) return Value.FromFloat(a.AsDouble() - b.AsDouble());
        throw TypeErr("-", a, b);
    }

    public static Value Mul(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer)
        {
            long r;
            try { r = checked(a.Num * b.Num); }
            catch (OverflowException) { throw Overflow(); }
            return Value.FromInt(r);
        }
        if (a.IsNull || b.IsNull) return Value.Null;
        if (a.IsNumber && b.IsNumber) return Value.FromFloat(a.AsDouble() * b.AsDouble());
        throw TypeErr("*", a, b);
    }

    public static Value Div(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer)
        {
            if (b.Num == 0) throw new CypherRuntimeException("/ by zero");
            if (a.Num == long.MinValue && b.Num == -1) throw Overflow();
            return Value.FromInt(a.Num / b.Num);
        }
        if (a.IsNull || b.IsNull) return Value.Null;
        if (a.IsNumber && b.IsNumber) return Value.FromFloat(a.AsDouble() / b.AsDouble());
        throw TypeErr("/", a, b);
    }

    public static Value Mod(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer)
        {
            if (b.Num == 0) throw new CypherRuntimeException("/ by zero");
            if (b.Num == -1) return Value.FromInt(0);
            return Value.FromInt(a.Num % b.Num);
        }
        if (a.IsNull || b.IsNull) return Value.Null;
        if (a.IsNumber && b.IsNumber) return Value.FromFloat(a.AsDouble() % b.AsDouble());
        throw TypeErr("%", a, b);
    }

    public static Value Pow(in Value a, in Value b)
    {
        if (a.IsNull || b.IsNull) return Value.Null;
        if (a.IsNumber && b.IsNumber) return Value.FromFloat(Math.Pow(a.AsDouble(), b.AsDouble()));
        throw TypeErr("^", a, b);
    }

    public static Value Neg(in Value a) => a.Kind switch
    {
        ValueKind.Integer => a.Num == long.MinValue ? throw Overflow() : Value.FromInt(-a.Num),
        ValueKind.Float => Value.FromFloat(-a.FloatBits),
        ValueKind.Null => Value.Null,
        _ => throw new CypherRuntimeException($"Cannot negate {a.Kind}"),
    };

    // ---------- boolean (three-valued) ----------
    private static int Tri(in Value v, string op)
    {
        if (v.Kind == ValueKind.Boolean) return v.Num != 0 ? 1 : 0;
        if (v.Kind == ValueKind.Null) return -1;
        throw new CypherRuntimeException($"{op} expects boolean operands, got {v.Kind}");
    }

    public static Value Not(in Value a)
    {
        int t = Tri(a, "NOT");
        return t < 0 ? Value.Null : Value.FromBool(t == 0);
    }

    public static Value Xor(in Value a, in Value b)
    {
        int x = Tri(a, "XOR"), y = Tri(b, "XOR");
        if (x < 0 || y < 0) return Value.Null;
        return Value.FromBool(x != y);
    }

    public static int TriAnd(in Value v) => Tri(v, "AND");
    public static int TriOr(in Value v) => Tri(v, "OR");

    // ---------- comparison ----------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Lt(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return Value.FromBool(a.Num < b.Num);
        var c = ValueOps.Compare(a, b);
        return c is null ? Value.Null : Value.FromBool(c < 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Gt(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return Value.FromBool(a.Num > b.Num);
        var c = ValueOps.Compare(a, b);
        return c is null ? Value.Null : Value.FromBool(c > 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Le(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return Value.FromBool(a.Num <= b.Num);
        var c = ValueOps.Compare(a, b);
        return c is null ? Value.Null : Value.FromBool(c <= 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Ge(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return Value.FromBool(a.Num >= b.Num);
        var c = ValueOps.Compare(a, b);
        return c is null ? Value.Null : Value.FromBool(c >= 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Eq(in Value a, in Value b)
    {
        if (a.Kind == b.Kind && a.Kind is ValueKind.Integer or ValueKind.Boolean or ValueKind.Node or ValueKind.Relationship)
            return Value.FromBool(a.Num == b.Num);
        return ValueOps.Equal(a, b);
    }

    public static Value Neq(in Value a, in Value b)
    {
        var r = Eq(a, b);
        return r.IsNull ? r : Value.FromBool(r.Num == 0);
    }

    public static Value In(in Value x, in Value list)
    {
        if (list.IsNull) return Value.Null;
        if (list.Kind != ValueKind.List) throw new CypherRuntimeException($"IN expects a list, got {list.Kind}");
        var items = list.ListItems;
        if (items.Length == 0) return Value.False;
        if (x.IsNull) return Value.Null;
        bool unknown = false;
        foreach (var it in items)
        {
            var r = Eq(x, it);
            if (r.IsNull) unknown = true;
            else if (r.Num != 0) return Value.True;
        }
        return unknown ? Value.Null : Value.False;
    }

    // ---------- strings ----------
    public static Value StartsWith(in Value a, in Value b) =>
        a.Kind == ValueKind.String && b.Kind == ValueKind.String ? Value.FromBool(a.Str.StartsWith(b.Str, StringComparison.Ordinal)) : Value.Null;

    public static Value EndsWith(in Value a, in Value b) =>
        a.Kind == ValueKind.String && b.Kind == ValueKind.String ? Value.FromBool(a.Str.EndsWith(b.Str, StringComparison.Ordinal)) : Value.Null;

    public static Value Contains(in Value a, in Value b) =>
        a.Kind == ValueKind.String && b.Kind == ValueKind.String ? Value.FromBool(a.Str.Contains(b.Str, StringComparison.Ordinal)) : Value.Null;

    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    public static Regex GetRegex(string pattern) =>
        RegexCache.GetOrAdd(pattern, p =>
        {
            if (RegexCache.Count > 1000) RegexCache.Clear();
            return new Regex("^(?:" + p + ")$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        });

    public static Value RegexMatch(in Value a, in Value b)
    {
        if (a.Kind != ValueKind.String || b.Kind != ValueKind.String) return Value.Null;
        return Value.FromBool(GetRegex(b.Str).IsMatch(a.Str));
    }

    // ---------- access ----------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Property(ExecContext ctx, in Value target, TokenRef key)
    {
        switch (target.Kind)
        {
            case ValueKind.Node:
                {
                    int k = key.Resolve(ctx.Store);
                    if (k < 0) return Value.Null;
                    var store = ctx.Store;
                    int id = target.EntityId;
                    if ((uint)id >= (uint)store.NodeHigh) return Value.Null;
                    var p = store.Nodes[id].Props;
                    return p is null ? Value.Null : p.Get(k);
                }
            case ValueKind.Relationship:
                {
                    int k = key.Resolve(ctx.Store);
                    if (k < 0) return Value.Null;
                    var store = ctx.Store;
                    int id = target.EntityId;
                    if ((uint)id >= (uint)store.RelHigh) return Value.Null;
                    var p = store.Rels[id].Props;
                    return p is null ? Value.Null : p.Get(k);
                }
            case ValueKind.Map:
                return target.MapItems.TryGetValue(key.Name, out var v) ? v : Value.Null;
            case ValueKind.Null:
                return Value.Null;
            default:
                throw new CypherRuntimeException($"Cannot access property '{key.Name}' on {target.Kind}");
        }
    }

    public static Value DynamicProperty(ExecContext ctx, in Value target, string key)
    {
        switch (target.Kind)
        {
            case ValueKind.Node: return ctx.Store.NodeProp(target.EntityId, ctx.Store.PropKeys.Lookup(key));
            case ValueKind.Relationship: return ctx.Store.RelProp(target.EntityId, ctx.Store.PropKeys.Lookup(key));
            case ValueKind.Map: return target.MapItems.TryGetValue(key, out var v) ? v : Value.Null;
            case ValueKind.Null: return Value.Null;
            default: throw new CypherRuntimeException($"Cannot access property '{key}' on {target.Kind}");
        }
    }

    public static Value Index(ExecContext ctx, in Value target, in Value idx)
    {
        if (target.IsNull || idx.IsNull) return Value.Null;
        if (target.Kind == ValueKind.List)
        {
            if (idx.Kind != ValueKind.Integer) throw new CypherRuntimeException("List index must be an integer");
            var items = target.ListItems;
            long i = idx.Num < 0 ? items.Length + idx.Num : idx.Num;
            return i >= 0 && i < items.Length ? items[i] : Value.Null;
        }
        if (idx.Kind == ValueKind.String) return DynamicProperty(ctx, target, idx.Str);
        throw new CypherRuntimeException($"Cannot index {target.Kind} with {idx.Kind}");
    }

    public static Value Slice(in Value target, in Value from, in Value to, bool hasFrom, bool hasTo)
    {
        if (target.IsNull) return Value.Null;
        if (target.Kind != ValueKind.List) throw new CypherRuntimeException("Slicing requires a list");
        if (hasFrom && from.IsNull || hasTo && to.IsNull) return Value.Null;
        var items = target.ListItems;
        long n = items.Length;
        long s = hasFrom ? from.AsInt64() : 0;
        long e = hasTo ? to.AsInt64() : n;
        if (s < 0) s += n;
        if (e < 0) e += n;
        s = Math.Clamp(s, 0, n);
        e = Math.Clamp(e, 0, n);
        if (e <= s) return Value.EmptyList;
        return Value.FromList(items.AsSpan((int)s, (int)(e - s)).ToArray());
    }

    public static Value HasLabels(ExecContext ctx, in Value target, TokenRef[] labels)
    {
        if (target.IsNull) return Value.Null;
        if (target.Kind != ValueKind.Node) throw new CypherRuntimeException($"Label check requires a node, got {target.Kind}");
        var store = ctx.Store;
        if (!store.NodeAlive(target.EntityId)) return Value.False;
        ref var n = ref store.Nodes[target.EntityId];
        foreach (var l in labels)
        {
            int id = l.Resolve(store);
            if (id < 0 || !n.HasLabel(id)) return Value.False;
        }
        return Value.True;
    }

    public static Value[] AsList(in Value v, string fn)
    {
        if (v.Kind == ValueKind.List) return v.ListItems;
        throw new CypherRuntimeException($"{fn} expects a list, got {v.Kind}");
    }
}
