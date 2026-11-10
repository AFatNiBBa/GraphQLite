using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace GraphDb;

/// <summary>Kind of a Cypher value.</summary>
public enum ValueKind : byte
{
    Null = 0,
    Boolean,
    Integer,
    Float,
    String,
    List,
    Map,
    Node,
    Relationship,
    Path,
}

/// <summary>
/// An immutable Cypher value. Compact (24 bytes), allocation-free for scalars.
/// Lists are backed by <c>Value[]</c>, maps by <c>Dictionary&lt;string, Value&gt;</c>.
/// Nodes/relationships carry their id and, when returned from a query, a property snapshot.
/// </summary>
public readonly struct Value : IEquatable<Value>
{
    internal readonly object? Obj;
    internal readonly long Num;
    public readonly ValueKind Kind;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Value(ValueKind kind, long num, object? obj)
    {
        Kind = kind;
        Num = num;
        Obj = obj;
    }

    public static readonly Value Null = default;
    public static readonly Value True = new(ValueKind.Boolean, 1, null);
    public static readonly Value False = new(ValueKind.Boolean, 0, null);
    internal static readonly Value EmptyList = new(ValueKind.List, 0, Array.Empty<Value>());

    // ---------- factories ----------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value FromBool(bool b) => b ? True : False;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value FromInt(long v) => new(ValueKind.Integer, v, null);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value FromFloat(double v) => new(ValueKind.Float, BitConverter.DoubleToInt64Bits(v), null);
    public static Value FromString(string? s) => s is null ? Null : new(ValueKind.String, 0, s);
    public static Value FromList(Value[] items) => new(ValueKind.List, 0, items);
    public static Value FromList(IEnumerable<Value> items) => new(ValueKind.List, 0, items.ToArray());
    public static Value FromMap(Dictionary<string, Value> map) => new(ValueKind.Map, 0, map);
    internal static Value FromNodeId(int id) => new(ValueKind.Node, id, null);
    internal static Value FromRelId(int id) => new(ValueKind.Relationship, id, null);
    internal static Value FromNode(Node n) => new(ValueKind.Node, n.Id, n);
    internal static Value FromRel(Relationship r) => new(ValueKind.Relationship, r.Id, r);
    internal static Value FromPath(GraphPath p) => new(ValueKind.Path, 0, p);

    public static implicit operator Value(long v) => FromInt(v);
    public static implicit operator Value(int v) => FromInt(v);
    public static implicit operator Value(double v) => FromFloat(v);
    public static implicit operator Value(bool v) => FromBool(v);
    public static implicit operator Value(string? v) => FromString(v);

    // ---------- accessors ----------
    public bool IsNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Kind == ValueKind.Null;
    }

    public bool IsNumber => Kind is ValueKind.Integer or ValueKind.Float;

    public bool AsBoolean() => Kind == ValueKind.Boolean ? Num != 0 : throw TypeError("Boolean");

    public long AsInt64() => Kind switch
    {
        ValueKind.Integer => Num,
        ValueKind.Float => (long)BitConverter.Int64BitsToDouble(Num),
        _ => throw TypeError("Integer"),
    };

    public int AsInt32() => checked((int)AsInt64());

    public double AsDouble() => Kind switch
    {
        ValueKind.Float => BitConverter.Int64BitsToDouble(Num),
        ValueKind.Integer => Num,
        _ => throw TypeError("Float"),
    };

    public string AsString() => Kind == ValueKind.String ? (string)Obj! : throw TypeError("String");

    public IReadOnlyList<Value> AsList() => Kind == ValueKind.List ? (Value[])Obj! : throw TypeError("List");

    public ReadOnlySpan<Value> AsListSpan() => Kind == ValueKind.List ? (Value[])Obj! : throw TypeError("List");

    public IReadOnlyDictionary<string, Value> AsMap() =>
        Kind == ValueKind.Map ? (Dictionary<string, Value>)Obj! : throw TypeError("Map");

    /// <summary>Returns the node snapshot (available for values returned by queries).</summary>
    public Node AsNode() => Kind == ValueKind.Node && Obj is Node n ? n : throw TypeError("Node");

    public Relationship AsRelationship() =>
        Kind == ValueKind.Relationship && Obj is Relationship r ? r : throw TypeError("Relationship");

    public GraphPath AsPath() => Kind == ValueKind.Path ? (GraphPath)Obj! : throw TypeError("GraphPath");

    internal Value[] ListItems
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Value[])Obj!;
    }

    internal Dictionary<string, Value> MapItems
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Dictionary<string, Value>)Obj!;
    }

    internal double FloatBits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitConverter.Int64BitsToDouble(Num);
    }

    internal string Str
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (string)Obj!;
    }

    internal int EntityId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)Num;
    }

    /// <summary>Converts to a plain CLR object (long, double, string, bool, List, Dictionary, Node...).</summary>
    public object? ToObject() => Kind switch
    {
        ValueKind.Null => null,
        ValueKind.Boolean => Num != 0,
        ValueKind.Integer => Num,
        ValueKind.Float => FloatBits,
        ValueKind.String => Obj,
        ValueKind.List => ListItems.Select(v => v.ToObject()).ToList(),
        ValueKind.Map => MapItems.ToDictionary(kv => kv.Key, kv => kv.Value.ToObject()),
        ValueKind.Node => Obj ?? (object)Num,
        ValueKind.Relationship => Obj ?? (object)Num,
        ValueKind.Path => Obj,
        _ => null,
    };

    /// <summary>Converts a CLR object into a Value (used for query parameters).</summary>
    public static Value FromObject(object? o)
    {
        // fast paths for the most common concrete types (exact type checks, struct enumerators)
        if (o is null) return Null;
        var t = o.GetType();
        if (t == typeof(string)) return new(ValueKind.String, 0, o);
        if (t == typeof(int)) return FromInt((int)o);
        if (t == typeof(long)) return FromInt((long)o);
        if (t == typeof(double)) return FromFloat((double)o);
        if (t == typeof(bool)) return FromBool((bool)o);
        if (t == typeof(Dictionary<string, object?>))
        {
            var src = (Dictionary<string, object?>)o;
            var d = new Dictionary<string, Value>(src.Count);
            foreach (var kv in src) d[kv.Key] = FromObject(kv.Value);
            return FromMap(d);
        }
        if (t == typeof(List<object?>))
        {
            var src = (List<object?>)o;
            var arr = new Value[src.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = FromObject(src[i]);
            return FromList(arr);
        }
        if (t == typeof(object[]))
        {
            var src = (object?[])o;
            var arr = new Value[src.Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = FromObject(src[i]);
            return FromList(arr);
        }
        return FromObjectSlow(o);
    }

    private static Value FromObjectSlow(object o)
    {
        switch (o)
        {
            case null: return Null;
            case Value v: return v;
            case bool b: return FromBool(b);
            case string s: return FromString(s);
            case int i: return FromInt(i);
            case long l: return FromInt(l);
            case short sh: return FromInt(sh);
            case byte by: return FromInt(by);
            case sbyte sb: return FromInt(sb);
            case ushort us: return FromInt(us);
            case uint ui: return FromInt(ui);
            case ulong ul: return FromInt(checked((long)ul));
            case double d: return FromFloat(d);
            case float f: return FromFloat(f);
            case decimal m: return FromFloat((double)m);
            case char c: return FromString(c.ToString());
            case Guid g: return FromString(g.ToString());
            case DateTime dt: return FromString(dt.ToString("O", CultureInfo.InvariantCulture));
            case DateTimeOffset dto: return FromString(dto.ToString("O", CultureInfo.InvariantCulture));
            case Enum e: return FromString(e.ToString());
            case Node n: return FromNode(n);
            case Relationship r: return FromRel(r);
            case GraphPath p: return FromPath(p);
            case IReadOnlyDictionary<string, Value> rv:
                {
                    var d = new Dictionary<string, Value>(rv.Count);
                    foreach (var kv in rv) d[kv.Key] = kv.Value;
                    return FromMap(d);
                }
            case IDictionary<string, object?> dict:
                {
                    var d = new Dictionary<string, Value>(dict.Count);
                    foreach (var kv in dict) d[kv.Key] = FromObject(kv.Value);
                    return FromMap(d);
                }
            case IReadOnlyDictionary<string, object?> rdict:
                {
                    var d = new Dictionary<string, Value>(rdict.Count);
                    foreach (var kv in rdict) d[kv.Key] = FromObject(kv.Value);
                    return FromMap(d);
                }
            case System.Collections.IDictionary nd:
                {
                    var d = new Dictionary<string, Value>(nd.Count);
                    foreach (System.Collections.DictionaryEntry kv in nd) d[kv.Key.ToString()!] = FromObject(kv.Value);
                    return FromMap(d);
                }
            case Value[] arr: return FromList(arr);
            case System.Collections.IEnumerable en:
                {
                    var list = new List<Value>();
                    foreach (var x in en) list.Add(FromObject(x));
                    return FromList(list.ToArray());
                }
            default:
                throw new ArgumentException($"Unsupported parameter type: {o.GetType()}");
        }
    }

    private InvalidCastException TypeError(string expected) =>
        new($"Expected {expected} but value is {Kind}");

    public bool Equals(Value other) => ValueOps.Equivalent(this, other);
    public override bool Equals(object? obj) => obj is Value v && Equals(v);
    public override int GetHashCode() => ValueOps.Hash(this);
    public static bool operator ==(Value a, Value b) => a.Equals(b);
    public static bool operator !=(Value a, Value b) => !a.Equals(b);

    public override string ToString()
    {
        var sb = new StringBuilder();
        Append(sb, true);
        return sb.ToString();
    }

    /// <summary>Plain string form (strings are not quoted at top level).</summary>
    public string ToDisplayString()
    {
        if (Kind == ValueKind.String) return Str;
        var sb = new StringBuilder();
        Append(sb, true);
        return sb.ToString();
    }

    internal void Append(StringBuilder sb, bool quoteStrings)
    {
        switch (Kind)
        {
            case ValueKind.Null: sb.Append("null"); break;
            case ValueKind.Boolean: sb.Append(Num != 0 ? "true" : "false"); break;
            case ValueKind.Integer: sb.Append(Num.ToString(CultureInfo.InvariantCulture)); break;
            case ValueKind.Float: sb.Append(FormatFloat(FloatBits)); break;
            case ValueKind.String:
                if (quoteStrings) sb.Append('\'').Append(Str.Replace("\\", "\\\\").Replace("'", "\\'")).Append('\'');
                else sb.Append(Str);
                break;
            case ValueKind.List:
                sb.Append('[');
                var items = ListItems;
                for (int i = 0; i < items.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    items[i].Append(sb, true);
                }
                sb.Append(']');
                break;
            case ValueKind.Map:
                AppendMap(sb, MapItems);
                break;
            case ValueKind.Node:
                if (Obj is Node n) sb.Append(n.ToString());
                else sb.Append("Node(").Append(Num).Append(')');
                break;
            case ValueKind.Relationship:
                if (Obj is Relationship r) sb.Append(r.ToString());
                else sb.Append("Rel(").Append(Num).Append(')');
                break;
            case ValueKind.Path:
                sb.Append(((GraphPath)Obj!).ToString());
                break;
        }
    }

    internal static void AppendMap(StringBuilder sb, IEnumerable<KeyValuePair<string, Value>> map)
    {
        sb.Append('{');
        bool first = true;
        foreach (var kv in map)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(kv.Key).Append(": ");
            kv.Value.Append(sb, true);
        }
        sb.Append('}');
    }

    internal static string FormatFloat(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        if (s.IndexOfAny(['.', 'E', 'e']) < 0) s += ".0";
        return s;
    }
}
