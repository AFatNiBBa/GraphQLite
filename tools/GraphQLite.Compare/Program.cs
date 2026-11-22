using System.Diagnostics;
using Neo4j.Driver;
using G = GraphQLite;

// Compares GraphQLite with Neo4j on the same data and queries as tools/GraphQLite.Bench (200k nodes, 1M relationships),
// but time-based: every measure warms up first (JIT, plan caches) and keeps the best of three windows.
//
//   GraphQLite.Compare embedded <directory> [normal|full]           GraphQLite in-process, on disk
//   GraphQLite.Compare bolt <url> <user> <password>                  any Bolt server: Neo4j or GraphQLite.Server
//
// Options: --skip-load (reuse the data already loaded), --clients <n> (parallel clients; default: processor count
// in-process, 32 over Bolt), --parallel-runtime (Neo4j Enterprise: analytical queries with CYPHER runtime=parallel).
// The Bolt mode DELETES everything in the target database before loading. Neo4j embedded is measured by
// neo4j-embedded/Bench.java (same queries).

if (args.Length < 2 || args[0] is not ("embedded" or "bolt") || args[0] == "bolt" && args.Length < 4)
{
    Console.Error.WriteLine("usage: GraphQLite.Compare embedded <directory> [normal|full] | bolt <url> <user> <password> [options]");
    return 1;
}

const int Nodes = 200_000, RelsPerNode = 5, Batch = 10_000;
bool embedded = args[0] == "embedded";
ITarget target = embedded
    ? new Embedded(args[1], args.Length > 2 && args[2] == "full" ? G.SyncMode.Full : G.SyncMode.Normal)
    : new Bolt(args[1], args[2], args[3]);
int clientsArg = Array.IndexOf(args, "--clients");
int clients = clientsArg > 0 ? int.Parse(args[clientsArg + 1]) : embedded ? Environment.ProcessorCount : 32;
bool skipLoad = args.Contains("--skip-load");
string pre = args.Contains("--parallel-runtime") ? "CYPHER runtime=parallel " : "";
Console.WriteLine($"target: {(embedded ? $"GraphQLite in-process {args[1]} {(args.Length > 2 ? args[2] : "normal")}" : $"bolt {args[1]}")}");

static void Report(string name, double perSecond, string unit = "ops/s") =>
    Console.WriteLine($"{name,-50} {perSecond,14:N0} {unit}");

if (!skipLoad)
{
    await target.Reset();
    await target.Run("CREATE CONSTRAINT person_id IF NOT EXISTS FOR (p:Person) REQUIRE p.id IS UNIQUE", null);
    var sw = Stopwatch.StartNew();
    for (int start = 0; start < Nodes; start += Batch)
    {
        var rows = new List<object?>(Batch);
        for (int i = start; i < Math.Min(Nodes, start + Batch); i++)
            rows.Add(new Dictionary<string, object?> { ["id"] = i, ["name"] = "person" + i, ["age"] = 18 + i % 60 });
        await target.Run("UNWIND $rows AS r CREATE (:Person {id: r.id, name: r.name, age: r.age})", new() { ["rows"] = rows });
    }
    Report("bulk create nodes (UNWIND, 10k/tx)", Nodes / sw.Elapsed.TotalSeconds);
    var lr = new Random(42);
    sw.Restart();
    long totalRels = (long)Nodes * RelsPerNode;
    for (long start = 0; start < totalRels; start += Batch)
    {
        var rows = new List<object?>(Batch);
        for (long i = start; i < Math.Min(totalRels, start + Batch); i++)
            rows.Add(new Dictionary<string, object?> { ["a"] = (int)(i / RelsPerNode), ["b"] = lr.Next(Nodes), ["w"] = lr.Next(100) });
        await target.Run("UNWIND $rows AS r MATCH (a:Person {id: r.a}), (b:Person {id: r.b}) CREATE (a)-[:KNOWS {w: r.w}]->(b)",
            new() { ["rows"] = rows });
    }
    Report("bulk create rels (UNWIND + 2 index seeks)", totalRels / sw.Elapsed.TotalSeconds);
}

// ---------------------------------------------------------------- timed loops: warm-up, then best of 3 windows
async Task<double> Timed(int parallel, double seconds, Func<Random, (string Query, Dictionary<string, object?> Parameters)> next)
{
    async Task<double> Window(double secs, int seed)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, parallel).Select(c => Task.Run(async () =>
        {
            var r = new Random(seed * 1000 + c);
            long n = 0;
            while (sw.Elapsed.TotalSeconds < secs)
            {
                var (q, p) = next(r);
                await target.Run(q, p);
                n++;
            }
            Interlocked.Add(ref total, n);
        })));
        return total / sw.Elapsed.TotalSeconds;
    }
    await Window(Math.Max(1, seconds / 2), 0);
    double best = 0;
    for (int w = 1; w <= 3; w++) best = Math.Max(best, await Window(seconds, w));
    return best;
}

const string Lookup = "MATCH (p:Person {id: $id}) RETURN p.name";
const string OneHop = "MATCH (p:Person {id: $id})-[:KNOWS]->(f) RETURN count(f)";
const string TwoHop = "MATCH (p:Person {id: $id})-[:KNOWS]->()-[:KNOWS]->(fof) RETURN count(DISTINCT fof)";
const string Update = "MATCH (p:Person {id: $id}) SET p.visits = coalesce(p.visits, 0) + 1";
(string, Dictionary<string, object?>) Id(string q, Random r) => (q, new() { ["id"] = r.Next(Nodes) });

Report("point lookup by unique index (1 client)", await Timed(1, 2, r => Id(Lookup, r)));
Report($"point lookup by unique index ({clients} clients)", await Timed(clients, 2, r => Id(Lookup, r)));
Report("1-hop neighbour count (1 client)", await Timed(1, 2, r => Id(OneHop, r)));
Report("2-hop friends-of-friends (1 client)", await Timed(1, 2, r => Id(TwoHop, r)));
Report($"2-hop friends-of-friends ({clients} clients)", await Timed(clients, 2, r => Id(TwoHop, r)));
Report("shortestPath (max 6 hops, 1 client)", await Timed(1, 3, r => (
    "MATCH (a:Person {id: $a}), (b:Person {id: $b}) MATCH p = shortestPath((a)-[:KNOWS*..6]->(b)) RETURN length(p)",
    new() { ["a"] = r.Next(Nodes), ["b"] = r.Next(Nodes) })));

// ---------------------------------------------------------------- analytical queries: best of 5 after 3 warm-ups
async Task Analytic(string name, long rows, string q)
{
    for (int i = 0; i < 3; i++) await target.Run(q, null);
    var best = TimeSpan.MaxValue;
    for (int i = 0; i < 5; i++)
    {
        var s = Stopwatch.StartNew();
        await target.Run(q, null);
        if (s.Elapsed < best) best = s.Elapsed;
    }
    Report($"{name} [{best.TotalMilliseconds:N1} ms]", rows / best.TotalSeconds, "rows/s");
}
await Analytic("label scan + group by age", Nodes, pre + "MATCH (p:Person) RETURN p.age AS age, count(*) AS c ORDER BY c DESC LIMIT 3");
await Analytic("label scan + filter", Nodes, pre + "MATCH (p:Person) WHERE p.age > 50 AND p.name STARTS WITH 'person1' RETURN count(p)");
await Analytic("expand all relationships + filter on rel prop", (long)Nodes * RelsPerNode,
    pre + "MATCH (:Person)-[k:KNOWS]->(:Person) WHERE k.w < 10 RETURN count(k)");
await Analytic("in-degree top-5 (aggregate + top-N)", (long)Nodes * RelsPerNode,
    pre + "MATCH (p:Person)<-[:KNOWS]-(f) RETURN p.id, count(f) AS deg ORDER BY deg DESC LIMIT 5");

// ---------------------------------------------------------------- single-statement write transactions
Report("single-statement update (1 client)", await Timed(1, 3, r => Id(Update, r)));
Report($"single-statement update ({clients} clients)", await Timed(clients, 3, r => Id(Update, r)));

await target.DisposeAsync();
return 0;

interface ITarget : IAsyncDisposable
{
    Task Reset();
    Task Run(string query, Dictionary<string, object?>? parameters);
}

sealed class Embedded(string directory, G.SyncMode sync) : ITarget
{
    private G.GraphDatabase? _db;

    private G.GraphDatabase Db => _db ??= G.GraphDatabase.Open(directory, new G.DatabaseOptions { Sync = sync });

    public Task Reset()
    {
        _db?.Dispose();
        _db = null;
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        return Task.CompletedTask;
    }

    public Task Run(string query, Dictionary<string, object?>? parameters)
    {
        Db.Execute(query, parameters);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _db?.Dispose();
        return ValueTask.CompletedTask;
    }
}

sealed class Bolt(string url, string user, string password) : ITarget
{
    private readonly IDriver _driver = GraphDatabase.Driver(url, AuthTokens.Basic(user, password), o => o.WithMaxConnectionPoolSize(64));

    /// <summary>Deletes every node, constraint and index (except the token lookup indexes).</summary>
    public async Task Reset()
    {
        await Run("MATCH (n) CALL (n) { DETACH DELETE n } IN TRANSACTIONS OF 10000 ROWS", null);
        var drops = new List<string>();
        await using (var s = _driver.AsyncSession())
        {
            foreach (var (kind, drop) in new[] { ("CONSTRAINTS", "CONSTRAINT"), ("INDEXES", "INDEX") })
            {
                var c = await s.RunAsync($"SHOW {kind} YIELD name, type RETURN name, type");
                foreach (var r in await c.ToListAsync())
                    if (r["type"].As<string>() != "LOOKUP") drops.Add($"DROP {drop} `{r["name"].As<string>()}` IF EXISTS");
            }
        }
        foreach (var d in drops) await Run(d, null);
    }

    public async Task Run(string query, Dictionary<string, object?>? parameters)
    {
        await using var s = _driver.AsyncSession();
        var c = await s.RunAsync(query, parameters ?? new Dictionary<string, object?>());
        await c.ToListAsync(); // fetch every row: a discarded result could skip the work on a streaming server
    }

    public ValueTask DisposeAsync() => _driver.DisposeAsync();
}
