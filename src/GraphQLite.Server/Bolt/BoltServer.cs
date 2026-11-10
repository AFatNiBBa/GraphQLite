using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace GraphQLite.Server.Bolt;

public sealed class BoltServerOptions
{
    /// <summary>Address and port to listen on (Bolt default: 7687).</summary>
    public IPEndPoint Listen { get; init; } = new(IPAddress.Loopback, 7687);

    /// <summary>Required user for basic authentication; null accepts any credentials.</summary>
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>Name of the (single) database served; clients may omit it or use this name.</summary>
    public string DatabaseName { get; init; } = "neo4j";

    /// <summary>Address returned in routing tables (<c>neo4j://</c>); defaults to the address the client connected to.</summary>
    public string? AdvertisedAddress { get; init; }

    /// <summary>
    /// Agent reported in HELLO. Drivers read it as "product/version" and some decide features from the version,
    /// so the default declares the Neo4j version whose Bolt behaviour is implemented.
    /// </summary>
    public string ServerAgent { get; init; } = "Neo4j/5.26.0";

    /// <summary>
    /// Certificate (with private key) for TLS: clients may then use <c>bolt+s://</c>, <c>neo4j+s://</c> and secure
    /// WebSockets. TLS is detected from the first byte, so the same port also accepts plain connections unless
    /// <see cref="RequireTls"/> is set. See <see cref="TlsCertificates"/>.
    /// </summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>Rejects unencrypted connections (only with a <see cref="Certificate"/>).</summary>
    public bool RequireTls { get; init; }

    /// <summary>Receives unexpected connection errors.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Serves a <see cref="GraphDatabase"/> over the Bolt protocol, so that Neo4j drivers and tools can use it
/// through <c>bolt://</c> and <c>neo4j://</c> URIs (and their <c>+s</c> variants with TLS). The same port accepts
/// raw Bolt, Bolt over WebSocket (Neo4j Browser) and TLS. One thread per connection.
/// </summary>
public sealed class BoltServer : IDisposable
{
    private readonly List<TcpListener> _listeners = new();
    private readonly List<Thread> _acceptThreads = new();
    private readonly HashSet<BoltConnection> _connections = new();
    private long _nextId;
    private volatile bool _stopped;

    public GraphDatabase Database { get; }
    public BoltServerOptions Options { get; }

    /// <summary>Actual listening endpoint (useful with port 0).</summary>
    public IPEndPoint LocalEndpoint => (IPEndPoint)_listeners[0].LocalEndpoint;

    public BoltServer(GraphDatabase database, BoltServerOptions? options = null)
    {
        Database = database;
        Options = options ?? new BoltServerOptions();
    }

    /// <summary>
    /// Starts listening. "localhost" usually resolves to ::1 first, and clients wait for that attempt to fail
    /// before trying 127.0.0.1: the IPv4 loopback is therefore paired with the IPv6 one, and "any" listens dual-mode.
    /// </summary>
    public void Start()
    {
        var ep = Options.Listen;
        if (ep.Address.Equals(IPAddress.Any) && Socket.OSSupportsIPv6)
        {
            var l = new TcpListener(IPAddress.IPv6Any, ep.Port);
            l.Server.DualMode = true;
            Listen(l);
        }
        else
        {
            Listen(new TcpListener(ep));
            if (ep.Address.Equals(IPAddress.Loopback) && Socket.OSSupportsIPv6)
            {
                try { Listen(new TcpListener(IPAddress.IPv6Loopback, LocalEndpoint.Port)); }
                catch (SocketException) { } // IPv6 disabled or port taken: IPv4 only
            }
        }
    }

    private void Listen(TcpListener listener)
    {
        listener.Start(512);
        _listeners.Add(listener);
        var t = new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "bolt-accept" };
        _acceptThreads.Add(t);
        t.Start();
    }

    private void AcceptLoop(TcpListener listener)
    {
        while (!_stopped)
        {
            Socket socket;
            try
            {
                socket = listener.AcceptSocket();
            }
            catch (Exception) when (_stopped)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            var conn = new BoltConnection(socket, this, "bolt-" + Interlocked.Increment(ref _nextId));
            lock (_connections) _connections.Add(conn);
            new Thread(conn.Run) { IsBackground = true, Name = "bolt-connection" }.Start();
        }
    }

    internal void Remove(BoltConnection c)
    {
        lock (_connections) _connections.Remove(c);
    }

    /// <summary>Opaque bookmark returned after commits (a single instance is always causally consistent).</summary>
    internal string Bookmark() => "GB:" + Database.Store.Pager.CommittedSeq;

    /// <summary>Stops listening and closes every connection (open transactions are rolled back).</summary>
    public void Dispose()
    {
        _stopped = true;
        foreach (var l in _listeners) l.Stop();
        BoltConnection[] open;
        lock (_connections) open = _connections.ToArray();
        foreach (var c in open) c.Close();
        foreach (var t in _acceptThreads) t.Join(1000);
    }
}
