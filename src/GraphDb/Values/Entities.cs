using System.Text;

namespace GraphDb;

/// <summary>Snapshot of a node returned by a query.</summary>
public sealed class Node : IEquatable<Node>
{
    public long Id { get; }
    public IReadOnlyList<string> Labels { get; }
    public IReadOnlyDictionary<string, Value> Properties { get; }

    public Node(long id, IReadOnlyList<string> labels, IReadOnlyDictionary<string, Value> properties)
    {
        Id = id;
        Labels = labels;
        Properties = properties;
    }

    public Value this[string key] => Properties.TryGetValue(key, out var v) ? v : Value.Null;
    public bool HasLabel(string label) => Labels.Contains(label);

    public bool Equals(Node? other) => other is not null && other.Id == Id;
    public override bool Equals(object? obj) => obj is Node n && Equals(n);
    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString()
    {
        var sb = new StringBuilder("(");
        foreach (var l in Labels) sb.Append(':').Append(l);
        if (Properties.Count > 0)
        {
            if (Labels.Count > 0) sb.Append(' ');
            Value.AppendMap(sb, Properties);
        }
        return sb.Append(')').ToString();
    }
}

/// <summary>Snapshot of a relationship returned by a query.</summary>
public sealed class Relationship : IEquatable<Relationship>
{
    public long Id { get; }
    public string Type { get; }
    public long StartNodeId { get; }
    public long EndNodeId { get; }
    public IReadOnlyDictionary<string, Value> Properties { get; }

    public Relationship(long id, string type, long start, long end, IReadOnlyDictionary<string, Value> properties)
    {
        Id = id;
        Type = type;
        StartNodeId = start;
        EndNodeId = end;
        Properties = properties;
    }

    public Value this[string key] => Properties.TryGetValue(key, out var v) ? v : Value.Null;

    public bool Equals(Relationship? other) => other is not null && other.Id == Id;
    public override bool Equals(object? obj) => obj is Relationship r && Equals(r);
    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString()
    {
        var sb = new StringBuilder("[:").Append(Type);
        if (Properties.Count > 0)
        {
            sb.Append(' ');
            Value.AppendMap(sb, Properties);
        }
        return sb.Append(']').ToString();
    }
}

/// <summary>A path: alternating nodes and relationships.</summary>
public sealed class GraphPath
{
    internal readonly long[] NodeIds;
    internal readonly long[] RelIds;
    private readonly Node[]? _nodes;
    private readonly Relationship[]? _rels;

    internal GraphPath(long[] nodeIds, long[] relIds)
    {
        NodeIds = nodeIds;
        RelIds = relIds;
    }

    internal GraphPath(long[] nodeIds, long[] relIds, Node[] nodes, Relationship[] rels) : this(nodeIds, relIds)
    {
        _nodes = nodes;
        _rels = rels;
    }

    public int Length => RelIds.Length;
    public IReadOnlyList<Node> Nodes => _nodes ?? throw new InvalidOperationException("GraphPath is not materialized");
    public IReadOnlyList<Relationship> Relationships => _rels ?? throw new InvalidOperationException("GraphPath is not materialized");
    public Node Start => Nodes[0];
    public Node End => Nodes[^1];

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < NodeIds.Length; i++)
        {
            if (_nodes != null) sb.Append(_nodes[i]); else sb.Append('(').Append(NodeIds[i]).Append(')');
            if (i < RelIds.Length)
            {
                bool forward = _rels == null || _rels[i].StartNodeId == NodeIds[i];
                sb.Append(forward ? "-" : "<-");
                if (_rels != null) sb.Append(_rels[i]); else sb.Append('[').Append(RelIds[i]).Append(']');
                sb.Append(forward ? "->" : "-");
            }
        }
        return sb.ToString();
    }

    internal bool SameAs(GraphPath o) => NodeIds.AsSpan().SequenceEqual(o.NodeIds) && RelIds.AsSpan().SequenceEqual(o.RelIds);
}
