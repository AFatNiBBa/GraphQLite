namespace GraphDb.Server.Bolt;

/// <summary>Conversion between GraphDb values and Bolt (PackStream) structures.</summary>
internal static class BoltValues
{
    private const long EpochTicks = 621_355_968_000_000_000; // 1970-01-01T00:00:00
    private const int EpochDay = 719_162;                    // DateOnly(1970, 1, 1).DayNumber
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    // structure tags
    public const byte Node = 0x4E, Relationship = 0x52, UnboundRelationship = 0x72, Path = 0x50;
    public const byte Date = 0x44, Time = 0x54, LocalTime = 0x74, LocalDateTime = 0x64, Duration = 0x45;
    public const byte DateTime = 0x49, DateTimeZoneId = 0x69, LegacyDateTime = 0x46, LegacyDateTimeZoneId = 0x66;
    public const byte Point2D = 0x58, Point3D = 0x59;

    /// <summary>Element id of a node or relationship (same format as the <c>elementId()</c> function).</summary>
    public static string NodeElementId(long id) => "n:" + id;
    public static string RelElementId(long id) => "r:" + id;

    /// <summary>Encodes a value. <paramref name="v5"/>: Bolt 5 entities (element ids);
    /// <paramref name="utc"/>: UTC-based DateTime structures (Bolt 5, or 4.4 with the "utc" patch).</summary>
    public static void Write(PackWriter w, in Value v, bool v5, bool utc)
    {
        switch (v.Kind)
        {
            case ValueKind.Null: w.Null(); return;
            case ValueKind.Boolean: w.Bool(v.AsBoolean()); return;
            case ValueKind.Integer: w.Int(v.AsInt64()); return;
            case ValueKind.Float: w.Float(v.AsDouble()); return;
            case ValueKind.String: w.String(v.AsString()); return;
            case ValueKind.List:
                {
                    var items = v.AsListSpan();
                    w.ListHeader(items.Length);
                    foreach (var x in items) Write(w, x, v5, utc);
                    return;
                }
            case ValueKind.Map:
                WriteMap(w, v.AsMap(), v5, utc);
                return;
            case ValueKind.Node:
                WriteNode(w, v.AsNode(), v5, utc);
                return;
            case ValueKind.Relationship:
                {
                    var r = v.AsRelationship();
                    w.StructHeader(v5 ? 8 : 5, Relationship);
                    w.Int(r.Id);
                    w.Int(r.StartNodeId);
                    w.Int(r.EndNodeId);
                    w.String(r.Type);
                    WriteMap(w, r.Properties, v5, utc);
                    if (v5)
                    {
                        w.String(RelElementId(r.Id));
                        w.String(NodeElementId(r.StartNodeId));
                        w.String(NodeElementId(r.EndNodeId));
                    }
                    return;
                }
            case ValueKind.Path:
                WritePath(w, v.AsPath(), v5, utc);
                return;
            case ValueKind.Date:
                w.StructHeader(1, Date);
                w.Int(v.AsDate().DayNumber - EpochDay);
                return;
            case ValueKind.LocalTime:
                w.StructHeader(1, LocalTime);
                w.Int(v.AsLocalTime().Ticks * 100);
                return;
            case ValueKind.Time:
                w.StructHeader(2, Time);
                w.Int(v.AsLocalTime().Ticks * 100);
                w.Int((long)v.AsOffset().TotalSeconds);
                return;
            case ValueKind.LocalDateTime:
                {
                    var (s, n) = Split(v.AsLocalDateTime().Ticks - EpochTicks);
                    w.StructHeader(2, LocalDateTime);
                    w.Int(s);
                    w.Int(n);
                    return;
                }
            case ValueKind.DateTime:
                {
                    var dto = v.AsDateTime();
                    var zone = ZoneName(v);
                    long ticks = (utc ? dto.UtcTicks : dto.Ticks) - EpochTicks;
                    var (s, n) = Split(ticks);
                    w.StructHeader(3, zone is null ? (utc ? DateTime : LegacyDateTime) : (utc ? DateTimeZoneId : LegacyDateTimeZoneId));
                    w.Int(s);
                    w.Int(n);
                    if (zone is null) w.Int((long)dto.Offset.TotalSeconds);
                    else w.String(zone);
                    return;
                }
            case ValueKind.Duration:
                {
                    var d = v.AsDuration();
                    w.StructHeader(4, Duration);
                    w.Int(d.Months);
                    w.Int(d.Days);
                    w.Int(d.Seconds);
                    w.Int(d.Nanoseconds);
                    return;
                }
            case ValueKind.Point:
                {
                    var p = v.AsPoint();
                    w.StructHeader(p.Z.HasValue ? 4 : 3, p.Z.HasValue ? Point3D : Point2D);
                    w.Int(p.Srid);
                    w.Float(p.X);
                    w.Float(p.Y);
                    if (p.Z.HasValue) w.Float(p.Z.Value);
                    return;
                }
        }
        throw new InvalidOperationException($"Cannot send a value of kind {v.Kind}");
    }

    /// <summary>Named zone of a DateTime value (null for a fixed offset).</summary>
    private static string? ZoneName(in Value v) => v.Zone.Name;

    /// <summary>Splits ticks since the epoch into (seconds, nanoseconds) with non-negative nanoseconds.</summary>
    private static (long Seconds, long Nanos) Split(long ticks)
    {
        long s = Math.DivRem(ticks, TicksPerSecond, out long rem);
        if (rem < 0)
        {
            rem += TicksPerSecond;
            s--;
        }
        return (s, rem * 100);
    }

    private static void WriteMap(PackWriter w, IReadOnlyDictionary<string, Value> map, bool v5, bool utc)
    {
        w.MapHeader(map.Count);
        foreach (var (k, x) in map)
        {
            w.String(k);
            Write(w, x, v5, utc);
        }
    }

    private static void WriteNode(PackWriter w, GraphDb.Node n, bool v5, bool utc)
    {
        w.StructHeader(v5 ? 4 : 3, Node);
        w.Int(n.Id);
        w.ListHeader(n.Labels.Count);
        foreach (var l in n.Labels) w.String(l);
        WriteMap(w, n.Properties, v5, utc);
        if (v5) w.String(NodeElementId(n.Id));
    }

    private static void WritePath(PackWriter w, GraphPath p, bool v5, bool utc)
    {
        // unique nodes and relationships, then the traversal as (relationship index, node index) pairs
        var nodes = new List<GraphDb.Node>();
        var nodeIndex = new Dictionary<long, int>();
        var rels = new List<GraphDb.Relationship>();
        var relIndex = new Dictionary<long, int>();
        var indices = new List<long>();
        foreach (var n in p.Nodes)
            if (nodeIndex.TryAdd(n.Id, nodes.Count)) nodes.Add(n);
        for (int i = 0; i < p.Relationships.Count; i++)
        {
            var r = p.Relationships[i];
            if (!relIndex.TryGetValue(r.Id, out int ri))
            {
                ri = rels.Count;
                relIndex[r.Id] = ri;
                rels.Add(r);
            }
            bool forward = r.StartNodeId == p.Nodes[i].Id;
            indices.Add(forward ? ri + 1 : -(ri + 1));
            indices.Add(nodeIndex[p.Nodes[i + 1].Id]);
        }
        w.StructHeader(3, Path);
        w.ListHeader(nodes.Count);
        foreach (var n in nodes) WriteNode(w, n, v5, utc);
        w.ListHeader(rels.Count);
        foreach (var r in rels)
        {
            w.StructHeader(v5 ? 4 : 3, UnboundRelationship);
            w.Int(r.Id);
            w.String(r.Type);
            WriteMap(w, r.Properties, v5, utc);
            if (v5) w.String(RelElementId(r.Id));
        }
        w.ListHeader(indices.Count);
        foreach (var x in indices) w.Int(x);
    }

    // ------------------------------------------------------------------ decoding (query parameters)

    /// <summary>Converts a decoded PackStream value into a GraphDb value.</summary>
    public static Value ToValue(object? o) => o switch
    {
        null => Value.Null,
        bool b => Value.FromBool(b),
        long l => Value.FromInt(l),
        double d => Value.FromFloat(d),
        string s => Value.FromString(s),
        byte[] bytes => Value.FromList(bytes.Select(x => Value.FromInt(x)).ToArray()),
        List<object?> list => Value.FromList(list.Select(ToValue).ToArray()),
        Dictionary<string, object?> map => Value.FromMap(map.ToDictionary(kv => kv.Key, kv => ToValue(kv.Value))),
        PackStruct st => FromStruct(st),
        _ => throw new BoltProtocolException($"Unsupported value {o.GetType().Name}"),
    };

    private static long L(PackStruct s, int i) => s.Fields[i] is long l ? l : throw Bad(s);
    private static double D(PackStruct s, int i) => s.Fields[i] switch { double d => d, long l => l, _ => throw Bad(s) };
    private static string Str(PackStruct s, int i) => s.Fields[i] as string ?? throw Bad(s);
    private static BoltProtocolException Bad(PackStruct s) => new($"Invalid structure 0x{s.Tag:X2}");

    private static long Ticks(long seconds, long nanos) => EpochTicks + seconds * TicksPerSecond + nanos / 100;

    private static Value FromStruct(PackStruct s)
    {
        int n = s.Fields.Length;
        switch (s.Tag)
        {
            case Date when n == 1:
                return Value.FromDate(DateOnly.FromDayNumber((int)(L(s, 0) + EpochDay)));
            case LocalTime when n == 1:
                return Value.FromLocalTime(new TimeOnly(L(s, 0) / 100));
            case Time when n == 2:
                return Value.FromTime(new TimeOnly(L(s, 0) / 100), TimeSpan.FromSeconds(L(s, 1)));
            case LocalDateTime when n == 2:
                return Value.FromLocalDateTime(new System.DateTime(Ticks(L(s, 0), L(s, 1))));
            case DateTime when n == 3:
                return Value.FromZonedUtc(Ticks(L(s, 0), L(s, 1)), ZoneInfo.FromOffset((int)L(s, 2)));
            case DateTimeZoneId when n == 3:
                return Value.FromZonedUtc(Ticks(L(s, 0), L(s, 1)), ZoneInfo.FromName(Str(s, 2)));
            case LegacyDateTime when n == 3:
                return Value.FromZonedUtc(Ticks(L(s, 0), L(s, 1)) - L(s, 2) * TicksPerSecond, ZoneInfo.FromOffset((int)L(s, 2)));
            case LegacyDateTimeZoneId when n == 3:
                return Temporal.MakeDateTime(Ticks(L(s, 0), L(s, 1)), ZoneInfo.FromName(Str(s, 2)));
            case Duration when n == 4:
                return Value.FromDuration(new CypherDuration(L(s, 0), L(s, 1), L(s, 2), L(s, 3)));
            case Point2D when n == 3:
                return Value.FromPoint(new CypherPoint((int)L(s, 0), D(s, 1), D(s, 2)));
            case Point3D when n == 4:
                return Value.FromPoint(new CypherPoint((int)L(s, 0), D(s, 1), D(s, 2), D(s, 3)));
            case Node or Relationship or UnboundRelationship or Path:
                throw new BoltClientException("Neo.ClientError.Statement.TypeError", "Nodes, relationships and paths cannot be used as parameters");
        }
        throw Bad(s);
    }
}
