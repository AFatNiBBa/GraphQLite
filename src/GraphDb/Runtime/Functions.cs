using System.Globalization;

namespace GraphDb.Runtime;

/// <summary>Scalar function library. Each function is compiled into a closure over its argument evaluators.</summary>
internal static class Functions
{
    private static CypherRuntimeException Err(string fn, string msg) => new($"{fn}(): {msg}");

    private static void Arity(string name, Eval[] a, int min, int max)
    {
        if (a.Length < min || a.Length > max)
            throw new CypherSemanticException(min == max
                ? $"Function {name}() expects {min} argument(s), got {a.Length}"
                : $"Function {name}() expects {min}..{max} arguments, got {a.Length}");
    }

    private static Eval F1(string n, Eval[] a, Func<ExecContext, Value, Value> f)
    {
        Arity(n, a, 1, 1);
        var x = a[0];
        return ctx => f(ctx, x(ctx));
    }

    private static Eval N1(string n, Eval[] a, Func<double, double> f)
    {
        Arity(n, a, 1, 1);
        var x = a[0];
        return ctx =>
        {
            var v = x(ctx);
            if (v.IsNull) return Value.Null;
            if (!v.IsNumber) throw Err(n, $"expected a number, got {v.Kind}");
            return Value.FromFloat(f(v.AsDouble()));
        };
    }

    private static Eval S1(string n, Eval[] a, Func<string, string> f)
    {
        Arity(n, a, 1, 1);
        var x = a[0];
        return ctx =>
        {
            var v = x(ctx);
            if (v.IsNull) return Value.Null;
            if (v.Kind != ValueKind.String) throw Err(n, $"expected a string, got {v.Kind}");
            return Value.FromString(f(v.Str));
        };
    }

    public static bool Exists(string name) => KnownNames.Contains(name);

    /// <summary>Builds a closure for the function, or returns null when the function is unknown.</summary>
    public static Eval? Build(string name, Eval[] a)
    {
        switch (name)
        {
            // ------------------------------------------------ graph
            case "id":
                return F1(name, a, (ctx, v) => v.Kind switch
                {
                    ValueKind.Node or ValueKind.Relationship => Value.FromInt(v.Num),
                    ValueKind.Null => Value.Null,
                    _ => throw Err("id", $"expected node or relationship, got {v.Kind}"),
                });
            case "elementid":
                return F1(name, a, (ctx, v) => v.Kind switch
                {
                    ValueKind.Node => Value.FromString("n:" + v.Num),
                    ValueKind.Relationship => Value.FromString("r:" + v.Num),
                    ValueKind.Null => Value.Null,
                    _ => throw Err("elementId", $"expected node or relationship, got {v.Kind}"),
                });
            case "labels":
                return F1(name, a, (ctx, v) =>
                {
                    if (v.IsNull) return Value.Null;
                    if (v.Kind != ValueKind.Node) throw Err("labels", $"expected a node, got {v.Kind}");
                    var ls = ctx.Store.NodeLabels(v.EntityId);
                    if (ls.Length == 0) return Value.EmptyList;
                    var r = new Value[ls.Length];
                    for (int i = 0; i < ls.Length; i++) r[i] = Value.FromString(ctx.Store.Labels.Name(ls[i]));
                    return Value.FromList(r);
                });
            case "type":
                return F1(name, a, (ctx, v) =>
                {
                    if (v.IsNull) return Value.Null;
                    if (v.Kind != ValueKind.Relationship) throw Err("type", $"expected a relationship, got {v.Kind}");
                    return Value.FromString(ctx.Store.RelTypes.Name(ctx.Store.RelInfo(v.EntityId).Type));
                });
            case "keys":
                return F1(name, a, (ctx, v) =>
                {
                    switch (v.Kind)
                    {
                        case ValueKind.Null: return Value.Null;
                        case ValueKind.Map: return Value.FromList(v.MapItems.Keys.Select(Value.FromString).ToArray());
                        case ValueKind.Node:
                        case ValueKind.Relationship:
                            {
                                var keys = ctx.Store.PropKeysOf(v.Kind == ValueKind.Node ? ctx.Store.NodePropsAddr(v.EntityId) : ctx.Store.RelPropsAddr(v.EntityId));
                                var r = new Value[keys.Length];
                                for (int i = 0; i < keys.Length; i++) r[i] = Value.FromString(ctx.Store.PropKeys.Name(keys[i]));
                                return Value.FromList(r);
                            }
                        default: throw Err("keys", $"expected node, relationship or map, got {v.Kind}");
                    }
                });
            case "properties":
                return F1(name, a, (ctx, v) => v.Kind switch
                {
                    ValueKind.Null => Value.Null,
                    ValueKind.Map => v,
                    ValueKind.Node => Value.FromMap(ctx.Store.PropsDict(ctx.Store.NodePropsAddr(v.EntityId))),
                    ValueKind.Relationship => Value.FromMap(ctx.Store.PropsDict(ctx.Store.RelPropsAddr(v.EntityId))),
                    _ => throw Err("properties", $"expected node, relationship or map, got {v.Kind}"),
                });
            case "startnode":
                return F1(name, a, (ctx, v) => v.Kind == ValueKind.Relationship ? Value.FromNodeId(ctx.Store.RelInfo(v.EntityId).Src)
                    : v.IsNull ? Value.Null : throw Err("startNode", "expected a relationship"));
            case "endnode":
                return F1(name, a, (ctx, v) => v.Kind == ValueKind.Relationship ? Value.FromNodeId(ctx.Store.RelInfo(v.EntityId).Dst)
                    : v.IsNull ? Value.Null : throw Err("endNode", "expected a relationship"));
            case "degree":
                return F1(name, a, (ctx, v) =>
                {
                    if (v.IsNull) return Value.Null;
                    var (o, i) = ctx.Store.Degree(v.EntityId);
                    return Value.FromInt(o + i);
                });

            // ------------------------------------------------ temporal
            case "date":
            case "localtime":
            case "time":
            case "localdatetime":
            case "datetime":
            case "date.realtime":
            case "date.statement":
            case "date.transaction":
            case "localtime.realtime":
            case "localtime.statement":
            case "localtime.transaction":
            case "time.realtime":
            case "time.statement":
            case "time.transaction":
            case "localdatetime.realtime":
            case "localdatetime.statement":
            case "localdatetime.transaction":
            case "datetime.realtime":
            case "datetime.statement":
            case "datetime.transaction":
                {
                    Arity(name, a, 0, 1);
                    string kind = name.Split('.')[0];
                    if (name.Contains('.'))
                    {
                        // the optional argument is a time zone
                        var tzArg = a.Length > 0 ? a[0] : null;
                        return ctx =>
                        {
                            var now = Temporal.Construct("datetime", []);
                            if (tzArg is null) return kind == "datetime" ? now : Temporal.Convert(kind, now, null);
                            var zoned = Value.FromZonedUtc(now.Num, ZoneInfo.FromName(tzArg(ctx).AsString()));
                            return kind == "datetime" ? zoned : Temporal.Convert(kind, zoned, null);
                        };
                    }
                    var args = a;
                    return ctx => Temporal.Construct(kind, args.Length == 0 ? [] : [args[0](ctx)]);
                }
            case "date.truncate":
            case "localtime.truncate":
            case "time.truncate":
            case "localdatetime.truncate":
            case "datetime.truncate":
                {
                    Arity(name, a, 2, 3);
                    string kind = name.Split('.')[0];
                    var unit = a[0];
                    var val = a[1];
                    return ctx =>
                    {
                        var u = unit(ctx);
                        var v = val(ctx);
                        return u.IsNull || v.IsNull ? Value.Null : Temporal.Truncate(kind, u.AsString(), v);
                    };
                }
            case "datetime.fromepoch":
                {
                    Arity(name, a, 1, 2);
                    var s = a[0];
                    var ns = a.Length > 1 ? a[1] : null;
                    return ctx => Temporal.Construct("datetime", [Value.FromMap(new Dictionary<string, Value>
                    {
                        ["epochSeconds"] = s(ctx),
                        ["nanosecond"] = ns is null ? Value.FromInt(0) : ns(ctx),
                    })]);
                }
            case "datetime.fromepochmillis":
                return F1(name, a, (ctx, v) => Temporal.Construct("datetime", [Value.FromMap(new Dictionary<string, Value> { ["epochMillis"] = v })]));
            case "duration":
                return F1(name, a, (ctx, v) => Temporal.ConstructDuration(v));
            case "duration.between":
            case "duration.inmonths":
            case "duration.indays":
            case "duration.inseconds":
                {
                    Arity(name, a, 2, 2);
                    string unit = name switch
                    {
                        "duration.inmonths" => "months",
                        "duration.indays" => "days",
                        "duration.inseconds" => "seconds",
                        _ => "between",
                    };
                    var x = a[0];
                    var y = a[1];
                    return ctx => Temporal.Between(x(ctx), y(ctx), unit);
                }

            // ------------------------------------------------ spatial
            case "point":
                return F1(name, a, (ctx, v) => Temporal.MakePoint(v));
            case "point.distance":
            case "distance":
                {
                    Arity(name, a, 2, 2);
                    var x = a[0];
                    var y = a[1];
                    return ctx =>
                    {
                        var p = x(ctx);
                        var q = y(ctx);
                        if (p.IsNull || q.IsNull) return Value.Null;
                        double d = p.AsPoint().DistanceTo(q.AsPoint());
                        return double.IsNaN(d) ? Value.Null : Value.FromFloat(d);
                    };
                }
            case "point.withinbbox":
                {
                    Arity(name, a, 3, 3);
                    var pe = a[0];
                    var lo = a[1];
                    var hi = a[2];
                    return ctx =>
                    {
                        var p = pe(ctx);
                        var l = lo(ctx);
                        var h = hi(ctx);
                        if (p.IsNull || l.IsNull || h.IsNull) return Value.Null;
                        var pp = p.AsPoint();
                        var lp = l.AsPoint();
                        var hp = h.AsPoint();
                        if (pp.Srid != lp.Srid || pp.Srid != hp.Srid) return Value.Null;
                        return Value.FromBool(pp.X >= lp.X && pp.X <= hp.X && pp.Y >= lp.Y && pp.Y <= hp.Y);
                    };
                }

            // ------------------------------------------------ json
            case "tojson":
            case "json.stringify":
            case "apoc.convert.tojson":
                return F1(name, a, (ctx, v) =>
                {
                    var mat = new QueryResult.Materializer(ctx.Store);
                    return Value.FromString(mat.Convert(v).ToJson());
                });
            case "fromjson":
            case "json.parse":
            case "apoc.convert.fromjsonmap":
            case "apoc.convert.fromjsonlist":
                return F1(name, a, (ctx, v) => v.IsNull ? Value.Null : Value.FromJson(v.AsString()));
            case "valuetype":
                return F1(name, a, (ctx, v) => Value.FromString(v.Kind switch
                {
                    ValueKind.Null => "NULL",
                    ValueKind.Boolean => "BOOLEAN",
                    ValueKind.Integer => "INTEGER",
                    ValueKind.Float => "FLOAT",
                    ValueKind.String => "STRING",
                    ValueKind.List => "LIST<ANY>",
                    ValueKind.Map => "MAP",
                    ValueKind.Node => "NODE",
                    ValueKind.Relationship => "RELATIONSHIP",
                    ValueKind.Path => "PATH",
                    ValueKind.Date => "DATE",
                    ValueKind.LocalTime => "LOCAL TIME",
                    ValueKind.Time => "ZONED TIME",
                    ValueKind.LocalDateTime => "LOCAL DATETIME",
                    ValueKind.DateTime => "ZONED DATETIME",
                    ValueKind.Duration => "DURATION",
                    _ => "POINT",
                }));            case "nodes":
                return F1(name, a, (ctx, v) => v.Kind == ValueKind.Path
                    ? Value.FromList(((GraphPath)v.Obj!).NodeIds.Select(Value.FromNodeId).ToArray())
                    : v.IsNull ? Value.Null : throw Err("nodes", "expected a path"));
            case "relationships":
            case "rels":
                return F1(name, a, (ctx, v) => v.Kind == ValueKind.Path
                    ? Value.FromList(((GraphPath)v.Obj!).RelIds.Select(Value.FromRelId).ToArray())
                    : v.IsNull ? Value.Null : throw Err("relationships", "expected a path"));
            case "length":
                return F1(name, a, (ctx, v) => v.Kind switch
                {
                    ValueKind.Path => Value.FromInt(((GraphPath)v.Obj!).RelIds.Length),
                    ValueKind.List => Value.FromInt(v.ListItems.Length),
                    ValueKind.String => Value.FromInt(v.Str.Length),
                    ValueKind.Null => Value.Null,
                    _ => throw Err("length", $"expected a path, got {v.Kind}"),
                });

            // ------------------------------------------------ lists
            case "size":
                return F1(name, a, (ctx, v) => v.Kind switch
                {
                    ValueKind.List => Value.FromInt(v.ListItems.Length),
                    ValueKind.String => Value.FromInt(v.Str.Length),
                    ValueKind.Map => Value.FromInt(v.MapItems.Count),
                    ValueKind.Null => Value.Null,
                    _ => throw Err("size", $"expected a list or string, got {v.Kind}"),
                });
            case "head":
                return F1(name, a, (ctx, v) => v.IsNull ? Value.Null : ExprOps.AsList(v, "head") is { Length: > 0 } l ? l[0] : Value.Null);
            case "last":
                return F1(name, a, (ctx, v) => v.IsNull ? Value.Null : ExprOps.AsList(v, "last") is { Length: > 0 } l ? l[^1] : Value.Null);
            case "tail":
                return F1(name, a, (ctx, v) => v.IsNull ? Value.Null
                    : ExprOps.AsList(v, "tail") is { Length: > 1 } l ? Value.FromList(l[1..]) : Value.EmptyList);
            case "reverse":
                return F1(name, a, (ctx, v) =>
                {
                    if (v.IsNull) return Value.Null;
                    if (v.Kind == ValueKind.String)
                    {
                        var arr = v.Str.ToCharArray();
                        Array.Reverse(arr);
                        return Value.FromString(new string(arr));
                    }
                    var l = (Value[])ExprOps.AsList(v, "reverse").Clone();
                    Array.Reverse(l);
                    return Value.FromList(l);
                });
            case "range":
                {
                    Arity(name, a, 2, 3);
                    var s = a[0]; var e = a[1]; var st = a.Length > 2 ? a[2] : null;
                    return ctx =>
                    {
                        long start = s(ctx).AsInt64(), end = e(ctx).AsInt64(), step = st is null ? 1 : st(ctx).AsInt64();
                        if (step == 0) throw Err("range", "step cannot be zero");
                        long count = step > 0 ? (end >= start ? (end - start) / step + 1 : 0) : (start >= end ? (start - end) / -step + 1 : 0);
                        if (count > 100_000_000) throw Err("range", "range is too large");
                        var r = new Value[count];
                        long x = start;
                        for (long i = 0; i < count; i++, x += step) r[i] = Value.FromInt(x);
                        return Value.FromList(r);
                    };
                }
            case "isempty":
                return F1(name, a, (ctx, v) => v.Kind switch
                {
                    ValueKind.List => Value.FromBool(v.ListItems.Length == 0),
                    ValueKind.String => Value.FromBool(v.Str.Length == 0),
                    ValueKind.Map => Value.FromBool(v.MapItems.Count == 0),
                    ValueKind.Null => Value.Null,
                    _ => throw Err("isEmpty", $"unsupported type {v.Kind}"),
                });

            // ------------------------------------------------ general
            case "coalesce":
                {
                    if (a.Length == 0) throw new CypherSemanticException("coalesce() requires at least one argument");
                    var args = a;
                    return ctx =>
                    {
                        foreach (var x in args)
                        {
                            var v = x(ctx);
                            if (!v.IsNull) return v;
                        }
                        return Value.Null;
                    };
                }
            case "exists":
                return F1(name, a, (ctx, v) => Value.FromBool(!v.IsNull));
            case "timestamp":
                Arity(name, a, 0, 0);
                return ctx => Value.FromInt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            case "randomuuid":
                Arity(name, a, 0, 0);
                return ctx => Value.FromString(Guid.NewGuid().ToString());
            case "rand":
                Arity(name, a, 0, 0);
                return ctx => Value.FromFloat(Random.Shared.NextDouble());
            case "pi":
                Arity(name, a, 0, 0);
                return ctx => Value.FromFloat(Math.PI);
            case "e":
                Arity(name, a, 0, 0);
                return ctx => Value.FromFloat(Math.E);

            // ------------------------------------------------ conversion
            case "tointeger":
            case "toint":
                return F1(name, a, (ctx, v) => ToInteger(v, true));
            case "tointegerornull":
                return F1(name, a, (ctx, v) => ToInteger(v, false));
            case "tofloat":
                return F1(name, a, (ctx, v) => ToFloat(v, true));
            case "tofloatornull":
                return F1(name, a, (ctx, v) => ToFloat(v, false));
            case "tostring":
                return F1(name, a, (ctx, v) => ToStr(v, true));
            case "tostringornull":
                return F1(name, a, (ctx, v) => ToStr(v, false));
            case "toboolean":
                return F1(name, a, (ctx, v) => ToBool(v, true));
            case "tobooleanornull":
                return F1(name, a, (ctx, v) => ToBool(v, false));

            // ------------------------------------------------ strings
            case "toupper":
            case "upper":
                return S1(name, a, s => s.ToUpperInvariant());
            case "tolower":
            case "lower":
                return S1(name, a, s => s.ToLowerInvariant());
            case "trim":
                return S1(name, a, s => s.Trim());
            case "ltrim":
                return S1(name, a, s => s.TrimStart());
            case "rtrim":
                return S1(name, a, s => s.TrimEnd());
            case "char_length":
            case "character_length":
                return F1(name, a, (ctx, v) => v.IsNull ? Value.Null : Value.FromInt(v.AsString().Length));
            case "substring":
                {
                    Arity(name, a, 2, 3);
                    var s = a[0]; var st = a[1]; var ln = a.Length > 2 ? a[2] : null;
                    return ctx =>
                    {
                        var v = s(ctx);
                        if (v.IsNull) return Value.Null;
                        var str = v.AsString();
                        long start = st(ctx).AsInt64();
                        if (start < 0) throw Err("substring", "start must be non-negative");
                        if (start >= str.Length) return Value.FromString("");
                        long len = ln is null ? str.Length - start : ln(ctx).AsInt64();
                        if (len < 0) throw Err("substring", "length must be non-negative");
                        len = Math.Min(len, str.Length - start);
                        return Value.FromString(str.Substring((int)start, (int)len));
                    };
                }
            case "left":
            case "right":
                {
                    Arity(name, a, 2, 2);
                    var s = a[0]; var n = a[1];
                    bool left = name == "left";
                    return ctx =>
                    {
                        var v = s(ctx);
                        if (v.IsNull) return Value.Null;
                        var str = v.AsString();
                        long k = n(ctx).AsInt64();
                        if (k < 0) throw Err(name, "length must be non-negative");
                        int len = (int)Math.Min(k, str.Length);
                        return Value.FromString(left ? str[..len] : str[(str.Length - len)..]);
                    };
                }
            case "replace":
                {
                    Arity(name, a, 3, 3);
                    var s = a[0]; var f = a[1]; var r = a[2];
                    return ctx =>
                    {
                        var v = s(ctx); var fv = f(ctx); var rv = r(ctx);
                        if (v.IsNull || fv.IsNull || rv.IsNull) return Value.Null;
                        var find = fv.AsString();
                        if (find.Length == 0) return v;
                        return Value.FromString(v.AsString().Replace(find, rv.AsString(), StringComparison.Ordinal));
                    };
                }
            case "split":
                {
                    Arity(name, a, 2, 2);
                    var s = a[0]; var d = a[1];
                    return ctx =>
                    {
                        var v = s(ctx); var dv = d(ctx);
                        if (v.IsNull || dv.IsNull) return Value.Null;
                        var str = v.AsString();
                        var delim = dv.AsString();
                        string[] parts = delim.Length == 0 ? str.Select(c => c.ToString()).ToArray() : str.Split(delim);
                        return Value.FromList(parts.Select(Value.FromString).ToArray());
                    };
                }

            // ------------------------------------------------ math
            case "abs":
                return F1(name, a, (ctx, v) => v.Kind switch
                {
                    ValueKind.Integer => v.Num == long.MinValue ? throw Err("abs", "integer overflow") : Value.FromInt(Math.Abs(v.Num)),
                    ValueKind.Float => Value.FromFloat(Math.Abs(v.FloatBits)),
                    ValueKind.Null => Value.Null,
                    _ => throw Err("abs", $"expected a number, got {v.Kind}"),
                });
            case "sign":
                return F1(name, a, (ctx, v) => v.Kind switch
                {
                    ValueKind.Integer => Value.FromInt(Math.Sign(v.Num)),
                    ValueKind.Float => Value.FromInt(Math.Sign(v.FloatBits)),
                    ValueKind.Null => Value.Null,
                    _ => throw Err("sign", $"expected a number, got {v.Kind}"),
                });
            case "ceil": return N1(name, a, Math.Ceiling);
            case "floor": return N1(name, a, Math.Floor);
            case "round":
                {
                    Arity(name, a, 1, 2);
                    var x = a[0]; var p = a.Length > 1 ? a[1] : null;
                    return ctx =>
                    {
                        var v = x(ctx);
                        if (v.IsNull) return Value.Null;
                        int prec = p is null ? 0 : (int)p(ctx).AsInt64();
                        return Value.FromFloat(Math.Round(v.AsDouble(), prec, MidpointRounding.AwayFromZero));
                    };
                }
            case "sqrt": return N1(name, a, Math.Sqrt);
            case "exp": return N1(name, a, Math.Exp);
            case "log": return N1(name, a, Math.Log);
            case "log10": return N1(name, a, Math.Log10);
            case "sin": return N1(name, a, Math.Sin);
            case "cos": return N1(name, a, Math.Cos);
            case "tan": return N1(name, a, Math.Tan);
            case "cot": return N1(name, a, x => 1.0 / Math.Tan(x));
            case "asin": return N1(name, a, Math.Asin);
            case "acos": return N1(name, a, Math.Acos);
            case "atan": return N1(name, a, Math.Atan);
            case "degrees": return N1(name, a, x => x * 180.0 / Math.PI);
            case "radians": return N1(name, a, x => x * Math.PI / 180.0);
            case "atan2":
                {
                    Arity(name, a, 2, 2);
                    var y = a[0]; var x = a[1];
                    return ctx =>
                    {
                        var yv = y(ctx); var xv = x(ctx);
                        if (yv.IsNull || xv.IsNull) return Value.Null;
                        return Value.FromFloat(Math.Atan2(yv.AsDouble(), xv.AsDouble()));
                    };
                }
        }
        return null;
    }

    private static readonly HashSet<string> KnownNames =
    [
        "id", "elementid", "labels", "type", "keys", "properties", "startnode", "endnode", "nodes", "relationships", "rels", "length",
        "size", "head", "last", "tail", "reverse", "range", "isempty", "coalesce", "exists", "timestamp", "randomuuid", "rand", "pi", "e",
        "tointeger", "toint", "tointegerornull", "tofloat", "tofloatornull", "tostring", "tostringornull", "toboolean", "tobooleanornull",
        "toupper", "upper", "tolower", "lower", "trim", "ltrim", "rtrim", "char_length", "character_length", "substring", "left", "right",
        "replace", "split", "abs", "sign", "ceil", "floor", "round", "sqrt", "exp", "log", "log10", "sin", "cos", "tan", "cot", "asin",
        "acos", "atan", "degrees", "radians", "atan2", "degree",
        "date", "localtime", "time", "localdatetime", "datetime", "date.realtime", "date.statement", "date.transaction",
        "localtime.realtime", "localtime.statement", "localtime.transaction", "time.realtime", "time.statement", "time.transaction",
        "localdatetime.realtime", "localdatetime.statement", "localdatetime.transaction", "datetime.realtime", "datetime.statement",
        "datetime.transaction", "date.truncate", "localtime.truncate", "time.truncate", "localdatetime.truncate", "datetime.truncate",
        "datetime.fromepoch", "datetime.fromepochmillis", "duration", "duration.between", "duration.inmonths", "duration.indays",
        "duration.inseconds", "point", "point.distance", "distance", "point.withinbbox", "tojson", "json.stringify", "apoc.convert.tojson",
        "fromjson", "json.parse", "apoc.convert.fromjsonmap", "apoc.convert.fromjsonlist", "valuetype",
    ];

    public static Value ToInteger(in Value v, bool strict)
    {
        switch (v.Kind)
        {
            case ValueKind.Null: return Value.Null;
            case ValueKind.Integer: return v;
            case ValueKind.Float:
                {
                    double d = v.FloatBits;
                    if (double.IsNaN(d) || double.IsInfinity(d)) return Value.Null;
                    return Value.FromInt((long)Math.Truncate(d));
                }
            case ValueKind.Boolean: return Value.FromInt(v.Num);
            case ValueKind.String:
                {
                    var s = v.Str.Trim();
                    if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return Value.FromInt(l);
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsInfinity(d))
                        return Value.FromInt((long)Math.Truncate(d));
                    return Value.Null;
                }
            default:
                if (strict) throw Err("toInteger", $"cannot convert {v.Kind}");
                return Value.Null;
        }
    }

    public static Value ToFloat(in Value v, bool strict)
    {
        switch (v.Kind)
        {
            case ValueKind.Null: return Value.Null;
            case ValueKind.Float: return v;
            case ValueKind.Integer: return Value.FromFloat(v.Num);
            case ValueKind.String:
                return double.TryParse(v.Str.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Value.FromFloat(d) : Value.Null;
            default:
                if (strict) throw Err("toFloat", $"cannot convert {v.Kind}");
                return Value.Null;
        }
    }

    public static Value ToStr(in Value v, bool strict)
    {
        switch (v.Kind)
        {
            case ValueKind.Null: return Value.Null;
            case ValueKind.String: return v;
            case ValueKind.Integer:
            case ValueKind.Float:
            case ValueKind.Boolean:
                return Value.FromString(v.ToDisplayString());
            case ValueKind.Date:
            case ValueKind.LocalTime:
            case ValueKind.Time:
            case ValueKind.LocalDateTime:
            case ValueKind.DateTime:
            case ValueKind.Duration:
            case ValueKind.Point:
                return Value.FromString(Temporal.Format(v));
            default:
                if (strict) throw Err("toString", $"cannot convert {v.Kind}");
                return Value.Null;
        }
    }

    public static Value ToBool(in Value v, bool strict)
    {
        switch (v.Kind)
        {
            case ValueKind.Null: return Value.Null;
            case ValueKind.Boolean: return v;
            case ValueKind.String:
                {
                    var s = v.Str.Trim();
                    if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return Value.True;
                    if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return Value.False;
                    return Value.Null;
                }
            case ValueKind.Integer: return Value.FromBool(v.Num != 0);
            default:
                if (strict) throw Err("toBoolean", $"cannot convert {v.Kind}");
                return Value.Null;
        }
    }
}
