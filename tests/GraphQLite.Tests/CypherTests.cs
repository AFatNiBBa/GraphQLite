namespace GraphQLite.Tests;

public class CypherTests : IDisposable
{
    private readonly GraphDatabase _db = GraphDatabase.OpenInMemory();

    public void Dispose() => _db.Dispose();

    private QueryResult Q(string q, Dictionary<string, object?>? p = null) => _db.Execute(q, p);

    private List<T> Col<T>(string q, Dictionary<string, object?>? p = null) => Q(q, p).Select(r => r.Get<T>(0)).ToList();

    private void SeedSocial()
    {
        Q("""
          CREATE (a:Person {name: 'Alice', age: 30}),
                 (b:Person {name: 'Bob', age: 25}),
                 (c:Person {name: 'Carol', age: 35}),
                 (d:Person:Admin {name: 'Dave', age: 40}),
                 (acme:Company {name: 'Acme'}),
                 (a)-[:KNOWS {since: 2010}]->(b),
                 (b)-[:KNOWS {since: 2012}]->(c),
                 (c)-[:KNOWS {since: 2015}]->(d),
                 (a)-[:WORKS_AT]->(acme),
                 (d)-[:WORKS_AT]->(acme)
          """);
    }

    [Fact]
    public void Literals_And_Arithmetic()
    {
        var r = Q("RETURN 1 + 2 * 3 AS a, 7 / 2 AS b, 7.0 / 2 AS c, 2 ^ 10 AS d, 10 % 3 AS e, -5 AS f, 'x' + 1 AS g")[0];
        Assert.Equal(7L, r.Get<long>("a"));
        Assert.Equal(3L, r.Get<long>("b"));
        Assert.Equal(3.5, r.Get<double>("c"));
        Assert.Equal(1024.0, r.Get<double>("d"));
        Assert.Equal(1L, r.Get<long>("e"));
        Assert.Equal(-5L, r.Get<long>("f"));
        Assert.Equal("x1", r.Get<string>("g"));
    }

    [Fact]
    public void Null_Semantics()
    {
        var r = Q("RETURN null = null AS a, null OR true AS b, null AND false AS c, 1 IN [1, null] AS d, 2 IN [1, null] AS e, null IS NULL AS f")[0];
        Assert.True(r["a"].IsNull);
        Assert.True(r.Get<bool>("b"));
        Assert.False(r.Get<bool>("c"));
        Assert.True(r.Get<bool>("d"));
        Assert.True(r["e"].IsNull);
        Assert.True(r.Get<bool>("f"));
    }

    [Fact]
    public void Create_And_Match()
    {
        SeedSocial();
        Assert.Equal(5, _db.NodeCount);
        Assert.Equal(5, _db.RelationshipCount);
        var names = Col<string>("MATCH (p:Person) RETURN p.name ORDER BY p.name");
        Assert.Equal(["Alice", "Bob", "Carol", "Dave"], names);
        Assert.Equal(["Dave"], Col<string>("MATCH (p:Person:Admin) RETURN p.name"));
        Assert.Equal(["Bob"], Col<string>("MATCH (:Person {name: 'Alice'})-[:KNOWS]->(f) RETURN f.name"));
        Assert.Equal(["Alice"], Col<string>("MATCH (f)-[:KNOWS]->(:Person {name: 'Bob'}) RETURN f.name"));
        Assert.Equal(["Alice", "Carol"], Col<string>("MATCH (:Person {name: 'Bob'})-[:KNOWS]-(f) RETURN f.name ORDER BY f.name"));
    }

    [Fact]
    public void Where_Predicates()
    {
        SeedSocial();
        Assert.Equal(["Carol", "Dave"], Col<string>("MATCH (p:Person) WHERE p.age > 30 RETURN p.name ORDER BY p.name"));
        Assert.Equal(["Alice"], Col<string>("MATCH (p:Person) WHERE p.name STARTS WITH 'Al' RETURN p.name"));
        Assert.Equal(["Carol"], Col<string>("MATCH (p:Person) WHERE p.name ENDS WITH 'ol' RETURN p.name"));
        Assert.Equal(["Bob"], Col<string>("MATCH (p:Person) WHERE p.name CONTAINS 'o' AND p.age < 30 RETURN p.name"));
        Assert.Equal(["Alice", "Bob"], Col<string>("MATCH (p:Person) WHERE p.name =~ '(Al|B).*' RETURN p.name ORDER BY p.name"));
        Assert.Equal(["Bob", "Dave"], Col<string>("MATCH (p:Person) WHERE p.name IN ['Bob', 'Dave', 'Zed'] RETURN p.name ORDER BY p.name"));
        Assert.Equal(["Dave"], Col<string>("MATCH (p) WHERE p:Admin RETURN p.name"));
        Assert.Equal(["Acme"], Col<string>("MATCH (n) WHERE n.age IS NULL RETURN n.name"));
        Assert.Equal(["Alice", "Dave"], Col<string>("MATCH (p:Person) WHERE (p)-[:WORKS_AT]->() RETURN p.name ORDER BY p.name"));
        Assert.Equal(["Bob", "Carol"], Col<string>("MATCH (p:Person) WHERE NOT (p)-[:WORKS_AT]->() RETURN p.name ORDER BY p.name"));
        Assert.Equal(["Alice", "Dave"], Col<string>("MATCH (p:Person) WHERE EXISTS { (p)-[:WORKS_AT]->(:Company) } RETURN p.name ORDER BY p.name"));
    }

    [Fact]
    public void Parameters()
    {
        SeedSocial();
        var p = new Dictionary<string, object?> { ["min"] = 30, ["names"] = new[] { "Alice", "Carol" } };
        Assert.Equal(["Carol"], Col<string>("MATCH (p:Person) WHERE p.age > $min AND p.name IN $names RETURN p.name", p));
        Assert.Throws<CypherSemanticException>(() => Q("RETURN $missing"));
    }

    [Fact]
    public void Aggregation()
    {
        SeedSocial();
        var r = Q("MATCH (p:Person) RETURN count(*) AS c, sum(p.age) AS s, avg(p.age) AS a, min(p.age) AS mn, max(p.age) AS mx, collect(p.name) AS names")[0];
        Assert.Equal(4L, r.Get<long>("c"));
        Assert.Equal(130L, r.Get<long>("s"));
        Assert.Equal(32.5, r.Get<double>("a"));
        Assert.Equal(25L, r.Get<long>("mn"));
        Assert.Equal(40L, r.Get<long>("mx"));
        Assert.Equal(4, r["names"].AsList().Count);

        var grouped = Q("MATCH (p:Person)-[:WORKS_AT]->(c:Company) RETURN c.name AS company, count(p) AS n, collect(p.name) AS who");
        Assert.Single(grouped);
        Assert.Equal(2L, grouped[0].Get<long>("n"));

        Assert.Equal(0L, Q("MATCH (x:Nope) RETURN count(x)").Scalar<long>());
        Assert.Empty(Q("MATCH (x:Nope) RETURN x.name, count(x)"));
        Assert.Equal(3L, Q("UNWIND [1, 1, 2, 3, null] AS x RETURN count(DISTINCT x)").Scalar<long>());
        Assert.Equal(11L, Q("UNWIND [1, 2, 3] AS x RETURN sum(x) * 2 - 1").Scalar<long>());
    }

    [Fact]
    public void Aggregation_With_OrderBy_On_Aggregate()
    {
        SeedSocial();
        var r = Q("MATCH (p:Person)-[:KNOWS]-(f) RETURN p.name AS name, count(f) AS degree ORDER BY degree DESC, name LIMIT 2");
        Assert.Equal(2, r.Count);
        Assert.Equal(2L, r[0].Get<long>("degree"));
        Assert.Equal("Bob", r[0].Get<string>("name"));
        Assert.Equal("Carol", r[1].Get<string>("name"));
    }

    [Fact]
    public void With_Pipeline()
    {
        SeedSocial();
        var r = Col<string>("""
            MATCH (p:Person)
            WITH p, p.age * 2 AS dbl
            WHERE dbl > 60
            WITH p.name AS name ORDER BY name DESC
            RETURN name
            """);
        Assert.Equal(["Dave", "Carol"], r);
        Assert.Throws<CypherSemanticException>(() => Q("MATCH (p:Person) WITH p.name AS n RETURN p"));
    }

    [Fact]
    public void Order_Skip_Limit_Distinct()
    {
        Assert.Equal([5L, 4L, 3L], Col<long>("UNWIND range(1, 5) AS x RETURN x ORDER BY x DESC LIMIT 3"));
        Assert.Equal([3L, 4L], Col<long>("UNWIND range(1, 5) AS x RETURN x SKIP 2 LIMIT 2"));
        Assert.Equal([1L, 2L, 3L], Col<long>("UNWIND [3, 1, 2, 1, 3] AS x RETURN DISTINCT x ORDER BY x"));
        Assert.Equal([1L, 2L], Col<long>("UNWIND [3, 1, 2] AS x RETURN x ORDER BY x SKIP 0 LIMIT 2"));
        // ordering across types: null last
        var mixed = Q("UNWIND [2, null, 'a', 1.5] AS x RETURN x ORDER BY x").Select(r => r[0].ToString()).ToList();
        Assert.Equal(["'a'", "1.5", "2", "null"], mixed);
    }

    [Fact]
    public void Optional_Match()
    {
        SeedSocial();
        var r = Q("MATCH (p:Person) OPTIONAL MATCH (p)-[:WORKS_AT]->(c) RETURN p.name AS n, c.name AS c ORDER BY n");
        Assert.Equal(4, r.Count);
        Assert.Equal("Acme", r[0].Get<string>("c"));
        Assert.True(r[1]["c"].IsNull);
        var r2 = Q("OPTIONAL MATCH (x:Nope) RETURN x");
        Assert.Single(r2);
        Assert.True(r2[0]["x"].IsNull);
    }

    [Fact]
    public void Variable_Length_And_Paths()
    {
        SeedSocial();
        Assert.Equal(["Bob", "Carol", "Dave"], Col<string>("MATCH (:Person {name:'Alice'})-[:KNOWS*]->(x) RETURN x.name ORDER BY x.name"));
        Assert.Equal(["Carol"], Col<string>("MATCH (:Person {name:'Alice'})-[:KNOWS*2]->(x) RETURN x.name"));
        Assert.Equal(["Alice", "Bob"], Col<string>("MATCH (:Person {name:'Alice'})-[:KNOWS*0..1]->(x) RETURN x.name ORDER BY x.name"));
        var p = Q("MATCH p = (:Person {name:'Alice'})-[:KNOWS*3]->(x) RETURN p, length(p) AS len, [n IN nodes(p) | n.name] AS names")[0];
        Assert.Equal(3L, p.Get<long>("len"));
        Assert.Equal(["Alice", "Bob", "Carol", "Dave"], p["names"].AsList().Select(v => v.AsString()));
        var path = p["p"].AsPath();
        Assert.Equal("Dave", path.End["name"].AsString());
        Assert.Equal(3, path.Relationships.Count);
        var rels = Q("MATCH (:Person {name:'Alice'})-[r:KNOWS*2]->() RETURN [x IN r | x.since] AS s")[0];
        Assert.Equal([2010L, 2012L], rels["s"].AsList().Select(v => v.AsInt64()));
        // backwards traversal keeps pattern order
        var back = Q("MATCH (a)-[r:KNOWS*2]->(:Person {name:'Carol'}) RETURN [x IN r | x.since] AS s")[0];
        Assert.Equal([2010L, 2012L], back["s"].AsList().Select(v => v.AsInt64()));
    }

    [Fact]
    public void Shortest_Path()
    {
        SeedSocial();
        Q("MATCH (a:Person {name:'Alice'}), (d:Person {name:'Dave'}) CREATE (a)-[:KNOWS]->(x:Person {name:'X'})-[:KNOWS]->(d)");
        var r = Q("MATCH (a:Person {name:'Alice'}), (d:Person {name:'Dave'}) MATCH p = shortestPath((a)-[:KNOWS*]->(d)) RETURN length(p)");
        Assert.Equal(2L, r.Scalar<long>());
        var all = Q("MATCH (a:Person {name:'Alice'}), (c:Person {name:'Carol'}) MATCH p = allShortestPaths((a)-[:KNOWS*]-(c)) RETURN p");
        Assert.Single(all);
    }

    [Fact]
    public void Relationship_Uniqueness_In_Match()
    {
        Q("CREATE (a:N {id: 1})-[:R]->(b:N {id: 2})");
        // the single relationship cannot be used twice in one pattern
        Assert.Empty(Q("MATCH (a)-[r1]-(b)-[r2]-(c) RETURN a"));
        Assert.Equal(2, Q("MATCH (a)-[r]-(b) RETURN a, b").Count);
    }

    [Fact]
    public void Self_Loop_Undirected_Once()
    {
        Q("CREATE (a:L)-[:SELF]->(a)");
        Assert.Single(Q("MATCH (a:L)-[r]-(b) RETURN r"));
        Assert.Single(Q("MATCH (a:L)-[r]->(a) RETURN r"));
    }

    [Fact]
    public void Set_Remove_Labels_And_Properties()
    {
        Q("CREATE (:P {name: 'a', x: 1})");
        var s = Q("MATCH (n:P) SET n.y = 2, n:Q, n.x = null RETURN n").Statistics;
        Assert.Equal(1, s.LabelsAdded);
        var n = Q("MATCH (n:Q) RETURN n")[0]["n"].AsNode();
        Assert.True(n.HasLabel("P") && n.HasLabel("Q"));
        Assert.Equal(2L, n["y"].AsInt64());
        Assert.True(n["x"].IsNull);
        Q("MATCH (n:P) SET n += {z: 3, y: null}");
        n = Q("MATCH (n:P) RETURN n")[0]["n"].AsNode();
        Assert.Equal(3L, n["z"].AsInt64());
        Assert.False(n.Properties.ContainsKey("y"));
        Q("MATCH (n:P) SET n = {only: true}");
        n = Q("MATCH (n:P) RETURN n")[0]["n"].AsNode();
        Assert.Equal(["only"], n.Properties.Keys);
        Q("MATCH (n:P) REMOVE n:Q, n.only");
        n = Q("MATCH (n:P) RETURN n")[0]["n"].AsNode();
        Assert.Empty(n.Properties);
        Assert.Equal(["P"], n.Labels);
    }

    [Fact]
    public void Delete_And_Detach_Delete()
    {
        SeedSocial();
        Assert.Throws<ConstraintViolationException>(() => Q("MATCH (p:Person {name: 'Alice'}) DELETE p"));
        Assert.Equal(5, _db.NodeCount); // statement rolled back
        var s = Q("MATCH (p:Person {name: 'Alice'}) DETACH DELETE p").Statistics;
        Assert.Equal(1, s.NodesDeleted);
        Assert.Equal(2, s.RelationshipsDeleted);
        Assert.Equal(4, _db.NodeCount);
        Q("MATCH (a)-[r]->(b) DELETE r");
        Assert.Equal(0, _db.RelationshipCount);
        Q("MATCH (n) DELETE n");
        Assert.Equal(0, _db.NodeCount);
    }

    [Fact]
    public void Merge()
    {
        var s1 = Q("MERGE (p:Person {name: 'Ann'}) ON CREATE SET p.created = true ON MATCH SET p.seen = true RETURN p").Statistics;
        Assert.Equal(1, s1.NodesCreated);
        var s2 = Q("MERGE (p:Person {name: 'Ann'}) ON CREATE SET p.created = true ON MATCH SET p.seen = true RETURN p").Statistics;
        Assert.Equal(0, s2.NodesCreated);
        var n = Q("MATCH (p:Person) RETURN p")[0]["p"].AsNode();
        Assert.True(n["created"].AsBoolean() && n["seen"].AsBoolean());

        Q("UNWIND ['a', 'b', 'a', 'c', 'b'] AS x MERGE (:Tag {v: x})");
        Assert.Equal(3L, Q("MATCH (t:Tag) RETURN count(t)").Scalar<long>());

        Q("MATCH (a:Tag {v:'a'}), (b:Tag {v:'b'}) MERGE (a)-[:LINK]->(b)");
        Q("MATCH (a:Tag {v:'a'}), (b:Tag {v:'b'}) MERGE (a)-[:LINK]->(b)");
        Assert.Equal(1L, Q("MATCH ()-[r:LINK]->() RETURN count(r)").Scalar<long>());
    }

    [Fact]
    public void Unwind_Bulk_Create_With_Parameters()
    {
        var rows = Enumerable.Range(0, 1000).Select(i => new Dictionary<string, object?> { ["id"] = i, ["name"] = "n" + i }).ToList();
        var s = Q("UNWIND $rows AS r CREATE (:Item {id: r.id, name: r.name})", new() { ["rows"] = rows }).Statistics;
        Assert.Equal(1000, s.NodesCreated);
        Assert.Equal(499500L, Q("MATCH (i:Item) RETURN sum(i.id)").Scalar<long>());
    }

    [Fact]
    public void Indexes_Are_Used_And_Maintained()
    {
        Q("UNWIND range(1, 500) AS i CREATE (:User {uid: i, grp: i % 5})");
        Q("CREATE INDEX user_uid FOR (u:User) ON (u.uid)");
        var plan = Q("EXPLAIN MATCH (u:User {uid: 42}) RETURN u").Scalar<string>()!;
        Assert.Contains("NodeIndexSeek", plan);
        Assert.Equal([42L], Col<long>("MATCH (u:User) WHERE u.uid = 42 RETURN u.uid"));
        Assert.Equal([7L, 9L], Col<long>("MATCH (u:User) WHERE u.uid IN [9, 7, 9999] RETURN u.uid ORDER BY u.uid"));
        Q("MATCH (u:User {uid: 42}) SET u.uid = 4242");
        Assert.Empty(Q("MATCH (u:User {uid: 42}) RETURN u"));
        Assert.Single(Q("MATCH (u:User {uid: 4242}) RETURN u"));
        Q("MATCH (u:User {uid: 4242}) REMOVE u:User");
        Assert.Empty(Q("MATCH (u:User {uid: 4242}) RETURN u"));
        var ix = Q("CALL db.indexes() YIELD name, label, property RETURN name, label, property");
        Assert.Equal("user_uid", ix[0].Get<string>("name"));
        Assert.Equal("User", ix[0].Get<string>("label"));
        Q("DROP INDEX user_uid");
        Assert.DoesNotContain("NodeIndexSeek", Q("EXPLAIN MATCH (u:User {uid: 42}) RETURN u").Scalar<string>()!);
    }

    [Fact]
    public void Unique_Constraint()
    {
        Q("CREATE CONSTRAINT FOR (p:Acct) REQUIRE p.email IS UNIQUE");
        Q("CREATE (:Acct {email: 'a@x'})");
        Assert.Throws<ConstraintViolationException>(() => Q("CREATE (:Acct {email: 'a@x'})"));
        Assert.Equal(1L, Q("MATCH (a:Acct) RETURN count(a)").Scalar<long>());
        Q("CREATE (:Other {email: 'a@x'})");
        Assert.Throws<ConstraintViolationException>(() => Q("MATCH (o:Other) SET o:Acct"));
        Q("CREATE CONSTRAINT IF NOT EXISTS FOR (p:Acct) REQUIRE p.email IS UNIQUE");
    }

    [Fact]
    public void Union()
    {
        Assert.Equal([1L, 2L], Col<long>("RETURN 1 AS x UNION RETURN 2 AS x UNION RETURN 1 AS x").Order().ToList());
        Assert.Equal(3, Q("RETURN 1 AS x UNION ALL RETURN 2 AS x UNION ALL RETURN 1 AS x").Count);
    }

    [Fact]
    public void List_And_String_Functions()
    {
        var r = Q("""
            RETURN size([1,2,3]) AS s, head([1,2]) AS h, last([1,2]) AS l, tail([1,2,3]) AS t,
                   [x IN range(1,10) WHERE x % 2 = 0 | x * x] AS sq, reduce(acc = 0, x IN [1,2,3] | acc + x) AS red,
                   all(x IN [2,4] WHERE x % 2 = 0) AS allEven, any(x IN [1,2] WHERE x > 1) AS anyGt, none(x IN [1] WHERE x > 1) AS no,
                   single(x IN [1,2,3] WHERE x = 2) AS one,
                   toUpper('abc') AS up, substring('hello', 1, 3) AS sub, replace('aXbX', 'X', '-') AS rep, split('a,b,c', ',') AS sp,
                   trim('  x ') AS tr, reverse('abc') AS rev, left('hello', 2) AS lft, toString(42) AS ts, toInteger('12') AS ti,
                   toFloat('1.5') AS tf, coalesce(null, 'd') AS co, [1,2,3,4][1..3] AS slice, [1,2,3][-1] AS neg,
                   CASE WHEN 1 > 2 THEN 'a' ELSE 'b' END AS cs, CASE 2 WHEN 1 THEN 'one' WHEN 2 THEN 'two' END AS cs2,
                   abs(-3) AS ab, round(2.567, 2) AS rnd, sqrt(16) AS sq2, {a: 1, b: [1, 2]}.b AS mapb
            """)[0];
        Assert.Equal(3L, r.Get<long>("s"));
        Assert.Equal(1L, r.Get<long>("h"));
        Assert.Equal(2L, r.Get<long>("l"));
        Assert.Equal([2L, 3L], r["t"].AsList().Select(v => v.AsInt64()));
        Assert.Equal([4L, 16L, 36L, 64L, 100L], r["sq"].AsList().Select(v => v.AsInt64()));
        Assert.Equal(6L, r.Get<long>("red"));
        Assert.True(r.Get<bool>("allEven") && r.Get<bool>("anyGt") && r.Get<bool>("no") && r.Get<bool>("one"));
        Assert.Equal("ABC", r.Get<string>("up"));
        Assert.Equal("ell", r.Get<string>("sub"));
        Assert.Equal("a-b-", r.Get<string>("rep"));
        Assert.Equal(3, r["sp"].AsList().Count);
        Assert.Equal("x", r.Get<string>("tr"));
        Assert.Equal("cba", r.Get<string>("rev"));
        Assert.Equal("he", r.Get<string>("lft"));
        Assert.Equal("42", r.Get<string>("ts"));
        Assert.Equal(12L, r.Get<long>("ti"));
        Assert.Equal(1.5, r.Get<double>("tf"));
        Assert.Equal("d", r.Get<string>("co"));
        Assert.Equal([2L, 3L], r["slice"].AsList().Select(v => v.AsInt64()));
        Assert.Equal(3L, r.Get<long>("neg"));
        Assert.Equal("b", r.Get<string>("cs"));
        Assert.Equal("two", r.Get<string>("cs2"));
        Assert.Equal(3L, r.Get<long>("ab"));
        Assert.Equal(2.57, r.Get<double>("rnd"));
        Assert.Equal(4.0, r.Get<double>("sq2"));
        Assert.Equal(2, r["mapb"].AsList().Count);
    }

    [Fact]
    public void Entity_Functions_And_Map_Projection()
    {
        SeedSocial();
        var r = Q("""
            MATCH (a:Person {name:'Alice'})-[k:KNOWS]->(b)
            RETURN id(a) >= 0 AS hasId, labels(a) AS l, type(k) AS t, keys(a) AS ks, properties(k) AS pk,
                   startNode(k) = a AS sn, endNode(k) = b AS en, a {.name, .age, friend: b.name} AS proj, a {.*} AS all
            """)[0];
        Assert.True(r.Get<bool>("hasId"));
        Assert.Equal(["Person"], r["l"].AsList().Select(v => v.AsString()));
        Assert.Equal("KNOWS", r.Get<string>("t"));
        Assert.Equal(2, r["ks"].AsList().Count);
        Assert.Equal(2010L, r["pk"].AsMap()["since"].AsInt64());
        Assert.True(r.Get<bool>("sn") && r.Get<bool>("en"));
        var proj = r["proj"].AsMap();
        Assert.Equal("Bob", proj["friend"].AsString());
        Assert.Equal(30L, proj["age"].AsInt64());
        Assert.Equal(2, r["all"].AsMap().Count);
    }

    [Fact]
    public void Pattern_Comprehension_And_Count_Subquery()
    {
        SeedSocial();
        var r = Q("MATCH (a:Person {name:'Alice'}) RETURN [(a)-[:KNOWS]->(f) | f.name] AS friends, COUNT { (a)--() } AS deg")[0];
        Assert.Equal(["Bob"], r["friends"].AsList().Select(v => v.AsString()));
        Assert.Equal(2L, r.Get<long>("deg"));
    }

    [Fact]
    public void Foreach_Clause()
    {
        Q("CREATE (:Root)");
        Q("MATCH (r:Root) FOREACH (i IN range(1, 3) | CREATE (r)-[:HAS]->(:Child {i: i}))");
        Assert.Equal(3L, Q("MATCH (:Root)-[:HAS]->(c) RETURN count(c)").Scalar<long>());
    }

    [Fact]
    public void Procedures()
    {
        SeedSocial();
        var labels = Col<string>("CALL db.labels()").Order().ToList();
        Assert.Equal(["Admin", "Company", "Person"], labels);
        Assert.Equal(["KNOWS", "WORKS_AT"], Col<string>("CALL db.relationshipTypes() YIELD relationshipType AS t RETURN t ORDER BY t"));
    }

    [Fact]
    public void Syntax_And_Semantic_Errors()
    {
        Assert.Throws<CypherSyntaxException>(() => Q("MATCH (n RETURN n"));
        Assert.Throws<CypherSemanticException>(() => Q("MATCH (n) RETURN m"));
        Assert.Throws<CypherSemanticException>(() => Q("RETURN nosuchfn(1)"));
        Assert.Throws<CypherSemanticException>(() => Q("MATCH (n)"));
        Assert.Throws<CypherRuntimeException>(() => Q("RETURN 1 / 0"));
    }

    [Fact]
    public void Explicit_Transaction_Commit_And_Rollback()
    {
        using (var tx = _db.BeginTransaction())
        {
            tx.Execute("CREATE (:T {v: 1})");
            Assert.Equal(1L, tx.Execute("MATCH (t:T) RETURN count(t)").Scalar<long>());
            tx.Rollback();
        }
        Assert.Equal(0, _db.NodeCount);
        using (var tx = _db.BeginTransaction())
        {
            tx.Execute("CREATE (:T {v: 1})");
            // a failing statement is undone but the transaction survives
            Assert.Throws<CypherRuntimeException>(() => tx.Execute("CREATE (:T {v: 2}) WITH 1 AS x RETURN x / 0"));
            tx.Commit();
        }
        Assert.Equal([1L], Col<long>("MATCH (t:T) RETURN t.v"));
        using (var rtx = _db.BeginReadTransaction())
            Assert.Throws<GraphQLiteException>(() => rtx.Execute("CREATE (:X)"));
    }

    [Fact]
    public void Rollback_Restores_Everything()
    {
        SeedSocial();
        Q("CREATE INDEX FOR (p:Person) ON (p.name)");
        var before = Q("MATCH (n) OPTIONAL MATCH (n)-[r]->() RETURN n.name AS n, labels(n) AS l, properties(n) AS p, count(r) AS c ORDER BY n").Select(r => r.ToString()).ToList();
        using (var tx = _db.BeginTransaction())
        {
            tx.Execute("MATCH (p:Person {name:'Alice'}) DETACH DELETE p");
            tx.Execute("MATCH (p:Person {name:'Bob'}) SET p.name = 'Robert', p:Changed REMOVE p.age");
            tx.Execute("MATCH (c:Company) REMOVE c:Company");
            tx.Execute("CREATE (:Person {name: 'New'})-[:KNOWS]->(:Person {name: 'New2'})");
            tx.Execute("DROP INDEX index_Person_name");
        }
        var after = Q("MATCH (n) OPTIONAL MATCH (n)-[r]->() RETURN n.name AS n, labels(n) AS l, properties(n) AS p, count(r) AS c ORDER BY n").Select(r => r.ToString()).ToList();
        Assert.Equal(before, after);
        Assert.Single(Q("MATCH (p:Person {name: 'Bob'}) RETURN p"));
        Assert.Contains("NodeIndexSeek", Q("EXPLAIN MATCH (p:Person {name: 'Bob'}) RETURN p").Scalar<string>()!);
    }

    [Fact]
    public void Return_Star_And_Column_Names()
    {
        Q("CREATE (:A {x: 1})");
        var r = Q("MATCH (a:A) WITH a, a.x AS x RETURN *");
        Assert.Equal(["a", "x"], r.Columns);
        var r2 = Q("MATCH (a:A) RETURN a.x, a.x + 1");
        Assert.Equal(["a.x", "a.x + 1"], r2.Columns);
    }

    [Fact]
    public void Tokens_Created_Earlier_In_Same_Query_Are_Visible()
    {
        var r = Q("UNWIND range(1, 3) AS i CREATE (n:N {i: i}) WITH n MATCH (m:N) RETURN n.i AS i, count(m) AS c ORDER BY i");
        Assert.Equal([3L, 3L, 3L], r.Select(x => x.Get<long>("c")));
        var r2 = Q("CREATE (a:Q)-[:NEWTYPE]->(b:Q) WITH a MATCH (a)-[:NEWTYPE]->(x:Q) RETURN count(x)");
        Assert.Equal(1L, r2.Scalar<long>());
        var r3 = Q("CREATE (a:Z)-[:T2]->(:Z)-[:T2]->(b:Z) WITH a, b MATCH p = (a)-[:T2*]->(b) RETURN length(p)");
        Assert.Equal(2L, r3.Scalar<long>());
    }

    [Fact]
    public void Cartesian_Product_And_Multiple_Parts()
    {
        Q("UNWIND range(1, 3) AS i CREATE (:X {i: i}) CREATE (:Y {i: i})");
        Assert.Equal(9, Q("MATCH (x:X), (y:Y) RETURN x, y").Count);
        Assert.Equal(3, Q("MATCH (x:X), (y:Y) WHERE x.i = y.i RETURN x, y").Count);
    }
}
