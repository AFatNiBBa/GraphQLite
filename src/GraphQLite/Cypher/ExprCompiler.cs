using GraphQLite.Runtime;
using GraphQLite.Storage;

namespace GraphQLite.Cypher;

/// <summary>Variables visible to an expression plus optional structural (by source text) matches,
/// used after aggregation to refer to already computed values.</summary>
internal sealed class CompileScope(Dictionary<string, int> vars, Dictionary<string, int>? textSlots = null)
{
    public readonly Dictionary<string, int> Vars = vars;
    public readonly Dictionary<string, int>? TextSlots = textSlots;
}

internal sealed partial class Planner
{
    public Eval Compile(Expr e, Dictionary<string, int> vars) => Compile(e, new CompileScope(vars));

    public Eval Compile(Expr e, CompileScope s)
    {
        if (s.TextSlots != null && e is not LiteralExpr && s.TextSlots.TryGetValue(e.Text, out var ts))
            return ctx => ctx.Row[ts];

        switch (e)
        {
            case LiteralExpr lit:
                {
                    var v = lit.Value;
                    return _ => v;
                }
            case ParamExpr p:
                {
                    int idx = ParamIndex(p.Name);
                    return ctx => ctx.Params[idx];
                }
            case VarExpr v:
                {
                    int slot = Slot(v.Name, s);
                    return ctx => ctx.Row[slot];
                }
            case PropertyExpr pe:
                {
                    var key = new TokenRef(pe.Key, TokenKind.PropKey);
                    if (pe.Target is VarExpr tv && (s.TextSlots is null || !s.TextSlots.ContainsKey(tv.Text)))
                    {
                        int slot = Slot(tv.Name, s);
                        return ctx => ExprOps.Property(ctx, ctx.Row[slot], key);
                    }
                    var target = Compile(pe.Target, s);
                    return ctx => ExprOps.Property(ctx, target(ctx), key);
                }
            case IndexExpr ie:
                {
                    var t = Compile(ie.Target, s);
                    var i = Compile(ie.Index, s);
                    return ctx => ExprOps.Index(ctx, t(ctx), i(ctx));
                }
            case SliceExpr se:
                {
                    var t = Compile(se.Target, s);
                    var f = se.From is null ? null : Compile(se.From, s);
                    var to = se.To is null ? null : Compile(se.To, s);
                    return ctx => ExprOps.Slice(t(ctx), f is null ? Value.Null : f(ctx), to is null ? Value.Null : to(ctx), f != null, to != null);
                }
            case BinaryExpr be:
                return CompileBinary(be, s);
            case UnaryExpr ue:
                {
                    var o = Compile(ue.Operand, s);
                    return ue.Op switch
                    {
                        UnOp.Not => ctx => ExprOps.Not(o(ctx)),
                        UnOp.Neg => ctx => ExprOps.Neg(o(ctx)),
                        _ => ctx =>
                        {
                            var v = o(ctx);
                            return v.IsNumber || v.IsNull ? v : throw new CypherRuntimeException($"Cannot apply unary + to {v.Kind}");
                        },
                    };
                }
            case IsNullExpr isn:
                {
                    var o = Compile(isn.Operand, s);
                    return isn.Negated ? ctx => Value.FromBool(!o(ctx).IsNull) : ctx => Value.FromBool(o(ctx).IsNull);
                }
            case LabelCheckExpr lc:
                {
                    var t = Compile(lc.Target, s);
                    if (lc.Expression is { } lexpr) return ctx => ExprOps.LabelExpr(ctx, t(ctx), lexpr);
                    var labels = lc.Labels.Select(l => new TokenRef(l, TokenKind.Label)).ToArray();
                    return ctx => ExprOps.HasLabels(ctx, t(ctx), labels);
                }
            case ListExpr le:
                {
                    if (le.Items.All(x => x is LiteralExpr))
                    {
                        var constant = Value.FromList(le.Items.Select(x => ((LiteralExpr)x).Value).ToArray());
                        return _ => constant;
                    }
                    var items = le.Items.Select(x => Compile(x, s)).ToArray();
                    return ctx =>
                    {
                        var arr = new Value[items.Length];
                        for (int i = 0; i < arr.Length; i++) arr[i] = items[i](ctx);
                        return Value.FromList(arr);
                    };
                }
            case MapExpr me:
                {
                    var keys = me.Keys.ToArray();
                    var vals = me.Values.Select(x => Compile(x, s)).ToArray();
                    return ctx =>
                    {
                        var d = new Dictionary<string, Value>(keys.Length);
                        for (int i = 0; i < keys.Length; i++) d[keys[i]] = vals[i](ctx);
                        return Value.FromMap(d);
                    };
                }
            case CountStarExpr:
                throw new CypherSemanticException("Invalid use of aggregating function count(*) in this context");
            case FuncExpr fe:
                {
                    if (AggSpec.Parse(fe.Name) != null)
                        throw new CypherSemanticException($"Invalid use of aggregating function {fe.Name}(...) in this context");
                    var args = fe.Args.Select(x => Compile(x, s)).ToArray();
                    var f = Functions.Build(fe.Name, args) ?? throw new CypherSemanticException($"Unknown function '{fe.Name}'");
                    return f;
                }
            case CaseExpr ce:
                return CompileCase(ce, s);
            case ListCompExpr lc:
                {
                    var list = Compile(lc.List, s);
                    int slot = NewSlot();
                    var inner = Child(s, lc.Variable, slot);
                    var where = lc.Where is null ? null : Compile(lc.Where, inner);
                    var map = lc.Map is null ? null : Compile(lc.Map, inner);
                    return ctx =>
                    {
                        var lv = list(ctx);
                        if (lv.IsNull) return Value.Null;
                        var items = ExprOps.AsList(lv, "list comprehension");
                        var result = new List<Value>(items.Length);
                        foreach (var it in items)
                        {
                            ctx.Row[slot] = it;
                            if (where != null && !ValueOps.IsTrue(where(ctx))) continue;
                            result.Add(map is null ? it : map(ctx));
                        }
                        return Value.FromList(result.ToArray());
                    };
                }
            case QuantifierExpr q:
                {
                    var list = Compile(q.List, s);
                    int slot = NewSlot();
                    var pred = Compile(q.Where, Child(s, q.Variable, slot));
                    var kind = q.Kind;
                    return ctx =>
                    {
                        var lv = list(ctx);
                        if (lv.IsNull) return Value.Null;
                        int trues = 0, nulls = 0, n = 0;
                        foreach (var it in ExprOps.AsList(lv, "quantifier"))
                        {
                            n++;
                            ctx.Row[slot] = it;
                            var r = pred(ctx);
                            if (r.IsNull) nulls++;
                            else if (r.Kind == ValueKind.Boolean && r.Num != 0)
                            {
                                trues++;
                                if (kind == QuantKind.Any) return Value.True;
                                if (kind == QuantKind.None) return Value.False;
                                if (kind == QuantKind.Single && trues > 1) return Value.False;
                            }
                            else if (kind == QuantKind.All) return Value.False;
                        }
                        return kind switch
                        {
                            QuantKind.All => nulls > 0 ? Value.Null : Value.True,
                            QuantKind.Any => nulls > 0 ? Value.Null : Value.False,
                            QuantKind.None => nulls > 0 ? Value.Null : Value.True,
                            _ => nulls > 0 ? Value.Null : Value.FromBool(trues == 1),
                        };
                    };
                }
            case ReduceExpr r:
                {
                    var init = Compile(r.Init, s);
                    var list = Compile(r.List, s);
                    int accSlot = NewSlot(), varSlot = NewSlot();
                    var inner = Child(Child(s, r.Acc, accSlot), r.Variable, varSlot);
                    var body = Compile(r.Body, inner);
                    return ctx =>
                    {
                        var acc = init(ctx);
                        var lv = list(ctx);
                        if (lv.IsNull) return Value.Null;
                        foreach (var it in ExprOps.AsList(lv, "reduce"))
                        {
                            ctx.Row[accSlot] = acc;
                            ctx.Row[varSlot] = it;
                            acc = body(ctx);
                        }
                        return acc;
                    };
                }
            case PatternSubqueryExpr ps:
                return CompileSubquery(ps, s);
            case MapProjectionExpr mp:
                return CompileMapProjection(mp, s);
        }
        throw new CypherSemanticException($"Unsupported expression: {e.Text}");
    }

    private static int Slot(string name, CompileScope s)
    {
        if (s.Vars.TryGetValue(name, out var slot)) return slot;
        throw new CypherSemanticException($"Variable `{name}` not defined");
    }

    private static CompileScope Child(CompileScope s, string name, int slot)
    {
        var vars = new Dictionary<string, int>(s.Vars) { [name] = slot };
        return new CompileScope(vars, s.TextSlots);
    }

    private Eval CompileBinary(BinaryExpr be, CompileScope s)
    {
        var l = Compile(be.Left, s);
        var r = Compile(be.Right, s);
        switch (be.Op)
        {
            case BinOp.And:
                return ctx =>
                {
                    int a = ExprOps.TriAnd(l(ctx));
                    if (a == 0) return Value.False;
                    int b = ExprOps.TriAnd(r(ctx));
                    if (b == 0) return Value.False;
                    return a < 0 || b < 0 ? Value.Null : Value.True;
                };
            case BinOp.Or:
                return ctx =>
                {
                    int a = ExprOps.TriOr(l(ctx));
                    if (a == 1) return Value.True;
                    int b = ExprOps.TriOr(r(ctx));
                    if (b == 1) return Value.True;
                    return a < 0 || b < 0 ? Value.Null : Value.False;
                };
            case BinOp.Xor: return ctx => ExprOps.Xor(l(ctx), r(ctx));
            case BinOp.Add: return ctx => ExprOps.Add(l(ctx), r(ctx));
            case BinOp.Sub: return ctx => ExprOps.Sub(l(ctx), r(ctx));
            case BinOp.Mul: return ctx => ExprOps.Mul(l(ctx), r(ctx));
            case BinOp.Div: return ctx => ExprOps.Div(l(ctx), r(ctx));
            case BinOp.Mod: return ctx => ExprOps.Mod(l(ctx), r(ctx));
            case BinOp.Pow: return ctx => ExprOps.Pow(l(ctx), r(ctx));
            case BinOp.Eq:
                if (be.Right is LiteralExpr lit)
                {
                    var c = lit.Value;
                    return ctx => ExprOps.Eq(l(ctx), c);
                }
                return ctx => ExprOps.Eq(l(ctx), r(ctx));
            case BinOp.Neq: return ctx => ExprOps.Neq(l(ctx), r(ctx));
            case BinOp.Lt:
                if (be.Right is LiteralExpr lt) { var c = lt.Value; return ctx => ExprOps.Lt(l(ctx), c); }
                return ctx => ExprOps.Lt(l(ctx), r(ctx));
            case BinOp.Gt:
                if (be.Right is LiteralExpr gt) { var c = gt.Value; return ctx => ExprOps.Gt(l(ctx), c); }
                return ctx => ExprOps.Gt(l(ctx), r(ctx));
            case BinOp.Le: return ctx => ExprOps.Le(l(ctx), r(ctx));
            case BinOp.Ge: return ctx => ExprOps.Ge(l(ctx), r(ctx));
            case BinOp.In:
                if (be.Right is ListExpr { Items.Count: > 8 } le && le.Items.All(x => x is LiteralExpr { Value.IsNull: false }))
                {
                    // large constant list: hash lookup
                    var set = new HashSet<Value>(le.Items.Select(x => ((LiteralExpr)x).Value), ValueEquivalenceComparer.Instance);
                    return ctx =>
                    {
                        var v = l(ctx);
                        if (v.IsNull) return Value.Null;
                        if (v.Kind is ValueKind.List or ValueKind.Map) return ExprOps.In(v, r(ctx));
                        return Value.FromBool(set.Contains(v));
                    };
                }
                return ctx => ExprOps.In(l(ctx), r(ctx));
            case BinOp.StartsWith: return ctx => ExprOps.StartsWith(l(ctx), r(ctx));
            case BinOp.EndsWith: return ctx => ExprOps.EndsWith(l(ctx), r(ctx));
            case BinOp.Contains: return ctx => ExprOps.Contains(l(ctx), r(ctx));
            case BinOp.Regex:
                if (be.Right is LiteralExpr { Value.Kind: ValueKind.String } rx)
                {
                    var regex = ExprOps.GetRegex(rx.Value.Str);
                    return ctx =>
                    {
                        var v = l(ctx);
                        return v.Kind == ValueKind.String ? Value.FromBool(regex.IsMatch(v.Str)) : Value.Null;
                    };
                }
                return ctx => ExprOps.RegexMatch(l(ctx), r(ctx));
        }
        throw new CypherSemanticException($"Unsupported operator {be.Op}");
    }

    private Eval CompileCase(CaseExpr ce, CompileScope s)
    {
        var whens = ce.Whens.Select(w => Compile(w, s)).ToArray();
        var thens = ce.Thens.Select(t => Compile(t, s)).ToArray();
        var @else = ce.Else is null ? null : Compile(ce.Else, s);
        if (ce.Test is null)
        {
            return ctx =>
            {
                for (int i = 0; i < whens.Length; i++)
                    if (ValueOps.IsTrue(whens[i](ctx))) return thens[i](ctx);
                return @else is null ? Value.Null : @else(ctx);
            };
        }
        var test = Compile(ce.Test, s);
        return ctx =>
        {
            var v = test(ctx);
            for (int i = 0; i < whens.Length; i++)
                if (ValueOps.IsTrue(ExprOps.Eq(v, whens[i](ctx)))) return thens[i](ctx);
            return @else is null ? Value.Null : @else(ctx);
        };
    }

    private Eval CompileSubquery(PatternSubqueryExpr ps, CompileScope s)
    {
        var innerVars = new Dictionary<string, int>(s.Vars);
        var plan = PlanPatternMatch(new StartOp(), innerVars, ps.Pattern, ps.Where, nested: true);
        switch (ps.Kind)
        {
            case SubqueryKind.Exists:
                return ctx => Value.FromBool(plan.Open(ctx).Next());
            case SubqueryKind.Count:
                return ctx =>
                {
                    var c = plan.Open(ctx);
                    long n = 0;
                    while (c.Next()) n++;
                    return Value.FromInt(n);
                };
            default:
                {
                    var map = Compile(ps.Map!, new CompileScope(innerVars));
                    return ctx =>
                    {
                        var c = plan.Open(ctx);
                        var list = new List<Value>();
                        while (c.Next()) list.Add(map(ctx));
                        return Value.FromList(list.ToArray());
                    };
                }
        }
    }

    private Eval CompileMapProjection(MapProjectionExpr mp, CompileScope s)
    {
        int slot = Slot(mp.Variable, s);
        var items = mp.Items.Select(it => (it.Kind, Name: it.Key, Token: new TokenRef(it.Key, TokenKind.PropKey),
            Eval: it.Kind switch
            {
                MapProjKind.Literal => Compile(it.Value!, s),
                MapProjKind.Variable => Compile(new VarExpr(it.Key) { Text = it.Key }, s),
                _ => null,
            })).ToArray();
        return ctx =>
        {
            var target = ctx.Row[slot];
            if (target.IsNull) return Value.Null;
            var d = new Dictionary<string, Value>();
            foreach (var it in items)
            {
                switch (it.Kind)
                {
                    case MapProjKind.Property:
                        d[it.Name] = ExprOps.Property(ctx, target, it.Token);
                        break;
                    case MapProjKind.AllProperties:
                        {
                            Dictionary<string, Value> all = target.Kind switch
                            {
                                ValueKind.Map => target.MapItems,
                                ValueKind.Node => ctx.Store.PropsDict(ctx.Store.NodePropsAddr(target.EntityId)),
                                ValueKind.Relationship => ctx.Store.PropsDict(ctx.Store.RelPropsAddr(target.EntityId)),
                                _ => throw new CypherRuntimeException($"Map projection requires a node, relationship or map, got {target.Kind}"),
                            };
                            foreach (var kv in all) d[kv.Key] = kv.Value;
                            break;
                        }
                    default:
                        d[it.Name] = it.Eval!(ctx);
                        break;
                }
            }
            return Value.FromMap(d);
        };
    }

    // ------------------------------------------------------------------ AST analysis

    public static bool IsAggregate(Expr e) => e is CountStarExpr || e is FuncExpr f && AggSpec.Parse(f.Name) != null;

    /// <summary>Collects top-level aggregate calls (not nested inside other aggregates).</summary>
    public static void CollectAggregates(Expr e, List<Expr> into)
    {
        if (IsAggregate(e))
        {
            into.Add(e);
            return;
        }
        foreach (var c in AllChildren(e)) CollectAggregates(c, into);
    }

    public static bool ContainsAggregate(Expr e)
    {
        var l = new List<Expr>();
        CollectAggregates(e, l);
        return l.Count > 0;
    }

    private static IEnumerable<Expr> AllChildren(Expr e)
    {
        switch (e)
        {
            case ListCompExpr lc:
                yield return lc.List;
                if (lc.Where != null) yield return lc.Where;
                if (lc.Map != null) yield return lc.Map;
                break;
            case QuantifierExpr q:
                yield return q.List;
                yield return q.Where;
                break;
            case ReduceExpr r:
                yield return r.Init;
                yield return r.List;
                yield return r.Body;
                break;
            case MapProjectionExpr mp:
                foreach (var it in mp.Items) if (it.Value != null) yield return it.Value;
                break;
            case PatternSubqueryExpr:
                break;
            default:
                foreach (var c in e.Children) yield return c;
                break;
        }
    }

    /// <summary>Free variables referenced by an expression (respecting comprehension scoping).</summary>
    public static HashSet<string> FreeVars(Expr e)
    {
        var acc = new HashSet<string>();
        FreeVars(e, [], acc);
        return acc;
    }

    private static void FreeVars(Expr e, HashSet<string> bound, HashSet<string> acc)
    {
        switch (e)
        {
            case VarExpr v:
                if (!bound.Contains(v.Name)) acc.Add(v.Name);
                return;
            case ListCompExpr lc:
                {
                    FreeVars(lc.List, bound, acc);
                    var b2 = new HashSet<string>(bound) { lc.Variable };
                    if (lc.Where != null) FreeVars(lc.Where, b2, acc);
                    if (lc.Map != null) FreeVars(lc.Map, b2, acc);
                    return;
                }
            case QuantifierExpr q:
                {
                    FreeVars(q.List, bound, acc);
                    FreeVars(q.Where, new HashSet<string>(bound) { q.Variable }, acc);
                    return;
                }
            case ReduceExpr r:
                {
                    FreeVars(r.Init, bound, acc);
                    FreeVars(r.List, bound, acc);
                    FreeVars(r.Body, new HashSet<string>(bound) { r.Acc, r.Variable }, acc);
                    return;
                }
            case MapProjectionExpr mp:
                if (!bound.Contains(mp.Variable)) acc.Add(mp.Variable);
                foreach (var it in mp.Items)
                {
                    if (it.Value != null) FreeVars(it.Value, bound, acc);
                    if (it.Kind == MapProjKind.Variable && !bound.Contains(it.Key)) acc.Add(it.Key);
                }
                return;
            case PatternSubqueryExpr ps:
                foreach (var part in ps.Pattern)
                {
                    if (part.PathVariable != null) acc.Add(part.PathVariable);
                    foreach (var n in part.Nodes)
                    {
                        if (n.Variable != null && !bound.Contains(n.Variable)) acc.Add(n.Variable);
                        if (n.Props != null) FreeVars(n.Props, bound, acc);
                    }
                    foreach (var r in part.Rels)
                    {
                        if (r.Variable != null && !bound.Contains(r.Variable)) acc.Add(r.Variable);
                        if (r.Props != null) FreeVars(r.Props, bound, acc);
                    }
                }
                if (ps.Where != null) FreeVars(ps.Where, bound, acc);
                if (ps.Map != null) FreeVars(ps.Map, bound, acc);
                return;
            default:
                foreach (var c in e.Children) FreeVars(c, bound, acc);
                return;
        }
    }

    public static IEnumerable<Expr> SplitAnd(Expr e)
    {
        if (e is BinaryExpr { Op: BinOp.And } b)
        {
            foreach (var x in SplitAnd(b.Left)) yield return x;
            foreach (var x in SplitAnd(b.Right)) yield return x;
        }
        else yield return e;
    }
}
