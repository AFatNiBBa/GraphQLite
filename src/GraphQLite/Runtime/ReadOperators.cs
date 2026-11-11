using System.Numerics;
using System.Runtime.CompilerServices;
using GraphQLite.Cypher;
using GraphQLite.Storage;

namespace GraphQLite.Runtime;

internal static class LabelUtil
{
    /// <summary>Resolves label tokens; <paramref name="impossible"/> when one of them does not exist yet.</summary>
    public static int[]? Resolve(TokenRef[] labels, StoreTx store, out bool impossible)
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
    public static bool HasAll(StoreTx tx, long node, int[]? labels)
    {
        if (labels is null) return true;
        foreach (var l in labels) if (!tx.HasLabel(node, l)) return false;
        return true;
    }

    public static string Format(TokenRef[] labels) => string.Concat(labels.Select(l => ":" + l.Name));
}

// ====================================================================== scans

internal sealed unsafe class AllNodesScanOp(Operator input, int slot, string variable) : UnaryOperator(input), IPartitionedScan
{
    public override string Name => "AllNodesScan";
    public override bool RowLocal => true;
    public override string Details => variable;
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx, slot);

    public long EstimateRows(StoreTx tx) => tx.NodeCount;

    private sealed class C(AllNodesScanOp op, Cursor input, ExecContext ctx, int slot) : Cursor
    {
        private long _i, _high;
        private bool _active;

        public override bool Next()
        {
            var tx = ctx.Store;
            while (true)
            {
                if (!_active)
                {
                    if (!input.Next()) return false;
                    var (low, high) = ctx.PartitionOf(op);
                    _i = low;
                    _high = Math.Min(high, tx.NodeHigh);
                    _active = true;
                }
                while (_i < _high)
                {
                    long page = _i / NodeRec.PerPage;
                    byte* p = tx.Page(Space.Nodes, page);
                    long end = Math.Min(_high, (page + 1) * NodeRec.PerPage);
                    for (; _i < end; _i++)
                    {
                        if ((((NodeRec*)(p + (int)(_i % NodeRec.PerPage) * NodeRec.Size))->Flags & NodeRec.InUse) == 0) continue;
                        ctx.CheckCancel();
                        ctx.Row[slot] = Value.FromNodeId(_i++);
                        return true;
                    }
                }
                _active = false;
            }
        }
    }
}

/// <summary>Iterates node ids from label bitmaps: AND of <c>all</c> labels, OR of <c>any</c> labels.</summary>
internal sealed unsafe class LabelBitmapIterator
{
    private readonly StoreTx _tx;
    private readonly long[] _allDirs, _anyDirs;
    private readonly long[] _allPages, _anyPages;
    private readonly long _high;
    private long _group;
    private int _word = 1024;
    private ulong _bits;
    private readonly bool _empty;
    private readonly long _firstGroup;
    private readonly int _firstWord;

    /// <summary>Iterates the node ids in [<paramref name="low"/>, <paramref name="high"/>); <paramref name="low"/> must be a multiple of 64.</summary>
    public LabelBitmapIterator(StoreTx tx, int[] all, int[] any, long low = 0, long high = long.MaxValue)
    {
        _tx = tx;
        _high = Math.Min(high, tx.NodeHigh);
        _firstGroup = low >> 16;
        _firstWord = (int)((low & 65535) >> 6);
        _group = _firstGroup - 1;
        _allDirs = all.Select(tx.LabelDir).ToArray();
        _anyDirs = any.Select(tx.LabelDir).ToArray();
        _allPages = new long[_allDirs.Length];
        _anyPages = new long[_anyDirs.Length];
        _empty = (_allDirs.Length == 0 && _anyDirs.Length == 0) || _allDirs.Contains(0) || (_anyDirs.Length > 0 && _anyDirs.All(d => d == 0));
    }

    private bool LoadGroup()
    {
        long maxGroup = (_high - 1) >> 16;
        while (++_group <= maxGroup)
        {
            bool ok = true;
            for (int i = 0; i < _allDirs.Length; i++)
            {
                _allPages[i] = _tx.LabelBitmapPage(_allDirs[i], _group);
                if (_allPages[i] == 0) ok = false;
            }
            bool anyOk = _anyDirs.Length == 0;
            for (int i = 0; i < _anyDirs.Length; i++)
            {
                _anyPages[i] = _anyDirs[i] == 0 ? 0 : _tx.LabelBitmapPage(_anyDirs[i], _group);
                if (_anyPages[i] != 0) anyOk = true;
            }
            if (ok && anyOk)
            {
                _word = _group == _firstGroup ? _firstWord : 0;
                return true;
            }
        }
        return false;
    }

    public bool Next(out long id)
    {
        id = -1;
        if (_empty) return false;
        while (true)
        {
            if (_bits != 0)
            {
                int bit = BitOperations.TrailingZeroCount(_bits);
                _bits &= _bits - 1;
                id = (_group << 16) | ((long)(_word - 1) << 6) | (uint)bit;
                if (id >= _high) return false;
                return true;
            }
            if (_word >= 1024 && !LoadGroup()) return false;
            while (_word < 1024)
            {
                int w = _word++;
                ulong bits = ulong.MaxValue;
                for (int i = 0; i < _allPages.Length; i++) bits &= ((ulong*)_tx.ReadPage(_allPages[i]))[w];
                if (_anyPages.Length > 0)
                {
                    ulong any = 0;
                    for (int i = 0; i < _anyPages.Length; i++)
                        if (_anyPages[i] != 0) any |= ((ulong*)_tx.ReadPage(_anyPages[i]))[w];
                    bits &= any;
                }
                if (bits != 0)
                {
                    _bits = bits;
                    break;
                }
            }
        }
    }
}

/// <summary>Label scan: nodes having all labels of <c>all</c> and at least one of <c>any</c>.</summary>
internal sealed class LabelScanOp(Operator input, int slot, string variable, TokenRef[] all, TokenRef[] any) : UnaryOperator(input), IPartitionedScan
{
    public override string Name => "NodeByLabelScan";
    public override bool RowLocal => true;
    public override string Details => variable + LabelUtil.Format(all) + (any.Length > 0 ? ":(" + string.Join("|", any.Select(a => a.Name)) + ")" : "");
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    public long EstimateRows(StoreTx tx)
    {
        long n = all.Length == 0 ? 0 : long.MaxValue;
        foreach (var t in all) n = Math.Min(n, t.Resolve(tx) is var id and >= 0 ? tx.LabelCount(id) : 0);
        foreach (var t in any) n += t.Resolve(tx) is var id and >= 0 ? tx.LabelCount(id) : 0;
        return n;
    }

    private sealed class C(LabelScanOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private LabelBitmapIterator? _it;

        public override bool Next()
        {
            while (true)
            {
                if (_it != null && _it.Next(out long id))
                {
                    ctx.CheckCancel();
                    ctx.Row[op.slot] = Value.FromNodeId(id);
                    return true;
                }
                if (!input.Next()) return false;
                // resolved per input row: labels may be created by earlier clauses of the same query
                var all = LabelUtil.Resolve(op.all, ctx.Store, out bool impossible) ?? [];
                if (impossible) { _it = null; continue; }
                var any = op.any.Select(t => t.Resolve(ctx.Store)).Where(x => x >= 0).ToArray();
                if (op.any.Length > 0 && any.Length == 0) { _it = null; continue; }
                var (low, high) = ctx.PartitionOf(op);
                _it = new LabelBitmapIterator(ctx.Store, all, any, low, high);
            }
        }
    }

    private readonly int slot = slot;
    private readonly TokenRef[] all = all;
    private readonly TokenRef[] any = any;
}

internal enum SeekMode { Equal, In, Range, Prefix }

/// <summary>
/// Seeks nodes through a (label, property) B+tree index. Falls back to a label scan if the index does not
/// exist at execution time. Candidates are re-checked by the predicate the planner keeps above this operator.
/// </summary>
internal sealed class NodeIndexSeekOp(Operator input, int slot, string variable, TokenRef[] labels, TokenRef label, TokenRef key, SeekMode mode,
    Eval? value, Eval? low, bool lowInclusive, Eval? high, bool highInclusive, string desc) : UnaryOperator(input)
{
    private readonly int slot = slot;
    private readonly TokenRef[] labels = labels;
    private readonly TokenRef label = label;
    private readonly TokenRef key = key;
    private readonly SeekMode mode = mode;
    private readonly Eval? value = value;
    private readonly Eval? low = low;
    private readonly bool lowInclusive = lowInclusive;
    private readonly Eval? high = high;
    private readonly bool highInclusive = highInclusive;

    public override bool RowLocal => true;
    public override string Name => mode switch
    {
        SeekMode.In => "NodeIndexSeek(IN)",
        SeekMode.Range => "NodeIndexSeekByRange",
        SeekMode.Prefix => "NodeIndexSeekByPrefix",
        _ => "NodeIndexSeek",
    };

    public override string Details => $"{variable}{LabelUtil.Format(labels)}({key.Name}) {desc}";
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C(NodeIndexSeekOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private readonly Queue<(byte[] Low, byte[]? High)> _ranges = new();
        private readonly List<long> _ids = new();
        private int _idPos;
        private IndexDef? _ix;
        private IndexRangeCursor? _cur;
        private LabelBitmapIterator? _fallback;
        private HashSet<long>? _seen;
        private int[]? _labels;
        private readonly BinWriter _w = new(64);

        private byte[]? Encode(in Value v)
        {
            _w.Reset();
            return IndexKeys.EncodeValue(v, _w) ? _w.Written.ToArray() : null;
        }

        private void Plan()
        {
            _ranges.Clear();
            _ids.Clear();
            _idPos = 0;
            _seen = null;
            var tx = ctx.Store;
            switch (op.mode)
            {
                case SeekMode.Equal when tx.IndexLookup(_ix!, op.value!(ctx), _w, _ids):
                    break;
                case SeekMode.In when tx.HasHashIndex(_ix!):
                    {
                        var v = op.value!(ctx);
                        if (v.Kind != ValueKind.List) break;
                        int n = 0;
                        foreach (var x in v.ListItems) { tx.IndexLookup(_ix!, x, _w, _ids); n++; }
                        if (n > 1) _seen = new HashSet<long>();
                        break;
                    }
                case SeekMode.Equal:
                    {
                        var k = Encode(op.value!(ctx));
                        if (k != null) _ranges.Enqueue((k, IndexKeys.Successor(k)));
                        break;
                    }
                case SeekMode.In:
                    {
                        var v = op.value!(ctx);
                        if (v.Kind != ValueKind.List) break;
                        var keys = new HashSet<string>();
                        foreach (var x in v.ListItems)
                        {
                            var k = Encode(x);
                            if (k != null && keys.Add(Convert.ToBase64String(k))) _ranges.Enqueue((k, IndexKeys.Successor(k)));
                        }
                        if (_ranges.Count > 1) _seen = new HashSet<long>();
                        break;
                    }
                case SeekMode.Prefix:
                    {
                        var v = op.value!(ctx);
                        if (v.Kind != ValueKind.String) break;
                        _w.Reset();
                        _w.WriteByte(IndexKeys.TString);
                        IndexKeys.WriteEscaped(_w, v.Str, terminate: false);
                        var k = _w.Written.ToArray();
                        _ranges.Enqueue((k, IndexKeys.Successor(k)));
                        break;
                    }
                case SeekMode.Range:
                    {
                        Value lo = op.low?.Invoke(ctx) ?? Value.Null, hi = op.high?.Invoke(ctx) ?? Value.Null;
                        if (op.low != null && lo.IsNull || op.high != null && hi.IsNull) break;
                        var any = op.low != null ? lo : hi;
                        byte tag = IndexKeys.TagOf(any.Kind);
                        if (tag == 0) break;
                        if (op.low != null && op.high != null && IndexKeys.TagOf(hi.Kind) != tag) break;
                        byte[] lowKey;
                        if (op.low is null) lowKey = [tag];
                        else
                        {
                            var k = Encode(lo);
                            if (k is null) break;
                            lowKey = op.lowInclusive ? k : IndexKeys.Successor(k) ?? k;
                        }
                        byte[]? highKey;
                        if (op.high is null) highKey = [(byte)(tag + 1)];
                        else
                        {
                            var k = Encode(hi);
                            if (k is null) break;
                            highKey = op.highInclusive ? IndexKeys.Successor(k) : k;
                        }
                        _ranges.Enqueue((lowKey, highKey));
                        break;
                    }
            }
        }

        private bool Accept(long id)
        {
            var tx = ctx.Store;
            if (!tx.NodeAlive(id) || !LabelUtil.HasAll(tx, id, _labels)) return false;
            return _seen is null || _seen.Add(id);
        }

        public override bool Next()
        {
            var tx = ctx.Store;
            while (true)
            {
                if (_fallback != null)
                {
                    while (_fallback.Next(out long fid))
                    {
                        if (!Accept(fid)) continue;
                        ctx.Row[op.slot] = Value.FromNodeId(fid);
                        return true;
                    }
                    _fallback = null;
                }
                while (_idPos < _ids.Count)
                {
                    long id = _ids[_idPos++];
                    if (!Accept(id)) continue;
                    ctx.Row[op.slot] = Value.FromNodeId(id);
                    return true;
                }
                while (_cur != null || _ranges.Count > 0)
                {
                    if (_cur is null)
                    {
                        var (lo, hi) = _ranges.Dequeue();
                        _cur = tx.IndexScan(_ix!, lo, hi);
                        if (_cur is null) continue;
                    }
                    while (_cur.Next(out long id))
                    {
                        if (!Accept(id)) continue;
                        ctx.CheckCancel();
                        ctx.Row[op.slot] = Value.FromNodeId(id);
                        return true;
                    }
                    _cur = null;
                }
                if (!input.Next()) return false;
                _labels = LabelUtil.Resolve(op.labels, tx, out bool impossible);
                if (impossible) continue;
                int lid = op.label.Resolve(tx), kid = op.key.Resolve(tx);
                if (lid < 0 || kid < 0) continue;
                _ix = tx.Catalog.Find(lid, kid);
                if (_ix is null)
                {
                    // index dropped after planning: label scan, the predicate above re-checks values
                    _fallback = new LabelBitmapIterator(tx, [lid], []);
                    continue;
                }
                Plan();
            }
        }
    }
}

internal sealed class NodeByIdSeekOp(Operator input, int slot, string variable, Eval ids) : UnaryOperator(input)
{
    public override string Name => "NodeByIdSeek";
    public override bool RowLocal => true;
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
                        if (v.Kind != ValueKind.Integer || !ctx.Store.NodeAlive(v.Num)) continue;
                        ctx.Row[slot] = Value.FromNodeId(v.Num);
                        return true;
                    }
                }
                if (!input.Next()) return false;
                var x = ids(ctx);
                _list = x.Kind == ValueKind.List ? x.ListItems.Distinct(ValueEquivalenceComparer.Instance).ToArray() : [x];
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
    public static bool IsUsed(Value[] row, int[] slots, long rel)
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

    public static bool AllResolved(TokenRef[] tokens, StoreTx store)
    {
        foreach (var t in tokens) if (t.Resolve(store) < 0) return false;
        return true;
    }

    public static int[]? ResolveTypes(TokenRef[] types, StoreTx store, out bool impossible)
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

/// <summary>Iterates adjacency entries of one node block by block, re-fetching page memory on every call.</summary>
internal unsafe struct AdjWalker
{
    public long Block;
    public int Idx;

    public void Start(StoreTx tx, long node, bool outgoing)
    {
        Block = tx.AdjHead(node, outgoing);
        Idx = 0;
    }

    /// <summary>Returns the next entry, or false at the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Next(StoreTx tx, out AdjEntry e)
    {
        while (Block != 0)
        {
            var b = (AdjBlock*)tx.R(Block);
            if (Idx < b->Count)
            {
                e = StoreTx.Entries((byte*)b)[Idx++];
                return true;
            }
            Block = b->Next;
            Idx = 0;
        }
        e = default;
        return false;
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
    public override bool RowLocal => true;
    public override string Details => desc;
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C(ExpandOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private int[]? _types;
        private int[]? _labels;
        private bool _impossible, _complete;
        private AdjWalker _walk;
        private long _from, _target;
        private byte _phase; // 0 = out, 1 = in
        private bool _active;

        private void Resolve()
        {
            _types = RelCheck.ResolveTypes(op.types, ctx.Store, out bool i1);
            _labels = LabelUtil.Resolve(op.toLabels, ctx.Store, out bool i2);
            _impossible = i1 || i2;
            _complete = RelCheck.AllResolved(op.types, ctx.Store) && !i2;
        }

        public override bool Next()
        {
            var row = ctx.Row;
            var tx = ctx.Store;
            while (true)
            {
                if (!_active)
                {
                    if (!input.Next()) return false;
                    if (!_complete)
                    {
                        Resolve();
                        if (_impossible) continue;
                    }
                    var from = row[op.fromSlot];
                    if (from.Kind != ValueKind.Node || !tx.NodeAlive(from.EntityId)) continue;
                    _from = from.EntityId;
                    if (op.into)
                    {
                        var to = row[op.toSlot];
                        if (to.Kind != ValueKind.Node) continue;
                        _target = to.EntityId;
                    }
                    _phase = op.dir == Direction.In ? (byte)1 : (byte)0;
                    _walk.Start(tx, _from, _phase == 0);
                    _active = true;
                }
                while (_walk.Next(tx, out var e))
                {
                    if (_types != null && !RelCheck.TypeOk(_types, e.Type)) continue;
                    if (_phase == 1 && op.dir == Direction.Both && e.Other == _from) continue; // self loop already seen
                    if (op.into)
                    {
                        if (e.Other != _target) continue;
                    }
                    else if (_labels != null && !LabelUtil.HasAll(tx, e.Other, _labels)) continue;
                    if (op.uniqueSlots.Length > 0 && RelCheck.IsUsed(row, op.uniqueSlots, e.Rel)) continue;
                    row[op.relSlot] = Value.FromRelId(e.Rel);
                    if (!op.into) row[op.toSlot] = Value.FromNodeId(e.Other);
                    ctx.CheckCancel();
                    return true;
                }
                if (_phase == 0 && op.dir == Direction.Both)
                {
                    _phase = 1;
                    _walk.Start(tx, _from, false);
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
    public override bool RowLocal => true;
    public override string Details => desc;
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private struct Frame
    {
        public long Node;
        public AdjWalker Walk;
        public byte Phase;
    }

    private sealed class C(VarExpandOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private int[]? _types;
        private int[]? _labels;
        private bool _impossible, _complete;
        private readonly List<Frame> _stack = new();
        private readonly List<long> _path = new();
        private Value[]? _filterValues;
        private bool _active, _pendingZero;
        private long _from, _target;

        private void Resolve()
        {
            _types = RelCheck.ResolveTypes(op.types, ctx.Store, out bool i1);
            _labels = LabelUtil.Resolve(op.toLabels, ctx.Store, out bool i2);
            _impossible = (i1 && op.min > 0) || i2;
            _complete = RelCheck.AllResolved(op.types, ctx.Store) && !i2;
        }

        private Frame MakeFrame(long node)
        {
            var f = new Frame { Node = node, Phase = op.dir == Direction.In ? (byte)1 : (byte)0 };
            f.Walk.Start(ctx.Store, node, f.Phase == 0);
            return f;
        }

        private bool TargetOk(long node)
        {
            if (op.into) return node == _target;
            return _labels is null || LabelUtil.HasAll(ctx.Store, node, _labels);
        }

        private bool RelOk(long rel)
        {
            var f = op.relFilter;
            for (int i = 0; i < f.Length; i++)
            {
                var actual = ctx.Store.RelProp(rel, f[i].Key.Resolve(ctx.Store));
                if (!ValueOps.IsTrue(ValueOps.Equal(actual, _filterValues![i]))) return false;
            }
            return true;
        }

        private void Emit(long node)
        {
            var arr = new Value[_path.Count];
            if (op.reverse)
                for (int i = 0; i < arr.Length; i++) arr[i] = Value.FromRelId(_path[_path.Count - 1 - i]);
            else
                for (int i = 0; i < arr.Length; i++) arr[i] = Value.FromRelId(_path[i]);
            ctx.Row[op.relSlot] = Value.FromList(arr);
            if (!op.into) ctx.Row[op.toSlot] = Value.FromNodeId(node);
        }

        public override bool Next()
        {
            var row = ctx.Row;
            var tx = ctx.Store;
            while (true)
            {
                if (!_active)
                {
                    if (!input.Next()) return false;
                    if (!_complete)
                    {
                        Resolve();
                        if (_impossible) continue;
                    }
                    var from = row[op.fromSlot];
                    if (from.Kind != ValueKind.Node || !tx.NodeAlive(from.EntityId)) continue;
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
                        for (int i = 0; i < op.relFilter.Length; i++) _filterValues[i] = op.relFilter[i].Value(ctx);
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
                    ctx.CheckCancel();
                    int top = _stack.Count - 1;
                    var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_stack);
                    ref var f = ref span[top];
                    if (!f.Walk.Next(tx, out var e))
                    {
                        if (f.Phase == 0 && op.dir == Direction.Both)
                        {
                            f.Phase = 1;
                            f.Walk.Start(tx, f.Node, false);
                            continue;
                        }
                        _stack.RemoveAt(top);
                        if (_path.Count > 0 && top > 0) _path.RemoveAt(_path.Count - 1);
                        continue;
                    }
                    if (_types != null && !RelCheck.TypeOk(_types, e.Type)) continue;
                    if (f.Phase == 1 && op.dir == Direction.Both && e.Other == f.Node) continue;
                    if (_path.Contains(e.Rel)) continue;
                    if (op.uniqueSlots.Length > 0 && RelCheck.IsUsed(row, op.uniqueSlots, e.Rel)) continue;
                    if (op.relFilter.Length > 0 && !RelOk(e.Rel)) continue;
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

/// <summary>shortestPath / allShortestPaths between two bound nodes (BFS; bidirectional for a single path).</summary>
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
    public override bool RowLocal => true;
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C(ShortestPathOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private readonly List<(long[] Nodes, long[] Rels)> _results = new();
        private int _pos;
        private int[]? _types;
        private bool _resolved, _impossible;
        private readonly List<AdjEntry> _adj = new();

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

        /// <summary>Neighbours of a node in the given direction (self loops reported once).</summary>
        private List<AdjEntry> Neighbours(long n, Direction dir)
        {
            _adj.Clear();
            var tx = ctx.Store;
            var w = new AdjWalker();
            if (dir != Direction.In)
            {
                w.Start(tx, n, true);
                while (w.Next(tx, out var e)) if (RelCheck.TypeOk(_types, e.Type)) _adj.Add(e);
            }
            if (dir != Direction.Out)
            {
                w.Start(tx, n, false);
                while (w.Next(tx, out var e))
                    if (RelCheck.TypeOk(_types, e.Type) && !(dir == Direction.Both && e.Other == n)) _adj.Add(e);
            }
            return _adj;
        }

        private void SearchBidirectional(long src, long dst)
        {
            var fwd = new Dictionary<long, (long Prev, long Rel)> { [src] = (-1, -1) };
            var bwd = new Dictionary<long, (long Next, long Rel)> { [dst] = (-1, -1) };
            var fFrontier = new List<long> { src };
            var bFrontier = new List<long> { dst };
            var next = new List<long>();
            int maxLen = op.max < 0 ? int.MaxValue : op.max;
            int len = 0;
            long meet = -1;
            var bdir = op.dir switch { Direction.Out => Direction.In, Direction.In => Direction.Out, _ => Direction.Both };
            while (meet < 0 && len < maxLen && fFrontier.Count > 0 && bFrontier.Count > 0)
            {
                len++;
                bool forward = fFrontier.Count <= bFrontier.Count;
                var frontier = forward ? fFrontier : bFrontier;
                next.Clear();
                foreach (var n in frontier)
                {
                    ctx.CheckCancel();
                    foreach (var e in Neighbours(n, forward ? op.dir : bdir))
                    {
                        if (forward)
                        {
                            if (!fwd.TryAdd(e.Other, (n, e.Rel))) continue;
                            if (bwd.ContainsKey(e.Other)) { meet = e.Other; break; }
                        }
                        else
                        {
                            if (!bwd.TryAdd(e.Other, (n, e.Rel))) continue;
                            if (fwd.ContainsKey(e.Other)) { meet = e.Other; break; }
                        }
                        next.Add(e.Other);
                    }
                    if (meet >= 0) break;
                }
                if (forward) (fFrontier, next) = (next, fFrontier);
                else (bFrontier, next) = (next, bFrontier);
            }
            if (meet < 0 || len < op.min) return;
            var nodes = new List<long>();
            var rels = new List<long>();
            for (long cur = meet; cur != src;)
            {
                var (prev, rel) = fwd[cur];
                nodes.Add(cur);
                rels.Add(rel);
                cur = prev;
            }
            nodes.Add(src);
            nodes.Reverse();
            rels.Reverse();
            for (long cur = meet; cur != dst;)
            {
                var (nx, rel) = bwd[cur];
                rels.Add(rel);
                nodes.Add(nx);
                cur = nx;
            }
            _results.Add((nodes.ToArray(), rels.ToArray()));
        }

        private void Search(long src, long dst)
        {
            if (src == dst)
            {
                if (op.min == 0) _results.Add(([src], []));
                return;
            }
            var parents = new Dictionary<long, List<(long Prev, long Rel)>>();
            var depthOf = new Dictionary<long, int> { [src] = 0 };
            var frontier = new List<long> { src };
            int depth = 0;
            bool found = false;
            int maxDepth = op.max < 0 ? int.MaxValue : op.max;
            while (frontier.Count > 0 && !found && depth < maxDepth)
            {
                depth++;
                var next = new List<long>();
                foreach (var n in frontier)
                {
                    ctx.CheckCancel();
                    foreach (var e in Neighbours(n, op.dir))
                    {
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
                frontier = next;
            }
            if (!found || depth < op.min) return;
            var relStack = new List<long>();
            var nodeStack = new List<long> { dst };
            Walk(dst);

            void Walk(long node)
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
    public override bool RowLocal => true;
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    public static Value Build(ExecContext ctx, int[] nodeSlots, int[] relSlots)
    {
        var row = ctx.Row;
        var store = ctx.Store;
        var first = row[nodeSlots[0]];
        if (first.Kind != ValueKind.Node) return Value.Null;
        var nodes = new List<long> { first.EntityId };
        var rels = new List<long>();
        long cur = first.EntityId;
        for (int i = 0; i < relSlots.Length; i++)
        {
            var r = row[relSlots[i]];
            if (r.Kind == ValueKind.Relationship) Step(r.EntityId);
            else if (r.Kind == ValueKind.List)
            {
                foreach (var x in r.ListItems) Step(x.EntityId);
            }
            else return Value.Null;
        }
        return Value.FromPath(new GraphPath(nodes.ToArray(), rels.ToArray()));

        void Step(long rel)
        {
            var (src, dst, _) = store.RelInfo(rel);
            long other = src == cur ? dst : src;
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
    public override bool RowLocal => true;
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
    public override bool RowLocal => true;
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
    public override bool RowLocal => true;
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
    public override bool RowLocal => true;
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

/// <summary>CALL { subquery }: runs the inner plan for every input row. A unit subquery (no RETURN)
/// passes each input row through once; with IN TRANSACTIONS the work is committed every N input rows.</summary>
internal sealed class ApplyOp(Operator input, Operator inner, bool unit, Eval? batchSize) : UnaryOperator(input)
{
    private readonly Operator inner = inner;
    private readonly bool unit = unit;
    private readonly Eval? batchSize = batchSize;

    public override string Name => batchSize is null ? "Apply" : "TransactionApply";
    public override bool RowLocal => batchSize is null;
    public override IEnumerable<Operator> Children => [Input, inner];
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C(ApplyOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private Cursor? _inner;
        private long _batch = -1, _inBatch;

        private void AfterInputRow()
        {
            if (op.batchSize is null) return;
            if (_batch < 0)
            {
                if (ctx.CommitBatch is null) throw new GraphQLiteException("CALL { ... } IN TRANSACTIONS can only be used in implicit (auto-commit) transactions");
                _batch = LimitOp.EvalCount(ctx, op.batchSize, "IN TRANSACTIONS OF");
                if (_batch <= 0) _batch = 1000;
            }
            if (++_inBatch >= _batch)
            {
                _inBatch = 0;
                ctx.CheckPendingDeletes();
                ctx.CommitBatch!();
            }
        }

        public override bool Next()
        {
            while (true)
            {
                if (_inner != null)
                {
                    if (_inner.Next()) return true;
                    _inner = null;
                    AfterInputRow();
                }
                if (!input.Next()) return false;
                if (op.unit)
                {
                    var saved = RowOps.Snapshot(ctx.Row);
                    var c = op.inner.Open(ctx);
                    while (c.Next()) { }
                    RowOps.Restore(saved, ctx.Row);
                    AfterInputRow();
                    return true;
                }
                _inner = op.inner.Open(ctx);
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
                    var heap = new PriorityQueue<(Value[] Keys, Value[] Row, int Seq), (Value[] Keys, Value[] Row, int Seq)>(
                        Comparer<(Value[], Value[], int)>.Create((a, b) => cmp.Compare(b, a)));
                    // the keys of a row go into a reused array, copied only when the row enters the heap
                    var probe = new Value[op.keys.Length];
                    while (input.Next())
                    {
                        if (n == 0) continue;
                        for (int i = 0; i < probe.Length; i++) probe[i] = op.keys[i].Eval(ctx);
                        var cand = (probe, (Value[])null!, seq++);
                        if (heap.Count < n || cmp.Compare(cand, heap.Peek()) < 0)
                        {
                            cand.Item1 = (Value[])probe.Clone();
                            cand.Item2 = RowOps.Snapshot(ctx.Row);
                            if (heap.Count < n) heap.Enqueue(cand, cand);
                            else heap.EnqueueDequeue(cand, cand);
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
