using GraphQLite.Cypher;
using GraphQLite.Storage;

namespace GraphQLite.Runtime;

internal delegate PropertyMap? PropsBuilder(ExecContext ctx);
internal sealed record CreateNodeSpec(int Slot, TokenRef[] Labels, PropsBuilder? Props);
internal sealed record CreateRelSpec(int Slot, int FromSlot, int ToSlot, TokenRef Type, PropsBuilder? Props);
internal sealed record PathSpec(int PathSlot, int[] NodeSlots, int[] RelSlots);

internal sealed class CreateSpec(CreateNodeSpec[] nodes, CreateRelSpec[] rels, PathSpec[] paths)
{
    public readonly CreateNodeSpec[] Nodes = nodes;
    public readonly CreateRelSpec[] Rels = rels;
    public readonly PathSpec[] Paths = paths;

    public void Execute(ExecContext ctx)
    {
        var store = ctx.Store;
        var row = ctx.Row;
        foreach (var n in Nodes)
        {
            int[]? labels = null;
            if (n.Labels.Length > 0)
            {
                labels = new int[n.Labels.Length];
                for (int i = 0; i < labels.Length; i++) labels[i] = n.Labels[i].GetOrCreate(store);
            }
            var props = n.Props?.Invoke(ctx);
            long id = store.CreateNode(labels, props);
            ctx.Stats.NodesCreated++;
            ctx.Stats.LabelsAdded += labels?.Distinct().Count() ?? 0;
            ctx.Stats.PropertiesSet += props?.Count ?? 0;
            row[n.Slot] = Value.FromNodeId(id);
        }
        foreach (var r in Rels)
        {
            var a = row[r.FromSlot];
            var b = row[r.ToSlot];
            if (a.Kind != ValueKind.Node || b.Kind != ValueKind.Node)
                throw new CypherRuntimeException("Failed to create relationship: endpoint node is null");
            var props = r.Props?.Invoke(ctx);
            long id = store.CreateRel(a.EntityId, b.EntityId, r.Type.GetOrCreate(store), props);
            ctx.Stats.RelationshipsCreated++;
            ctx.Stats.PropertiesSet += props?.Count ?? 0;
            row[r.Slot] = Value.FromRelId(id);
        }
        foreach (var p in Paths) row[p.PathSlot] = BuildPathOp.Build(ctx, p.NodeSlots, p.RelSlots);
    }
}

internal sealed class CompiledSetItem(SetKind kind, Eval target, TokenRef? key, Eval? value, TokenRef[]? labels, string[]? subPath = null)
{
    public readonly SetKind Kind = kind;
    public readonly Eval Target = target;
    public readonly TokenRef? Key = key;
    public readonly Eval? Value = value;
    public readonly TokenRef[]? Labels = labels;
    /// <summary>Keys inside the map stored in property <see cref="Key"/> (<c>SET n.a.b.c = v</c>: Key = a, SubPath = [b, c]).</summary>
    public readonly string[]? SubPath = subPath;
}

internal static class Mutations
{
    public static PropertyMap? BuildProps(ExecContext ctx, Value map)
    {
        if (map.IsNull) return null;
        if (map.Kind != ValueKind.Map) throw new CypherRuntimeException($"Expected a map of properties, got {map.Kind}");
        var m = map.MapItems;
        if (m.Count == 0) return null;
        var props = new PropertyMap(m.Count);
        foreach (var kv in m)
        {
            if (kv.Value.IsNull) continue;
            StoreTx.ValidatePropertyValue(kv.Value);
            props.Set(ctx.Store.PropKeys.GetOrCreate(kv.Key), kv.Value);
        }
        return props.Count == 0 ? null : props;
    }

    private static void SetProp(ExecContext ctx, in Value entity, int key, in Value v)
    {
        if (entity.Kind == ValueKind.Node) ctx.Store.SetNodeProp(entity.EntityId, key, v);
        else ctx.Store.SetRelProp(entity.EntityId, key, v);
        ctx.Stats.PropertiesSet++;
    }

    private static PropertyMap? PropsOf(ExecContext ctx, in Value entity) =>
        ctx.Store.DecodeProps(entity.Kind == ValueKind.Node ? ctx.Store.NodePropsAddr(entity.EntityId) : ctx.Store.RelPropsAddr(entity.EntityId));

    private static void CheckEntity(ExecContext ctx, in Value e)
    {
        if (e.Kind == ValueKind.Node && !ctx.Store.NodeAlive(e.EntityId) || e.Kind == ValueKind.Relationship && !ctx.Store.RelAlive(e.EntityId))
            throw new CypherRuntimeException("Cannot set properties on a deleted entity");
    }

    public static void ApplySet(ExecContext ctx, CompiledSetItem item)
    {
        var target = item.Target(ctx);
        if (target.IsNull) return;
        var store = ctx.Store;
        switch (item.Kind)
        {
            case SetKind.Property:
                {
                    if (target.Kind is not (ValueKind.Node or ValueKind.Relationship))
                        throw new CypherRuntimeException($"SET requires a node or relationship, got {target.Kind}");
                    CheckEntity(ctx, target);
                    var v = item.Value!(ctx);
                    if (item.SubPath is { Length: > 0 })
                    {
                        SetNested(ctx, target, item, v);
                        break;
                    }
                    if (v.IsNull)
                    {
                        int k = item.Key!.Resolve(store);
                        if (k >= 0) SetProp(ctx, target, k, v);
                    }
                    else SetProp(ctx, target, item.Key!.GetOrCreate(store), v);
                    break;
                }
            case SetKind.ReplaceMap:
            case SetKind.MergeMap:
                {
                    if (target.Kind is not (ValueKind.Node or ValueKind.Relationship))
                        throw new CypherRuntimeException($"SET requires a node or relationship, got {target.Kind}");
                    CheckEntity(ctx, target);
                    var src = item.Value!(ctx);
                    Dictionary<string, Value> map = src.Kind switch
                    {
                        ValueKind.Map => src.MapItems,
                        ValueKind.Node => store.PropsDict(store.NodePropsAddr(src.EntityId)),
                        ValueKind.Relationship => store.PropsDict(store.RelPropsAddr(src.EntityId)),
                        ValueKind.Null => new Dictionary<string, Value>(),
                        _ => throw new CypherRuntimeException($"Expected a map, got {src.Kind}"),
                    };
                    if (item.Kind == SetKind.ReplaceMap && PropsOf(ctx, target) is { } existing)
                    {
                        foreach (var k in existing.Keys.ToArray())
                            if (!map.ContainsKey(store.PropKeys.Name(k))) SetProp(ctx, target, k, Value.Null);
                    }
                    foreach (var kv in map)
                    {
                        if (kv.Value.IsNull)
                        {
                            int k = store.PropKeys.Lookup(kv.Key);
                            if (k >= 0) SetProp(ctx, target, k, Value.Null);
                        }
                        else SetProp(ctx, target, store.PropKeys.GetOrCreate(kv.Key), kv.Value);
                    }
                    break;
                }
            case SetKind.Labels:
                {
                    if (target.Kind != ValueKind.Node) throw new CypherRuntimeException("Labels can only be set on nodes");
                    CheckEntity(ctx, target);
                    foreach (var l in item.Labels!)
                        if (store.AddLabel(target.EntityId, l.GetOrCreate(store))) ctx.Stats.LabelsAdded++;
                    break;
                }
        }
    }

    public static void ApplyRemove(ExecContext ctx, CompiledSetItem item)
    {
        var target = item.Target(ctx);
        if (target.IsNull) return;
        var store = ctx.Store;
        if (item.Kind == SetKind.Labels)
        {
            if (target.Kind != ValueKind.Node) throw new CypherRuntimeException("Labels can only be removed from nodes");
            if (!store.NodeAlive(target.EntityId)) return;
            foreach (var l in item.Labels!)
            {
                int id = l.Resolve(store);
                if (id >= 0 && store.RemoveLabel(target.EntityId, id)) ctx.Stats.LabelsRemoved++;
            }
            return;
        }
        if (target.Kind is not (ValueKind.Node or ValueKind.Relationship))
            throw new CypherRuntimeException($"REMOVE requires a node or relationship, got {target.Kind}");
        int key = item.Key!.Resolve(store);
        if (key < 0) return;
        if (target.Kind == ValueKind.Node && !store.NodeAlive(target.EntityId)) return;
        if (target.Kind == ValueKind.Relationship && !store.RelAlive(target.EntityId)) return;
        if (item.SubPath is { Length: > 0 })
        {
            SetNested(ctx, target, item, Value.Null);
            return;
        }
        var cur = target.Kind == ValueKind.Node ? store.NodeProp(target.EntityId, key) : store.RelProp(target.EntityId, key);
        if (cur.IsNull) return;
        SetProp(ctx, target, key, Value.Null);
    }

    /// <summary>
    /// <c>SET n.a.b.c = v</c> / <c>REMOVE n.a.b.c</c>: rebuilds the maps along the path inside property <c>a</c>.
    /// Missing maps are created; null removes the key (and leaves the property untouched if the path does not exist).
    /// </summary>
    private static void SetNested(ExecContext ctx, in Value target, CompiledSetItem item, Value v)
    {
        var store = ctx.Store;
        int key = v.IsNull ? item.Key!.Resolve(store) : item.Key!.GetOrCreate(store);
        if (key < 0) return;
        var cur = target.Kind == ValueKind.Node ? store.NodeProp(target.EntityId, key) : store.RelProp(target.EntityId, key);
        if (v.IsNull && cur.IsNull) return;
        var updated = SetIn(cur, item.SubPath!, 0, v, out bool changed);
        if (changed) SetProp(ctx, target, key, updated);
    }

    private static Value SetIn(in Value cur, string[] path, int i, in Value v, out bool changed)
    {
        if (i == path.Length)
        {
            changed = true;
            return v;
        }
        if (cur.IsNull)
        {
            if (v.IsNull)
            {
                changed = false;
                return cur;
            }
        }
        else if (cur.Kind != ValueKind.Map)
        {
            throw new CypherRuntimeException($"Cannot set key '{path[i]}' of a {cur.Kind.ToString().ToLowerInvariant()}: only maps have keys");
        }
        var map = cur.IsNull ? new Dictionary<string, Value>() : new Dictionary<string, Value>(cur.MapItems);
        var child = SetIn(map.GetValueOrDefault(path[i]), path, i + 1, v, out changed);
        if (!changed) return cur;
        if (child.IsNull)
        {
            if (!map.Remove(path[i]))
            {
                changed = false;
                return cur;
            }
        }
        else map[path[i]] = child;
        return Value.FromMap(map);
    }

    public static void Delete(ExecContext ctx, in Value v, bool detach)
    {
        var store = ctx.Store;
        switch (v.Kind)
        {
            case ValueKind.Null: return;
            case ValueKind.Relationship:
                if (store.RelAlive(v.EntityId))
                {
                    store.DeleteRel(v.EntityId);
                    ctx.Stats.RelationshipsDeleted++;
                }
                return;
            case ValueKind.Node:
                {
                    long id = v.EntityId;
                    if (!store.NodeAlive(id)) return;
                    if (detach)
                    {
                        foreach (var r in store.NodeRels(id))
                        {
                            if (!store.RelAlive(r)) continue;
                            store.DeleteRel(r);
                            ctx.Stats.RelationshipsDeleted++;
                        }
                    }
                    else
                    {
                        var (o, i) = store.Degree(id);
                        if (o > 0 || i > 0) (ctx.PendingNodeDeletes ??= new HashSet<long>()).Add(id);
                    }
                    store.DeleteNode(id);
                    ctx.Stats.NodesDeleted++;
                    return;
                }
            case ValueKind.Path:
                {
                    var p = (GraphPath)v.Obj!;
                    foreach (var r in p.RelIds) Delete(ctx, Value.FromRelId(r), false);
                    foreach (var n in p.NodeIds) Delete(ctx, Value.FromNodeId(n), detach);
                    return;
                }
            case ValueKind.List:
                foreach (var x in v.ListItems) Delete(ctx, x, detach);
                return;
            default:
                throw new CypherRuntimeException($"DELETE expects a node, relationship or path, got {v.Kind}");
        }
    }
}

internal sealed class CreateOp(Operator input, CreateSpec spec) : UnaryOperator(input)
{
    public override string Name => "Create";
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, spec);

    private sealed class C(Cursor input, ExecContext ctx, CreateSpec spec) : Cursor
    {
        public override bool Next()
        {
            if (!input.Next()) return false;
            spec.Execute(ctx);
            return true;
        }
    }
}

internal sealed class MergeOp(Operator input, Operator match, CreateSpec create, CompiledSetItem[] onCreate, CompiledSetItem[] onMatch)
    : UnaryOperator(input)
{
    private readonly Operator match = match;
    private readonly CreateSpec create = create;
    private readonly CompiledSetItem[] onCreate = onCreate;
    private readonly CompiledSetItem[] onMatch = onMatch;

    public override string Name => "Merge";
    public override IEnumerable<Operator> Children => [Input, match];
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C(MergeOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private Cursor? _match;

        public override bool Next()
        {
            while (true)
            {
                if (_match != null)
                {
                    if (_match.Next())
                    {
                        foreach (var s in op.onMatch) Mutations.ApplySet(ctx, s);
                        return true;
                    }
                    _match = null;
                }
                if (!input.Next()) return false;
                var m = op.match.Open(ctx);
                if (m.Next())
                {
                    foreach (var s in op.onMatch) Mutations.ApplySet(ctx, s);
                    _match = m;
                    return true;
                }
                op.create.Execute(ctx);
                foreach (var s in op.onCreate) Mutations.ApplySet(ctx, s);
                return true;
            }
        }
    }
}

internal sealed class SetOp(Operator input, CompiledSetItem[] items, bool remove) : UnaryOperator(input)
{
    public override string Name => remove ? "Remove" : "SetProperties";
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, items, remove);

    private sealed class C(Cursor input, ExecContext ctx, CompiledSetItem[] items, bool remove) : Cursor
    {
        public override bool Next()
        {
            if (!input.Next()) return false;
            foreach (var it in items)
            {
                if (remove) Mutations.ApplyRemove(ctx, it);
                else Mutations.ApplySet(ctx, it);
            }
            return true;
        }
    }
}

internal sealed class DeleteOp(Operator input, Eval[] exprs, bool detach) : UnaryOperator(input)
{
    public override string Name => detach ? "DetachDelete" : "Delete";
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, exprs, detach);

    private sealed class C(Cursor input, ExecContext ctx, Eval[] exprs, bool detach) : Cursor
    {
        public override bool Next()
        {
            if (!input.Next()) return false;
            foreach (var e in exprs) Mutations.Delete(ctx, e(ctx), detach);
            return true;
        }
    }
}

internal sealed class ForeachOp(Operator input, int slot, Eval list, Operator body) : UnaryOperator(input)
{
    public override string Name => "Foreach";
    public override IEnumerable<Operator> Children => [Input, body];
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, slot, list, body);

    private sealed class C(Cursor input, ExecContext ctx, int slot, Eval list, Operator body) : Cursor
    {
        public override bool Next()
        {
            if (!input.Next()) return false;
            var v = list(ctx);
            if (v.IsNull) return true;
            var saved = RowOps.Snapshot(ctx.Row);
            var items = v.Kind == ValueKind.List ? v.ListItems : [v];
            foreach (var item in items)
            {
                ctx.Row[slot] = item;
                var c = body.Open(ctx);
                while (c.Next()) { }
                RowOps.Restore(saved, ctx.Row);
            }
            return true;
        }
    }
}
