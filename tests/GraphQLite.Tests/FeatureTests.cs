using GraphQLite.Storage;
namespace GraphQLite.Tests;

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

    // ------------------------------------------------------------------ parallel aggregation

    [Fact]
    public void Parallel_Aggregation_Matches_Sequential()
    {
        using var par = GraphDatabase.OpenInMemory(new DatabaseOptions { MaxDegreeOfParallelism = 8 });
        using var seq = GraphDatabase.OpenInMemory(new DatabaseOptions { MaxDegreeOfParallelism = 1 });
        const string load = """
            UNWIND range(0, 199999) AS i
            CREATE (n:N {i: i, g: i % 37, s: 'v' + (i % 1000), f: (i % 101) / 7.0})
            WITH n, i WHERE i % 3 = 0 SET n:M
            """;
        const string rels = "MATCH (a:M) WHERE a.i % 2 = 0 MATCH (b:N {i: (a.i * 7) % 200000}) CREATE (a)-[:R {w: a.i % 13}]->(b)";
        foreach (var db in new[] { par, seq })
        {
            db.Execute("CREATE INDEX FOR (n:N) ON (n.i)");
            db.Execute(load);
            db.Execute(rels);
            db.Execute("MATCH (n:N) WHERE n.i % 1000 = 0 DETACH DELETE n"); // holes in the id space
        }
        string[] queries =
        [
            "MATCH (n:N) RETURN count(*), sum(n.i), min(n.s), max(n.f), avg(n.i), round(stDev(n.f), 9)",
            "MATCH (n:N) RETURN n.g AS g, count(*) AS c, collect(n.i)[0..5] AS first, sum(n.f) > 0 AS pos",
            "MATCH (n) WHERE n.i > 150000 RETURN count(DISTINCT n.s), collect(DISTINCT n.g)[0..10]",
            "MATCH (n:M:N) RETURN n.g % 5 AS k, percentileDisc(n.i, 0.5) AS p, count(n) AS c ORDER BY k",
            "MATCH (a:M)-[r:R]->(b) RETURN r.w AS w, count(*) AS c, sum(b.i) AS s ORDER BY w",
            "MATCH (a:M) OPTIONAL MATCH (a)-[:R]->(b) RETURN count(b), count(a)",
            "MATCH (n:N) WHERE n.i < 10 RETURN count(*)",
            "MATCH (n:Missing) RETURN count(*), collect(n)",
            "MATCH (n:N) WITH n.g AS g, count(*) AS c RETURN sum(c), count(g)",
        ];
        foreach (var q in queries)
        {
            string Dump(GraphDatabase db) => string.Join("\n", db.Execute(q).Select(r => r.ToString()));
            Assert.Equal(Dump(seq), Dump(par));
        }
        // errors raised by a worker surface as the original exception
        Assert.Throws<CypherRuntimeException>(() => par.Execute("MATCH (n:N) RETURN sum(n.s)"));
    }

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

    [Fact]
    public void Unique_Hash_Index_Agrees_With_A_Full_Scan()
    {
        Q("CREATE CONSTRAINT FOR (u:U) REQUIRE u.k IS UNIQUE");
        // enough entries to split the buckets many times, then moves, deletions and mixed types
        Q("UNWIND range(0, 49999) AS i CREATE (:U {k: i, s: 'u' + toString(i)})");
        Q("MATCH (u:U) WHERE u.k % 3 = 0 SET u.k = u.k + 100000");
        Q("MATCH (u:U) WHERE u.k % 7 = 1 DETACH DELETE u");
        Q("UNWIND range(0, 999) AS i CREATE (:U {k: 'str' + toString(i)})");
        Q("CREATE (:U {k: 300000.5}), (:U {k: date('2020-01-01')}), (:U {k: true})");

        var expected = Q("MATCH (u:U) RETURN u.k AS k, id(u) AS id").ToDictionary(r => r["k"].ToString(), r => r.Get<long>("id"));
        var probes = new List<object?>();
        for (int i = 0; i < 160000; i += 1) probes.Add((long)i);
        for (int i = 0; i < 1100; i++) probes.Add("str" + i);
        probes.Add(300000.5);
        probes.Add(true);
        var found = Q("UNWIND $xs AS x MATCH (u:U {k: x}) RETURN u.k AS k, id(u) AS id", new() { ["xs"] = probes })
            .ToDictionary(r => r["k"].ToString(), r => r.Get<long>("id"));
        Assert.Equal(expected.Count - 1, found.Count); // the date is not among the probes
        foreach (var (k, id) in found) Assert.Equal(expected[k], id);

        // IN, numeric equality across int and float, temporal keys
        Assert.Equal(3, Q("MATCH (u:U) WHERE u.k IN [2, 'str5', 2, 300000.5, 3] RETURN u").Count);
        Assert.Equal(["u2"], Col<string>("MATCH (u:U {k: 2.0}) RETURN u.s"));
        Assert.Single(Q("MATCH (u:U {k: date('2020-01-01')}) RETURN u"));

        // the constraint is still enforced through the hash index
        Assert.Throws<ConstraintViolationException>(() => Q("CREATE (:U {k: 2})"));
        Assert.Throws<ConstraintViolationException>(() => Q("MATCH (u:U {k: 4}) SET u.k = 'str7'"));
        Q("MATCH (u:U {k: 2}) DELETE u");
        Q("CREATE (:U {k: 2, s: 'again'})");
        Assert.Equal(["again"], Col<string>("MATCH (u:U {k: 2}) RETURN u.s"));

        // after dropping the constraint the same queries fall back to a label scan
        var name = Q("CALL db.constraints() YIELD name RETURN name").Single().Get<string>(0);
        Q($"DROP CONSTRAINT {name}");
        Assert.Equal(["again"], Col<string>("MATCH (u:U {k: 2}) RETURN u.s"));
    }

    [Fact]
    public void Hash_Index_Handles_Collisions_Overflow_And_Frees_Its_Space()
    {
        var tx = _db.Store.BeginWrite();
        long pagesAfterFirst = 0;
        for (int round = 0; round < 2; round++)
        {
            long meta = HashIndex.Create(tx);
            for (int i = 0; i < 500; i++) HashIndex.Insert(tx, meta, 42, i); // one hash: a long overflow chain
            for (int i = 0; i < 20000; i++) HashIndex.Insert(tx, meta, (ulong)(i + 1) * 0x9E3779B97F4A7C15, 1000 + i);
            var ids = new List<long>();
            HashIndex.Lookup(tx, meta, 42, ids);
            Assert.Equal(Enumerable.Range(0, 500).Select(i => (long)i), ids.Order());
            for (int i = 0; i < 500; i += 2) HashIndex.Remove(tx, meta, 42, i);
            HashIndex.Remove(tx, meta, 42, 100_000); // absent: no-op
            ids.Clear();
            HashIndex.Lookup(tx, meta, 42, ids);
            Assert.Equal(Enumerable.Range(0, 250).Select(i => (long)(2 * i + 1)), ids.Order());
            for (int i = 0; i < 20000; i++)
            {
                ids.Clear();
                HashIndex.Lookup(tx, meta, (ulong)(i + 1) * 0x9E3779B97F4A7C15, ids);
                Assert.Equal([1000L + i], ids);
            }
            HashIndex.Free(tx, meta);
            if (round == 0) pagesAfterFirst = tx.DataPages;
        }
        Assert.Equal(pagesAfterFirst, tx.DataPages); // the second round reused everything the first one freed
        tx.Abort();
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
        Assert.Throws<GraphQLiteException>(() => tx.Execute("UNWIND [1] AS i CALL (i) { CREATE (:X) } IN TRANSACTIONS"));
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

    // ------------------------------------------------------------------ procedures

    [Fact]
    public void Standalone_Call_With_Yield()
    {
        Q("CREATE (:A), (:B)");
        var r = Q("CALL db.labels() YIELD label");
        Assert.Equal(["label"], r.Columns);
        Assert.Equal(["A", "B"], r.Select(x => x.Get<string>(0)).Order());
        var renamed = Q("CALL db.labels() YIELD label AS l WHERE l = 'B'");
        Assert.Equal(["l"], renamed.Columns);
        Assert.Equal("B", renamed.Single().Get<string>("l"));
    }

    [Fact]
    public void Schema_Visualization()
    {
        Q("CREATE CONSTRAINT FOR (p:Person) REQUIRE p.email IS UNIQUE");
        Q("CREATE INDEX FOR (c:City) ON (c.name)");
        Q("CREATE (:Person:Dev {email: 'a'})-[:LIVES_IN]->(:City {name: 'Roma'}), (:Person {email: 'b'})-[:KNOWS]->(:Person {email: 'c'})");
        var row = Q("CALL db.schema.visualization() YIELD nodes, relationships").Single();
        var nodes = row["nodes"].AsList().Select(v => v.AsNode()).ToDictionary(n => n.Labels[0]);
        Assert.Equal(["City", "Dev", "Person"], nodes.Keys.Order());
        Assert.All(nodes.Values, n => Assert.True(n.Id < 0));
        Assert.Equal("name", nodes["City"]["indexes"].AsList()[0].AsString());
        Assert.Single(nodes["Person"]["constraints"].AsList());
        var rels = row["relationships"].AsList().Select(v => v.AsRelationship())
            .Select(r => (nodes.Values.Single(n => n.Id == r.StartNodeId).Labels[0], r.Type, nodes.Values.Single(n => n.Id == r.EndNodeId).Labels[0]))
            .Order()
            .ToList();
        Assert.Equal([("Dev", "LIVES_IN", "City"), ("Person", "KNOWS", "Person"), ("Person", "LIVES_IN", "City")], rels);
    }
}
