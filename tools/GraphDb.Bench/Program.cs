using System.Diagnostics;
using GraphDb;

// Usage: GraphDb.Bench [nodes] [relsPerNode] [file]
int nodes = args.Length > 0 ? int.Parse(args[0]) : 200_000;
int relsPerNode = args.Length > 1 ? int.Parse(args[1]) : 5;
string? file = args.Length > 2 ? args[2] : null;

if (file != null)
{
    if (Directory.Exists(file)) Directory.Delete(file, true);
}

using var db = file is null ? GraphDatabase.OpenInMemory() : GraphDatabase.Open(file, new DatabaseOptions { Sync = SyncMode.Normal });
Console.WriteLine($"GraphDb benchmark: {nodes:N0} nodes, {nodes * relsPerNode:N0} relationships, {(file is null ? "in-memory" : file)}");
Console.WriteLine($"Processors: {Environment.ProcessorCount}");
Console.WriteLine();

static void Report(string name, long ops, TimeSpan t) =>
    Console.WriteLine($"{name,-48} {ops,12:N0} ops  {t.TotalMilliseconds,10:N1} ms  {ops / t.TotalSeconds,14:N0} ops/s");

db.Execute("CREATE CONSTRAINT FOR (p:Person) REQUIRE p.id IS UNIQUE");

// ---------------------------------------------------------------- bulk load nodes
var sw = Stopwatch.StartNew();
const int batch = 10_000;
for (int start = 0; start < nodes; start += batch)
{
    var rows = new List<object?>(batch);
    for (int i = start; i < Math.Min(nodes, start + batch); i++)
        rows.Add(new Dictionary<string, object?> { ["id"] = i, ["name"] = "person" + i, ["age"] = 18 + i % 60 });
    db.Execute("UNWIND $rows AS r CREATE (:Person {id: r.id, name: r.name, age: r.age})", new Dictionary<string, object?> { ["rows"] = rows });
}
Report("bulk create nodes (UNWIND, 10k/tx)", nodes, sw.Elapsed);

// ---------------------------------------------------------------- bulk load relationships
var rnd = new Random(42);
sw.Restart();
long totalRels = (long)nodes * relsPerNode;
for (long start = 0; start < totalRels; start += batch)
{
    var rows = new List<object?>(batch);
    for (long i = start; i < Math.Min(totalRels, start + batch); i++)
        rows.Add(new Dictionary<string, object?> { ["a"] = (int)(i / relsPerNode), ["b"] = rnd.Next(nodes), ["w"] = rnd.Next(100) });
    db.Execute("UNWIND $rows AS r MATCH (a:Person {id: r.a}), (b:Person {id: r.b}) CREATE (a)-[:KNOWS {w: r.w}]->(b)",
        new Dictionary<string, object?> { ["rows"] = rows });
}
Report("bulk create rels (UNWIND + 2 index seeks)", totalRels, sw.Elapsed);

// ---------------------------------------------------------------- point lookups
int lookups = 200_000;
var p = new Dictionary<string, object?> { ["id"] = 0 };
sw.Restart();
for (int i = 0; i < lookups; i++)
{
    p["id"] = rnd.Next(nodes);
    db.Execute("MATCH (p:Person {id: $id}) RETURN p.name", p);
}
Report("point lookup by unique index (1 thread)", lookups, sw.Elapsed);

// ---------------------------------------------------------------- parallel point lookups
int threads = Environment.ProcessorCount;
sw.Restart();
Parallel.For(0, threads, t =>
{
    var r = new Random(t);
    var pp = new Dictionary<string, object?> { ["id"] = 0 };
    for (int i = 0; i < lookups; i++)
    {
        pp["id"] = r.Next(nodes);
        db.Execute("MATCH (p:Person {id: $id}) RETURN p.name", pp);
    }
});
Report($"point lookup by unique index ({threads} threads)", (long)lookups * threads, sw.Elapsed);

// ---------------------------------------------------------------- 1-hop / 2-hop
int hops = 20_000;
sw.Restart();
long sum = 0;
for (int i = 0; i < hops; i++)
{
    p["id"] = rnd.Next(nodes);
    sum += db.Execute("MATCH (p:Person {id: $id})-[:KNOWS]->(f) RETURN count(f)", p).Scalar<long>();
}
Report("1-hop neighbour count", hops, sw.Elapsed);

sw.Restart();
for (int i = 0; i < hops; i++)
{
    p["id"] = rnd.Next(nodes);
    sum += db.Execute("MATCH (p:Person {id: $id})-[:KNOWS]->()-[:KNOWS]->(fof) RETURN count(DISTINCT fof)", p).Scalar<long>();
}
Report("2-hop friends-of-friends (distinct)", hops, sw.Elapsed);

sw.Restart();
Parallel.For(0, threads, t =>
{
    var r = new Random(t);
    var pp = new Dictionary<string, object?> { ["id"] = 0 };
    for (int i = 0; i < hops; i++)
    {
        pp["id"] = r.Next(nodes);
        db.Execute("MATCH (p:Person {id: $id})-[:KNOWS]->()-[:KNOWS]->(fof) RETURN count(DISTINCT fof)", pp);
    }
});
Report($"2-hop friends-of-friends ({threads} threads)", (long)hops * threads, sw.Elapsed);

// ---------------------------------------------------------------- scans / aggregations
sw.Restart();
var agg = db.Execute("MATCH (p:Person) RETURN p.age AS age, count(*) AS c ORDER BY c DESC LIMIT 3");
Report("label scan + group by age", nodes, sw.Elapsed);

sw.Restart();
var filt = db.Execute("MATCH (p:Person) WHERE p.age > 50 AND p.name STARTS WITH 'person1' RETURN count(p)").Scalar<long>();
Report("label scan + filter", nodes, sw.Elapsed);

sw.Restart();
var relScan = db.Execute("MATCH (:Person)-[k:KNOWS]->(:Person) WHERE k.w < 10 RETURN count(k)").Scalar<long>();
Report("expand all relationships + filter on rel prop", totalRels, sw.Elapsed);

sw.Restart();
var top = db.Execute("MATCH (p:Person)<-[:KNOWS]-(f) RETURN p.id, count(f) AS deg ORDER BY deg DESC LIMIT 5");
Report("in-degree top-5 (aggregate + top-N)", totalRels, sw.Elapsed);

sw.Restart();
var sp = 0L;
for (int i = 0; i < 1000; i++)
{
    var pr = new Dictionary<string, object?> { ["a"] = rnd.Next(nodes), ["b"] = rnd.Next(nodes) };
    sp += db.Execute("MATCH (a:Person {id: $a}), (b:Person {id: $b}) MATCH p = shortestPath((a)-[:KNOWS*..6]->(b)) RETURN length(p)", pr).Count;
}
Report("shortestPath (max 6 hops)", 1000, sw.Elapsed);

// ---------------------------------------------------------------- single-row writes
sw.Restart();
int writes = 20_000;
for (int i = 0; i < writes; i++)
{
    p["id"] = rnd.Next(nodes);
    db.Execute("MATCH (p:Person {id: $id}) SET p.visits = coalesce(p.visits, 0) + 1", p);
}
Report("single-statement update transactions", writes, sw.Elapsed);

if (file != null)
{
    sw.Restart();
    db.Checkpoint();
    Report("checkpoint", 1, sw.Elapsed);
    Console.WriteLine($"database size: {new DirectoryInfo(file).GetFiles().Sum(x => x.Length) / 1024.0 / 1024.0:N1} MB");
}
Console.WriteLine($"(checksum {sum + sp + filt + relScan + agg.Count + top.Count})");
