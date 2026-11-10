using System.Net;
using GraphQLite.Server.Bolt;
using Neo4j.Driver; // As<T>() extensions; GraphQLite types still win name lookups (enclosing namespace)
using N = Neo4j.Driver;

namespace GraphQLite.Tests;

/// <summary>End-to-end tests of the Bolt server with the official Neo4j .NET driver.</summary>
public sealed class BoltTests : IAsyncLifetime
{
    private readonly GraphDatabase _db = GraphDatabase.OpenInMemory();
    private BoltServer _server = null!;
    private N.IDriver _driver = null!;

    private string Address => $"localhost:{_server.LocalEndpoint.Port}";

    public Task InitializeAsync()
    {
        _server = new BoltServer(_db, new BoltServerOptions { Listen = new IPEndPoint(IPAddress.Loopback, 0) });
        _server.Start();
        _driver = N.GraphDatabase.Driver("bolt://" + Address, N.AuthTokens.None);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _driver.DisposeAsync();
        _server.Dispose();
        _db.Dispose();
    }

    private async Task<List<N.IRecord>> Run(string query, object? parameters = null, N.IDriver? driver = null)
    {
        await using var session = (driver ?? _driver).AsyncSession();
        var cursor = await session.RunAsync(query, parameters);
        return await cursor.ToListAsync();
    }

    [Fact]
    public async Task Connects_And_Runs_A_Query()
    {
        await _driver.VerifyConnectivityAsync();
        var info = await _driver.GetServerInfoAsync();
        Assert.StartsWith("Neo4j/", info.Agent);
        Assert.Equal("5.4", info.ProtocolVersion);
        var rows = await Run("RETURN 1 AS x, 'a' + 'b' AS s");
        Assert.Equal(1L, rows[0]["x"].As<long>());
        Assert.Equal("ab", rows[0]["s"].As<string>());
    }

    [Fact]
    public async Task Routing_Driver_Uses_The_Server_As_Its_Own_Cluster()
    {
        await using var routing = N.GraphDatabase.Driver("neo4j://" + Address, N.AuthTokens.None);
        await routing.VerifyConnectivityAsync();
        var created = await routing.ExecutableQuery("CREATE (p:Person {name: $name}) RETURN p.name AS name")
            .WithParameters(new { name = "Alice" })
            .ExecuteAsync();
        Assert.Equal(1, created.Summary.Counters.NodesCreated);
        var read = await routing.ExecutableQuery("MATCH (p:Person) RETURN p.name AS name")
            .WithConfig(new N.QueryConfig(N.RoutingControl.Readers))
            .ExecuteAsync();
        Assert.Equal("Alice", read.Result.Single()["name"].As<string>());
    }

    [Fact]
    public async Task Values_Round_Trip()
    {
        var p = new Dictionary<string, object?>
        {
            ["i"] = 42L,
            ["neg"] = -1_000_000_000_000L,
            ["f"] = 1.5,
            ["b"] = true,
            ["s"] = "héllo",
            ["n"] = null,
            ["list"] = new List<object> { 1L, "x", new List<object> { 2.5 } },
            ["map"] = new Dictionary<string, object> { ["k"] = new Dictionary<string, object> { ["deep"] = 1L } },
            ["date"] = new N.LocalDate(2024, 2, 29),
            ["ltime"] = new N.LocalTime(13, 45, 30, 123_456_700),
            ["otime"] = new N.OffsetTime(8, 0, 0, 0, 3600),
            ["ldt"] = new N.LocalDateTime(1969, 7, 20, 20, 17, 40, 0),
            ["dt"] = new N.ZonedDateTime(new DateTimeOffset(2020, 1, 2, 3, 4, 5, 600, TimeSpan.FromHours(-5))),
            ["dur"] = new N.Duration(14, 3, 3661, 5_000_000),
            ["pt"] = new N.Point(4326, 12.49, 41.9),
            ["pt3"] = new N.Point(9157, 1, 2, 3),
        };
        var r = (await Run("RETURN $i AS i, $neg AS neg, $f AS f, $b AS b, $s AS s, $n AS n, $list AS list, $map AS map, " +
            "$date AS date, $ltime AS ltime, $otime AS otime, $ldt AS ldt, $dt AS dt, $dur AS dur, $pt AS pt, $pt3 AS pt3, " +
            "$date.year AS year, $dt.hour AS hour, $dt.offsetSeconds AS off, $dur.months AS months", p))[0];
        Assert.Equal(42L, r["i"].As<long>());
        Assert.Equal(-1_000_000_000_000L, r["neg"].As<long>());
        Assert.Equal(1.5, r["f"].As<double>());
        Assert.True(r["b"].As<bool>());
        Assert.Equal("héllo", r["s"].As<string>());
        Assert.Null(r["n"]);
        Assert.Equal(new N.OffsetTime(8, 0, 0, 0, 3600), r["otime"]);
        Assert.Equal(new N.Point(9157, 1, 2, 3), r["pt3"]);
        Assert.Equal(2024L, r["year"].As<long>());
        Assert.Equal(3L, r["hour"].As<long>());
        Assert.Equal(-18000L, r["off"].As<long>());
        Assert.Equal(14L, r["months"].As<long>());
        Assert.Equal(new List<object> { 1L, "x", new List<object> { 2.5 } }, r["list"].As<List<object>>());
        Assert.Equal(1L, r["map"].As<Dictionary<string, object>>()["k"].As<Dictionary<string, object>>()["deep"]);
        Assert.Equal(new N.LocalDate(2024, 2, 29), r["date"]);
        Assert.Equal(new N.LocalTime(13, 45, 30, 123_456_700), r["ltime"]);
        Assert.Equal(new N.LocalDateTime(1969, 7, 20, 20, 17, 40, 0), r["ldt"]);
        Assert.Equal(new N.Duration(14, 3, 3661, 5_000_000), r["dur"]);
        Assert.Equal(new N.Point(4326, 12.49, 41.9), r["pt"]);
        var dt = r["dt"].As<N.ZonedDateTime>();
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 3, 4, 5, 600, TimeSpan.FromHours(-5)), dt.ToDateTimeOffset());
        // values created on the server side
        var t = (await Run("RETURN date('2001-02-03') AS d, datetime('2015-06-24T12:50:35.556+01:00') AS dt, duration('P1DT2H') AS du"))[0];
        Assert.Equal(new N.LocalDate(2001, 2, 3), t["d"]);
        Assert.Equal(new DateTimeOffset(2015, 6, 24, 12, 50, 35, 556, TimeSpan.FromHours(1)), t["dt"].As<N.ZonedDateTime>().ToDateTimeOffset());
        Assert.Equal(new N.Duration(0, 1, 7200, 0), t["du"]);
    }

    [Fact]
    public async Task Nodes_Relationships_And_Paths()
    {
        var r = (await Run("CREATE p = (a:A:B {x: 1})-[k:KNOWS {since: 2020}]->(b:C {y: 'z'})<-[:LIKES]-(a) RETURN a, k, b, p"))[0];
        var a = r["a"].As<N.INode>();
        var k = r["k"].As<N.IRelationship>();
        var b = r["b"].As<N.INode>();
        Assert.Equal(["A", "B"], a.Labels.Order());
        Assert.Equal(1L, a.Properties["x"]);
        Assert.Equal("KNOWS", k.Type);
        Assert.Equal(2020L, k["since"]);
        Assert.Equal(a.ElementId, k.StartNodeElementId);
        Assert.Equal(b.ElementId, k.EndNodeElementId);
        var p = r["p"].As<N.IPath>();
        Assert.Equal(3, p.Nodes.Count);
        Assert.Equal(2, p.Relationships.Count);
        Assert.Equal(a.ElementId, p.Start.ElementId);
        Assert.Equal(a.ElementId, p.End.ElementId);
        Assert.Equal("LIKES", p.Relationships[1].Type);
        Assert.Equal(a.ElementId, p.Relationships[1].StartNodeElementId); // traversed backwards
        // element ids match elementId() and can be used to look entities up again
        var again = await Run("MATCH (n) WHERE elementId(n) = $id RETURN n.x AS x", new { id = a.ElementId });
        Assert.Equal(1L, again.Single()["x"].As<long>());
    }

    [Fact]
    public async Task Explicit_Transactions_Commit_And_Roll_Back()
    {
        await using (var session = _driver.AsyncSession())
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("CREATE (:T {v: 1})");
                var c = await tx.RunAsync("MATCH (t:T) RETURN count(t) AS c");
                Assert.Equal(1L, (await c.SingleAsync())["c"].As<long>());
                await tx.RunAsync("CREATE (:T {v: 2})");
            });
            var tx2 = await session.BeginTransactionAsync();
            await tx2.RunAsync("CREATE (:T {v: 3})");
            await tx2.RollbackAsync();
            var count = await session.ExecuteReadAsync(async tx => (await (await tx.RunAsync("MATCH (t:T) RETURN count(t) AS c")).SingleAsync())["c"].As<long>());
            Assert.Equal(2L, count);
        }
        Assert.Equal(2L, _db.ExecuteScalar<long>("MATCH (t:T) RETURN count(t)"));
    }

    [Fact]
    public async Task Errors_Have_Neo4j_Codes_And_The_Session_Recovers()
    {
        await using var session = _driver.AsyncSession();
        var syntax = await Assert.ThrowsAsync<N.ClientException>(async () => await (await session.RunAsync("RETURN (")).ToListAsync());
        Assert.Equal("Neo.ClientError.Statement.SyntaxError", syntax.Code);
        await session.RunAsync("CREATE CONSTRAINT FOR (u:U) REQUIRE u.id IS UNIQUE");
        await (await session.RunAsync("CREATE (:U {id: 1})")).ConsumeAsync();
        var dup = await Assert.ThrowsAnyAsync<N.Neo4jException>(async () => await (await session.RunAsync("CREATE (:U {id: 1})")).ConsumeAsync());
        Assert.Equal("Neo.ClientError.Schema.ConstraintValidationFailed", dup.Code);
        var missing = await Assert.ThrowsAnyAsync<N.Neo4jException>(async () => await (await session.RunAsync("RETURN $nope")).ConsumeAsync());
        Assert.Equal("Neo.ClientError.Statement.ParameterMissing", missing.Code);
        Assert.Equal(1L, (await (await session.RunAsync("MATCH (u:U) RETURN count(u) AS c")).SingleAsync())["c"].As<long>());
    }

    [Fact]
    public async Task Read_Access_Mode_Rejects_Writes()
    {
        await using var session = _driver.AsyncSession(o => o.WithDefaultAccessMode(N.AccessMode.Read));
        var ex = await Assert.ThrowsAnyAsync<N.Neo4jException>(async () => await (await session.RunAsync("CREATE (:X)")).ConsumeAsync());
        Assert.Equal("Neo.ClientError.Statement.AccessMode", ex.Code);
        await Assert.ThrowsAnyAsync<N.Neo4jException>(() => session.ExecuteReadAsync(async tx => await (await tx.RunAsync("CREATE (:X)")).ConsumeAsync()));
        Assert.Equal(0L, _db.ExecuteScalar<long>("MATCH (x:X) RETURN count(x)"));
    }

    [Fact]
    public async Task Large_Results_Are_Streamed_In_Batches()
    {
        await using var session = _driver.AsyncSession(o => o.WithFetchSize(100));
        var cursor = await session.RunAsync("UNWIND range(1, 2500) AS i RETURN i, 'row' + i AS s");
        long sum = 0;
        int n = 0;
        while (await cursor.FetchAsync())
        {
            sum += cursor.Current["i"].As<long>();
            n++;
        }
        Assert.Equal(2500, n);
        Assert.Equal(2500L * 2501 / 2, sum);
        var summary = await cursor.ConsumeAsync();
        Assert.Equal(N.QueryType.ReadOnly, summary.QueryType);
    }

    [Fact]
    public async Task Concurrent_Sessions()
    {
        await Run("UNWIND range(1, 100) AS i CREATE (:C {i: i})");
        var tasks = Enumerable.Range(0, 16).Select(async t =>
        {
            for (int i = 0; i < 20; i++)
            {
                if (i % 5 == 0) await Run("CREATE (:W {t: $t})", new { t });
                var rows = await Run("MATCH (c:C) WHERE c.i <= $n RETURN count(c) AS c", new { n = t + 1 });
                Assert.Equal(t + 1, rows[0]["c"].As<long>());
            }
        });
        await Task.WhenAll(tasks);
        Assert.Equal(16L * 4, _db.ExecuteScalar<long>("MATCH (w:W) RETURN count(w)"));
    }

    [Fact]
    public async Task Authentication_Is_Enforced_When_Configured()
    {
        using var server = new BoltServer(_db, new BoltServerOptions
        {
            Listen = new IPEndPoint(IPAddress.Loopback, 0),
            Username = "neo4j",
            Password = "secret",
        });
        server.Start();
        string uri = $"bolt://localhost:{server.LocalEndpoint.Port}";
        await using (var bad = N.GraphDatabase.Driver(uri, N.AuthTokens.Basic("neo4j", "wrong")))
            await Assert.ThrowsAsync<N.AuthenticationException>(() => bad.VerifyConnectivityAsync());
        await using var good = N.GraphDatabase.Driver(uri, N.AuthTokens.Basic("neo4j", "secret"));
        Assert.Equal(7L, (await Run("RETURN 7 AS x", driver: good))[0]["x"].As<long>());
    }

    /// <summary>Bolt 4.4 (older drivers): no element ids, local-time DateTime structures unless the "utc" patch is negotiated.</summary>
    [Fact]
    public void Bolt_4_4_Raw_Protocol()
    {
        using var client = new System.Net.Sockets.TcpClient();
        client.Connect(IPAddress.Loopback, _server.LocalEndpoint.Port);
        var s = client.GetStream();
        s.Write([0x60, 0x60, 0xB0, 0x17, 0, 0, 4, 4, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0]);
        var version = new byte[4];
        s.ReadExactly(version);
        Assert.Equal(new byte[] { 0, 0, 4, 4 }, version);

        var w = new PackWriter();
        void Send(byte tag, params Action[] fields)
        {
            w.Reset();
            w.StructHeader(fields.Length, tag);
            foreach (var f in fields) f();
            var data = w.Written.ToArray();
            s.Write([(byte)(data.Length >> 8), (byte)data.Length]);
            s.Write(data);
            s.Write([0, 0]);
        }
        PackStruct Receive()
        {
            var msg = new List<byte>();
            var h = new byte[2];
            while (true)
            {
                s.ReadExactly(h);
                int n = h[0] << 8 | h[1];
                if (n == 0) break;
                var chunk = new byte[n];
                s.ReadExactly(chunk);
                msg.AddRange(chunk);
            }
            return (PackStruct)new PackReader(msg.ToArray()).Read()!;
        }

        Send(0x01, () => w.Meta(new Dictionary<string, object?> { ["user_agent"] = "raw/1.0", ["scheme"] = "none" }));
        Assert.Equal(0x70, Receive().Tag);
        // legacy DateTime parameter: local seconds + offset
        Send(0x10,
            () => w.String("CREATE (n:Raw {d: $d}) RETURN n, n.d AS d, n.d.hour AS h"),
            () =>
            {
                w.MapHeader(1);
                w.String("d");
                w.StructHeader(3, 0x46);
                w.Int(new DateTimeOffset(2020, 1, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()); // 10:00 local
                w.Int(0);
                w.Int(7200);
            },
            () => w.MapHeader(0));
        Assert.Equal(0x70, Receive().Tag);
        Send(0x3F, () => w.Meta(new Dictionary<string, object?> { ["n"] = -1L }));
        var record = Receive();
        Assert.Equal(0x71, record.Tag);
        var fields = (List<object?>)record.Fields[0]!;
        var node = (PackStruct)fields[0]!;
        Assert.Equal(0x4E, node.Tag);
        Assert.Equal(3, node.Fields.Length); // no element id before Bolt 5
        var d = (PackStruct)fields[1]!;
        Assert.Equal(0x46, d.Tag);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), d.Fields[0]);
        Assert.Equal(7200L, d.Fields[2]);
        Assert.Equal(10L, fields[2]);
        var done = Receive();
        Assert.Equal(0x70, done.Tag);
        Assert.Equal("rw", ((Dictionary<string, object?>)done.Fields[0]!)["type"]);
        Send(0x02);
    }

    [Fact]
    public async Task Unknown_Database_Is_Rejected()
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase("other"));
        var ex = await Assert.ThrowsAnyAsync<N.Neo4jException>(async () => await (await session.RunAsync("RETURN 1")).ConsumeAsync());
        Assert.Equal("Neo.ClientError.Database.DatabaseNotFound", ex.Code);
        await using var named = _driver.AsyncSession(o => o.WithDatabase("neo4j"));
        Assert.Equal(1L, (await (await named.RunAsync("RETURN 1 AS x")).SingleAsync())["x"].As<long>());
    }

    // ------------------------------------------------------------------ Neo4j Browser

    /// <summary>The queries Neo4j Browser sends after connecting and while showing results.</summary>
    [Fact]
    public async Task Neo4j_Browser_Queries()
    {
        await Run("CREATE (a:Person {name: 'a'})-[:KNOWS]->(b:Person {name: 'b'})-[:KNOWS]->(c:City {name: 'c'})");
        async Task<List<N.IRecord>> Sys(string q, string db = "system")
        {
            await using var session = _driver.AsyncSession(o => o.WithDatabase(db).WithDefaultAccessMode(N.AccessMode.Read));
            return await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(q)).ToListAsync());
        }
        var components = (await Sys("CALL dbms.components() YIELD name, versions, edition")).Single();
        Assert.Equal("5.26.0", components["versions"].As<List<string>>()[0]);
        Assert.Equal("community", components["edition"].As<string>());

        var dbs = await Sys("SHOW DATABASES");
        Assert.Equal(["neo4j", "system"], dbs.Select(r => r["name"].As<string>()));
        Assert.True(dbs[0]["default"].As<bool>());
        Assert.Equal("online", dbs[0]["currentStatus"].As<string>());
        await Sys("SHOW DATABASES", "SYSTEM"); // the connection check uses upper case

        Assert.Single(await Sys("CALL dbms.showCurrentUser()"));
        Assert.Contains(await Sys("CALL dbms.clientConfig()"), r => r["name"].As<string>() == "dbms.security.auth_enabled");
        Assert.Contains(await Sys("SHOW PROCEDURES yield name, description, signature"), r => r["name"].As<string>() == "db.labels");
        await Sys("SHOW FUNCTIONS yield name, description, signature");

        var meta = await Run("""
            CALL db.labels() YIELD label
            RETURN {name:'labels', data:COLLECT(label)[..1000]} AS result
            UNION ALL
            CALL db.relationshipTypes() YIELD relationshipType
            RETURN {name:'relationshipTypes', data:COLLECT(relationshipType)[..1000]} AS result
            UNION ALL
            CALL db.propertyKeys() YIELD propertyKey
            RETURN {name:'propertyKeys', data:COLLECT(propertyKey)[..1000]} AS result
            """);
        Assert.Equal(3, meta.Count);
        Assert.Equal(["City", "Person"], meta[0]["result"].As<Dictionary<string, object>>()["data"].As<List<string>>().Order());
        var counts = await Run("""
            MATCH () RETURN { name:'nodes', data:count(*) } AS result
            UNION ALL
            MATCH ()-[]->() RETURN { name:'relationships', data: count(*)} AS result
            """);
        Assert.Equal(3L, counts[0]["result"].As<Dictionary<string, object>>()["data"].As<long>());
        Assert.Equal(2L, counts[1]["result"].As<Dictionary<string, object>>()["data"].As<long>());

        // graph view: relationships between the displayed nodes, then neighbour expansion
        var ids = (await Run("MATCH (n) RETURN id(n) AS id")).Select(r => r["id"].As<long>()).ToList();
        var rels = await Sys("MATCH (a)-[r]->(b) WHERE id(a) IN [" + string.Join(",", ids) + "] AND id(b) IN [" + string.Join(",", ids) + "] RETURN r;", "neo4j");
        Assert.Equal(2, rels.Count);
        await using (var session = _driver.AsyncSession(o => o.WithDefaultAccessMode(N.AccessMode.Read)))
        {
            var withParams = await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(
                "MATCH (a)-[r]->(b) WHERE id(a) IN $existingNodeIds AND id(b) IN $newNodeIds RETURN r;",
                new { existingNodeIds = ids, newNodeIds = ids })).ToListAsync());
            Assert.Equal(2, withParams.Count);
        }
        long b = (await Run("MATCH (n {name: 'b'}) RETURN id(n) AS id"))[0]["id"].As<long>();
        long a = (await Run("MATCH (n {name: 'a'}) RETURN id(n) AS id"))[0]["id"].As<long>();
        var expand = await Run($"""
            MATCH (a) WHERE id(a) = {b}
            WITH a, size([(a)--() | 1]) AS allNeighboursCount
            MATCH path = (a)--(o) WHERE NOT id(o) IN [{a}]
            RETURN path, allNeighboursCount
            ORDER BY id(o)
            LIMIT 100
            """);
        Assert.Single(expand);
        Assert.Equal(2L, expand[0]["allNeighboursCount"].As<long>());
    }

    [Fact]
    public async Task Http_Request_Gets_The_Discovery_Document()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"http://{Address}/");
        Assert.Contains($"\"bolt_direct\":\"bolt://{Address}\"", json);
        Assert.Contains("\"neo4j_version\":\"5.26.0\"", json);
    }

    // ------------------------------------------------------------------ WebSocket and TLS

    /// <summary>Minimal Bolt 5 client over any stream (raw TCP, TLS or WebSocket).</summary>
    private sealed class RawBolt(Stream s)
    {
        private readonly PackWriter _w = new();

        public void Handshake()
        {
            s.Write([0x60, 0x60, 0xB0, 0x17, 0, 4, 4, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            var version = new byte[4];
            s.ReadExactly(version);
            Assert.Equal(new byte[] { 0, 0, 4, 5 }, version);
        }

        public void Send(byte tag, params Action<PackWriter>[] fields)
        {
            _w.Reset();
            _w.StructHeader(fields.Length, tag);
            foreach (var f in fields) f(_w);
            var data = _w.Written.ToArray();
            var frame = new byte[data.Length + 4];
            frame[0] = (byte)(data.Length >> 8);
            frame[1] = (byte)data.Length;
            data.CopyTo(frame, 2);
            s.Write(frame);
        }

        public PackStruct Receive()
        {
            var msg = new List<byte>();
            var h = new byte[2];
            while (true)
            {
                s.ReadExactly(h);
                int n = h[0] << 8 | h[1];
                if (n == 0) break;
                var chunk = new byte[n];
                s.ReadExactly(chunk);
                msg.AddRange(chunk);
            }
            return (PackStruct)new PackReader(msg.ToArray()).Read()!;
        }

        /// <summary>HELLO, LOGON, then RUN + PULL of a query; returns the first column of each record.</summary>
        public List<object?> Query(string query)
        {
            Send(0x01, w => w.Meta(new Dictionary<string, object?> { ["user_agent"] = "raw/1.0" }));
            Assert.Equal(0x70, Receive().Tag);
            Send(0x6A, w => w.Meta(new Dictionary<string, object?> { ["scheme"] = "none" }));
            Assert.Equal(0x70, Receive().Tag);
            Send(0x10, w => w.String(query), w => w.MapHeader(0), w => w.MapHeader(0));
            Assert.Equal(0x70, Receive().Tag);
            Send(0x3F, w => w.Meta(new Dictionary<string, object?> { ["n"] = -1L }));
            var values = new List<object?>();
            while (true)
            {
                var m = Receive();
                if (m.Tag != 0x71)
                {
                    Assert.Equal(0x70, m.Tag);
                    return values;
                }
                values.Add(((List<object?>)m.Fields[0]!)[0]);
            }
        }
    }

    private static async Task<Stream> WebSocket(Uri uri, bool trustAll = false)
    {
        var ws = new System.Net.WebSockets.ClientWebSocket();
        if (trustAll) ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await ws.ConnectAsync(uri, CancellationToken.None);
        return new WebSocketByteStream(ws);
    }

    [Fact]
    public async Task Bolt_Over_WebSocket()
    {
        using var s = await WebSocket(new Uri($"ws://{Address}"));
        var bolt = new RawBolt(s);
        bolt.Handshake();
        Assert.Equal([1L, 2L, 3L], bolt.Query("UNWIND [1, 2, 3] AS x RETURN x"));
    }

    [Fact]
    public async Task Tls_For_Drivers_And_WebSockets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gdbtls_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cert = TlsCertificates.SelfSigned(Path.Combine(dir, "cert.pfx"));
            Assert.Equal(cert.Thumbprint, TlsCertificates.SelfSigned(Path.Combine(dir, "cert.pfx")).Thumbprint); // reused
            using var server = new BoltServer(_db, new BoltServerOptions { Listen = new IPEndPoint(IPAddress.Loopback, 0), Certificate = cert });
            server.Start();
            string address = $"localhost:{server.LocalEndpoint.Port}";
            foreach (var scheme in new[] { "bolt+ssc", "neo4j+ssc", "bolt" })
            {
                await using var driver = N.GraphDatabase.Driver($"{scheme}://{address}", N.AuthTokens.None);
                Assert.Equal(42L, (await Run("RETURN 42 AS x", driver: driver))[0]["x"].As<long>());
            }
            using (var s = await WebSocket(new Uri($"wss://{address}"), trustAll: true))
            {
                var bolt = new RawBolt(s);
                bolt.Handshake();
                Assert.Equal(["tls"], bolt.Query("RETURN 'tls'"));
            }
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var http = new HttpClient(handler);
            Assert.Contains("bolt+s://", await http.GetStringAsync($"https://{address}/"));

            using var strict = new BoltServer(_db, new BoltServerOptions
            {
                Listen = new IPEndPoint(IPAddress.Loopback, 0),
                Certificate = cert,
                RequireTls = true,
            });
            strict.Start();
            await using var plain = N.GraphDatabase.Driver($"bolt://localhost:{strict.LocalEndpoint.Port}", N.AuthTokens.None);
            await Assert.ThrowsAnyAsync<Exception>(() => plain.VerifyConnectivityAsync());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ streaming and concurrency

    [Fact]
    public async Task Huge_Result_Is_Streamed()
    {
        await using var session = _driver.AsyncSession(o => o.WithFetchSize(10_000));
        var cursor = await session.RunAsync("UNWIND range(1, 2000000) AS i RETURN i");
        long n = 0, sum = 0;
        while (await cursor.FetchAsync())
        {
            n++;
            sum += cursor.Current["i"].As<long>();
        }
        Assert.Equal(2000000, n);
        Assert.Equal(2000000L * 2000001 / 2, sum);
    }

    [Fact]
    public async Task Open_Write_Transactions_Do_Not_Block_Each_Other()
    {
        await using var s1 = _driver.AsyncSession();
        await using var s2 = _driver.AsyncSession();
        var t1 = await s1.BeginTransactionAsync();
        var t2 = await s2.BeginTransactionAsync(); // with exclusive writers this would wait for t1
        await (await t1.RunAsync("CREATE (:T {n: 1})")).ConsumeAsync();
        await (await t2.RunAsync("CREATE (:T {n: 2})")).ConsumeAsync();
        await t2.CommitAsync();
        await t1.CommitAsync();
        Assert.Equal(2L, _db.ExecuteScalar<long>("MATCH (t:T) RETURN count(t)"));
    }

    [Fact]
    public async Task Conflicting_Managed_Transactions_Are_Retried_By_The_Driver()
    {
        await Run("CREATE (:Counter {v: 0})");
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (int i = 0; i < 10; i++)
            {
                await using var session = _driver.AsyncSession();
                await session.ExecuteWriteAsync(async tx =>
                {
                    var v = (await (await tx.RunAsync("MATCH (c:Counter) RETURN c.v AS v")).SingleAsync())["v"].As<long>();
                    await (await tx.RunAsync("MATCH (c:Counter) SET c.v = $v", new { v = v + 1 })).ConsumeAsync();
                });
            }
        });
        await Task.WhenAll(tasks);
        Assert.Equal(80L, _db.ExecuteScalar<long>("MATCH (c:Counter) RETURN c.v"));
    }
}
