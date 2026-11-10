using GraphDb.Runtime;
using GraphDb.Storage;

namespace GraphDb.Cypher;

internal sealed class PlannedQuery
{
    public required Operator Root;
    public required string[] Columns;
    public required int[] ColumnSlots;
    public required int SlotCount;
    public required string[] ParamNames;
    public required bool IsWrite;
    public bool Explain;
}

/// <summary>
/// Turns the AST into a tree of physical operators. All variables of a query share a single
/// "wide row" (Value[]); the planner assigns each variable a fixed slot.
/// </summary>
internal sealed partial class Planner
{
    private readonly GraphStore _store;
    private int _slots;
    private int _anon;
    private readonly Dictionary<string, int> _params = new(StringComparer.Ordinal);
    private bool _readsSinceBarrier;
    private bool _varExpandSinceBarrier;
    private bool _writesSinceBarrier;
    private bool _isWrite;

    private Planner(GraphStore store) => _store = store;

    private int NewSlot() => _slots++;
    private string Anon() => "  anon" + (_anon++);
    public static bool IsAnon(string name) => name.StartsWith("  ", StringComparison.Ordinal);

    private int ParamIndex(string name)
    {
        if (!_params.TryGetValue(name, out var i))
        {
            i = _params.Count;
            _params[name] = i;
        }
        return i;
    }

    public static PlannedQuery Plan(GraphStore store, QueryStatement q)
    {
        var p = new Planner(store);
        return p.PlanQuery(q);
    }

    private PlannedQuery PlanQuery(QueryStatement q)
    {
        Operator root;
        string[] columns;
        int[] slots;
        if (q.Parts.Count == 1)
        {
            (root, columns, slots) = PlanSingle(q.Parts[0], null, null);
        }
        else
        {
            int[]? unionSlots = null;
            string[]? unionCols = null;
            var branches = new List<Operator>();
            foreach (var part in q.Parts)
            {
                var (op, cols, s) = PlanSingle(part, unionSlots, unionCols);
                unionSlots ??= s;
                unionCols ??= cols;
                branches.Add(op);
            }
            bool all = q.UnionAll.All(x => x);
            if (q.UnionAll.Distinct().Count() > 1) throw new CypherSemanticException("Mixing UNION and UNION ALL is not supported");
            root = new UnionOp(branches.ToArray(), unionSlots!, !all);
            columns = unionCols!;
            slots = unionSlots!;
        }
        var names = new string[_params.Count];
        foreach (var kv in _params) names[kv.Value] = kv.Key;
        return new PlannedQuery
        {
            Root = root,
            Columns = columns,
            ColumnSlots = slots,
            SlotCount = _slots,
            ParamNames = names,
            IsWrite = _isWrite,
            Explain = q.Explain,
        };
    }

    // ================================================================== single query

    private (Operator, string[], int[]) PlanSingle(List<Clause> clauses, int[]? unionSlots, string[]? unionCols)
    {
        Operator op = new StartOp();
        var scope = new Dictionary<string, int>(StringComparer.Ordinal);
        _readsSinceBarrier = false;
        _varExpandSinceBarrier = false;
        _writesSinceBarrier = false;
        for (int i = 0; i < clauses.Count; i++)
        {
            var clause = clauses[i];
            bool last = i == clauses.Count - 1;
            switch (clause)
            {
                case ReturnClause rc:
                    {
                        if (!last) throw new CypherSemanticException("RETURN can only be used at the end of the query");
                        var (o, newScope, cols) = PlanProjection(op, scope, rc.Projection, unionSlots);
                        if (unionCols != null && !cols.SequenceEqual(unionCols))
                            throw new CypherSemanticException("All sub queries in an UNION must have the same return column names");
                        var colSlots = cols.Select(c => newScope[c]).ToArray();
                        return (o, cols, colSlots);
                    }
                case CallClause cc when last && cc.Yields is null && i == 0:
                    {
                        // standalone procedure call: return every output column
                        var proc = Procedures.Find(cc.Procedure) ?? throw new CypherSemanticException($"Unknown procedure '{cc.Procedure}'");
                        var outSlots = proc.Outputs.Select(_ => NewSlot()).ToArray();
                        var args = (cc.Args ?? []).Select(a => Compile(a, scope)).ToArray();
                        op = new ProcedureCallOp(op, proc, args, outSlots);
                        return (op, proc.Outputs, outSlots);
                    }
                default:
                    op = PlanClause(op, scope, clause);
                    if (last)
                    {
                        if (clause is MatchClause or WithClause or UnwindClause or CallClause)
                            throw new CypherSemanticException($"Query cannot conclude with {ClauseName(clause)} (must be RETURN or an update clause)");
                        if (unionSlots != null) throw new CypherSemanticException("UNION sub queries must end with RETURN");
                        return (op, [], []);
                    }
                    break;
            }
        }
        throw new CypherSemanticException("Empty query");
    }

    private static string ClauseName(Clause c) => c switch
    {
        MatchClause => "MATCH",
        WithClause => "WITH",
        UnwindClause => "UNWIND",
        CallClause => "CALL",
        _ => c.GetType().Name,
    };

    private Operator PlanClause(Operator op, Dictionary<string, int> scope, Clause clause)
    {
        switch (clause)
        {
            case MatchClause m:
                // reads after writes must observe the complete effect of the previous clauses (Neo4j "Eager")
                if (_writesSinceBarrier) op = Barrier(op);
                return PlanMatch(op, scope, m);
            case UnwindClause u:
                {
                    var list = Compile(u.List, scope);
                    int slot = NewSlot();
                    scope[u.Variable] = slot;
                    return new UnwindOp(op, list, slot);
                }
            case WithClause w:
                {
                    var (o, newScope, _) = PlanProjection(op, scope, w.Projection, null);
                    scope.Clear();
                    foreach (var kv in newScope) scope[kv.Key] = kv.Value;
                    if (w.Where != null) o = Filter(o, Compile(w.Where, scope), w.Where.Text);
                    return o;
                }
            case CreateClause c:
                {
                    MarkWrite();
                    if (_varExpandSinceBarrier) op = Barrier(op);
                    var spec = BuildCreateSpec(c.Pattern, new HashSet<string>(scope.Keys), scope, false);
                    return new CreateOp(op, spec);
                }
            case MergeClause mc:
                return PlanMerge(op, scope, mc);
            case SetClause sc:
                MarkWrite();
                return new SetOp(op, sc.Items.Select(it => CompileSetItem(it, scope)).ToArray(), false);
            case RemoveClause rc:
                MarkWrite();
                return new SetOp(op, rc.Items.Select(it => CompileSetItem(it, scope)).ToArray(), true);
            case DeleteClause dc:
                {
                    MarkWrite();
                    if (_readsSinceBarrier) op = Barrier(op);
                    return new DeleteOp(op, dc.Exprs.Select(e => Compile(e, scope)).ToArray(), dc.Detach);
                }
            case ForeachClause fc:
                {
                    MarkWrite();
                    var list = Compile(fc.List, scope);
                    var inner = new Dictionary<string, int>(scope, StringComparer.Ordinal);
                    int slot = NewSlot();
                    inner[fc.Variable] = slot;
                    bool r = _readsSinceBarrier, v = _varExpandSinceBarrier;
                    Operator body = new StartOp();
                    foreach (var c in fc.Body) body = PlanClause(body, inner, c);
                    _readsSinceBarrier = r;
                    _varExpandSinceBarrier = v;
                    if (_readsSinceBarrier) op = Barrier(op);
                    return new ForeachOp(op, slot, list, body);
                }
            case CallClause cc:
                {
                    var proc = Procedures.Find(cc.Procedure) ?? throw new CypherSemanticException($"Unknown procedure '{cc.Procedure}'");
                    var args = (cc.Args ?? []).Select(a => Compile(a, scope)).ToArray();
                    var outSlots = new int[proc.Outputs.Length];
                    Array.Fill(outSlots, -1);
                    var yields = cc.Yields ?? proc.Outputs.Select(o => (o, o)).ToList();
                    foreach (var (field, alias) in yields)
                    {
                        int idx = Array.FindIndex(proc.Outputs, o => o.Equals(field, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0) throw new CypherSemanticException($"Procedure {proc.Name} has no output '{field}'");
                        int slot = NewSlot();
                        outSlots[idx] = slot;
                        scope[alias] = slot;
                    }
                    op = new ProcedureCallOp(op, proc, args, outSlots);
                    if (cc.Where != null) op = Filter(op, Compile(cc.Where, scope), cc.Where.Text);
                    return op;
                }
            case ReturnClause:
                throw new CypherSemanticException("RETURN can only be used at the end of the query");
        }
        throw new CypherSemanticException($"Unsupported clause {clause.GetType().Name}");
    }

    private void MarkWrite()
    {
        _isWrite = true;
        _writesSinceBarrier = true;
    }

    private Operator Barrier(Operator op)
    {
        _readsSinceBarrier = false;
        _varExpandSinceBarrier = false;
        _writesSinceBarrier = false;
        return new EagerOp(op);
    }

    private static Operator Filter(Operator op, Eval pred, string text) => new FilterOp(op, pred, text);

    // ================================================================== projection

    private (Operator, Dictionary<string, int>, string[]) PlanProjection(Operator op, Dictionary<string, int> scope, Projection proj, int[]? fixedSlots)
    {
        var items = new List<ReturnItem>();
        if (proj.Star)
        {
            foreach (var name in scope.Keys.Where(k => !IsAnon(k)).Order(StringComparer.Ordinal))
                items.Add(new ReturnItem(new VarExpr(name) { Text = name }, null, name));
            if (items.Count == 0 && proj.Items.Count == 0)
                throw new CypherSemanticException("RETURN * is not allowed when there are no variables in scope");
        }
        items.AddRange(proj.Items);
        var dup = items.GroupBy(i => i.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new CypherSemanticException($"Multiple result columns with the same name '{dup.Key}'");
        if (fixedSlots != null && fixedSlots.Length != items.Count)
            throw new CypherSemanticException("All sub queries in an UNION must have the same number of return columns");

        var newScope = new Dictionary<string, int>(StringComparer.Ordinal);
        var columns = items.Select(i => i.Name).ToArray();
        bool hasAgg = items.Any(i => ContainsAggregate(i.Expr));
        var oldScope = new CompileScope(scope);
        CompileScope orderScope;

        if (!hasAgg)
        {
            var pSlots = new List<int>();
            var pEvals = new List<Eval>();
            var textSlots = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                int slot;
                if (fixedSlots is null && it.Expr is VarExpr v && (it.Alias is null || it.Alias == v.Name))
                {
                    slot = Slot(v.Name, oldScope);
                }
                else
                {
                    slot = fixedSlots?[i] ?? NewSlot();
                    pSlots.Add(slot);
                    pEvals.Add(Compile(it.Expr, oldScope));
                }
                newScope[it.Name] = slot;
                textSlots.TryAdd(it.Expr.Text, slot);
            }
            if (pSlots.Count > 0) op = new ProjectionOp(op, pSlots.ToArray(), pEvals.ToArray(), string.Join(", ", columns));
            if (proj.Distinct)
            {
                op = new DistinctOp(op, items.Select(i => newScope[i.Name]).ToArray());
                orderScope = new CompileScope(newScope, textSlots);
            }
            else
            {
                var merged = new Dictionary<string, int>(scope, StringComparer.Ordinal);
                foreach (var kv in newScope) merged[kv.Key] = kv.Value;
                orderScope = new CompileScope(merged);
            }
        }
        else
        {
            // ---- aggregation: grouping keys are the items without aggregates
            var keySlots = new List<int>();
            var keyEvals = new List<Eval>();
            var textSlots = new Dictionary<string, int>(StringComparer.Ordinal);
            var aggs = new List<AggSpec>();
            var aggByText = new Dictionary<string, int>(StringComparer.Ordinal);

            void AddAggregates(Expr e)
            {
                var found = new List<Expr>();
                CollectAggregates(e, found);
                foreach (var a in found)
                {
                    if (aggByText.ContainsKey(a.Text)) continue;
                    int slot = NewSlot();
                    aggByText[a.Text] = slot;
                    textSlots[a.Text] = slot;
                    aggs.Add(MakeAgg(a, oldScope, slot));
                }
            }

            var postSlots = new List<int>();
            var postItems = new List<(int Index, Expr Expr)>();
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (ContainsAggregate(it.Expr))
                {
                    AddAggregates(it.Expr);
                    postItems.Add((i, it.Expr));
                    continue;
                }
                int slot;
                if (fixedSlots is null && it.Expr is VarExpr v && (it.Alias is null || it.Alias == v.Name)) slot = Slot(v.Name, oldScope);
                else slot = fixedSlots?[i] ?? NewSlot();
                keySlots.Add(slot);
                keyEvals.Add(Compile(it.Expr, oldScope));
                newScope[it.Name] = slot;
                textSlots.TryAdd(it.Expr.Text, slot);
            }
            if (proj.OrderBy != null)
                foreach (var s in proj.OrderBy) AddAggregates(s.Expr);

            op = new AggregateOp(op, keySlots.ToArray(), keyEvals.ToArray(), aggs.ToArray());
            _readsSinceBarrier = false;
            _varExpandSinceBarrier = false;
            _writesSinceBarrier = false;

            // grouping variables remain visible (first-row snapshot) for post-aggregation expressions
            var postVars = new Dictionary<string, int>(scope, StringComparer.Ordinal);
            foreach (var kv in newScope) postVars[kv.Key] = kv.Value;
            var postScope = new CompileScope(postVars, textSlots);
            var pSlots = new List<int>();
            var pEvals = new List<Eval>();
            foreach (var (index, expr) in postItems)
            {
                var it = items[index];
                int slot;
                if (fixedSlots is null && aggByText.TryGetValue(expr.Text, out var direct)) slot = direct;
                else
                {
                    slot = fixedSlots?[index] ?? NewSlot();
                    pSlots.Add(slot);
                    pEvals.Add(Compile(expr, postScope));
                }
                newScope[it.Name] = slot;
                textSlots.TryAdd(expr.Text, slot);
            }
            if (pSlots.Count > 0) op = new ProjectionOp(op, pSlots.ToArray(), pEvals.ToArray(), string.Join(", ", columns));
            if (proj.Distinct) op = new DistinctOp(op, items.Select(i => newScope[i.Name]).ToArray());
            var orderVars = new Dictionary<string, int>(newScope, StringComparer.Ordinal);
            orderScope = new CompileScope(orderVars, textSlots);
        }

        // ---- ORDER BY / SKIP / LIMIT
        var empty = new CompileScope(new Dictionary<string, int>());
        Eval? skip = proj.Skip is null ? null : Compile(proj.Skip, empty);
        Eval? limit = proj.Limit is null ? null : Compile(proj.Limit, empty);
        if (proj.OrderBy != null)
        {
            var keys = proj.OrderBy.Select(s => new SortKey(Compile(s.Expr, orderScope), s.Descending)).ToArray();
            Eval? top = null;
            if (limit != null)
            {
                var l = limit;
                var sk = skip;
                top = sk is null ? l : ctx => ExprOps.Add(l(ctx), sk(ctx));
            }
            op = new SortOp(op, keys, top);
            _readsSinceBarrier = false;
            _varExpandSinceBarrier = false;
            _writesSinceBarrier = false;
        }
        if (skip != null) op = new SkipOp(op, skip);
        if (limit != null) op = new LimitOp(op, limit);
        return (op, newScope, columns);
    }

    private AggSpec MakeAgg(Expr e, CompileScope scope, int slot)
    {
        if (e is CountStarExpr) return new AggSpec(AggKind.CountStar, null, null, false, slot, e.Text);
        var f = (FuncExpr)e;
        var kind = AggSpec.Parse(f.Name)!.Value;
        foreach (var a in f.Args)
            if (ContainsAggregate(a)) throw new CypherSemanticException("Cannot nest aggregate functions");
        bool percentile = kind is AggKind.PercentileCont or AggKind.PercentileDisc;
        int expected = percentile ? 2 : 1;
        if (f.Args.Count != expected) throw new CypherSemanticException($"{f.Name}() expects {expected} argument(s)");
        var arg = Compile(f.Args[0], scope);
        var param = percentile ? Compile(f.Args[1], scope) : null;
        return new AggSpec(kind, arg, param, f.Distinct, slot, e.Text);
    }

    // ================================================================== MATCH

    private Operator PlanMatch(Operator op, Dictionary<string, int> scope, MatchClause m)
    {
        if (!m.Optional) return PlanPatternMatch(op, scope, m.Pattern, m.Where, nested: false);
        var inner = new Dictionary<string, int>(scope, StringComparer.Ordinal);
        var innerPlan = PlanPatternMatch(new StartOp(), inner, m.Pattern, m.Where, nested: false);
        var newSlots = new List<int>();
        foreach (var kv in inner)
        {
            if (scope.ContainsKey(kv.Key)) continue;
            scope[kv.Key] = kv.Value;
            newSlots.Add(kv.Value);
        }
        return new OptionalOp(op, innerPlan, newSlots.ToArray());
    }

    private sealed class Pred(Expr expr, HashSet<string> deps)
    {
        public readonly Expr Expr = expr;
        public readonly HashSet<string> Deps = deps;
        public bool Used;
    }

    private sealed class MatchState
    {
        public required Dictionary<string, int> Scope;
        public required List<Pred> Preds;
        public required Dictionary<string, List<string>> Labels;
        public required List<int> ClauseRels;
    }

    private Operator PlanPatternMatch(Operator op, Dictionary<string, int> scope, List<PatternPart> parts, Expr? where, bool nested)
    {
        foreach (var part in parts)
        {
            foreach (var n in part.Nodes) n.Variable ??= Anon();
            foreach (var r in part.Rels) r.Variable ??= Anon();
        }
        var preds = new List<Pred>();
        if (where != null)
            foreach (var c in SplitAnd(where)) preds.Add(new Pred(c, FreeVars(c)));

        var labels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            foreach (var n in part.Nodes)
            {
                if (!labels.TryGetValue(n.Variable!, out var l)) labels[n.Variable!] = l = new List<string>();
                foreach (var x in n.Labels) if (!l.Contains(x)) l.Add(x);
                AddInlineProps(preds, n.Variable!, n.Props);
            }
            foreach (var r in part.Rels)
                if (!r.VarLength) AddInlineProps(preds, r.Variable!, r.Props);
        }
        foreach (var (v, l) in labels)
        {
            if (l.Count == 0 || !scope.ContainsKey(v)) continue;
            var target = new VarExpr(v) { Text = v };
            preds.Add(new Pred(new LabelCheckExpr(target, l.ToList()) { Text = v + ":" + string.Join(":", l) }, [v]));
            l.Clear();
        }

        var st = new MatchState { Scope = scope, Preds = preds, Labels = labels, ClauseRels = new List<int>() };
        var remaining = new List<PatternPart>(parts);
        op = ApplyFilters(op, st);
        while (remaining.Count > 0)
        {
            int pick = remaining.FindIndex(p => p.Nodes.Any(n => scope.ContainsKey(n.Variable!)));
            if (pick < 0)
            {
                double best = double.MaxValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    foreach (var n in remaining[i].Nodes)
                    {
                        var c = ChooseLeaf(n, st).Cost;
                        if (c < best) { best = c; pick = i; }
                    }
                }
            }
            var part = remaining[pick];
            remaining.RemoveAt(pick);
            op = PlanPart(op, part, st);
        }
        var leftovers = preds.Where(p => !p.Used).ToList();
        foreach (var p in leftovers)
        {
            p.Used = true;
            op = Filter(op, Compile(p.Expr, scope), p.Expr.Text);
        }
        return op;
    }

    private static void AddInlineProps(List<Pred> preds, string variable, Expr? props)
    {
        if (props is null) return;
        if (props is not MapExpr map)
            throw new CypherSemanticException("Parameter maps cannot be used in MATCH patterns; use a WHERE clause instead");
        for (int i = 0; i < map.Keys.Count; i++)
        {
            var target = new VarExpr(variable) { Text = variable };
            var prop = new PropertyExpr(target, map.Keys[i]) { Text = variable + "." + map.Keys[i] };
            var eq = new BinaryExpr(BinOp.Eq, prop, map.Values[i]) { Text = prop.Text + " = " + map.Values[i].Text };
            var deps = FreeVars(map.Values[i]);
            deps.Add(variable);
            preds.Add(new Pred(eq, deps));
        }
    }

    private Operator ApplyFilters(Operator op, MatchState st)
    {
        List<Pred>? ready = null;
        foreach (var p in st.Preds)
        {
            if (p.Used || !p.Deps.All(st.Scope.ContainsKey)) continue;
            p.Used = true;
            (ready ??= new List<Pred>()).Add(p);
        }
        if (ready is null) return op;
        var evals = ready.Select(p => Compile(p.Expr, st.Scope)).ToArray();
        var text = string.Join(" AND ", ready.Select(p => p.Expr.Text));
        if (evals.Length == 1) return Filter(op, evals[0], text);
        return Filter(op, ctx =>
        {
            foreach (var e in evals)
            {
                var v = e(ctx);
                if (v.Kind != ValueKind.Boolean || v.Num == 0) return Value.False;
            }
            return Value.True;
        }, text);
    }

    private enum LeafKind { AllNodes, Label, Index, IndexIn, Id }

    private sealed class Leaf
    {
        public LeafKind Kind;
        public double Cost;
        public Pred? Pred;
        public Expr? Value;
        public string? Label;
        public string? Key;
    }

    private static bool BoundIn(HashSet<string> deps, Dictionary<string, int> scope) => deps.All(scope.ContainsKey);

    private Leaf ChooseLeaf(NodePattern node, MatchState st)
    {
        var v = node.Variable!;
        var labels = st.Labels.TryGetValue(v, out var l) ? l : [];
        var best = new Leaf { Kind = LeafKind.AllNodes, Cost = _store.NodeCount * 1.2 + 10 };
        foreach (var lbl in labels)
        {
            int id = _store.Labels.Lookup(lbl);
            double c = id < 0 ? 0 : _store.LabelCount(id);
            if (c < best.Cost) best = new Leaf { Kind = LeafKind.Label, Cost = c };
        }
        foreach (var p in st.Preds)
        {
            if (p.Used || p.Expr is not BinaryExpr { Op: BinOp.Eq or BinOp.In } be) continue;
            foreach (var (self, other, swapped) in new[] { (be.Left, be.Right, false), (be.Right, be.Left, true) })
            {
                if (be.Op == BinOp.In && swapped) continue;
                var otherDeps = FreeVars(other);
                if (otherDeps.Contains(v) || !BoundIn(otherDeps, st.Scope)) continue;
                if (self is FuncExpr { Name: "id", Args: [VarExpr iv] } && iv.Name == v)
                {
                    double c = be.Op == BinOp.In ? (other is ListExpr le ? le.Items.Count : 10) : 1;
                    if (c < best.Cost) best = new Leaf { Kind = LeafKind.Id, Cost = c, Pred = p, Value = other };
                    continue;
                }
                if (self is PropertyExpr { Target: VarExpr pv } pe && pv.Name == v)
                {
                    int key = _store.PropKeys.Lookup(pe.Key);
                    if (key < 0) continue;
                    foreach (var lbl in labels)
                    {
                        int lid = _store.Labels.Lookup(lbl);
                        if (lid < 0) continue;
                        var ix = _store.FindIndex(lid, key);
                        if (ix is null) continue;
                        double c;
                        if (be.Op == BinOp.In) c = 2 * (other is ListExpr le ? le.Items.Count : 10);
                        else if (ix.Unique) c = 0.5;
                        else if (other is LiteralExpr lit) c = 0.5 + ix.EstimateCount(lit.Value);
                        else c = 1 + Math.Min(50, _store.LabelCount(lid) / 100.0);
                        if (c < best.Cost)
                            best = new Leaf { Kind = be.Op == BinOp.In ? LeafKind.IndexIn : LeafKind.Index, Cost = c, Pred = p, Value = other, Label = lbl, Key = pe.Key };
                    }
                }
            }
        }
        return best;
    }

    private Operator BuildLeaf(Operator op, NodePattern node, MatchState st)
    {
        var v = node.Variable!;
        var leaf = ChooseLeaf(node, st);
        int slot = NewSlot();
        var labelNames = st.Labels.TryGetValue(v, out var l) ? l : [];
        var labels = labelNames.Select(x => new TokenRef(x, TokenKind.Label)).ToArray();
        _readsSinceBarrier = true;
        if (leaf.Pred != null) leaf.Pred.Used = true;
        switch (leaf.Kind)
        {
            case LeafKind.AllNodes:
                op = new AllNodesScanOp(op, slot, v);
                break;
            case LeafKind.Label:
                op = new LabelScanOp(op, slot, v, labels);
                break;
            case LeafKind.Index:
            case LeafKind.IndexIn:
                op = new NodeIndexSeekOp(op, slot, v, labels, new TokenRef(leaf.Label!, TokenKind.Label),
                    new TokenRef(leaf.Key!, TokenKind.PropKey), Compile(leaf.Value!, st.Scope), leaf.Kind == LeafKind.IndexIn);
                break;
            case LeafKind.Id:
                op = new NodeByIdSeekOp(op, slot, v, Compile(leaf.Value!, st.Scope));
                if (labels.Length > 0)
                {
                    var lbls = labels;
                    op = Filter(op, ctx => ExprOps.HasLabels(ctx, ctx.Row[slot], lbls), v + LabelUtil.Format(labels));
                }
                break;
        }
        st.Scope[v] = slot;
        return ApplyFilters(op, st);
    }

    private Operator PlanPart(Operator op, PatternPart part, MatchState st)
    {
        var scope = st.Scope;
        if (part.Shortest != ShortestKind.None) return PlanShortest(op, part, st);
        var nodes = part.Nodes;
        int start = nodes.FindIndex(n => scope.ContainsKey(n.Variable!));
        if (start < 0)
        {
            double best = double.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                var c = ChooseLeaf(nodes[i], st).Cost;
                if (c < best) { best = c; start = i; }
            }
            op = BuildLeaf(op, nodes[start], st);
        }
        for (int i = start; i < nodes.Count - 1; i++) op = PlanStep(op, part.Rels[i], nodes[i], nodes[i + 1], true, st);
        for (int i = start; i > 0; i--) op = PlanStep(op, part.Rels[i - 1], nodes[i], nodes[i - 1], false, st);
        if (part.PathVariable != null)
        {
            int slot = NewSlot();
            op = new BuildPathOp(op, slot, nodes.Select(n => scope[n.Variable!]).ToArray(), part.Rels.Select(r => scope[r.Variable!]).ToArray());
            scope[part.PathVariable] = slot;
            op = ApplyFilters(op, st);
        }
        return op;
    }

    private static Direction Flip(Direction d) => d switch
    {
        Direction.Out => Direction.In,
        Direction.In => Direction.Out,
        _ => Direction.Both,
    };

    private Operator PlanStep(Operator op, RelPattern rel, NodePattern from, NodePattern to, bool forward, MatchState st)
    {
        var scope = st.Scope;
        var dir = forward ? rel.Dir : Flip(rel.Dir);
        int fromSlot = scope[from.Variable!];
        bool toBound = scope.TryGetValue(to.Variable!, out int toSlot);
        if (!toBound) toSlot = NewSlot();
        var types = rel.Types.Select(t => new TokenRef(t, TokenKind.RelType)).ToArray();
        var toLabels = toBound ? [] : (st.Labels.TryGetValue(to.Variable!, out var l) ? l : []).Select(x => new TokenRef(x, TokenKind.Label)).ToArray();
        bool relBound = scope.TryGetValue(rel.Variable!, out int boundRel);
        int relSlot = NewSlot();
        var uniq = st.ClauseRels.ToArray();
        string arrowL = dir == Direction.In ? "<-" : "-", arrowR = dir == Direction.Out ? "->" : "-";
        string typeText = types.Length == 0 ? "" : ":" + string.Join("|", types.Select(t => t.Name));
        string desc = $"({from.Variable}){arrowL}[{(IsAnon(rel.Variable!) ? "" : rel.Variable)}{typeText}{(rel.VarLength ? $"*{rel.Min}..{rel.Max}" : "")}]{arrowR}({to.Variable}{LabelUtil.Format(toLabels)})";
        if (rel.VarLength)
        {
            (TokenRef, Eval)[] filter = [];
            if (rel.Props is MapExpr pm)
                filter = pm.Keys.Select((k, i) => (new TokenRef(k, TokenKind.PropKey), Compile(pm.Values[i], scope))).ToArray();
            else if (rel.Props != null) throw new CypherSemanticException("Parameter maps cannot be used in MATCH patterns");
            int min = rel.Min ?? 1, max = rel.Max ?? -1;
            if (max >= 0 && min > max) throw new CypherSemanticException("Invalid variable length range: min > max");
            op = new VarExpandOp(op, fromSlot, relSlot, toSlot, dir, types, toLabels, toBound, uniq, min, max, filter, !forward, desc);
            _varExpandSinceBarrier = true;
        }
        else
        {
            op = new ExpandOp(op, fromSlot, relSlot, toSlot, dir, types, toLabels, toBound, uniq, desc);
        }
        _readsSinceBarrier = true;
        if (!relBound) st.ClauseRels.Add(relSlot);
        if (!toBound) scope[to.Variable!] = toSlot;
        if (relBound)
        {
            int a = relSlot, b = boundRel;
            op = Filter(op, ctx => ExprOps.Eq(ctx.Row[a], ctx.Row[b]), rel.Variable!);
        }
        else scope[rel.Variable!] = relSlot;
        return ApplyFilters(op, st);
    }

    private Operator PlanShortest(Operator op, PatternPart part, MatchState st)
    {
        var scope = st.Scope;
        var a = part.Nodes[0];
        var b = part.Nodes[1];
        var rel = part.Rels[0];
        if (!scope.ContainsKey(a.Variable!)) op = BuildLeaf(op, a, st);
        if (!scope.ContainsKey(b.Variable!)) op = BuildLeaf(op, b, st);
        int relSlot = NewSlot();
        int pathSlot = part.PathVariable != null ? NewSlot() : -1;
        var types = rel.Types.Select(t => new TokenRef(t, TokenKind.RelType)).ToArray();
        int min = rel.VarLength ? rel.Min ?? 1 : 1;
        int max = rel.VarLength ? rel.Max ?? -1 : 1;
        if (min > 1) throw new CypherSemanticException("shortestPath only supports a minimal length of 0 or 1");
        op = new ShortestPathOp(op, scope[a.Variable!], scope[b.Variable!], relSlot, pathSlot, rel.Dir, types, min, max,
            part.Shortest == ShortestKind.AllShortest);
        _readsSinceBarrier = true;
        _varExpandSinceBarrier = true;
        scope[rel.Variable!] = relSlot;
        if (part.PathVariable != null) scope[part.PathVariable] = pathSlot;
        return ApplyFilters(op, st);
    }

    // ================================================================== updates

    private CreateSpec BuildCreateSpec(List<PatternPart> parts, HashSet<string> boundBefore, Dictionary<string, int> scope, bool merge)
    {
        var nodes = new List<CreateNodeSpec>();
        var rels = new List<CreateRelSpec>();
        var paths = new List<PathSpec>();
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (part.Shortest != ShortestKind.None) throw new CypherSemanticException("shortestPath cannot be used in CREATE/MERGE");
            foreach (var n in part.Nodes)
            {
                var name = n.Variable ??= Anon();
                if (boundBefore.Contains(name))
                {
                    if (!merge && (n.Labels.Count > 0 || n.Props != null))
                        throw new CypherSemanticException($"Can't create node `{name}` with labels or properties here. The variable is already declared in this context");
                    continue;
                }
                if (created.Contains(name))
                {
                    if (n.Labels.Count > 0 || n.Props != null)
                        throw new CypherSemanticException($"Can't create node `{name}` twice with labels or properties");
                    continue;
                }
                created.Add(name);
                if (!scope.TryGetValue(name, out var slot)) scope[name] = slot = NewSlot();
                var props = n.Props is null ? null : CompileProps(n.Props, scope);
                nodes.Add(new CreateNodeSpec(slot, n.Labels.Distinct().Select(x => new TokenRef(x, TokenKind.Label)).ToArray(), props));
            }
            for (int i = 0; i < part.Rels.Count; i++)
            {
                var r = part.Rels[i];
                var name = r.Variable ??= Anon();
                if (boundBefore.Contains(name))
                {
                    if (merge) continue;
                    throw new CypherSemanticException($"Can't create relationship `{name}`: the variable is already declared in this context");
                }
                if (r.VarLength) throw new CypherSemanticException("Variable length relationships cannot be used in CREATE/MERGE");
                if (r.Types.Count != 1)
                    throw new CypherSemanticException("A single relationship type must be specified for CREATE/MERGE");
                if (r.Dir == Direction.Both && !merge)
                    throw new CypherSemanticException("Only directed relationships are supported in CREATE");
                int fromSlot = scope[part.Nodes[i].Variable!];
                int toSlot = scope[part.Nodes[i + 1].Variable!];
                if (r.Dir == Direction.In) (fromSlot, toSlot) = (toSlot, fromSlot);
                if (!scope.TryGetValue(name, out var slot)) scope[name] = slot = NewSlot();
                var props = r.Props is null ? null : CompileProps(r.Props, scope);
                rels.Add(new CreateRelSpec(slot, fromSlot, toSlot, new TokenRef(r.Types[0], TokenKind.RelType), props));
            }
            if (part.PathVariable != null)
            {
                if (!scope.TryGetValue(part.PathVariable, out var ps)) scope[part.PathVariable] = ps = NewSlot();
                paths.Add(new PathSpec(ps, part.Nodes.Select(n => scope[n.Variable!]).ToArray(), part.Rels.Select(x => scope[x.Variable!]).ToArray()));
            }
        }
        return new CreateSpec(nodes.ToArray(), rels.ToArray(), paths.ToArray());
    }

    /// <summary>Literal maps compile straight into a PropertyMap builder (no intermediate dictionary per row).</summary>
    private PropsBuilder CompileProps(Expr props, Dictionary<string, int> scope)
    {
        if (props is MapExpr m)
        {
            var keys = m.Keys.Select(k => new TokenRef(k, TokenKind.PropKey)).ToArray();
            var vals = m.Values.Select(v => Compile(v, scope)).ToArray();
            return ctx =>
            {
                PropertyMap? pm = null;
                for (int i = 0; i < keys.Length; i++)
                {
                    var v = vals[i](ctx);
                    if (v.IsNull) continue;
                    GraphStore.ValidatePropertyValue(v);
                    (pm ??= new PropertyMap(keys.Length)).Set(keys[i].GetOrCreate(ctx.Store), v);
                }
                return pm;
            };
        }
        var e = Compile(props, scope);
        return ctx => Mutations.BuildProps(ctx, e(ctx));
    }

    private Operator PlanMerge(Operator op, Dictionary<string, int> scope, MergeClause mc)
    {
        MarkWrite();
        if (_varExpandSinceBarrier) op = Barrier(op);
        var part = mc.Part;
        foreach (var n in part.Nodes)
            if (n.Props != null && n.Props is not MapExpr) throw new CypherSemanticException("Parameter maps cannot be used in MERGE patterns; use {key: $value}");
        var boundBefore = new HashSet<string>(scope.Keys, StringComparer.Ordinal);
        var inner = new Dictionary<string, int>(scope, StringComparer.Ordinal);
        bool r = _readsSinceBarrier, v = _varExpandSinceBarrier;
        var match = PlanPatternMatch(new StartOp(), inner, [part], null, nested: true);
        _readsSinceBarrier = r;
        _varExpandSinceBarrier = v;
        var spec = BuildCreateSpec([part], boundBefore, inner, merge: true);
        foreach (var kv in inner) scope[kv.Key] = kv.Value;
        var onCreate = mc.OnCreate.Select(it => CompileSetItem(it, scope)).ToArray();
        var onMatch = mc.OnMatch.Select(it => CompileSetItem(it, scope)).ToArray();
        return new MergeOp(op, match, spec, onCreate, onMatch);
    }

    private CompiledSetItem CompileSetItem(SetItem it, Dictionary<string, int> scope)
    {
        var target = Compile(it.Target, scope);
        return it.Kind switch
        {
            SetKind.Property => new CompiledSetItem(it.Kind, target, new TokenRef(it.Key!, TokenKind.PropKey),
                it.Value is null ? null : Compile(it.Value, scope), null),
            SetKind.Labels => new CompiledSetItem(it.Kind, target, null, null, it.Labels!.Select(l => new TokenRef(l, TokenKind.Label)).ToArray()),
            _ => new CompiledSetItem(it.Kind, target, null, Compile(it.Value!, scope), null),
        };
    }
}
