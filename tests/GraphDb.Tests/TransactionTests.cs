namespace GraphDb.Tests;

/// <summary>Streamed results and concurrent (optimistic) write transactions.</summary>
public class TransactionTests : IDisposable
{
    private readonly GraphDatabase _db = GraphDatabase.OpenInMemory();

    public void Dispose() => _db.Dispose();

    private long Count(string q) => _db.ExecuteScalar<long>(q);

    // ------------------------------------------------------------------ streaming

    [Fact]
    public void Stream_Produces_Rows_Lazily()
    {
        using var cursor = _db.Stream("UNWIND range(1, 3000000) AS i RETURN i, i * 2 AS d");
        Assert.Equal(["i", "d"], cursor.Columns);
        long n = 0, sum = 0;
        while (cursor.MoveNext())
        {
            n++;
            sum += cursor.Current.Get<long>("d");
        }
        Assert.Equal(3000000, n);
        Assert.Equal(3000000L * 3000001, sum);
        Assert.True(cursor.IsCompleted);
    }

    [Fact]
    public void Stream_Disposed_Early_Releases_The_Transaction()
    {
        _db.Execute("UNWIND range(1, 1000) AS i CREATE (:N {i: i})");
        using (var cursor = _db.Stream("MATCH (n:N) RETURN n.i"))
        {
            Assert.True(cursor.MoveNext());
        }
        // a checkpoint needs every reader gone: it would hang if the read transaction were still open
        _db.Checkpoint();
        Assert.Equal(1000, Count("MATCH (n:N) RETURN count(n)"));
    }

    [Fact]
    public void Updating_Stream_Applies_All_Updates_Even_If_Not_Read()
    {
        _db.Execute("UNWIND range(1, 100) AS i CREATE (:N {i: i})");
        using (var cursor = _db.Stream("MATCH (n:N) SET n.done = true RETURN n.i"))
        {
            Assert.True(cursor.MoveNext());
        }
        Assert.Equal(100, Count("MATCH (n:N) WHERE n.done RETURN count(n)"));
    }

    [Fact]
    public void New_Statement_Buffers_The_Rows_Of_The_Previous_One()
    {
        _db.Execute("UNWIND range(1, 10) AS i CREATE (:N {i: i})");
        using var tx = _db.BeginTransaction();
        var first = tx.Stream("MATCH (n:N) RETURN n.i AS i ORDER BY i");
        Assert.True(first.MoveNext());
        Assert.Equal(1, first.Current.Get<long>("i"));
        // the next statement deletes everything: the first cursor still returns its own (already computed) rows
        tx.Execute("MATCH (n:N) DETACH DELETE n");
        var rest = new List<long>();
        while (first.MoveNext()) rest.Add(first.Current.Get<long>("i"));
        Assert.Equal(Enumerable.Range(2, 9).Select(i => (long)i), rest);
        tx.Commit();
        Assert.Equal(0, Count("MATCH (n) RETURN count(n)"));
    }

    [Fact]
    public void Failing_Stream_Rolls_Back_The_Statement()
    {
        using var tx = _db.BeginTransaction();
        tx.Execute("CREATE (:Keep)");
        var cursor = tx.Stream("UNWIND [1, 2, 0] AS x CREATE (:Gone) RETURN 10 / x");
        Assert.True(cursor.MoveNext());
        Assert.True(cursor.MoveNext());
        Assert.ThrowsAny<GraphDbException>(() => cursor.MoveNext());
        tx.Commit();
        Assert.Equal(1, Count("MATCH (n:Keep) RETURN count(n)"));
        Assert.Equal(0, Count("MATCH (n:Gone) RETURN count(n)"));
    }

    // ------------------------------------------------------------------ ids

    [Fact]
    public void Exclusive_Writer_Keeps_Ids_Dense_After_A_Failed_Statement()
    {
        Assert.Equal(0, _db.ExecuteScalar<long>("CREATE (n) RETURN id(n)"));
        using (var tx = _db.BeginTransaction())
        {
            Assert.ThrowsAny<GraphDbException>(() => tx.Execute("UNWIND [1, 0] AS x CREATE (n {v: 1 / x})"));
            Assert.Equal(1, tx.Execute("CREATE (n) RETURN id(n)").Scalar<long>());
            tx.Rollback();
        }
        Assert.Equal(1, _db.ExecuteScalar<long>("CREATE (n) RETURN id(n)"));
    }

    // ------------------------------------------------------------------ concurrent transactions

    [Fact]
    public void Concurrent_Writers_Do_Not_Block_Each_Other()
    {
        using var a = _db.BeginConcurrentTransaction();
        using var b = _db.BeginConcurrentTransaction();
        var na = a.Execute("CREATE (n:A {name: 'a', at: datetime()}) RETURN n, id(n) AS id").Single();
        var nb = b.Execute("CREATE (n:B {name: 'b', r: rand(), u: randomUUID()}) RETURN n, id(n) AS id").Single();
        Assert.NotEqual(na.Get<long>("id"), nb.Get<long>("id"));
        // uncommitted work is invisible to others
        Assert.Equal(0, Count("MATCH (n) RETURN count(n)"));
        a.Commit();
        // b started before a committed: it is replayed, and its result (same id, same random values) does not change
        b.Commit();
        Assert.Equal(1, Count("MATCH (n:A) RETURN count(n)"));
        Assert.Equal(1, Count("MATCH (n:B) RETURN count(n)"));
        Assert.Equal(nb.Get<long>("id"), Count("MATCH (n:B) RETURN id(n)"));
    }

    [Fact]
    public void Concurrent_Increments_Are_Not_Lost()
    {
        _db.Execute("CREATE (:Counter {v: 0})");
        using var a = _db.BeginConcurrentTransaction();
        using var b = _db.BeginConcurrentTransaction();
        a.Execute("MATCH (c:Counter) SET c.v = c.v + 1");
        b.Execute("MATCH (c:Counter) SET c.v = c.v + 1");
        a.Commit();
        b.Commit(); // replayed on top of a: no result was returned, so it commits
        Assert.Equal(2, Count("MATCH (c:Counter) RETURN c.v"));
    }

    [Fact]
    public void Conflicting_Read_Fails_At_Commit()
    {
        _db.Execute("CREATE (:Counter {v: 0})");
        using var a = _db.BeginConcurrentTransaction();
        using var b = _db.BeginConcurrentTransaction();
        Assert.Equal(0, a.Execute("MATCH (c:Counter) RETURN c.v").Scalar<long>());
        a.Execute("MATCH (c:Counter) SET c.v = 10");
        Assert.Equal(0, b.Execute("MATCH (c:Counter) SET c.v = c.v + 1 RETURN c.v - 1").Scalar<long>());
        a.Commit();
        // b returned 0, but on the latest state it would return 10
        Assert.Throws<TransactionConflictException>(b.Commit);
        Assert.Equal(10, Count("MATCH (c:Counter) RETURN c.v"));
    }

    [Fact]
    public void Concurrent_Unique_Violation_Is_Detected_At_Commit()
    {
        _db.Execute("CREATE CONSTRAINT FOR (u:User) REQUIRE u.email IS UNIQUE");
        using var a = _db.BeginConcurrentTransaction();
        using var b = _db.BeginConcurrentTransaction();
        a.Execute("CREATE (:User {email: 'x@y'})");
        b.Execute("CREATE (:User {email: 'x@y'})");
        a.Commit();
        Assert.Throws<TransactionConflictException>(b.Commit);
        Assert.Equal(1, Count("MATCH (u:User) RETURN count(u)"));
        // the retry sees the committed node and gets the constraint error
        using var retry = _db.BeginConcurrentTransaction();
        Assert.Throws<ConstraintViolationException>(() => retry.Execute("CREATE (:User {email: 'x@y'})"));
    }

    [Fact]
    public void Read_Only_Concurrent_Transaction_Never_Conflicts()
    {
        _db.Execute("CREATE (:N)");
        using var a = _db.BeginConcurrentTransaction();
        Assert.Equal(1, a.Execute("MATCH (n) RETURN count(n)").Scalar<long>());
        _db.Execute("CREATE (:N)");
        a.Commit();
    }

    [Fact]
    public async Task Concurrent_And_Exclusive_Writers_Under_Load()
    {
        _db.Execute("CREATE (:Counter {v: 0})");
        const int threads = 8, perThread = 50;
        int conflicts = 0;
        var workers = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                if (i % 5 == 0)
                {
                    _db.Execute("MATCH (c:Counter) SET c.v = c.v + 1 CREATE (:Item {t: $t, i: $i})", new Dictionary<string, object?> { ["t"] = t, ["i"] = i });
                    continue;
                }
                while (true)
                {
                    using var tx = _db.BeginConcurrentTransaction();
                    long v = tx.Execute("MATCH (c:Counter) RETURN c.v").Scalar<long>();
                    tx.Execute("MATCH (c:Counter) SET c.v = $v + 1", new Dictionary<string, object?> { ["v"] = v });
                    tx.Execute("CREATE (n:Item {t: $t, i: $i}) RETURN n", new Dictionary<string, object?> { ["t"] = t, ["i"] = i });
                    try
                    {
                        tx.Commit();
                        break;
                    }
                    catch (TransactionConflictException)
                    {
                        Interlocked.Increment(ref conflicts);
                    }
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);
        Assert.Equal(threads * perThread, Count("MATCH (c:Counter) RETURN c.v"));
        Assert.Equal(threads * perThread, Count("MATCH (n:Item) RETURN count(n)"));
        Assert.Equal(threads * perThread, Count("MATCH (n:Item) RETURN count(DISTINCT [n.t, n.i])"));
        Assert.Equal(threads * perThread + 1, Count("MATCH (n) RETURN count(DISTINCT id(n))"));
    }

    [Fact]
    public void Concurrent_Transactions_Survive_Restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gdbtx_" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var db = GraphDatabase.Open(dir))
            {
                using var a = db.BeginConcurrentTransaction();
                using var b = db.BeginConcurrentTransaction();
                a.Execute("CREATE (:A)-[:R]->(:A)");
                b.Execute("CREATE (:B)-[:R]->(:B)");
                b.Commit();
                a.Commit();
                db.Execute("CREATE (:C)");
            }
            using (var db = GraphDatabase.Open(dir))
            {
                Assert.Equal(5, db.ExecuteScalar<long>("MATCH (n) RETURN count(n)"));
                Assert.Equal(2, db.ExecuteScalar<long>("MATCH ()-[r:R]->() RETURN count(r)"));
                Assert.Equal(5, db.ExecuteScalar<long>("MATCH (n) RETURN count(DISTINCT id(n))"));
                // new ids do not collide with the persisted ones
                db.Execute("CREATE (:D)");
                Assert.Equal(6, db.ExecuteScalar<long>("MATCH (n) RETURN count(DISTINCT id(n))"));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
