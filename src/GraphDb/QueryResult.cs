using System.Collections;
using GraphDb.Storage;

namespace GraphDb;

/// <summary>Counters describing the updates performed by a query.</summary>
public sealed record QueryStatistics(
    int NodesCreated,
    int NodesDeleted,
    int RelationshipsCreated,
    int RelationshipsDeleted,
    int PropertiesSet,
    int LabelsAdded,
    int LabelsRemoved,
    int IndexesAdded,
    int IndexesRemoved)
{
    public static readonly QueryStatistics Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public bool ContainsUpdates =>
        NodesCreated + NodesDeleted + RelationshipsCreated + RelationshipsDeleted + PropertiesSet + LabelsAdded + LabelsRemoved +
        IndexesAdded + IndexesRemoved > 0;
}

/// <summary>One result row.</summary>
public sealed class Record : IReadOnlyDictionary<string, Value>
{
    private readonly QueryResult _owner;
    private readonly Value[] _values;

    internal Record(QueryResult owner, Value[] values)
    {
        _owner = owner;
        _values = values;
    }

    public IReadOnlyList<string> Keys => _owner.Columns;
    public IReadOnlyList<Value> Values => _values;
    public int Count => _values.Length;

    public Value this[int index] => _values[index];

    public Value this[string key] =>
        _owner.IndexOf(key) is var i and >= 0 ? _values[i] : throw new KeyNotFoundException($"No column named '{key}'");

    public T Get<T>(string key) => Convert<T>(this[key]);
    public T Get<T>(int index) => Convert<T>(_values[index]);

    internal static T Convert<T>(Value v)
    {
        if (typeof(T) == typeof(Value)) return (T)(object)v;
        if (v.IsNull)
        {
            if (default(T) is null) return default!;
            throw new InvalidCastException($"Cannot convert null to {typeof(T).Name}");
        }
        object? o = typeof(T) switch
        {
            var t when t == typeof(long) || t == typeof(long?) => v.AsInt64(),
            var t when t == typeof(int) || t == typeof(int?) => v.AsInt32(),
            var t when t == typeof(double) || t == typeof(double?) => v.AsDouble(),
            var t when t == typeof(float) || t == typeof(float?) => (float)v.AsDouble(),
            var t when t == typeof(bool) || t == typeof(bool?) => v.AsBoolean(),
            var t when t == typeof(string) => v.Kind == ValueKind.String ? v.AsString() : v.ToString(),
            var t when t == typeof(Node) => v.AsNode(),
            var t when t == typeof(Relationship) => v.AsRelationship(),
            var t when t == typeof(GraphPath) => v.AsPath(),
            var t when t == typeof(IReadOnlyList<Value>) => v.AsList(),
            var t when t == typeof(IReadOnlyDictionary<string, Value>) => v.AsMap(),
            _ => v.ToObject(),
        };
        return (T)o!;
    }

    public bool ContainsKey(string key) => _owner.IndexOf(key) >= 0;

    public bool TryGetValue(string key, out Value value)
    {
        int i = _owner.IndexOf(key);
        value = i >= 0 ? _values[i] : default;
        return i >= 0;
    }

    IEnumerable<string> IReadOnlyDictionary<string, Value>.Keys => _owner.Columns;
    IEnumerable<Value> IReadOnlyDictionary<string, Value>.Values => _values;

    public IEnumerator<KeyValuePair<string, Value>> GetEnumerator()
    {
        for (int i = 0; i < _values.Length; i++) yield return new(_owner.Columns[i], _values[i]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => "{" + string.Join(", ", this.Select(kv => $"{kv.Key}: {kv.Value}")) + "}";
}

/// <summary>Fully materialized result of a query. Safe to use after the transaction ended.</summary>
public sealed class QueryResult : IReadOnlyList<Record>
{
    private readonly List<Record> _rows;
    private readonly Dictionary<string, int> _colIndex;

    public IReadOnlyList<string> Columns { get; }
    public QueryStatistics Statistics { get; }

    internal QueryResult(string[] columns, List<Value[]> rows, QueryStatistics stats)
    {
        Columns = columns;
        Statistics = stats;
        _colIndex = new Dictionary<string, int>(columns.Length, StringComparer.Ordinal);
        for (int i = 0; i < columns.Length; i++) _colIndex.TryAdd(columns[i], i);
        _rows = new List<Record>(rows.Count);
        foreach (var r in rows) _rows.Add(new Record(this, r));
    }

    internal int IndexOf(string column) => _colIndex.TryGetValue(column, out var i) ? i : -1;

    public int Count => _rows.Count;
    public Record this[int index] => _rows[index];
    public IEnumerator<Record> GetEnumerator() => _rows.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>First column of the first row (or default when empty).</summary>
    public T? Scalar<T>() => _rows.Count == 0 || Columns.Count == 0 ? default : _rows[0].Get<T>(0);

    /// <summary>Renders the result as an ASCII table.</summary>
    public string ToTable(int maxRows = 100)
    {
        var cells = new List<string[]> { Columns.ToArray() };
        foreach (var r in _rows.Take(maxRows)) cells.Add(r.Values.Select(v => v.ToString()).ToArray());
        var widths = new int[Columns.Count];
        foreach (var row in cells)
            for (int i = 0; i < row.Length; i++) widths[i] = Math.Min(60, Math.Max(widths[i], row[i].Length));
        var sb = new System.Text.StringBuilder();
        string Sep() => "+" + string.Join("+", widths.Select(w => new string('-', w + 2))) + "+";
        sb.AppendLine(Sep());
        for (int r = 0; r < cells.Count; r++)
        {
            sb.Append('|');
            for (int i = 0; i < widths.Length; i++)
            {
                var s = cells[r][i];
                if (s.Length > widths[i]) s = s[..(widths[i] - 1)] + "…";
                sb.Append(' ').Append(s.PadRight(widths[i])).Append(" |");
            }
            sb.AppendLine();
            if (r == 0) sb.AppendLine(Sep());
        }
        sb.AppendLine(Sep());
        sb.Append(_rows.Count).Append(_rows.Count == 1 ? " row" : " rows");
        if (_rows.Count > maxRows) sb.Append($" ({maxRows} shown)");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ materialization

    internal sealed class Materializer(GraphStore store)
    {
        private readonly Dictionary<int, Node> _nodes = new();
        private readonly Dictionary<int, Relationship> _rels = new();

        public Value Convert(in Value v)
        {
            switch (v.Kind)
            {
                case ValueKind.Node:
                    return Value.FromNode(GetNode(v.EntityId));
                case ValueKind.Relationship:
                    return Value.FromRel(GetRel(v.EntityId));
                case ValueKind.Path:
                    {
                        var p = (GraphPath)v.Obj!;
                        return Value.FromPath(new GraphPath(p.NodeIds, p.RelIds, p.NodeIds.Select(GetNode).ToArray(), p.RelIds.Select(GetRel).ToArray()));
                    }
                case ValueKind.List:
                    {
                        var items = v.ListItems;
                        Value[]? copy = null;
                        for (int i = 0; i < items.Length; i++)
                        {
                            if (!NeedsWork(items[i])) continue;
                            copy ??= (Value[])items.Clone();
                            copy[i] = Convert(items[i]);
                        }
                        return copy is null ? v : Value.FromList(copy);
                    }
                case ValueKind.Map:
                    {
                        var m = v.MapItems;
                        if (!m.Values.Any(NeedsWork)) return v;
                        var d = new Dictionary<string, Value>(m.Count);
                        foreach (var kv in m) d[kv.Key] = Convert(kv.Value);
                        return Value.FromMap(d);
                    }
                default:
                    return v;
            }
        }

        private static bool NeedsWork(Value v) =>
            v.Kind is ValueKind.Node or ValueKind.Relationship or ValueKind.Path || v.Kind == ValueKind.List && v.ListItems.Any(NeedsWork) ||
            v.Kind == ValueKind.Map && v.MapItems.Values.Any(NeedsWork);

        private Node GetNode(int id)
        {
            if (!_nodes.TryGetValue(id, out var n)) _nodes[id] = n = store.MaterializeNode(id);
            return n;
        }

        private Relationship GetRel(int id)
        {
            if (!_rels.TryGetValue(id, out var r)) _rels[id] = r = store.MaterializeRel(id);
            return r;
        }
    }
}
