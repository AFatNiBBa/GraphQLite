namespace GraphDb.Tests;

public class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gdbtest_" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "db");
    private string WalPath => Path.Combine(DbPath, "wal");

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Data_Survives_Reopen()
    {
        using (var db = GraphDatabase.Open(DbPath))
        {
            db.Execute("CREATE INDEX FOR (p:Person) ON (p.name)");
            db.Execute("CREATE (:Person {name: 'A', tags: ['x', 'y'], score: 1.5, ok: true, born: date('1990-05-17')})-[:KNOWS {w: 3}]->(:Person {name: 'B'})");
            db.Execute("MATCH (p:Person {name: 'B'}) SET p.age = 40, p.profile = {city: 'Rome', langs: ['it', 'en'], address: {zip: '00100'}}");
        }
        Assert.True(Directory.Exists(DbPath));
        Assert.True(File.Exists(Path.Combine(DbPath, "data")));
        using (var db = GraphDatabase.Open(DbPath))
        {
            var r = db.Execute("MATCH (a:Person)-[k:KNOWS]->(b) RETURN a, k.w AS w, b.age AS age, b.profile.address.zip AS zip, b.profile.langs[1] AS lang")[0];
            var a = r["a"].AsNode();
            Assert.Equal("A", a["name"].AsString());
            Assert.Equal(2, a["tags"].AsList().Count);
            Assert.Equal(1.5, a["score"].AsDouble());
            Assert.True(a["ok"].AsBoolean());
            Assert.Equal(new DateOnly(1990, 5, 17), a["born"].AsDate());
            Assert.Equal(3L, r.Get<long>("w"));
            Assert.Equal(40L, r.Get<long>("age"));
            Assert.Equal("00100", r.Get<string>("zip"));
            Assert.Equal("en", r.Get<string>("lang"));
            Assert.Contains("NodeIndexSeek", db.Execute("EXPLAIN MATCH (p:Person {name:'A'}) RETURN p").Scalar<string>()!);
            Assert.Single(db.Execute("MATCH (p:Person {name:'A'}) RETURN p"));
        }
    }

    [Fact]
    public void Wal_Is_Replayed_After_A_Crash()
    {
        var opts = new DatabaseOptions { Sync = SyncMode.Full };
        var db = GraphDatabase.Open(DbPath, opts);
        db.Execute("UNWIND range(1, 100) AS i CREATE (:N {i: i})");
        db.Execute("MATCH (n:N) WHERE n.i % 2 = 0 DELETE n");
        db.Execute("MATCH (n:N {i: 1}) SET n:First REMOVE n.i");
        var tx = db.BeginTransaction();
        tx.Execute("CREATE (:Uncommitted)");
        Assert.True(new FileInfo(WalPath).Length > 0);
        tx.Rollback();
        db.SimulateCrash();

        using (db = GraphDatabase.Open(DbPath, opts))
        {
            Assert.Equal(50, db.NodeCount);
            Assert.Equal(1L, db.Execute("MATCH (n:First) WHERE n.i IS NULL RETURN count(n)").Scalar<long>());
            Assert.Equal(0L, db.Execute("MATCH (n:Uncommitted) RETURN count(n)").Scalar<long>());
            db.Execute("CREATE (:N {i: 1000})");
        }
        using (db = GraphDatabase.Open(DbPath, opts))
            Assert.Equal(51, db.NodeCount);
    }

    [Fact]
    public void Torn_Wal_Tail_Is_Discarded()
    {
        var db = GraphDatabase.Open(DbPath);
        db.Execute("CREATE (:A)");
        db.Execute("CREATE (:B)");
        db.SimulateCrash();
        using (var fs = new FileStream(WalPath, FileMode.Open))
            fs.SetLength(fs.Length - 3); // simulate a crash in the middle of the last frame
        using (db = GraphDatabase.Open(DbPath))
        {
            Assert.Equal(1, db.NodeCount);
            Assert.Equal(1L, db.Execute("MATCH (a:A) RETURN count(a)").Scalar<long>());
            db.Execute("CREATE (:C)");
        }
        using (db = GraphDatabase.Open(DbPath))
            Assert.Equal(2, db.NodeCount);
    }

    [Fact]
    public void Explicit_Checkpoint_Truncates_Wal()
    {
        var db = GraphDatabase.Open(DbPath);
        db.Execute("CREATE (:A {v: 1})");
        db.Checkpoint();
        Assert.Equal(0, new FileInfo(WalPath).Length);
        db.Execute("MATCH (a:A) SET a.v = 2");
        db.Execute("CREATE (:B)");
        db.SimulateCrash();
        using (db = GraphDatabase.Open(DbPath))
        {
            Assert.Equal(2L, db.Execute("MATCH (a:A) RETURN a.v").Scalar<long>());
            Assert.Equal(2, db.NodeCount);
        }
    }

    [Fact]
    public void Database_Directory_Is_Locked_By_Owner()
    {
        using var db = GraphDatabase.Open(DbPath);
        Assert.Throws<GraphDbException>(() => GraphDatabase.Open(DbPath));
    }

    [Fact]
    public void Larger_Than_Memory_Budget_With_Many_Checkpoints()
    {
        // a tiny checkpoint threshold forces data to be continuously moved into the memory-mapped files
        var opts = new DatabaseOptions { CheckpointThresholdBytes = 256 * 1024 };
        const int n = 60_000;
        using (var db = GraphDatabase.Open(DbPath, opts))
        {
            db.Execute("CREATE CONSTRAINT FOR (u:User) REQUIRE u.id IS UNIQUE");
            for (int start = 0; start < n; start += 5000)
            {
                var rows = Enumerable.Range(start, 5000).Select(i => (object?)new Dictionary<string, object?> { ["id"] = i, ["name"] = "user" + i }).ToList();
                db.Execute("UNWIND $rows AS r CREATE (:User {id: r.id, name: r.name})", new Dictionary<string, object?> { ["rows"] = rows });
            }
            db.Execute("MATCH (a:User), (b:User) WHERE a.id < 2000 AND b.id = a.id + 1 CREATE (a)-[:NEXT]->(b)");
            Assert.True(new FileInfo(WalPath).Length < 8 * 1024 * 1024);
        }
        using (var db = GraphDatabase.Open(DbPath, opts))
        {
            Assert.Equal(n, db.NodeCount);
            Assert.Equal(2000, db.RelationshipCount);
            Assert.Equal("user12345", db.Execute("MATCH (u:User {id: 12345}) RETURN u.name").Scalar<string>());
            Assert.Equal(1000L, db.Execute("MATCH (u:User) WHERE u.id >= 5000 AND u.id < 6000 RETURN count(u)").Scalar<long>());
            Assert.Equal(5L, db.Execute("MATCH p = (:User {id: 0})-[:NEXT*5]->(x) RETURN x.id").Scalar<long>());
            Assert.Throws<ConstraintViolationException>(() => db.Execute("CREATE (:User {id: 7})"));
        }
    }

    private static List<string> Dump(GraphDatabase db) =>
        db.Execute("MATCH (n) OPTIONAL MATCH (n)-[r]->(m) RETURN id(n) AS id, labels(n) AS l, properties(n) AS p, type(r) AS t, properties(r) AS rp, id(m) AS m")
            .Select(r => r.ToString()).Order(StringComparer.Ordinal).ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // unique: B+tree plus hash index
    public void Random_Workload_Survives_Repeated_Crashes(bool unique)
    {
        var rnd = new Random(12345);
        var opts = new DatabaseOptions { CheckpointThresholdBytes = 64 * 1024 };
        using var reference = GraphDatabase.OpenInMemory();
        var queries = new[]
        {
            "UNWIND range(1, $n) AS i CREATE (:A {k: $k + i, s: 'v' + toString(i), doc: {x: i, tags: [i, i * 2]}})",
            "MATCH (a:A) WHERE a.k % 7 = $m SET a.s = a.s + '!', a:B",
            "MATCH (a:A), (b:A) WHERE a.k = $k AND b.k > $k AND b.k < $k + 4 CREATE (a)-[:R {w: $m}]->(b)",
            "MATCH (a:B) WHERE a.k % 5 = $m REMOVE a:B SET a.doc = null",
            "MATCH (a:A) WHERE a.k % 11 = $m DETACH DELETE a",
            "MATCH (a:A)-[r:R]->() WHERE r.w = $m DELETE r",
        };
        var db = GraphDatabase.Open(DbPath, opts);
        var schema = unique ? "CREATE CONSTRAINT FOR (a:A) REQUIRE a.k IS UNIQUE" : "CREATE INDEX FOR (a:A) ON (a.k)";
        db.Execute(schema);
        reference.Execute(schema);
        int k = 0;
        for (int step = 0; step < 120; step++)
        {
            var q = queries[rnd.Next(queries.Length)];
            var p = new Dictionary<string, object?> { ["n"] = rnd.Next(1, 40), ["k"] = rnd.Next(0, k + 1), ["m"] = rnd.Next(0, 7) };
            if (q.StartsWith("UNWIND")) { p["k"] = k; k += 40; }
            db.Execute(q, p);
            reference.Execute(q, p);
            if (rnd.Next(10) == 0)
            {
                // an uncommitted transaction is lost by the crash
                var tx = db.BeginTransaction();
                tx.Execute("CREATE (:Lost)");
                db.SimulateCrash();
                db = GraphDatabase.Open(DbPath, opts);
                Assert.Equal(Dump(reference), Dump(db));
            }
        }
        db.SimulateCrash();
        using (db = GraphDatabase.Open(DbPath, opts))
        {
            Assert.Equal(Dump(reference), Dump(db));
            Assert.Equal(reference.Execute("MATCH (a:A) WHERE a.k >= 100 AND a.k < 300 RETURN count(a)").Scalar<long>(),
                db.Execute("MATCH (a:A) WHERE a.k >= 100 AND a.k < 300 RETURN count(a)").Scalar<long>());
            // equality seeks (hash index when unique) agree with the reference for every key ever created
            const string seek = "UNWIND range(0, $k) AS x OPTIONAL MATCH (a:A {k: x}) RETURN x, a.s ORDER BY x";
            var pk = new Dictionary<string, object?> { ["k"] = k };
            Assert.Equal(string.Join("|", reference.Execute(seek, pk).Select(r => $"{r[0]}={r[1]}")),
                string.Join("|", db.Execute(seek, pk).Select(r => $"{r[0]}={r[1]}")));
        }
    }

    [Fact]
    public void Deleted_Space_Is_Reused()
    {
        using var db = GraphDatabase.Open(DbPath);
        long Pages() => db.Execute("CALL db.stats() YIELD dataPages RETURN dataPages").Scalar<long>();
        db.Execute("UNWIND range(1, 5000) AS i CREATE (:T {payload: 'x' + toString(i) + '..........................................'})");
        db.Execute("MATCH (t:T) DETACH DELETE t");
        long after1 = Pages();
        for (int round = 0; round < 3; round++)
        {
            db.Execute("UNWIND range(1, 5000) AS i CREATE (:T {payload: 'x' + toString(i) + '..........................................'})");
            db.Execute("MATCH (t:T) DETACH DELETE t");
        }
        Assert.True(Pages() <= after1 + 8, $"data pages grew from {after1} to {Pages()}");
    }

    [Fact]
    public async Task Readers_Are_Not_Blocked_By_An_Open_Write_Transaction()
    {
        using var db = GraphDatabase.Open(DbPath);
        db.Execute("CREATE (:N)");
        using var tx = db.BeginTransaction();
        tx.Execute("CREATE (:N), (:N)");
        // another thread reads while the write transaction is open: it must not wait and must see the old snapshot
        var count = await Task.Run(() => db.Execute("MATCH (n:N) RETURN count(n)").Scalar<long>()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1L, count);
        Assert.Equal(3L, tx.Execute("MATCH (n:N) RETURN count(n)").Scalar<long>());
        tx.Commit();
        Assert.Equal(3L, db.Execute("MATCH (n:N) RETURN count(n)").Scalar<long>());
    }

    [Fact]
    public void Read_Transaction_Keeps_Its_Snapshot()
    {
        using var db = GraphDatabase.OpenInMemory();
        db.Execute("CREATE (:N {v: 1})");
        using var r = db.BeginReadTransaction();
        db.Execute("MATCH (n:N) SET n.v = 2 CREATE (:N {v: 3})");
        Assert.Equal([1L], r.Execute("MATCH (n:N) RETURN n.v").Select(x => x.Get<long>(0)));
        Assert.Equal([2L, 3L], db.Execute("MATCH (n:N) RETURN n.v ORDER BY n.v").Select(x => x.Get<long>(0)));
    }

    [Fact]
    public async Task Concurrent_Readers_With_Writer()
    {
        using var db = GraphDatabase.Open(DbPath, new DatabaseOptions { CheckpointThresholdBytes = 512 * 1024 });
        db.Execute("UNWIND range(1, 2000) AS i CREATE (:N {i: i})");
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        long reads = 0;
        using var cts = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var c = db.Execute("MATCH (n:N) RETURN count(n) AS c, sum(n.i) AS s")[0];
                    long count = c.Get<long>("c");
                    // writers always add nodes in pairs within one transaction: readers must never see half of it
                    if (count % 2 != 0) throw new Exception("Observed a partially committed transaction: " + count);
                    if (c.Get<long>("s") != 2001000) throw new Exception("Inconsistent sum");
                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();
        SpinWait.SpinUntil(() => Interlocked.Read(ref reads) > 20, TimeSpan.FromSeconds(10));
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                using var tx = db.BeginTransaction();
                tx.Execute("CREATE (:N {i: 0})");
                tx.Execute("CREATE (:N {i: 0})");
                tx.Commit();
            }
        });
        await writer;
        cts.Cancel();
        await Task.WhenAll(readers);
        Assert.Empty(errors);
        Assert.True(reads > 0);
        Assert.Equal(2600, db.NodeCount);
    }

    [Fact]
    public async Task Many_Read_Transactions_Run_In_Parallel()
    {
        using var db = GraphDatabase.OpenInMemory();
        db.Execute("CREATE (:N)");
        using var barrier = new Barrier(6);
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            using var tx = db.BeginReadTransaction();
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "readers were serialized");
            return tx.Execute("MATCH (n) RETURN count(n)").Scalar<long>();
        })).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(1L, r));
    }
}
