namespace GraphQLite.Tests;

/// <summary>Parallel execution (aggregations, scans, sorts) gives the results of a sequential run.</summary>
public class ParallelTests(ParallelTests.Graphs graphs) : IClassFixture<ParallelTests.Graphs>
{
    /// <summary>The same graph (200k nodes, holes in the id space) in a parallel and in a sequential database.</summary>
    public sealed class Graphs : IDisposable
    {
        public readonly GraphDatabase Par = Load(8), Seq = Load(1);

        private static GraphDatabase Load(int parallelism)
        {
            var db = GraphDatabase.OpenInMemory(new DatabaseOptions { MaxDegreeOfParallelism = parallelism });
            db.Execute("CREATE INDEX FOR (n:N) ON (n.i)");
            db.Execute("""
                UNWIND range(0, 199999) AS i
                CREATE (n:N {i: i, g: i % 37, s: 'v' + (i % 1000), f: (i % 101) / 7.0})
                WITH n, i WHERE i % 3 = 0 SET n:M
                """);
            db.Execute("MATCH (a:M) WHERE a.i % 2 = 0 MATCH (b:N {i: (a.i * 7) % 200000}) CREATE (a)-[:R {w: a.i % 13}]->(b)");
            db.Execute("MATCH (n:N) WHERE n.i % 1000 = 0 DETACH DELETE n"); // holes in the id space
            return db;
        }

        public void Dispose()
        {
            Par.Dispose();
            Seq.Dispose();
        }
    }

    private readonly GraphDatabase _par = graphs.Par, _seq = graphs.Seq;

    private static string Dump(GraphDatabase db, string q) => string.Join("\n", db.Execute(q).Select(r => r.ToString()));

    private void AssertSame(string[] queries)
    {
        foreach (var q in queries) Assert.Equal(Dump(_seq, q), Dump(_par, q));
    }

    [Fact]
    public void Parallel_Aggregation_Matches_Sequential()
    {
        AssertSame(
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
            // many groups, spanning several ranges and in order of first appearance
            "MATCH (n:N) RETURN n.i % 5003 AS k, count(*) AS c, collect(n.i)[0..3] AS l",
            "MATCH (n:N) RETURN n.s AS s, n.g AS g, count(n.f) AS c, max(n.i) AS m",
            "MATCH (b:N)<-[:R]-(a) RETURN b.i AS i, count(a) AS c ORDER BY c DESC, i LIMIT 5",
            "MATCH (a:M)-[:R]->(b) WITH a, count(b) AS c RETURN a.i, c ORDER BY a.i DESC LIMIT 3",
        ]);
        // errors raised by a worker surface as the original exception
        Assert.Throws<CypherRuntimeException>(() => _par.Execute("MATCH (n:N) RETURN sum(n.s)"));
    }

    [Fact]
    public void Parallel_Scans_Return_The_Rows_Of_A_Sequential_Run_In_The_Same_Order()
    {
        AssertSame(
        [
            "MATCH (n:N) RETURN n.i, n.s",
            "MATCH (n:N) WHERE n.g = 3 RETURN n",
            "MATCH (n) WHERE n.i > 150000 RETURN DISTINCT n.g",
            "MATCH (n:N) RETURN n.i SKIP 70000",
            "MATCH (n:M) RETURN n.i UNION ALL MATCH (n:N) WHERE n.i < 30000 RETURN n.i",
            "MATCH p = (a:M)-[r:R]->(b) WHERE r.w < 3 RETURN p, r, b",
            "MATCH (n:N) RETURN n.i LIMIT 7",
        ]);
        Assert.Contains("Gather", _par.Execute("EXPLAIN MATCH (n:N) RETURN n.i").Scalar<string>());

        // an error in a later range reaches the reader after the rows before it
        long rows = 0;
        Assert.Throws<CypherRuntimeException>(() =>
        {
            using var c = _par.Stream("MATCH (n:N) RETURN CASE WHEN n.i > 150000 THEN n.s * 2 ELSE 0 END");
            while (c.MoveNext()) rows++;
        });
        Assert.True(rows > 100_000, $"{rows} rows before the error");
    }

    [Fact]
    public void Parallel_Sorts_Keep_The_Scan_Order_Among_Equal_Keys()
    {
        AssertSame(
        [
            "MATCH (n:N) RETURN n.i, n.g ORDER BY n.g",
            "MATCH (n:N) RETURN n.i, n.g ORDER BY n.g DESC, n.s LIMIT 50",
            "MATCH (n:N) RETURN n.i ORDER BY n.f LIMIT 0",
            "MATCH (a:M)-[r:R]->(b) RETURN b.i, r.w ORDER BY r.w DESC LIMIT 20",
        ]);
    }

    [Fact]
    public void Parallel_Workers_Stop_With_The_Statement()
    {
        // stopping early, or ending the transaction with the result open, stops the workers before the snapshot is released
        for (int i = 0; i < 20; i++)
        {
            using (var c = _par.Stream("MATCH (a:M)-[r:R]->(b) RETURN a, b"))
            {
                for (int k = 0; k < 10 && c.MoveNext(); k++) { }
            }
            var tx = _par.BeginReadTransaction();
            var open = tx.Stream("MATCH (n:N) RETURN n");
            open.MoveNext();
            if (i % 2 == 0) tx.Rollback();
            else tx.Commit();
        }
        Assert.Equal(199_800L, _par.Execute("MATCH (n:N) RETURN count(*)").Scalar<long>());
    }
}
