namespace GraphDb.Tests;

public class FeatureTests : IDisposable
{
    private readonly GraphDatabase _db = GraphDatabase.OpenInMemory();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "gdbcsv_" + Guid.NewGuid().ToString("N"));

    public FeatureTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private QueryResult Q(string q, Dictionary<string, object?>? p = null) => _db.Execute(q, p);
    private List<T> Col<T>(string q, Dictionary<string, object?>? p = null) => Q(q, p).Select(r => r.Get<T>(0)).ToList();

    // ------------------------------------------------------------------ JSON / nested properties

    [Fact]
    public void Maps_And_Nested_Values_Are_Valid_Properties()
    {
        Q("CREATE (:Doc {id: 1, body: {title: 'Hello', tags: ['a', 'b'], meta: {views: 10, ratio: 0.5, nested: [{k: 1}, {k: 2}]}}})");
        var r = Q("MATCH (d:Doc) RETURN d.body.title AS t, d.body.meta.views AS v, d.body.meta.nested[1].k AS k, d.body['tags'] AS tags")[0];
        Assert.Equal("Hello", r.Get<string>("t"));
        Assert.Equal(10L, r.Get<long>("v"));
        Assert.Equal(2L, r.Get<long>("k"));
        Assert.Equal(2, r["tags"].AsList().Count);
        Assert.Equal(["Hello"], Col<string>("MATCH (d:Doc) WHERE d.body.meta.views > 5 RETURN d.body.title"));
        Q("MATCH (d:Doc) SET d.body = apoc.convert.fromJsonMap('{\"title\": \"New\", \"n\": [1, 2.5, null, true]}')");
        var b = Q("MATCH (d:Doc) RETURN d.body AS b")[0]["b"].AsMap();
        Assert.Equal("New", b["title"].AsString());
        Assert.Equal(2.5, b["n"].AsList()[1].AsDouble());
        Assert.Equal("{\"a\":[1,{\"b\":\"x\"}]}", Q("RETURN toJson({a: [1, {b: 'x'}]})").Scalar<string>());
        Assert.Throws<CypherRuntimeException>(() => Q("MATCH (d:Doc) WITH d MATCH (e:Doc) SET d.bad = e"));
    }

    [Fact]
    public void Json_Parameters_And_Large_Values()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"user\": {\"name\": \"Ann\", \"roles\": [\"admin\", \"dev\"]}}");
        Q("CREATE (:Cfg {data: $d})", new() { ["d"] = doc.RootElement });
        Assert.Equal("dev", Q("MATCH (c:Cfg) RETURN c.data.user.roles[1]").Scalar<string>());
        // a value larger than a page is stored in an overflow chain
        var big = string.Concat(Enumerable.Repeat("0123456789", 5000));
        Q("CREATE (:Big {text: $t, list: range(1, 3000)})", new() { ["t"] = big });
        var r = Q("MATCH (b:Big) RETURN size(b.text) AS s, size(b.list) AS n, b.list[2999] AS last")[0];
        Assert.Equal(50_000L, r.Get<long>("s"));
        Assert.Equal(3000L, r.Get<long>("n"));
        Assert.Equal(3000L, r.Get<long>("last"));
        Q("MATCH (b:Big) SET b.text = 'small'");
        Assert.Equal("small", Q("MATCH (b:Big) RETURN b.text").Scalar<string>());
    }

    // ------------------------------------------------------------------ temporal

    [Fact]
    public void Temporal_Types()
    {
        var r = Q("""
            WITH date('2024-01-31') AS d, datetime('2024-03-10T12:30:45.5+01:00') AS dt, localdatetime('2024-02-29T23:59') AS ldt,
                 duration('P1Y2M3DT4H5M6S') AS du, time('10:15:30Z') AS t, localtime('08:00') AS lt
            RETURN d.year AS y, d.month AS m, d.day AS dd, d.dayOfWeek AS dow, toString(d + duration('P1M')) AS nextMonth,
                   dt.hour AS h, dt.offset AS off, dt.epochMillis AS ms, toString(dt) AS dts,
                   toString(ldt + duration({days: 1})) AS ldt2, du.years AS dy, du.months AS dm, du.seconds AS ds,
                   toString(du) AS dus, t.minute AS tm, lt.hour AS lth,
                   d < date('2024-02-01') AS cmp, toString(duration.between(date('2024-01-01'), date('2024-03-15'))) AS between,
                   duration.inDays(date('2024-01-01'), date('2024-03-01')).days AS days,
                   toString(date.truncate('month', date('2024-05-17'))) AS trunc,
                   date({year: 2020, month: 2, day: 29}) = date('2020-02-29') AS eqMap
            """)[0];
        Assert.Equal(2024L, r.Get<long>("y"));
        Assert.Equal(1L, r.Get<long>("m"));
        Assert.Equal(31L, r.Get<long>("dd"));
        Assert.Equal(3L, r.Get<long>("dow"));
        Assert.Equal("2024-02-29", r.Get<string>("nextMonth"));
        Assert.Equal(12L, r.Get<long>("h"));
        Assert.Equal("+01:00", r.Get<string>("off"));
        Assert.Equal(new DateTimeOffset(2024, 3, 10, 11, 30, 45, 500, TimeSpan.Zero).ToUnixTimeMilliseconds(), r.Get<long>("ms"));
        Assert.Equal("2024-03-10T12:30:45.5+01:00", r.Get<string>("dts"));
        Assert.Equal("2024-03-01T23:59:00", r.Get<string>("ldt2"));
        Assert.Equal(1L, r.Get<long>("dy"));
        Assert.Equal(14L, r.Get<long>("dm"));
        Assert.Equal(4 * 3600 + 5 * 60 + 6L, r.Get<long>("ds"));
        Assert.Equal("P1Y2M3DT4H5M6S", r.Get<string>("dus"));
        Assert.Equal(15L, r.Get<long>("tm"));
        Assert.Equal(8L, r.Get<long>("lth"));
        Assert.True(r.Get<bool>("cmp"));
        Assert.Equal("P2M14D", r.Get<string>("between"));
        Assert.Equal(60L, r.Get<long>("days"));
        Assert.Equal("2024-05-01", r.Get<string>("trunc"));
        Assert.True(r.Get<bool>("eqMap"));
    }

    [Fact]
    public void Temporal_Properties_Index_And_Ordering()
    {
        Q("UNWIND range(0, 99) AS i CREATE (:Ev {at: date('2024-01-01') + duration({days: i}), n: i})");
        Q("CREATE INDEX FOR (e:Ev) ON (e.at)");
        var plan = Q("EXPLAIN MATCH (e:Ev) WHERE e.at >= date('2024-02-01') AND e.at < date('2024-03-01') RETURN e").Scalar<string>()!;
        Assert.Contains("NodeIndexSeekByRange", plan);
        Assert.Equal(29L, Q("MATCH (e:Ev) WHERE e.at >= date('2024-02-01') AND e.at < date('2024-03-01') RETURN count(e)").Scalar<long>());
        Assert.Equal([99L, 98L], Col<long>("MATCH (e:Ev) RETURN e.n ORDER BY e.at DESC LIMIT 2"));
        var d = Q("MATCH (e:Ev {n: 5}) RETURN e.at")[0][0];
        Assert.Equal(new DateOnly(2024, 1, 6), d.AsDate());
        Q("CREATE (:Ev {at: $p})", new() { ["p"] = new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.FromHours(2)) });
        Assert.Equal(10L, Q("MATCH (e:Ev) WHERE e.n IS NULL RETURN e.at.hour").Scalar<long>());
    }

    // ------------------------------------------------------------------ spatial

    [Fact]
    public void Points_And_Distance()
    {
        var r = Q("""
            WITH point({longitude: 12.4964, latitude: 41.9028}) AS rome, point({longitude: 9.19, latitude: 45.4642}) AS milan,
                 point({x: 0, y: 0}) AS o, point({x: 3, y: 4}) AS p
            RETURN round(point.distance(rome, milan) / 1000) AS km, point.distance(o, p) AS d, p.x AS x, rome.crs AS crs, rome.latitude AS lat
            """)[0];
        Assert.InRange(r.Get<double>("km"), 470, 485);
        Assert.Equal(5.0, r.Get<double>("d"));
        Assert.Equal(3.0, r.Get<double>("x"));
        Assert.Equal("wgs-84", r.Get<string>("crs"));
        Assert.Equal(41.9028, r.Get<double>("lat"));
        Q("CREATE (:Place {name: 'A', loc: point({x: 1, y: 1})}), (:Place {name: 'B', loc: point({x: 10, y: 10})})");
        Assert.Equal(["A"], Col<string>("MATCH (p:Place) WHERE point.distance(p.loc, point({x: 0, y: 0})) < 5 RETURN p.name"));
        Assert.Equal(1.0, Q("MATCH (p:Place {name: 'A'}) RETURN p.loc")[0][0].AsPoint().X);
    }

    // ------------------------------------------------------------------ label expressions

    [Fact]
    public void Label_Expressions()
    {
        Q("CREATE (:A {n: 'a'}), (:B {n: 'b'}), (:A:B {n: 'ab'}), (:C {n: 'c'}), ({n: 'none'})");
        Assert.Equal(["a", "ab", "b"], Col<string>("MATCH (x:A|B) RETURN x.n ORDER BY x.n"));
        Assert.Equal(["ab"], Col<string>("MATCH (x:A&B) RETURN x.n"));
        Assert.Equal(["ab"], Col<string>("MATCH (x:A:B) RETURN x.n"));
        Assert.Equal(["a"], Col<string>("MATCH (x:A&!B) RETURN x.n"));
        Assert.Equal(["c", "none"], Col<string>("MATCH (x:!(A|B)) RETURN x.n ORDER BY x.n"));
        Assert.Equal(["a", "ab", "b", "c"], Col<string>("MATCH (x:%) RETURN x.n ORDER BY x.n"));
        Assert.Equal(["a", "ab", "b"], Col<string>("MATCH (x) WHERE x:A|B RETURN x.n ORDER BY x.n"));
        Assert.Equal(["a", "ab"], Col<string>("MATCH (x) RETURN [y IN [x] WHERE y:A | y.n][0] AS n ORDER BY n").Where(s => s != null).ToList());
        Assert.Contains("NodeByLabelScan", Q("EXPLAIN MATCH (x:A|B) RETURN x").Scalar<string>()!);
    }

    // ------------------------------------------------------------------ indexes

    [Fact]
    public void Range_And_Prefix_Index_Seeks()
    {
        Q("UNWIND range(1, 1000) AS i CREATE (:P {age: i % 100, name: 'name' + toString(i)})");
        Q("CREATE INDEX FOR (p:P) ON (p.age)");
        Q("CREATE INDEX FOR (p:P) ON (p.name)");
        Assert.Contains("NodeIndexSeekByRange", Q("EXPLAIN MATCH (p:P) WHERE p.age > 90 RETURN p").Scalar<string>()!);
        Assert.Equal(90L, Q("MATCH (p:P) WHERE p.age > 90 RETURN count(p)").Scalar<long>());
        Assert.Equal(100L, Q("MATCH (p:P) WHERE 10 <= p.age AND p.age <= 19 RETURN count(p)").Scalar<long>());
        Assert.Equal(10L, Q("MATCH (p:P) WHERE p.age < 1 RETURN count(p)").Scalar<long>());
        Assert.Equal(10L, Q("MATCH (p:P) WHERE p.age > 98.5 RETURN count(p)").Scalar<long>());
        Assert.Contains("NodeIndexSeekByPrefix", Q("EXPLAIN MATCH (p:P) WHERE p.name STARTS WITH 'name99' RETURN p").Scalar<string>()!);
        Assert.Equal(11L, Q("MATCH (p:P) WHERE p.name STARTS WITH 'name99' RETURN count(p)").Scalar<long>());
        Assert.Equal(1L, Q("MATCH (p:P) WHERE p.name = 'name7' RETURN count(p)").Scalar<long>());
        // index maintenance
        Q("MATCH (p:P) WHERE p.age = 50 SET p.age = 500");
        Assert.Equal(10L, Q("MATCH (p:P) WHERE p.age >= 500 RETURN count(p)").Scalar<long>());
        Assert.Equal(0L, Q("MATCH (p:P) WHERE p.age = 50 RETURN count(p)").Scalar<long>());
        Q("MATCH (p:P) WHERE p.age = 500 DELETE p");
        Assert.Equal(0L, Q("MATCH (p:P) WHERE p.age >= 500 RETURN count(p)").Scalar<long>());
    }

    [Fact]
    public void Many_Labels_Per_Node()
    {
        Q("CREATE (:L1:L2:L3:L4:L5:L6 {v: 1})");
        Assert.Equal(6L, Q("MATCH (n:L6) RETURN size(labels(n))").Scalar<long>());
        Q("MATCH (n:L1) REMOVE n:L3 SET n:L7");
        var labels = Q("MATCH (n:L7) RETURN labels(n)")[0][0].AsList().Select(v => v.AsString()).Order().ToList();
        Assert.Equal(["L1", "L2", "L4", "L5", "L6", "L7"], labels);
        Assert.Empty(Q("MATCH (n:L3) RETURN n"));
    }

    // ------------------------------------------------------------------ subqueries

    [Fact]
    public void Call_Subqueries()
    {
        Q("CREATE (a:P {n: 'a'})-[:F]->(:P {n: 'b'}), (a)-[:F]->(:P {n: 'c'}), (:P {n: 'd'})");
        var r = Q("""
            MATCH (p:P)
            CALL (p) {
                OPTIONAL MATCH (p)-[:F]->(f)
                RETURN count(f) AS friends
            }
            RETURN p.n AS n, friends ORDER BY n
            """);
        Assert.Equal([2L, 0L, 0L, 0L], r.Select(x => x.Get<long>("friends")));
        var legacy = Q("MATCH (p:P) CALL { WITH p MATCH (p)-[:F]->(f) RETURN f.n AS fn } RETURN p.n, fn ORDER BY fn");
        Assert.Equal(["b", "c"], legacy.Select(x => x.Get<string>("fn")));
        var union = Col<string>("CALL { MATCH (p:P {n: 'a'}) RETURN p.n AS x UNION MATCH (p:P {n: 'd'}) RETURN p.n AS x } RETURN x ORDER BY x");
        Assert.Equal(["a", "d"], union);
        Q("MATCH (p:P) CALL (p) { SET p.visited = true }");
        Assert.Equal(4L, Q("MATCH (p:P) WHERE p.visited RETURN count(p)").Scalar<long>());
    }

    [Fact]
    public void Call_In_Transactions_Commits_In_Batches()
    {
        var s = Q("UNWIND range(1, 2500) AS i CALL (i) { CREATE (:Batch {i: i}) } IN TRANSACTIONS OF 1000 ROWS").Statistics;
        Assert.Equal(2500, s.NodesCreated);
        Assert.Equal(2500L, Q("MATCH (b:Batch) RETURN count(b)").Scalar<long>());
        using var tx = _db.BeginTransaction();
        Assert.Throws<GraphDbException>(() => tx.Execute("UNWIND [1] AS i CALL (i) { CREATE (:X) } IN TRANSACTIONS"));
    }

    // ------------------------------------------------------------------ LOAD CSV

    [Fact]
    public void Load_Csv()
    {
        var file = Path.Combine(_tmp, "people.csv");
        File.WriteAllText(file, "id,name,city\n1,Alice,Rome\n2,\"Bob, Jr.\",\n3,\"Carol \"\"C\"\"\",Milan\n");
        var r = Q("LOAD CSV WITH HEADERS FROM $f AS row CREATE (p:Person {id: toInteger(row.id), name: row.name, city: row.city}) RETURN count(p)",
            new() { ["f"] = file });
        Assert.Equal(3L, r.Scalar<long>());
        Assert.Equal("Bob, Jr.", Q("MATCH (p:Person {id: 2}) RETURN p.name").Scalar<string>());
        Assert.True(Q("MATCH (p:Person {id: 2}) RETURN p.city")[0][0].IsNull);
        Assert.Equal("Carol \"C\"", Q("MATCH (p:Person {id: 3}) RETURN p.name").Scalar<string>());

        var semi = Path.Combine(_tmp, "raw.csv");
        File.WriteAllText(semi, "a;b\nc;d\n");
        var rows = Q("LOAD CSV FROM $f AS line FIELDTERMINATOR ';' RETURN line[1] AS second", new() { ["f"] = new Uri(semi).AbsoluteUri });
        Assert.Equal(["b", "d"], rows.Select(x => x.Get<string>(0)));

        var big = Path.Combine(_tmp, "big.csv");
        File.WriteAllLines(big, new[] { "n" }.Concat(Enumerable.Range(0, 5000).Select(i => i.ToString())));
        var s = Q("LOAD CSV WITH HEADERS FROM $f AS row CALL (row) { CREATE (:Row {n: toInteger(row.n)}) } IN TRANSACTIONS OF 1000 ROWS",
            new() { ["f"] = big }).Statistics;
        Assert.Equal(5000, s.NodesCreated);
        Assert.Equal(12497500L, Q("MATCH (r:Row) RETURN sum(r.n)").Scalar<long>());
    }
}
