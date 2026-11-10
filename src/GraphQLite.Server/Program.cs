using System.Net;
using System.Runtime.InteropServices;
using GraphQLite;
using GraphQLite.Server.Bolt;

// Bolt server for a GraphQLite database directory: Neo4j drivers connect with bolt:// or neo4j:// URIs,
// Neo4j Browser through WebSockets on the same port. Run without arguments for the options.
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        GraphQLite.Server <database-directory | :memory:> [options]
          --host <address>        address to listen on (default 127.0.0.1; 0.0.0.0 for every interface)
          --port <port>           Bolt port (default 7687)
          --user <name>           require basic authentication with this user...
          --password <password>   ...and this password (default: no authentication)
          --database <name>       database name exposed to clients (default neo4j)
          --advertised <host:port> address returned in routing tables for neo4j:// URIs
          --sync off|normal|full  commit durability (default normal)
          --tls-cert <file>       enable TLS (bolt+s://, neo4j+s://, wss://) with this certificate:
                                  .pfx/.p12, or PEM (with --tls-key, or key in the same file)
          --tls-key <file>        PEM private key of --tls-cert
          --tls-password <pwd>    password of the .pfx or of the encrypted PEM key
          --tls-self-signed       enable TLS with a self-signed certificate, created on first use and
                                  stored as tls-selfsigned.pfx in the database directory
          --tls-required          refuse unencrypted connections (default: both on the same port)
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

bool selfSigned = args.Contains("--tls-self-signed");
if (selfSigned && Opt("--tls-cert") != null)
{
    Console.Error.WriteLine("--tls-cert and --tls-self-signed are alternatives");
    return 1;
}

using var db = GraphDatabase.Open(path, new DatabaseOptions { Sync = sync });
System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null;
if (Opt("--tls-cert") is { } certPath) certificate = TlsCertificates.Load(certPath, Opt("--tls-key"), Opt("--tls-password"));
else if (selfSigned)
{
    var file = System.IO.Path.Combine(db.Path ?? Environment.CurrentDirectory, "tls-selfsigned.pfx");
    certificate = TlsCertificates.SelfSigned(file, host.Equals(IPAddress.Any) || IPAddress.IsLoopback(host) ? null : [host.ToString()]);
    Console.WriteLine($"Self-signed certificate: {file} (thumbprint {certificate.Thumbprint})");
}
var server = new BoltServer(db, new BoltServerOptions
{
    Listen = new IPEndPoint(host, port),
    Username = Opt("--user"),
    Password = Opt("--password"),
    DatabaseName = Opt("--database") ?? "neo4j",
    AdvertisedAddress = Opt("--advertised"),
    Certificate = certificate,
    RequireTls = args.Contains("--tls-required"),
    Log = msg => Console.Error.WriteLine(msg),
});
server.Start();
Console.WriteLine($"GraphQLite {(db.IsInMemory ? "in-memory database" : db.Path)}");
var ep = server.LocalEndpoint;
string hostName = IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any) ? "localhost" : ep.Address.ToString();
string where = $"{hostName}:{ep.Port}";
Console.WriteLine($"Bolt listening on {ep}");
if (certificate is null || !args.Contains("--tls-required")) Console.WriteLine($"  drivers:        bolt://{where}  neo4j://{where}");
if (certificate != null)
{
    Console.WriteLine($"  drivers (TLS):  bolt+s://{where}  neo4j+s://{where}{(selfSigned ? "  (self-signed: bolt+ssc:// / neo4j+ssc://)" : "")}");
    Console.WriteLine($"  Neo4j Browser:  https://browser.neo4j.io/?connectURL=neo4j%2Bs://{where}");
    if (selfSigned) Console.WriteLine($"                  first open https://{where} and accept the certificate");
}
else Console.WriteLine($"  Neo4j Browser:  needs TLS when opened from https://browser.neo4j.io (use --tls-self-signed)");
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
