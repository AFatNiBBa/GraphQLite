using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace GraphDb.Server.Bolt;

/// <summary>
/// Detects what a client speaks on the Bolt port, like Neo4j does: TLS (optional or required), then either raw Bolt
/// or HTTP. An HTTP <c>GET</c> with <c>Upgrade: websocket</c> carries Bolt over WebSocket binary frames (Neo4j Browser
/// and the JavaScript driver in browsers); any other HTTP request gets the discovery document of Neo4j's HTTP port,
/// which also lets a user open <c>https://host:port</c> to accept a self-signed certificate.
/// </summary>
internal static class Transport
{
    private const uint BoltMagic = 0x6060B017;

    public enum Kind { Bolt, WebSocket, Closed }

    /// <summary>
    /// Negotiates the transport. On success <paramref name="stream"/> carries Bolt bytes positioned right after the
    /// 4-byte magic preamble.
    /// </summary>
    public static Kind Negotiate(Socket socket, ref Stream stream, BoltServerOptions options, out bool encrypted)
    {
        encrypted = false;
        Span<byte> first = stackalloc byte[1];
        if (socket.Receive(first, SocketFlags.Peek) == 0) return Kind.Closed;
        if (first[0] == 0x16) // TLS handshake record
        {
            if (options.Certificate is null) return Kind.Closed;
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            ssl.AuthenticateAsServer(new SslServerAuthenticationOptions
            {
                ServerCertificate = options.Certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None, // system default (TLS 1.2 / 1.3)
            });
            stream = ssl;
            encrypted = true;
        }
        else if (options.Certificate != null && options.RequireTls) return Kind.Closed;

        Span<byte> pre = stackalloc byte[4];
        if (!ReadExactly(stream, pre)) return Kind.Closed;
        if (BinaryPrimitives.ReadUInt32BigEndian(pre) == BoltMagic) return Kind.Bolt;
        if (!pre.SequenceEqual("GET "u8)) return Kind.Closed;

        var headers = ReadHttpHeaders(stream);
        if (headers is null) return Kind.Closed;
        if (headers.TryGetValue("upgrade", out var up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase) &&
            headers.TryGetValue("sec-websocket-key", out var key))
        {
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            stream.Write(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n"));
            stream.Flush();
            var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
            stream = new WebSocketByteStream(ws);
            if (!ReadExactly(stream, pre) || BinaryPrimitives.ReadUInt32BigEndian(pre) != BoltMagic) return Kind.Closed;
            return Kind.WebSocket;
        }
        WriteDiscovery(stream, headers.GetValueOrDefault("host"), socket, options, encrypted);
        return Kind.Closed;
    }

    private static bool ReadExactly(Stream s, Span<byte> dst)
    {
        while (dst.Length > 0)
        {
            int n = s.Read(dst);
            if (n == 0) return false;
            dst = dst[n..];
        }
        return true;
    }

    /// <summary>Reads the rest of the request line and the headers (lower-case names); null if malformed.</summary>
    private static Dictionary<string, string>? ReadHttpHeaders(Stream s)
    {
        var sb = new StringBuilder();
        Span<byte> b = stackalloc byte[1];
        while (true)
        {
            if (s.Read(b) == 0) return null;
            sb.Append((char)b[0]);
            if (sb.Length > 16384) return null;
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
        }
        var lines = sb.ToString().Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim().ToLowerInvariant()] = line[(c + 1)..].Trim();
        }
        return headers;
    }

    /// <summary>Same document as Neo4j's HTTP root: tells tools which Bolt URIs to use.</summary>
    private static void WriteDiscovery(Stream s, string? host, Socket socket, BoltServerOptions options, bool encrypted)
    {
        string address = options.AdvertisedAddress ?? host ?? socket.LocalEndPoint?.ToString() ?? "localhost:7687";
        string suffix = encrypted ? "+s" : "";
        string version = options.ServerAgent.Split('/').Last();
        var body = Encoding.UTF8.GetBytes(
            $"{{\"bolt_routing\":\"neo4j{suffix}://{address}\",\"bolt_direct\":\"bolt{suffix}://{address}\"," +
            $"\"neo4j_version\":\"{version}\",\"neo4j_edition\":\"community\"}}");
        s.Write(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        s.Write(body);
        s.Flush();
    }
}

/// <summary>Byte stream over a WebSocket: each write is one binary message, reads concatenate messages.</summary>
internal sealed class WebSocketByteStream(WebSocket ws) : Stream
{
    private bool _closed;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_closed) return 0;
        var tmp = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            while (true)
            {
                var r = ws.ReceiveAsync(tmp.AsMemory(0, buffer.Length), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    _closed = true;
                    try { ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                    return 0;
                }
                if (r.Count == 0) continue;
                tmp.AsSpan(0, r.Count).CopyTo(buffer);
                return r.Count;
            }
        }
        catch (WebSocketException ex)
        {
            throw new IOException("WebSocket error", ex);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            ws.SendAsync(buffer.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (WebSocketException ex)
        {
            throw new IOException("WebSocket error", ex);
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_closed && ws.State == WebSocketState.Open)
            {
                try
                {
                    using var cts = new CancellationTokenSource(1000);
                    ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).GetAwaiter().GetResult();
                }
                catch { }
            }
            ws.Dispose();
        }
        base.Dispose(disposing);
    }
}
