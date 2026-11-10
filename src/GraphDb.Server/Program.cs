using System.Net;
using System.Runtime.InteropServices;
using GraphDb;
using GraphDb.Server.Bolt;

// Bolt server for a GraphDb database directory: Neo4j drivers connect with bolt:// or neo4j:// URIs.
// Usage: GraphDb.Server <database-directory | :memory:> [--host 127.0.0.1] [--port 7687]
//        [--user neo4j --password secret] [--database neo4j] [--advertised host:port] [--sync off|normal|full]
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        GraphDb.Server <database-directory | :memory:> [options]
          --host <address>        address to listen on (default 127.0.0.1; 0.0.0.0 for every interface)
          --port <port>           Bolt port (default 7687)
          --user <name>           require basic authentication with this user...
          --password <password>   ...and this password (default: no authentication)
          --database <name>       database name exposed to clients (default neo4j)
          --advertised <host:port> address returned in routing tables for neo4j:// URIs
          --sync off|normal|full  commit durability (default normal)
        """);
    return args.Length == 0 ? 1 : 0;
}

string path = args[0];
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var host = IPAddress.Parse(Opt("--host") ?? "127.0.0.1");
int port = int.Parse(Opt("--port") ?? "7687");
var sync = (Opt("--sync") ?? "normal").ToLowerInvariant() switch
{
    "off" => SyncMode.Off,
    "full" => SyncMode.Full,
    _ => SyncMode.Normal,
};
if ((Opt("--user") is null) != (Opt("--password") is null))
{
    Console.Error.WriteLine("--user and --password must be used together");
    return 1;
}

using var db = GraphDatabase.Open(path, new DatabaseOptions { Sync = sync });
var server = new BoltServer(db, new BoltServerOptions
{
    Listen = new IPEndPoint(host, port),
    Username = Opt("--user"),
    Password = Opt("--password"),
    DatabaseName = Opt("--database") ?? "neo4j",
    AdvertisedAddress = Opt("--advertised"),
    Log = msg => Console.Error.WriteLine(msg),
});
server.Start();
Console.WriteLine($"GraphDb {(db.IsInMemory ? "in-memory database" : db.Path)}");
Console.WriteLine($"Bolt listening on {server.LocalEndpoint} (bolt://{server.LocalEndpoint}, neo4j://{server.LocalEndpoint})");
Console.WriteLine("Press Ctrl+C to stop.");

using var stop = new ManualResetEventSlim();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    stop.Set();
});
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Set();
};
stop.Wait();
Console.WriteLine("Stopping...");
server.Dispose();
return 0; // the database is checkpointed and closed by its Dispose
