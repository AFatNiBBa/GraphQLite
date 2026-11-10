using GraphDb.Cypher;
using System.Runtime.CompilerServices;
using GraphDb.Storage;

namespace GraphDb.Runtime;

internal static class LabelUtil
{
    public static int[]? Resolve(TokenRef[] labels, GraphStore store, out bool impossible)
    {
        impossible = false;
        if (labels.Length == 0) return null;
        var ids = new int[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            ids[i] = labels[i].Resolve(store);
            if (ids[i] < 0) impossible = true;
        }
        return ids;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasAll(ref NodeRecord n, int[]? labels)
    {
        if (labels is null) return true;
        foreach (var l in labels) if (!n.HasLabel(l)) return false;
        return true;
    }

    public static string Format(TokenRef[] labels) => string.Concat(labels.Select(l => ":" + l.Name));
}

// ====================================================================== scans

internal sealed class AllNodesScanOp(Operator input, int slot, string variable) : UnaryOperator(input)
{
    public override string Name => "AllNodesScan";
    public override string Details => variable;
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, slot);

    private sealed class C(Cursor input, ExecContext ctx, int slot) : Cursor
    {
        private int _i, _high;
        private bool _active;

        public override bool Next()
        {
            var store = ctx.Store;
            while (true)
            {
                if (!_active)
                {
                    if (!input.Next()) return false;
                    _i = 0;
                    _high = store.NodeHigh;
                    _active = true;
                }
                var nodes = store.Nodes;
                while (_i < _high)
                {
                    int id = _i++;
                    if (nodes[id].Alive)
                    {
                        ctx.CheckCancel();
                        ctx.Row[slot] = Value.FromNodeId(id);
                        return true;
                    }
                }
                _active = false;
            }
        }
    }
}

internal sealed class LabelScanOp(Operator input, int slot, string variable, TokenRef[] labels) : UnaryOperator(input)
{
    public override string Name => "NodeByLabelScan";
    public override string Details => variable + LabelUtil.Format(labels);
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, slot, labels);

    private sealed class C : Cursor
    {
        private readonly Cursor _input;
        private readonly ExecContext _ctx;
        private readonly int _slot;
        private readonly TokenRef[] _labels;
        private Bitset? _bits;
        private int[]? _others;
        private int _next, _high;
        private bool _active;

        public C(Cursor input, ExecContext ctx, int slot, TokenRef[] labels)
        {
            _input = input;
            _ctx = ctx;
            _slot = slot;
            _labels = labels;
        }

        /// <summary>Resolved lazily: labels may be created by earlier clauses of the same query.</summary>
        private void Resolve()
        {
            var ids = LabelUtil.Resolve(_labels, _ctx.Store, out bool impossible);
            if (impossible || ids is null) return;
            // drive the scan from the smallest label, check the others
            int best = 0;
            for (int i = 1; i < ids.Length; i++)
                if (_ctx.Store.LabelCount(ids[i]) < _ctx.Store.LabelCount(ids[best])) best = i;
            _bits = _ctx.Store.LabelBits(ids[best]);
            if (ids.Length > 1) _others = ids.Where((_, i) => i != best).ToArray();
        }

        public override bool Next()
        {
            var store = _ctx.Store;
            while (true)
            {
                if (!_active)
                {
                    if (!_input.Next()) return false;
                    if (_bits is null)
                    {
                        Resolve();
                        if (_bits is null) continue;
                    }
                    _next = 0;
                    _high = store.NodeHigh;
                    _active = true;
                }
                while (true)
                {
                    int id = _bits!.NextSetBit(_next);
                    if (id < 0 || id >= _high) break;
                    _next = id + 1;
                    if (_others != null && !LabelUtil.HasAll(ref store.Nodes[id], _others)) continue;
                    _ctx.CheckCancel();
                    _ctx.Row[_slot] = Value.FromNodeId(id);
                    return true;
                }
                _active = false;
            }
        }
    }
}

/// <summary>Seeks nodes through a (label, property) hash index; value may be a single value or a list (IN).</summary>
internal sealed class NodeIndexSeekOp(Operator input, int slot, string variable, TokenRef[] labels, TokenRef label, TokenRef key, Eval value, bool isList)
    : UnaryOperator(input)
{
    private readonly int slot = slot;
    private readonly TokenRef[] labels = labels;
    private readonly TokenRef label = label;
    private readonly TokenRef key = key;
    private readonly Eval value = value;
    private readonly bool isList = isList;
    public override string Name => isList ? "NodeIndexSeek(IN)" : "NodeIndexSeek";
    public override string Details => $"{variable}{LabelUtil.Format(labels)}({key.Name})";

    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C(NodeIndexSeekOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private readonly List<int> _ids = new();
        private int _pos;
        private int[]? _labels;
        private bool _resolved, _impossible;

        public override bool Next()
        {
            var store = ctx.Store;
            while (true)
            {
                while (_pos < _ids.Count)
                {
                    int id = _ids[_pos++];
                    if (!store.NodeAlive(id) || !LabelUtil.HasAll(ref store.Nodes[id], _labels)) continue;
                    ctx.Row[op.slot] = Value.FromNodeId(id);
                    return true;
                }
                if (!input.Next()) return false;
                _ids.Clear();
                _pos = 0;
                if (!_resolved)
                {
                    _labels = LabelUtil.Resolve(op.labels, store, out _impossible);
                    if (_impossible) continue;
                    _resolved = true;
                }
                var ix = store.FindIndex(op.label.Resolve(store), op.key.Resolve(store));
                var v = op.value(ctx);
                if (ix is null) continue;
                if (!op.isList) ix.Lookup(v, _ids);
                else if (v.Kind == ValueKind.List)
                {
                    var items = v.ListItems;
                    foreach (var x in items) ix.Lookup(x, _ids);
                    if (items.Length > 1 && _ids.Count > 1)
                    {
                        var distinct = new HashSet<int>(_ids);
                        _ids.Clear();
                        _ids.AddRange(distinct);
                    }
                }
            }
        }
    }
}

internal sealed class NodeByIdSeekOp(Operator input, int slot, string variable, Eval ids) : UnaryOperator(input)
{
    public override string Name => "NodeByIdSeek";
    public override string Details => variable;
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, slot, ids);

    private sealed class C(Cursor input, ExecContext ctx, int slot, Eval ids) : Cursor
    {
        private Value[]? _list;
        private int _pos;

        public override bool Next()
        {
            while (true)
            {
                if (_list != null)
                {
                    while (_pos < _list.Length)
                    {
                        var v = _list[_pos++];
                        if (v.Kind != ValueKind.Integer || v.Num < 0 || v.Num > int.MaxValue) continue;
                        if (!ctx.Store.NodeAlive((int)v.Num)) continue;
                        ctx.Row[slot] = Value.FromNodeId((int)v.Num);
                        return true;
                    }
                }
                if (!input.Next()) return false;
                var x = ids(ctx);
                _list = x.Kind == ValueKind.List ? x.ListItems : [x];
                _pos = 0;
            }
        }
    }
}

// ====================================================================== expand

internal static class RelCheck
{
    /// <summary>Relationship-isomorphism check against relationships already bound in the same MATCH.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsUsed(Value[] row, int[] slots, int rel)
    {
        foreach (var s in slots)
        {
            var v = row[s];
            if (v.Kind == ValueKind.Relationship)
            {
                if (v.Num == rel) return true;
            }
            else if (v.Kind == ValueKind.List)
            {
                foreach (var x in v.ListItems) if (x.Num == rel && x.Kind == ValueKind.Relationship) return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TypeOk(int[]? types, int type)
    {
        if (types is null) return true;
        foreach (var t in types) if (t == type) return true;
        return false;
    }

    public static bool AllResolved(TokenRef[] tokens, GraphStore store)
    {
        foreach (var t in tokens) if (t.Resolve(store) < 0) return false;
        return true;
    }

    public static int[]? ResolveTypes(TokenRef[] types, GraphStore store, out bool impossible)
    {
        impossible = false;
        if (types.Length == 0) return null;
        var list = new List<int>(types.Length);
        foreach (var t in types)
        {
            int id = t.Resolve(store);
            if (id >= 0) list.Add(id);
        }
        if (list.Count == 0) impossible = true;
        return list.ToArray();
    }
}

/// <summary>Single-hop expansion. With <c>into</c> the target node is already bound and only checked.</summary>
internal sealed class ExpandOp(Operator input, int fromSlot, int relSlot, int toSlot, Direction dir, TokenRef[] types,
    TokenRef[] toLabels, bool into, int[] uniqueSlots, string desc) : UnaryOperator(input)
{
    private readonly int fromSlot = fromSlot;
    private readonly int relSlot = relSlot;
    private readonly int toSlot = toSlot;
    private readonly Direction dir = dir;
    private readonly TokenRef[] types = types;
    private readonly TokenRef[] toLabels = toLabels;
    private readonly bool into = into;
    private readonly int[] uniqueSlots = uniqueSlots;
    public override string Name => into ? "ExpandInto" : "Expand";
    public override string Details => desc;
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C : Cursor
    {
        private readonly ExpandOp _op;
        private readonly Cursor _input;
        private readonly ExecContext _ctx;
        private int[]? _types;
        private int[]? _labels;
        private bool _impossible, _complete;
        private AdjEntry[]? _items;
        private int _count, _idx, _from, _target;
        private byte _phase; // 0 = out, 1 = in
        private bool _active;

        public C(ExpandOp op, Cursor input, ExecContext ctx)
        {
            _op = op;
            _input = input;
            _ctx = ctx;
        }

        private void Resolve()
        {
            _types = RelCheck.ResolveTypes(_op.types, _ctx.Store, out bool i1);
            _labels = LabelUtil.Resolve(_op.toLabels, _ctx.Store, out bool i2);
            _impossible = i1 || i2;
            _complete = RelCheck.AllResolved(_op.types, _ctx.Store) && !i2;
        }

        private void Load()
        {
            ref var n = ref _ctx.Store.Nodes[_from];
            ref var list = ref _phase == 0 ? ref n.Out : ref n.In;
            _items = list.Items;
            _count = list.Count;
            _idx = 0;
        }

        public override bool Next()
        {
            var op = _op;
            var row = _ctx.Row;
            var store = _ctx.Store;
            while (true)
            {
                if (!_active)
                {
                    if (!_input.Next()) return false;
                    if (!_complete)
                    {
                        Resolve();
                        if (_impossible) continue;
                    }
                    var from = row[op.fromSlot];
                    if (from.Kind != ValueKind.Node || !store.NodeAlive(from.EntityId)) continue;
                    _from = from.EntityId;
                    if (op.into)
                    {
                        var to = row[op.toSlot];
                        if (to.Kind != ValueKind.Node) continue;
                        _target = to.EntityId;
                    }
                    _phase = op.dir == Direction.In ? (byte)1 : (byte)0;
                    Load();
                    _active = true;
                }
                var items = _items;
                while (_idx < _count)
                {
                    ref readonly var e = ref items![_idx++];
                    if (_types != null && !RelCheck.TypeOk(_types, e.Type)) continue;
                    if (_phase == 1 && op.dir == Direction.Both && e.Other == _from) continue; // self loop already seen
                    if (op.into)
                    {
                        if (e.Other != _target) continue;
                    }
                    else if (_labels != null && !LabelUtil.HasAll(ref store.Nodes[e.Other], _labels)) continue;
                    if (op.uniqueSlots.Length > 0 && RelCheck.IsUsed(row, op.uniqueSlots, e.Rel)) continue;
                    row[op.relSlot] = Value.FromRelId(e.Rel);
                    if (!op.into) row[op.toSlot] = Value.FromNodeId(e.Other);
                    _ctx.CheckCancel();
                    return true;
                }
                if (_phase == 0 && op.dir == Direction.Both)
                {
                    _phase = 1;
                    Load();
                    continue;
                }
                _active = false;
            }
        }
    }
}

/// <summary>Variable-length expansion (DFS, relationship-unique trails).</summary>
internal sealed class VarExpandOp(Operator input, int fromSlot, int relSlot, int toSlot, Direction dir, TokenRef[] types,
    TokenRef[] toLabels, bool into, int[] uniqueSlots, int min, int max, (TokenRef Key, Eval Value)[] relFilter, bool reverse, string desc)
    : UnaryOperator(input)
{
    private readonly int fromSlot = fromSlot;
    private readonly int relSlot = relSlot;
    private readonly int toSlot = toSlot;
    private readonly Direction dir = dir;
    private readonly TokenRef[] types = types;
    private readonly TokenRef[] toLabels = toLabels;
    private readonly bool into = into;
    private readonly int[] uniqueSlots = uniqueSlots;
    private readonly int min = min;
    private readonly int max = max;
    private readonly (TokenRef Key, Eval Value)[] relFilter = relFilter;
    private readonly bool reverse = reverse;
    public override string Name => into ? "VarLengthExpandInto" : "VarLengthExpand";
    public override string Details => desc;
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private struct Frame
    {
        public int Node;
        public AdjEntry[]? Items;
        public int Count;
        public int Idx;
        public byte Phase;
    }

    private sealed class C : Cursor
    {
        private readonly VarExpandOp _op;
        private readonly Cursor _input;
        private readonly ExecContext _ctx;
        private int[]? _types;
        private int[]? _labels;
        private bool _impossible, _complete;
        private readonly List<Frame> _stack = new();
        private readonly List<int> _path = new();
        private Value[]? _filterValues;
        private bool _active, _pendingZero;
        private int _from, _target;

        public C(VarExpandOp op, Cursor input, ExecContext ctx)
        {
            _op = op;
            _input = input;
            _ctx = ctx;
        }

        private void Resolve()
        {
            _types = RelCheck.ResolveTypes(_op.types, _ctx.Store, out bool i1);
            _labels = LabelUtil.Resolve(_op.toLabels, _ctx.Store, out bool i2);
            _impossible = (i1 && _op.min > 0) || i2;
            _complete = RelCheck.AllResolved(_op.types, _ctx.Store) && !i2;
        }

        private Frame MakeFrame(int node)
        {
            var f = new Frame { Node = node, Phase = _op.dir == Direction.In ? (byte)1 : (byte)0 };
            LoadFrame(ref f);
            return f;
        }

        private void LoadFrame(ref Frame f)
        {
            ref var n = ref _ctx.Store.Nodes[f.Node];
            ref var list = ref f.Phase == 0 ? ref n.Out : ref n.In;
            f.Items = list.Items;
            f.Count = list.Count;
            f.Idx = 0;
        }

        private bool TargetOk(int node)
        {
            if (_op.into) return node == _target;
            return _labels is null || LabelUtil.HasAll(ref _ctx.Store.Nodes[node], _labels);
        }

        private bool RelOk(int rel)
        {
            var f = _op.relFilter;
            if (f.Length == 0) return true;
            var props = _ctx.Store.Rels[rel].Props;
            for (int i = 0; i < f.Length; i++)
            {
                int key = f[i].Key.Resolve(_ctx.Store);
                var actual = key >= 0 && props != null ? props.Get(key) : Value.Null;
                if (!ValueOps.IsTrue(ValueOps.Equal(actual, _filterValues![i]))) return false;
            }
            return true;
        }

        private void Emit(int node)
        {
            var arr = new Value[_path.Count];
            if (_op.reverse)
                for (int i = 0; i < arr.Length; i++) arr[i] = Value.FromRelId(_path[_path.Count - 1 - i]);
            else
                for (int i = 0; i < arr.Length; i++) arr[i] = Value.FromRelId(_path[i]);
            _ctx.Row[_op.relSlot] = Value.FromList(arr);
            if (!_op.into) _ctx.Row[_op.toSlot] = Value.FromNodeId(node);
        }

        public override bool Next()
        {
            var op = _op;
            var row = _ctx.Row;
            while (true)
            {
                if (!_active)
                {
                    if (!_input.Next()) return false;
                    if (!_complete)
                    {
                        Resolve();
                        if (_impossible) continue;
                    }
                    var from = row[op.fromSlot];
                    if (from.Kind != ValueKind.Node || !_ctx.Store.NodeAlive(from.EntityId)) continue;
                    _from = from.EntityId;
                    if (op.into)
                    {
                        var to = row[op.toSlot];
                        if (to.Kind != ValueKind.Node) continue;
                        _target = to.EntityId;
                    }
                    if (op.relFilter.Length > 0)
                    {
                        _filterValues ??= new Value[op.relFilter.Length];
                        for (int i = 0; i < op.relFilter.Length; i++) _filterValues[i] = op.relFilter[i].Value(_ctx);
                    }
                    _stack.Clear();
                    _path.Clear();
                    if (op.max != 0 && !(_types is { Length: 0 })) _stack.Add(MakeFrame(_from));
                    _pendingZero = op.min == 0;
                    _active = true;
                }
                if (_pendingZero)
                {
                    _pendingZero = false;
                    if (TargetOk(_from))
                    {
                        Emit(_from);
                        return true;
                    }
                }
                while (_stack.Count > 0)
                {
                    _ctx.CheckCancel();
                    int top = _stack.Count - 1;
                    var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_stack);
                    ref var f = ref span[top];
                    if (f.Idx >= f.Count)
                    {
                        if (f.Phase == 0 && op.dir == Direction.Both)
                        {
                            f.Phase = 1;
                            LoadFrame(ref f);
                            continue;
                        }
                        _stack.RemoveAt(top);
                        if (_path.Count > 0 && top > 0) _path.RemoveAt(_path.Count - 1);
                        continue;
                    }
                    var e = f.Items![f.Idx++];
                    if (_types != null && !RelCheck.TypeOk(_types, e.Type)) continue;
                    if (f.Phase == 1 && op.dir == Direction.Both && e.Other == f.Node) continue;
                    if (_path.Contains(e.Rel)) continue;
                    if (op.uniqueSlots.Length > 0 && RelCheck.IsUsed(row, op.uniqueSlots, e.Rel)) continue;
                    if (!RelOk(e.Rel)) continue;
                    _path.Add(e.Rel);
                    int depth = _path.Count;
                    bool emit = depth >= op.min && TargetOk(e.Other);
                    if (emit) Emit(e.Other);
                    if (op.max < 0 || depth < op.max) _stack.Add(MakeFrame(e.Other));
                    else _path.RemoveAt(_path.Count - 1);
                    if (emit) return true;
                }
                _active = false;
            }
        }
    }
}

/// <summary>shortestPath / allShortestPaths between two bound nodes (BFS).</summary>
internal sealed class ShortestPathOp(Operator input, int fromSlot, int toSlot, int relSlot, int pathSlot, Direction dir, TokenRef[] types,
    int min, int max, bool all) : UnaryOperator(input)
{
    private readonly int fromSlot = fromSlot;
    private readonly int toSlot = toSlot;
    private readonly int relSlot = relSlot;
    private readonly int pathSlot = pathSlot;
    private readonly Direction dir = dir;
    private readonly TokenRef[] types = types;
    private readonly int min = min;
    private readonly int max = max;
    private readonly bool all = all;
    public override string Name => all ? "AllShortestPaths" : "ShortestPath";
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C(ShortestPathOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private readonly List<(int[] Nodes, int[] Rels)> _results = new();
        private int _pos;
        private int[]? _types;
        private bool _resolved, _impossible;

        public override bool Next()
        {
            while (true)
            {
                if (_pos < _results.Count)
                {
                    var (nodes, rels) = _results[_pos++];
                    var relVals = new Value[rels.Length];
                    for (int i = 0; i < rels.Length; i++) relVals[i] = Value.FromRelId(rels[i]);
                    if (op.relSlot >= 0) ctx.Row[op.relSlot] = Value.FromList(relVals);
                    if (op.pathSlot >= 0) ctx.Row[op.pathSlot] = Value.FromPath(new GraphPath(nodes, rels));
                    return true;
                }
                if (!input.Next()) return false;
                _results.Clear();
                _pos = 0;
                var a = ctx.Row[op.fromSlot];
                var b = ctx.Row[op.toSlot];
                if (a.Kind != ValueKind.Node || b.Kind != ValueKind.Node) continue;
                if (!_resolved)
                {
                    _types = RelCheck.ResolveTypes(op.types, ctx.Store, out _impossible);
                    _resolved = RelCheck.AllResolved(op.types, ctx.Store);
                }
                if (_impossible) continue;
                if (op.all || a.EntityId == b.EntityId) Search(a.EntityId, b.EntityId);
                else SearchBidirectional(a.EntityId, b.EntityId);
            }
        }

        /// <summary>Bidirectional BFS: expands the smaller frontier each level (≈ O(b^(d/2)) instead of O(b^d)).</summary>
        private void SearchBidirectional(int src, int dst)
        {
            var store = ctx.Store;
            var fwd = new Dictionary<int, (int Prev, int Rel)> { [src] = (-1, -1) };
            var bwd = new Dictionary<int, (int Next, int Rel)> { [dst] = (-1, -1) };
            var fFrontier = new List<int> { src };
            var bFrontier = new List<int> { dst };
            var next = new List<int>();
            int maxLen = op.max < 0 ? int.MaxValue : op.max;
            int len = 0;
            int meet = -1;
            var fdir = op.dir;
            var bdir = op.dir switch { Direction.Out => Direction.In, Direction.In => Direction.Out, _ => Direction.Both };
            while (meet < 0 && len < maxLen && fFrontier.Count > 0 && bFrontier.Count > 0)
            {
                len++;
                bool forward = fFrontier.Count <= bFrontier.Count;
                var frontier = forward ? fFrontier : bFrontier;
                var dir = forward ? fdir : bdir;
                next.Clear();
                foreach (var n in frontier)
                {
                    ctx.CheckCancel();
                    ref var rec = ref store.Nodes[n];
                    if (dir != Direction.In && Visit(rec.Out.Span, n, false)) break;
                    if (dir != Direction.Out && Visit(rec.In.Span, n, dir == Direction.Both)) break;
                }
                if (forward) (fFrontier, next) = (next, fFrontier);
                else (bFrontier, next) = (next, bFrontier);

                bool Visit(ReadOnlySpan<AdjEntry> adj, int n, bool skipSelf)
                {
                    foreach (var e in adj)
                    {
                        if (_types != null && !RelCheck.TypeOk(_types, e.Type)) continue;
                        if (skipSelf && e.Other == n) continue;
                        if (forward)
                        {
                            if (!fwd.TryAdd(e.Other, (n, e.Rel))) continue;
                            if (bwd.ContainsKey(e.Other)) { meet = e.Other; return true; }
                        }
                        else
                        {
                            if (!bwd.TryAdd(e.Other, (n, e.Rel))) continue;
                            if (fwd.ContainsKey(e.Other)) { meet = e.Other; return true; }
                        }
                        next.Add(e.Other);
                    }
                    return false;
                }
            }
            if (meet < 0 || len < op.min) return;
            var nodes = new List<int>();
            var rels = new List<int>();
            for (int cur = meet; cur != src;)
            {
                var (prev, rel) = fwd[cur];
                nodes.Add(cur);
                rels.Add(rel);
                cur = prev;
            }
            nodes.Add(src);
            nodes.Reverse();
            rels.Reverse();
            for (int cur = meet; cur != dst;)
            {
                var (nx, rel) = bwd[cur];
                rels.Add(rel);
                nodes.Add(nx);
                cur = nx;
            }
            _results.Add((nodes.ToArray(), rels.ToArray()));
        }

        private void Search(int src, int dst)
        {
            if (src == dst)
            {
                if (op.min == 0) _results.Add(([src], []));
                return;
            }
            var store = ctx.Store;
            // parents: node -> list of (prevNode, rel) at shortest distance
            var parents = new Dictionary<int, List<(int Prev, int Rel)>>();
            var depthOf = new Dictionary<int, int> { [src] = 0 };
            var frontier = new List<int> { src };
            int depth = 0;
            bool found = false;
            int maxDepth = op.max < 0 ? int.MaxValue : op.max;
            while (frontier.Count > 0 && !found && depth < maxDepth)
            {
                depth++;
                var next = new List<int>();
                foreach (var n in frontier)
                {
                    ctx.CheckCancel();
                    ref var rec = ref store.Nodes[n];
                    if (op.dir != Direction.In) Visit(rec.Out.Span, n, false);
                    if (op.dir != Direction.Out) Visit(rec.In.Span, n, op.dir == Direction.Both);
                }
                frontier = next;

                void Visit(ReadOnlySpan<AdjEntry> adj, int n, bool skipSelf)
                {
                    foreach (var e in adj)
                    {
                        if (_types != null && !RelCheck.TypeOk(_types, e.Type)) continue;
                        if (skipSelf && e.Other == n) continue;
                        if (depthOf.TryGetValue(e.Other, out var d))
                        {
                            if (d == depth && op.all) parents[e.Other].Add((n, e.Rel));
                            continue;
                        }
                        depthOf[e.Other] = depth;
                        parents[e.Other] = [(n, e.Rel)];
                        if (e.Other == dst) found = true;
                        next.Add(e.Other);
                    }
                }
            }
            if (!found || depth < op.min) return;
            // reconstruct (all) paths backwards
            var relStack = new List<int>();
            var nodeStack = new List<int> { dst };
            Walk(dst);

            void Walk(int node)
            {
                if (node == src)
                {
                    var nodes = nodeStack.ToArray();
                    var rels = relStack.ToArray();
                    Array.Reverse(nodes);
                    Array.Reverse(rels);
                    _results.Add((nodes, rels));
                    return;
                }
                foreach (var (prev, rel) in parents[node])
                {
                    relStack.Add(rel);
                    nodeStack.Add(prev);
                    Walk(prev);
                    relStack.RemoveAt(relStack.Count - 1);
                    nodeStack.RemoveAt(nodeStack.Count - 1);
                    if (!op.all && _results.Count > 0) return;
                }
            }
        }
    }
}

/// <summary>Builds a path value from the node/relationship slots of a pattern part.</summary>
internal sealed class BuildPathOp(Operator input, int pathSlot, int[] nodeSlots, int[] relSlots) : UnaryOperator(input)
{
    private readonly int pathSlot = pathSlot;
    private readonly int[] nodeSlots = nodeSlots;
    private readonly int[] relSlots = relSlots;
    public override string Name => "ProjectPath";
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    public static Value Build(ExecContext ctx, int[] nodeSlots, int[] relSlots)
    {
        var row = ctx.Row;
        var store = ctx.Store;
        var first = row[nodeSlots[0]];
        if (first.Kind != ValueKind.Node) return Value.Null;
        var nodes = new List<int> { first.EntityId };
        var rels = new List<int>();
        int cur = first.EntityId;
        for (int i = 0; i < relSlots.Length; i++)
        {
            var r = row[relSlots[i]];
            if (r.Kind == ValueKind.Relationship)
            {
                Step(r.EntityId);
            }
            else if (r.Kind == ValueKind.List)
            {
                foreach (var x in r.ListItems) Step(x.EntityId);
            }
            else return Value.Null;
        }
        return Value.FromPath(new GraphPath(nodes.ToArray(), rels.ToArray()));

        void Step(int rel)
        {
            ref var rr = ref store.Rels[rel];
            int other = rr.Src == cur ? rr.Dst : rr.Src;
            rels.Add(rel);
            nodes.Add(other);
            cur = other;
        }
    }

    private sealed class C(BuildPathOp op, Cursor input, ExecContext ctx) : Cursor
    {
        public override bool Next()
        {
            if (!input.Next()) return false;
            ctx.Row[op.pathSlot] = Build(ctx, op.nodeSlots, op.relSlots);
            return true;
        }
    }
}

// ====================================================================== relational operators

internal sealed class FilterOp(Operator input, Eval predicate, string text) : UnaryOperator(input)
{
    public override string Name => "Filter";
    public override string Details => text;
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, predicate);

    private sealed class C(Cursor input, ExecContext ctx, Eval predicate) : Cursor
    {
        public override bool Next()
        {
            while (input.Next())
            {
                var v = predicate(ctx);
                if (v.Kind == ValueKind.Boolean && v.Num != 0) return true;
            }
            return false;
        }
    }
}

internal sealed class ProjectionOp(Operator input, int[] slots, Eval[] evals, string desc) : UnaryOperator(input)
{
    public override string Name => "Projection";
    public override string Details => desc;
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, slots, evals);

    private sealed class C(Cursor input, ExecContext ctx, int[] slots, Eval[] evals) : Cursor
    {
        public override bool Next()
        {
            if (!input.Next()) return false;
            var row = ctx.Row;
            for (int i = 0; i < slots.Length; i++) row[slots[i]] = evals[i](ctx);
            return true;
        }
    }
}

internal sealed class UnwindOp(Operator input, Eval list, int slot) : UnaryOperator(input)
{
    public override string Name => "Unwind";
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, list, slot);

    private sealed class C(Cursor input, ExecContext ctx, Eval list, int slot) : Cursor
    {
        private Value[]? _items;
        private int _pos;

        public override bool Next()
        {
            while (true)
            {
                if (_items != null && _pos < _items.Length)
                {
                    ctx.Row[slot] = _items[_pos++];
                    return true;
                }
                if (!input.Next()) return false;
                var v = list(ctx);
                _pos = 0;
                _items = v.Kind switch
                {
                    ValueKind.List => v.ListItems,
                    ValueKind.Null => null,
                    _ => [v],
                };
            }
        }
    }
}

internal sealed class OptionalOp(Operator input, Operator inner, int[] nullSlots) : UnaryOperator(input)
{
    public override string Name => "Optional";
    public override IEnumerable<Operator> Children => [Input, inner];
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, inner, nullSlots);

    private sealed class C(Cursor input, ExecContext ctx, Operator inner, int[] nullSlots) : Cursor
    {
        private Cursor? _inner;
        private bool _matched;

        public override bool Next()
        {
            while (true)
            {
                if (_inner != null)
                {
                    if (_inner.Next())
                    {
                        _matched = true;
                        return true;
                    }
                    _inner = null;
                    if (!_matched)
                    {
                        foreach (var s in nullSlots) ctx.Row[s] = Value.Null;
                        return true;
                    }
                }
                if (!input.Next()) return false;
                _matched = false;
                _inner = inner.Open(ctx);
            }
        }
    }
}

internal sealed class SkipOp(Operator input, Eval count) : UnaryOperator(input)
{
    public override string Name => "Skip";
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, count);

    private sealed class C(Cursor input, ExecContext ctx, Eval count) : Cursor
    {
        private long _skip = -1;

        public override bool Next()
        {
            if (_skip < 0) _skip = LimitOp.EvalCount(ctx, count, "SKIP");
            while (_skip > 0)
            {
                if (!input.Next()) return false;
                _skip--;
            }
            return input.Next();
        }
    }
}

internal sealed class LimitOp(Operator input, Eval count) : UnaryOperator(input)
{
    public override string Name => "Limit";
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, count);

    public static long EvalCount(ExecContext ctx, Eval e, string what)
    {
        var v = e(ctx);
        if (v.Kind == ValueKind.Float && Math.Floor(v.FloatBits) == v.FloatBits) return (long)v.FloatBits;
        if (v.Kind != ValueKind.Integer || v.Num < 0)
            throw new CypherRuntimeException($"{what} must be a non-negative integer, got {v}");
        return v.Num;
    }

    private sealed class C(Cursor input, ExecContext ctx, Eval count) : Cursor
    {
        private long _left = -1;

        public override bool Next()
        {
            if (_left < 0) _left = EvalCount(ctx, count, "LIMIT");
            if (_left == 0) return false;
            if (!input.Next()) return false;
            _left--;
            return true;
        }
    }
}

internal sealed class DistinctOp(Operator input, int[] slots) : UnaryOperator(input)
{
    public override string Name => "Distinct";
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, slots);

    private sealed class C(Cursor input, ExecContext ctx, int[] slots) : Cursor
    {
        private readonly HashSet<Value[]> _seen = new(ValueEquivalenceComparer.Instance);

        public override bool Next()
        {
            while (input.Next())
            {
                var key = new Value[slots.Length];
                for (int i = 0; i < slots.Length; i++) key[i] = ctx.Row[slots[i]];
                if (_seen.Add(key)) return true;
            }
            return false;
        }
    }
}

/// <summary>Materializes all input rows before producing any (isolates reads from subsequent writes).</summary>
internal sealed class EagerOp(Operator input) : UnaryOperator(input)
{
    public override string Name => "Eager";
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx);

    private sealed class C(Cursor input, ExecContext ctx) : Cursor
    {
        private List<Value[]>? _rows;
        private int _pos;

        public override bool Next()
        {
            if (_rows is null)
            {
                _rows = new List<Value[]>();
                while (input.Next()) _rows.Add(RowOps.Snapshot(ctx.Row));
            }
            if (_pos >= _rows.Count) return false;
            RowOps.Restore(_rows[_pos], ctx.Row);
            _rows[_pos++] = null!;
            return true;
        }
    }
}

internal readonly record struct SortKey(Eval Eval, bool Descending);

internal sealed class SortComparer(SortKey[] keys) : IComparer<(Value[] Keys, Value[] Row, int Seq)>
{
    public int Compare((Value[] Keys, Value[] Row, int Seq) a, (Value[] Keys, Value[] Row, int Seq) b)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            int c = ValueOps.OrderCompare(a.Keys[i], b.Keys[i]);
            if (c != 0) return keys[i].Descending ? -c : c;
        }
        return a.Seq.CompareTo(b.Seq);
    }
}

/// <summary>Full sort, or top-N (heap based) when a limit is known.</summary>
internal sealed class SortOp(Operator input, SortKey[] keys, Eval? limit) : UnaryOperator(input)
{
    private readonly SortKey[] keys = keys;
    private readonly Eval? limit = limit;
    public override string Name => limit is null ? "Sort" : "Top";
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C(SortOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private (Value[] Keys, Value[] Row, int Seq)[]? _rows;
        private int _pos;

        private Value[] Keys()
        {
            var k = new Value[op.keys.Length];
            for (int i = 0; i < k.Length; i++) k[i] = op.keys[i].Eval(ctx);
            return k;
        }

        public override bool Next()
        {
            if (_rows is null)
            {
                var cmp = new SortComparer(op.keys);
                int seq = 0;
                if (op.limit is null)
                {
                    var list = new List<(Value[], Value[], int)>();
                    while (input.Next()) list.Add((Keys(), RowOps.Snapshot(ctx.Row), seq++));
                    _rows = list.ToArray();
                    Array.Sort(_rows, cmp);
                }
                else
                {
                    long n = LimitOp.EvalCount(ctx, op.limit, "LIMIT");
                    // max-heap of the best n rows: the root is the worst kept row
                    var heap = new PriorityQueue<(Value[] Keys, Value[] Row, int Seq), (Value[] Keys, Value[] Row, int Seq)>(
                        Comparer<(Value[], Value[], int)>.Create((a, b) => cmp.Compare(b, a)));
                    while (input.Next())
                    {
                        if (n == 0) continue;
                        var keys = Keys();
                        var cand = (keys, (Value[])null!, seq++);
                        if (heap.Count < n)
                        {
                            cand.Item2 = RowOps.Snapshot(ctx.Row);
                            heap.Enqueue(cand, cand);
                        }
                        else if (cmp.Compare(cand, heap.Peek()) < 0)
                        {
                            cand.Item2 = RowOps.Snapshot(ctx.Row);
                            heap.EnqueueDequeue(cand, cand);
                        }
                    }
                    _rows = new (Value[], Value[], int)[heap.Count];
                    for (int i = _rows.Length - 1; i >= 0; i--) _rows[i] = heap.Dequeue();
                }
            }
            if (_pos >= _rows.Length) return false;
            RowOps.Restore(_rows[_pos].Row, ctx.Row);
            _rows[_pos++] = default;
            return true;
        }
    }
}

internal sealed class UnionOp(Operator[] branches, int[] columnSlots, bool distinct) : Operator
{
    private readonly Operator[] branches = branches;
    private readonly int[] columnSlots = columnSlots;
    private readonly bool distinct = distinct;
    public override string Name => distinct ? "Union" : "UnionAll";
    public override IEnumerable<Operator> Children => branches;
    public override Cursor Open(ExecContext ctx) => new C(this, ctx);

    private sealed class C(UnionOp op, ExecContext ctx) : Cursor
    {
        private int _branch = -1;
        private Cursor? _cur;
        private readonly HashSet<Value[]>? _seen = op.distinct ? new(ValueEquivalenceComparer.Instance) : null;

        public override bool Next()
        {
            while (true)
            {
                if (_cur != null && _cur.Next())
                {
                    if (_seen is null) return true;
                    var key = new Value[op.columnSlots.Length];
                    for (int i = 0; i < key.Length; i++) key[i] = ctx.Row[op.columnSlots[i]];
                    if (_seen.Add(key)) return true;
                    continue;
                }
                if (++_branch >= op.branches.Length) return false;
                _cur = op.branches[_branch].Open(ctx);
            }
        }
    }
}
