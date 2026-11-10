using System.Runtime.CompilerServices;

namespace GraphDb.Storage;

internal enum WalOp : byte
{
    CreateNode = 1,
    DeleteNode = 2,
    CreateRel = 3,
    DeleteRel = 4,
    SetNodeProp = 5,
    SetRelProp = 6,
    AddLabel = 7,
    RemoveLabel = 8,
    CreateIndex = 9,
    DropIndex = 10,
    Token = 11,
    Commit = 0xFF,
}

internal enum TokenKind : byte { Label = 0, RelType = 1, PropKey = 2 }

internal enum UndoKind : byte
{
    NodeCreated, NodeDeleted, RelCreated, RelDeleted, NodePropSet, RelPropSet, LabelAdded, LabelRemoved, IndexCreated, IndexDropped,
}

internal struct UndoEntry
{
    public UndoKind Kind;
    public int Id;
    public int Key;
    public Value Old;
    public int[]? Labels;
    public PropertyMap? Props;
    public PropertyIndex? Index;
}

/// <summary>Per-transaction change log: undo entries for rollback, serialized redo records for the WAL.</summary>
internal sealed class TxLog
{
    public readonly List<UndoEntry> Undo = new();
    public readonly BinWriter? Redo;
    public TxLog(bool durable) => Redo = durable ? new BinWriter(4096) : null;
    public bool HasChanges => Undo.Count > 0;
}

/// <summary>
/// In-memory graph store. Nodes and relationships live in contiguous struct arrays; adjacency is
/// stored inline in each node record. All mutations go through methods that record undo/redo info.
/// Thread-safety: callers hold the database lock (shared for reads, exclusive for writes).
/// </summary>
internal sealed class GraphStore
{
    public NodeRecord[] Nodes = new NodeRecord[1024];
    public int NodeHigh;
    public int NodeCount;
    public RelRecord[] Rels = new RelRecord[1024];
    public int RelHigh;
    public int RelCount;

    public readonly TokenTable Labels = new();
    public readonly TokenTable RelTypes = new();
    public readonly TokenTable PropKeys = new();

    private Bitset?[] _labelBits = new Bitset?[16];
    private List<PropertyIndex>?[] _indexesByLabel = new List<PropertyIndex>?[16];
    public readonly Dictionary<string, PropertyIndex> IndexesByName = new(StringComparer.Ordinal);
    private int[] _relTypeCounts = new int[16];

    /// <summary>Bumped on every schema change; part of the plan cache key.</summary>
    public long SchemaVersion;

    /// <summary>Sequence number of the last committed transaction.</summary>
    public long LastTxSeq;

    // ------------------------------------------------------------------ reads

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool NodeAlive(int id) => (uint)id < (uint)NodeHigh && Nodes[id].Alive;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RelAlive(int id) => (uint)id < (uint)RelHigh && Rels[id].Alive;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref NodeRecord Node(int id) => ref Nodes[id];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref RelRecord Rel(int id) => ref Rels[id];

    public Bitset? LabelBits(int label) => (uint)label < (uint)_labelBits.Length ? _labelBits[label] : null;

    public int LabelCount(int label) => LabelBits(label)?.Count ?? 0;

    public int RelTypeCount(int type) => (uint)type < (uint)_relTypeCounts.Length ? _relTypeCounts[type] : 0;

    public PropertyIndex? FindIndex(int label, int key)
    {
        if ((uint)label >= (uint)_indexesByLabel.Length) return null;
        var list = _indexesByLabel[label];
        if (list is null) return null;
        foreach (var ix in list) if (ix.Key == key) return ix;
        return null;
    }

    public IEnumerable<PropertyIndex> AllIndexes => IndexesByName.Values;

    public Value NodeProp(int id, int key)
    {
        if (key < 0 || !NodeAlive(id)) return Value.Null;
        var p = Nodes[id].Props;
        return p is null ? Value.Null : p.Get(key);
    }

    public Value RelProp(int id, int key)
    {
        if (key < 0 || !RelAlive(id)) return Value.Null;
        var p = Rels[id].Props;
        return p is null ? Value.Null : p.Get(key);
    }

    public Dictionary<string, Value> PropsToMap(PropertyMap? props)
    {
        if (props is null) return new Dictionary<string, Value>(0);
        var d = new Dictionary<string, Value>(props.Count);
        var keys = props.Keys;
        var vals = props.Values;
        for (int i = 0; i < keys.Length; i++) d[PropKeys.Name(keys[i])] = vals[i];
        return d;
    }

    public Node MaterializeNode(int id)
    {
        if (!NodeAlive(id)) return new Node(id, Array.Empty<string>(), new Dictionary<string, Value>());
        ref var n = ref Nodes[id];
        string[] labels = n.Labels is null ? Array.Empty<string>() : Array.ConvertAll(n.Labels, Labels.Name);
        return new Node(id, labels, PropsToMap(n.Props));
    }

    public Relationship MaterializeRel(int id)
    {
        ref var r = ref Rels[id];
        if (!r.Alive && id >= RelHigh) return new Relationship(id, "", -1, -1, new Dictionary<string, Value>());
        return new Relationship(id, RelTypes.Name(r.Type), r.Src, r.Dst, PropsToMap(r.Props));
    }

    // ------------------------------------------------------------------ helpers

    private void EnsureNodeCapacity(int id)
    {
        if (id >= Nodes.Length) Array.Resize(ref Nodes, Math.Max(Nodes.Length * 2, id + 1));
    }

    private void EnsureRelCapacity(int id)
    {
        if (id >= Rels.Length) Array.Resize(ref Rels, Math.Max(Rels.Length * 2, id + 1));
    }

    private Bitset GetOrCreateLabelBits(int label)
    {
        if (label >= _labelBits.Length) Array.Resize(ref _labelBits, Math.Max(_labelBits.Length * 2, label + 1));
        return _labelBits[label] ??= new Bitset();
    }

    private void BumpRelType(int type, int delta)
    {
        if (type >= _relTypeCounts.Length) Array.Resize(ref _relTypeCounts, Math.Max(_relTypeCounts.Length * 2, type + 1));
        _relTypeCounts[type] += delta;
    }

    private List<PropertyIndex>? IndexesFor(int label) =>
        (uint)label < (uint)_indexesByLabel.Length ? _indexesByLabel[label] : null;

    private void IndexAddNode(int id, int[]? labels, PropertyMap? props)
    {
        if (labels is null || props is null || IndexesByName.Count == 0) return;
        foreach (var l in labels)
        {
            var list = IndexesFor(l);
            if (list is null) continue;
            foreach (var ix in list)
                if (props.TryGet(ix.Key, out var v)) ix.Add(v, id);
        }
    }

    private void IndexRemoveNode(int id, int[]? labels, PropertyMap? props)
    {
        if (labels is null || props is null || IndexesByName.Count == 0) return;
        foreach (var l in labels)
        {
            var list = IndexesFor(l);
            if (list is null) continue;
            foreach (var ix in list)
                if (props.TryGet(ix.Key, out var v)) ix.Remove(v, id);
        }
    }

    private void CheckUnique(int id, int label, int key, in Value v)
    {
        var list = IndexesFor(label);
        if (list is null) return;
        foreach (var ix in list)
        {
            if (!ix.Unique || ix.Key != key) continue;
            int other = ix.Conflict(v, id);
            if (other >= 0)
                throw new ConstraintViolationException(
                    $"Node({other}) already exists with label `{Labels.Name(label)}` and property `{PropKeys.Name(key)}` = {v}");
        }
    }

    private void CheckUniqueAll(int id, int[]? labels, PropertyMap? props)
    {
        if (labels is null || props is null || IndexesByName.Count == 0) return;
        foreach (var l in labels)
        {
            var list = IndexesFor(l);
            if (list is null) continue;
            foreach (var ix in list)
                if (ix.Unique && props.TryGet(ix.Key, out var v)) CheckUnique(id, l, ix.Key, v);
        }
    }

    internal static void ValidatePropertyValue(in Value v)
    {
        switch (v.Kind)
        {
            case ValueKind.Null:
            case ValueKind.Boolean:
            case ValueKind.Integer:
            case ValueKind.Float:
            case ValueKind.String:
                return;
            case ValueKind.List:
                foreach (var x in v.ListItems)
                {
                    if (x.Kind is ValueKind.List or ValueKind.Map or ValueKind.Node or ValueKind.Relationship or ValueKind.Path)
                        throw new CypherRuntimeException("Property values can only be of primitive types or arrays thereof");
                }
                return;
            default:
                throw new CypherRuntimeException($"Property values can only be of primitive types or arrays thereof (got {v.Kind})");
        }
    }

    // ------------------------------------------------------------------ mutations

    public int CreateNode(int[]? labels, PropertyMap? props, TxLog? log) => CreateNodeAt(NodeHigh, labels, props, log);

    public int CreateNodeAt(int id, int[]? labels, PropertyMap? props, TxLog? log)
    {
        if (labels is { Length: > 1 }) Array.Sort(labels);
        CheckUniqueAll(id, labels, props);
        EnsureNodeCapacity(id);
        ref var n = ref Nodes[id];
        n.Alive = true;
        n.Labels = labels is { Length: > 0 } ? labels : null;
        n.Props = props is { Count: > 0 } ? props : null;
        if (id >= NodeHigh) NodeHigh = id + 1;
        NodeCount++;
        if (labels != null) foreach (var l in labels) GetOrCreateLabelBits(l).Set(id);
        IndexAddNode(id, n.Labels, n.Props);
        if (log != null)
        {
            log.Undo.Add(new UndoEntry { Kind = UndoKind.NodeCreated, Id = id });
            if (log.Redo is { } w)
            {
                w.WriteByte((byte)WalOp.CreateNode);
                w.WriteVarUInt((ulong)id);
                w.WriteIntArray(n.Labels);
                w.WriteProps(n.Props);
            }
        }
        return id;
    }

    /// <summary>Marks the node deleted. The caller is responsible for relationship checks.</summary>
    public void DeleteNode(int id, TxLog? log)
    {
        if (!NodeAlive(id)) return;
        ref var n = ref Nodes[id];
        if (n.Labels != null) foreach (var l in n.Labels) _labelBits[l]!.Clear(id);
        IndexRemoveNode(id, n.Labels, n.Props);
        log?.Undo.Add(new UndoEntry { Kind = UndoKind.NodeDeleted, Id = id, Labels = n.Labels, Props = n.Props });
        if (log?.Redo is { } w)
        {
            w.WriteByte((byte)WalOp.DeleteNode);
            w.WriteVarUInt((ulong)id);
        }
        n.Alive = false;
        n.Labels = null;
        n.Props = null;
        NodeCount--;
    }

    public int CreateRel(int src, int dst, int type, PropertyMap? props, TxLog? log) => CreateRelAt(RelHigh, src, dst, type, props, log);

    public int CreateRelAt(int id, int src, int dst, int type, PropertyMap? props, TxLog? log)
    {
        if (!NodeAlive(src) || !NodeAlive(dst))
            throw new CypherRuntimeException("Cannot create a relationship to a deleted node");
        EnsureRelCapacity(id);
        ref var r = ref Rels[id];
        r.Alive = true;
        r.Src = src;
        r.Dst = dst;
        r.Type = type;
        r.Props = props is { Count: > 0 } ? props : null;
        if (id >= RelHigh) RelHigh = id + 1;
        RelCount++;
        BumpRelType(type, 1);
        Nodes[src].Out.Add(new AdjEntry(id, dst, type));
        Nodes[dst].In.Add(new AdjEntry(id, src, type));
        if (log != null)
        {
            log.Undo.Add(new UndoEntry { Kind = UndoKind.RelCreated, Id = id });
            if (log.Redo is { } w)
            {
                w.WriteByte((byte)WalOp.CreateRel);
                w.WriteVarUInt((ulong)id);
                w.WriteVarUInt((ulong)src);
                w.WriteVarUInt((ulong)dst);
                w.WriteVarUInt((ulong)type);
                w.WriteProps(r.Props);
            }
        }
        return id;
    }

    public void DeleteRel(int id, TxLog? log)
    {
        if (!RelAlive(id)) return;
        ref var r = ref Rels[id];
        Nodes[r.Src].Out.Remove(id);
        Nodes[r.Dst].In.Remove(id);
        r.Alive = false;
        RelCount--;
        BumpRelType(r.Type, -1);
        log?.Undo.Add(new UndoEntry { Kind = UndoKind.RelDeleted, Id = id, Props = r.Props });
        if (log?.Redo is { } w)
        {
            w.WriteByte((byte)WalOp.DeleteRel);
            w.WriteVarUInt((ulong)id);
        }
        r.Props = null;
    }

    public void SetNodeProp(int id, int key, Value v, TxLog? log)
    {
        if (!NodeAlive(id)) throw new CypherRuntimeException($"Node {id} has been deleted");
        ValidatePropertyValue(v);
        ref var n = ref Nodes[id];
        var labels = n.Labels;
        bool indexed = false;
        if (labels != null && IndexesByName.Count > 0)
        {
            foreach (var l in labels)
            {
                var list = IndexesFor(l);
                if (list is null) continue;
                foreach (var ix in list)
                {
                    if (ix.Key != key) continue;
                    indexed = true;
                    if (ix.Unique && !v.IsNull) CheckUnique(id, l, key, v);
                }
            }
        }
        Value old;
        if (v.IsNull)
        {
            if (n.Props is null) return;
            old = n.Props.Remove(key);
            if (old.IsNull) return;
            if (n.Props.Count == 0) n.Props = null;
        }
        else
        {
            n.Props ??= new PropertyMap();
            old = n.Props.Set(key, v);
        }
        if (indexed)
        {
            foreach (var l in labels!)
            {
                var list = IndexesFor(l);
                if (list is null) continue;
                foreach (var ix in list)
                {
                    if (ix.Key != key) continue;
                    ix.Remove(old, id);
                    ix.Add(v, id);
                }
            }
        }
        log?.Undo.Add(new UndoEntry { Kind = UndoKind.NodePropSet, Id = id, Key = key, Old = old });
        if (log?.Redo is { } w)
        {
            w.WriteByte((byte)WalOp.SetNodeProp);
            w.WriteVarUInt((ulong)id);
            w.WriteVarUInt((ulong)key);
            w.WriteValue(v);
        }
    }

    public void SetRelProp(int id, int key, Value v, TxLog? log)
    {
        if (!RelAlive(id)) throw new CypherRuntimeException($"Relationship {id} has been deleted");
        ValidatePropertyValue(v);
        ref var r = ref Rels[id];
        Value old;
        if (v.IsNull)
        {
            if (r.Props is null) return;
            old = r.Props.Remove(key);
            if (old.IsNull) return;
            if (r.Props.Count == 0) r.Props = null;
        }
        else
        {
            r.Props ??= new PropertyMap();
            old = r.Props.Set(key, v);
        }
        log?.Undo.Add(new UndoEntry { Kind = UndoKind.RelPropSet, Id = id, Key = key, Old = old });
        if (log?.Redo is { } w)
        {
            w.WriteByte((byte)WalOp.SetRelProp);
            w.WriteVarUInt((ulong)id);
            w.WriteVarUInt((ulong)key);
            w.WriteValue(v);
        }
    }

    public bool AddLabel(int id, int label, TxLog? log)
    {
        if (!NodeAlive(id)) throw new CypherRuntimeException($"Node {id} has been deleted");
        ref var n = ref Nodes[id];
        if (n.HasLabel(label)) return false;
        var indexes = IndexesFor(label);
        if (indexes != null && n.Props != null)
        {
            foreach (var ix in indexes)
                if (ix.Unique && n.Props.TryGet(ix.Key, out var v)) CheckUnique(id, label, ix.Key, v);
        }
        var old = n.Labels;
        int[] nl;
        if (old is null) nl = [label];
        else
        {
            nl = new int[old.Length + 1];
            old.CopyTo(nl, 0);
            nl[^1] = label;
            Array.Sort(nl);
        }
        n.Labels = nl;
        GetOrCreateLabelBits(label).Set(id);
        if (indexes != null && n.Props != null)
        {
            foreach (var ix in indexes)
                if (n.Props.TryGet(ix.Key, out var v)) ix.Add(v, id);
        }
        log?.Undo.Add(new UndoEntry { Kind = UndoKind.LabelAdded, Id = id, Key = label });
        if (log?.Redo is { } w)
        {
            w.WriteByte((byte)WalOp.AddLabel);
            w.WriteVarUInt((ulong)id);
            w.WriteVarUInt((ulong)label);
        }
        return true;
    }

    public bool RemoveLabel(int id, int label, TxLog? log)
    {
        if (!NodeAlive(id)) throw new CypherRuntimeException($"Node {id} has been deleted");
        ref var n = ref Nodes[id];
        if (!n.HasLabel(label)) return false;
        var indexes = IndexesFor(label);
        if (indexes != null && n.Props != null)
        {
            foreach (var ix in indexes)
                if (n.Props.TryGet(ix.Key, out var v)) ix.Remove(v, id);
        }
        var old = n.Labels!;
        if (old.Length == 1) n.Labels = null;
        else
        {
            var nl = new int[old.Length - 1];
            int j = 0;
            foreach (var l in old) if (l != label) nl[j++] = l;
            n.Labels = nl;
        }
        _labelBits[label]!.Clear(id);
        log?.Undo.Add(new UndoEntry { Kind = UndoKind.LabelRemoved, Id = id, Key = label });
        if (log?.Redo is { } w)
        {
            w.WriteByte((byte)WalOp.RemoveLabel);
            w.WriteVarUInt((ulong)id);
            w.WriteVarUInt((ulong)label);
        }
        return true;
    }

    public PropertyIndex CreateIndex(string name, int label, int key, bool unique, TxLog? log)
    {
        if (IndexesByName.ContainsKey(name)) throw new GraphDbException($"An index named '{name}' already exists");
        if (FindIndex(label, key) != null)
            throw new GraphDbException($"An index on :{Labels.Name(label)}({PropKeys.Name(key)}) already exists");
        var ix = new PropertyIndex(name, label, key, unique);
        var bits = LabelBits(label);
        if (bits != null)
        {
            for (int id = bits.NextSetBit(0); id >= 0; id = bits.NextSetBit(id + 1))
            {
                var props = Nodes[id].Props;
                if (props is null || !props.TryGet(key, out var v)) continue;
                if (unique && ix.Conflict(v, id) >= 0)
                    throw new ConstraintViolationException(
                        $"Cannot create unique constraint: duplicate value {v} for :{Labels.Name(label)}({PropKeys.Name(key)})");
                ix.Add(v, id);
            }
        }
        AttachIndex(ix);
        log?.Undo.Add(new UndoEntry { Kind = UndoKind.IndexCreated, Index = ix });
        if (log?.Redo is { } w)
        {
            w.WriteByte((byte)WalOp.CreateIndex);
            w.WriteString(name);
            w.WriteVarUInt((ulong)label);
            w.WriteVarUInt((ulong)key);
            w.WriteByte(unique ? (byte)1 : (byte)0);
        }
        return ix;
    }

    public bool DropIndex(string name, TxLog? log)
    {
        if (!IndexesByName.TryGetValue(name, out var ix)) return false;
        DetachIndex(ix);
        log?.Undo.Add(new UndoEntry { Kind = UndoKind.IndexDropped, Index = ix });
        if (log?.Redo is { } w)
        {
            w.WriteByte((byte)WalOp.DropIndex);
            w.WriteString(name);
        }
        return true;
    }

    private void AttachIndex(PropertyIndex ix)
    {
        IndexesByName[ix.Name] = ix;
        if (ix.Label >= _indexesByLabel.Length)
            Array.Resize(ref _indexesByLabel, Math.Max(_indexesByLabel.Length * 2, ix.Label + 1));
        (_indexesByLabel[ix.Label] ??= new List<PropertyIndex>()).Add(ix);
        SchemaVersion++;
    }

    private void DetachIndex(PropertyIndex ix)
    {
        IndexesByName.Remove(ix.Name);
        _indexesByLabel[ix.Label]!.Remove(ix);
        SchemaVersion++;
    }

    // ------------------------------------------------------------------ rollback

    public void Rollback(TxLog log) => RollbackTo(log, 0, 0);

    /// <summary>Undoes changes recorded after the given marks (statement-level rollback).</summary>
    public void RollbackTo(TxLog log, int undoMark, int redoMark)
    {
        var undo = log.Undo;
        for (int i = undo.Count - 1; i >= undoMark; i--)
        {
            var e = undo[i];
            switch (e.Kind)
            {
                case UndoKind.NodeCreated:
                    {
                        ref var n = ref Nodes[e.Id];
                        if (n.Labels != null) foreach (var l in n.Labels) _labelBits[l]!.Clear(e.Id);
                        IndexRemoveNode(e.Id, n.Labels, n.Props);
                        n = default;
                        NodeCount--;
                        if (e.Id == NodeHigh - 1) NodeHigh--;
                        break;
                    }
                case UndoKind.NodeDeleted:
                    {
                        ref var n = ref Nodes[e.Id];
                        n.Alive = true;
                        n.Labels = e.Labels;
                        n.Props = e.Props;
                        if (n.Labels != null) foreach (var l in n.Labels) GetOrCreateLabelBits(l).Set(e.Id);
                        IndexAddNode(e.Id, n.Labels, n.Props);
                        NodeCount++;
                        break;
                    }
                case UndoKind.RelCreated:
                    {
                        ref var r = ref Rels[e.Id];
                        Nodes[r.Src].Out.Remove(e.Id);
                        Nodes[r.Dst].In.Remove(e.Id);
                        BumpRelType(r.Type, -1);
                        r = default;
                        RelCount--;
                        if (e.Id == RelHigh - 1) RelHigh--;
                        break;
                    }
                case UndoKind.RelDeleted:
                    {
                        ref var r = ref Rels[e.Id];
                        r.Alive = true;
                        r.Props = e.Props;
                        Nodes[r.Src].Out.Add(new AdjEntry(e.Id, r.Dst, r.Type));
                        Nodes[r.Dst].In.Add(new AdjEntry(e.Id, r.Src, r.Type));
                        BumpRelType(r.Type, 1);
                        RelCount++;
                        break;
                    }
                case UndoKind.NodePropSet:
                    {
                        ref var n = ref Nodes[e.Id];
                        var cur = n.Props?.Get(e.Key) ?? Value.Null;
                        if (n.Labels != null)
                        {
                            foreach (var l in n.Labels)
                            {
                                var list = IndexesFor(l);
                                if (list is null) continue;
                                foreach (var ix in list)
                                    if (ix.Key == e.Key) { ix.Remove(cur, e.Id); ix.Add(e.Old, e.Id); }
                            }
                        }
                        if (e.Old.IsNull) { n.Props?.Remove(e.Key); if (n.Props is { Count: 0 }) n.Props = null; }
                        else (n.Props ??= new PropertyMap()).Set(e.Key, e.Old);
                        break;
                    }
                case UndoKind.RelPropSet:
                    {
                        ref var r = ref Rels[e.Id];
                        if (e.Old.IsNull) { r.Props?.Remove(e.Key); if (r.Props is { Count: 0 }) r.Props = null; }
                        else (r.Props ??= new PropertyMap()).Set(e.Key, e.Old);
                        break;
                    }
                case UndoKind.LabelAdded:
                    RemoveLabel(e.Id, e.Key, null);
                    break;
                case UndoKind.LabelRemoved:
                    AddLabelNoCheck(e.Id, e.Key);
                    break;
                case UndoKind.IndexCreated:
                    DetachIndex(e.Index!);
                    break;
                case UndoKind.IndexDropped:
                    AttachIndex(e.Index!);
                    break;
            }
        }
        undo.RemoveRange(undoMark, undo.Count - undoMark);
        log.Redo?.Truncate(redoMark);
    }

    private void AddLabelNoCheck(int id, int label)
    {
        ref var n = ref Nodes[id];
        var old = n.Labels;
        int[] nl;
        if (old is null) nl = [label];
        else
        {
            nl = new int[old.Length + 1];
            old.CopyTo(nl, 0);
            nl[^1] = label;
            Array.Sort(nl);
        }
        n.Labels = nl;
        GetOrCreateLabelBits(label).Set(id);
        var indexes = IndexesFor(label);
        if (indexes != null && n.Props != null)
            foreach (var ix in indexes)
                if (n.Props.TryGet(ix.Key, out var v)) ix.Add(v, id);
    }

    // ------------------------------------------------------------------ replay

    /// <summary>Applies serialized redo records (WAL replay).</summary>
    public void Apply(BinReader r)
    {
        while (true)
        {
            var op = (WalOp)r.ReadByte();
            switch (op)
            {
                case WalOp.Commit: return;
                case WalOp.Token:
                    {
                        var kind = (TokenKind)r.ReadByte();
                        int id = r.ReadVarInt32();
                        var name = r.ReadString();
                        TokensOf(kind).DefineAt(id, name);
                        TokensOf(kind).PersistedCount = Math.Max(TokensOf(kind).PersistedCount, id + 1);
                        break;
                    }
                case WalOp.CreateNode:
                    {
                        int id = r.ReadVarInt32();
                        var labels = r.ReadIntArray();
                        var props = r.ReadProps();
                        CreateNodeAt(id, labels, props, null);
                        break;
                    }
                case WalOp.DeleteNode: DeleteNode(r.ReadVarInt32(), null); break;
                case WalOp.CreateRel:
                    {
                        int id = r.ReadVarInt32();
                        int src = r.ReadVarInt32();
                        int dst = r.ReadVarInt32();
                        int type = r.ReadVarInt32();
                        var props = r.ReadProps();
                        CreateRelAt(id, src, dst, type, props, null);
                        break;
                    }
                case WalOp.DeleteRel: DeleteRel(r.ReadVarInt32(), null); break;
                case WalOp.SetNodeProp:
                    {
                        int id = r.ReadVarInt32();
                        int key = r.ReadVarInt32();
                        SetNodeProp(id, key, r.ReadValue(), null);
                        break;
                    }
                case WalOp.SetRelProp:
                    {
                        int id = r.ReadVarInt32();
                        int key = r.ReadVarInt32();
                        SetRelProp(id, key, r.ReadValue(), null);
                        break;
                    }
                case WalOp.AddLabel:
                    {
                        int id = r.ReadVarInt32();
                        AddLabel(id, r.ReadVarInt32(), null);
                        break;
                    }
                case WalOp.RemoveLabel:
                    {
                        int id = r.ReadVarInt32();
                        RemoveLabel(id, r.ReadVarInt32(), null);
                        break;
                    }
                case WalOp.CreateIndex:
                    {
                        var name = r.ReadString();
                        int label = r.ReadVarInt32();
                        int key = r.ReadVarInt32();
                        bool unique = r.ReadByte() != 0;
                        CreateIndex(name, label, key, unique, null);
                        break;
                    }
                case WalOp.DropIndex: DropIndex(r.ReadString(), null); break;
                default: throw new InvalidDataException($"Unknown WAL op {op}");
            }
        }
    }

    public TokenTable TokensOf(TokenKind k) => k switch
    {
        TokenKind.Label => Labels,
        TokenKind.RelType => RelTypes,
        _ => PropKeys,
    };

    /// <summary>Writes definitions of tokens created since the last commit.</summary>
    public void WriteNewTokens(BinWriter w)
    {
        WriteNew(w, TokenKind.Label, Labels);
        WriteNew(w, TokenKind.RelType, RelTypes);
        WriteNew(w, TokenKind.PropKey, PropKeys);

        static void WriteNew(BinWriter w, TokenKind kind, TokenTable t)
        {
            for (int i = t.PersistedCount; i < t.Count; i++)
            {
                w.WriteByte((byte)WalOp.Token);
                w.WriteByte((byte)kind);
                w.WriteVarUInt((ulong)i);
                w.WriteString(t.Name(i));
            }
        }
    }

    public void MarkTokensPersisted()
    {
        Labels.PersistedCount = Labels.Count;
        RelTypes.PersistedCount = RelTypes.Count;
        PropKeys.PersistedCount = PropKeys.Count;
    }
}
