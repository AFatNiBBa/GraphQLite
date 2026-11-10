using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace GraphDb.Runtime;

/// <summary>
/// 64-bit fingerprint of the rows returned to a client. A concurrent transaction that has to be replayed at commit
/// compares the fingerprint of each statement with the one of its first execution: equal results mean that the
/// client saw what a serial execution would have shown. Only used within one process, never persisted.
/// </summary>
internal static class ResultHash
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h, ulong x)
    {
        h ^= x * 0xFF51AFD7ED558CCDUL;
        return BitOperations.RotateLeft(h, 31) * 0x9E3779B97F4A7C15UL;
    }

    public static ulong Row(ulong h, ReadOnlySpan<Value> row)
    {
        h = Mix(h, (ulong)row.Length);
        foreach (ref readonly var v in row) h = Add(h, v);
        return h;
    }

    public static ulong Stats(ulong h, QueryStatistics s) =>
        Mix(Mix(Mix(Mix(Mix(Mix(Mix(Mix(Mix(h, (ulong)s.NodesCreated), (ulong)s.NodesDeleted), (ulong)s.RelationshipsCreated),
            (ulong)s.RelationshipsDeleted), (ulong)s.PropertiesSet), (ulong)s.LabelsAdded), (ulong)s.LabelsRemoved),
            (ulong)s.IndexesAdded), (ulong)s.IndexesRemoved);

    public static ulong Add(ulong h, in Value v)
    {
        h = Mix(h, (ulong)v.Kind + 1);
        switch (v.Kind)
        {
            case ValueKind.Null:
                return h;
            case ValueKind.String:
                return Str(h, v.Str);
            case ValueKind.List:
                {
                    var items = v.ListItems;
                    h = Mix(h, (ulong)items.Length);
                    foreach (var x in items) h = Add(h, x);
                    return h;
                }
            case ValueKind.Map:
                return Map(h, v.MapItems);
            case ValueKind.Node:
                {
                    h = Mix(h, (ulong)v.Num);
                    if (v.Obj is not Node n) return h;
                    foreach (var l in n.Labels) h = Str(h, l);
                    return Map(h, n.Properties);
                }
            case ValueKind.Relationship:
                {
                    h = Mix(h, (ulong)v.Num);
                    if (v.Obj is not Relationship r) return h;
                    h = Mix(Mix(Str(h, r.Type), (ulong)r.StartNodeId), (ulong)r.EndNodeId);
                    return Map(h, r.Properties);
                }
            case ValueKind.Path:
                {
                    var p = (GraphPath)v.Obj!;
                    var nodes = p.MaterializedNodes;
                    var rels = p.MaterializedRels;
                    for (int i = 0; i < p.NodeIds.Length; i++)
                        h = Add(h, nodes is null ? Value.FromNodeId(p.NodeIds[i]) : Value.FromNode(nodes[i]));
                    for (int i = 0; i < p.RelIds.Length; i++)
                        h = Add(h, rels is null ? Value.FromRelId(p.RelIds[i]) : Value.FromRel(rels[i]));
                    return h;
                }
            case ValueKind.Duration:
            case ValueKind.Point:
                return Str(h, v.Obj!.ToString()!);
            default:
                // scalars and temporals: the number, plus the zone when there is one
                h = Mix(h, (ulong)v.Num);
                return v.Obj is null ? h : Str(h, v.Obj.ToString()!);
        }
    }

    /// <summary>Order-independent: the same map built in a different order has the same hash.</summary>
    private static ulong Map(ulong h, IReadOnlyDictionary<string, Value> m)
    {
        ulong acc = 0;
        foreach (var (k, x) in m) acc += Mix(Str(0, k), Add(0, x));
        return Mix(Mix(h, (ulong)m.Count), acc);
    }

    private static ulong Str(ulong h, string s)
    {
        var bytes = MemoryMarshal.AsBytes(s.AsSpan());
        h = Mix(h, (ulong)bytes.Length);
        while (bytes.Length >= 8)
        {
            h = Mix(h, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            bytes = bytes[8..];
        }
        ulong tail = 0;
        for (int i = 0; i < bytes.Length; i++) tail |= (ulong)bytes[i] << (8 * i);
        return Mix(h, tail);
    }
}
