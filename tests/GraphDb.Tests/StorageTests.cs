namespace GraphDb.Tests;

public class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gdbtest_" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "test.gdb");

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Data_Survives_Reopen_With_Checkpoint()
    {
        using (var db = GraphDatabase.Open(DbPath))
        {
            db.Execute("CREATE INDEX FOR (p:Person) ON (p.name)");
            db.Execute("CREATE (:Person {name: 'A', tags: ['x', 'y'], score: 1.5, ok: true})-[:KNOWS {w: 3}]->(:Person {name: 'B'})");
            db.Execute("MATCH (p:Person {name: 'B'}) SET p.age = 40");
        }
        Assert.True(File.Exists(DbPath));
        using (var db = GraphDatabase.Open(DbPath))
        {
            var r = db.Execute("MATCH (a:Person)-[k:KNOWS]->(b) RETURN a, k.w AS w, b.age AS age")[0];
            var a = r["a"].AsNode();
            Assert.Equal("A", a["name"].AsString());
            Assert.Equal(2, a["tags"].AsList().Count);
            Assert.Equal(1.5, a["score"].AsDouble());
            Assert.True(a["ok"].AsBoolean());
            Assert.Equal(3L, r.Get<long>("w"));
            Assert.Equal(40L, r.Get<long>("age"));
            Assert.Contains("NodeIndexSeek", db.Execute("EXPLAIN MATCH (p:Person {name:'A'}) RETURN p").Scalar<string>()!);
        }
    }

    [Fact]
    public void Wal_Is_Replayed_Without_Checkpoint()
    {
        var opts = new DatabaseOptions { CheckpointOnClose = false, Sync = SyncMode.Full };
        using (var db = GraphDatabase.Open(DbPath, opts))
        {
            db.Execute("UNWIND range(1, 100) AS i CREATE (:N {i: i})");
            db.Execute("MATCH (n:N) WHERE n.i % 2 = 0 DELETE n");
            db.Execute("MATCH (n:N {i: 1}) SET n:First REMOVE n.i");
            using var tx = db.BeginTransaction();
            tx.Execute("CREATE (:Uncommitted)");
            // disposed without commit -> rolled back
        }
        Assert.True(new FileInfo(DbPath + "-wal").Length > 0);
        using (var db = GraphDatabase.Open(DbPath, opts))
        {
            Assert.Equal(50, db.NodeCount);
            Assert.Equal(1L, db.Execute("MATCH (n:First) WHERE n.i IS NULL RETURN count(n)").Scalar<long>());
            Assert.Equal(0L, db.Execute("MATCH (n:Uncommitted) RETURN count(n)").Scalar<long>());
            // keep writing on top of the replayed state: ids stay consistent
            db.Execute("CREATE (:N {i: 1000})");
        }
        using (var db = GraphDatabase.Open(DbPath, opts))
            Assert.Equal(51, db.NodeCount);
    }

    [Fact]
    public void Torn_Wal_Tail_Is_Discarded()
    {
        var opts = new DatabaseOptions { CheckpointOnClose = false };
        using (var db = GraphDatabase.Open(DbPath, opts))
        {
            db.Execute("CREATE (:A)");
            db.Execute("CREATE (:B)");
        }
        // corrupt the last frame (simulate a crash during write)
        using (var fs = new FileStream(DbPath + "-wal", FileMode.Open))
        {
            fs.SetLength(fs.Length - 3);
        }
        using (var db = GraphDatabase.Open(DbPath, opts))
        {
            Assert.Equal(1, db.NodeCount);
            Assert.Equal(1L, db.Execute("MATCH (a:A) RETURN count(a)").Scalar<long>());
            db.Execute("CREATE (:C)");
        }
        using (var db = GraphDatabase.Open(DbPath, opts))
            Assert.Equal(2, db.NodeCount);
    }

    [Fact]
    public void Checkpoint_Then_More_Wal()
    {
        var opts = new DatabaseOptions { CheckpointOnClose = false };
        using (var db = GraphDatabase.Open(DbPath, opts))
        {
            db.Execute("CREATE (:A {v: 1})");
            db.Checkpoint();
            Assert.Equal(0, new FileInfo(DbPath + "-wal").Length);
            db.Execute("MATCH (a:A) SET a.v = 2");
            db.Execute("CREATE (:B)");
        }
        using (var db = GraphDatabase.Open(DbPath, opts))
        {
            Assert.Equal(2L, db.Execute("MATCH (a:A) RETURN a.v").Scalar<long>());
            Assert.Equal(2, db.NodeCount);
        }
    }

    [Fact]
    public void Database_File_Is_Locked_By_Owner()
    {
        using var db = GraphDatabase.Open(DbPath);
        Assert.Throws<GraphDbException>(() => GraphDatabase.Open(DbPath));
    }

    [Fact]
    public async Task Concurrent_Readers_With_Writer()
    {
        using var db = GraphDatabase.OpenInMemory();
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
                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();
        SpinWait.SpinUntil(() => Interlocked.Read(ref reads) > 20, TimeSpan.FromSeconds(10));
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
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
        Assert.Equal(2400, db.NodeCount);
    }

    [Fact]
    public async Task Many_Read_Transactions_Run_In_Parallel()
    {
        using var db = GraphDatabase.OpenInMemory();
        db.Execute("CREATE (:N)");
        // all readers must be inside their transaction at the same time
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
