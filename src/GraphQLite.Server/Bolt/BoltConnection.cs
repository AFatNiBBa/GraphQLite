using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;

namespace GraphQLite.Server.Bolt;

/// <summary>A protocol-level error reported to the client with a Neo4j status code.</summary>
internal sealed class BoltClientException(string code, string message) : Exception(message)
{
    public readonly string Code = code;
}

/// <summary>
/// One Bolt connection, served by a dedicated thread: a write transaction may wait on the writer gate, which
/// would starve the thread pool.
/// Implements Bolt 4.4 and 5.0–5.4 over TCP, TLS and WebSocket: handshake, chunking, HELLO/LOGON/LOGOFF,
/// RUN/PULL/DISCARD with streamed results, explicit transactions, RESET, ROUTE (so that <c>neo4j://</c> URIs work)
/// and TELEMETRY.
/// </summary>
internal sealed class BoltConnection : IDisposable
{
    private enum Msg : byte
    {
        Hello = 0x01, Goodbye = 0x02, Reset = 0x0F, Run = 0x10, Begin = 0x11, Commit = 0x12, Rollback = 0x13,
        Discard = 0x2F, Pull = 0x3F, Telemetry = 0x54, Route = 0x66, Logon = 0x6A, Logoff = 0x6B,
    }

    private const byte Success = 0x70, Record = 0x71, Ignored = 0x7E, Failure = 0x7F;

    /// <summary>Supported versions, best first: (major, minor).</summary>
    private static readonly (int Major, int Minor)[] Supported = [(5, 4), (5, 3), (5, 2), (5, 1), (5, 0), (4, 4)];

    private readonly Socket _socket;
    private Stream _stream;
    private readonly BoltServer _server;
    private readonly string _id;
    private readonly PackWriter _w = new();
    private byte[] _out = new byte[16384];
    private int _outLen;
    private readonly byte[] _in = new byte[16384];
    private int _inPos, _inLen;
    private byte[] _msg = new byte[4096];

    private int _major, _minor;
    private bool _utc;
    private bool _hello, _authenticated, _failed;
    private string? _user;

    private Transaction? _tx;
    private string _txDb = "";
    private readonly List<ResultStream> _results = new();
    private ResultStream? _auto;
    private long _nextQid;

    /// <summary>An open result: a streamed query, or rows computed by the server (system queries).</summary>
    private sealed class ResultStream(QueryCursor? cursor, Queue<Value[]>? rows, long qid, long tFirst, string db)
    {
        public readonly QueryCursor? Cursor = cursor;
        public readonly Queue<Value[]>? Rows = rows;
        public readonly long Qid = qid;
        public readonly long TFirst = tFirst;
        public readonly string Db = db;

        public Value[]? Next() => Cursor != null ? Cursor.NextValues() : Rows!.TryDequeue(out var r) ? r : null;
        public QueryStatistics Statistics => Cursor?.Statistics ?? QueryStatistics.Empty;
        public void Dispose() => Cursor?.Dispose();
    }

    public BoltConnection(Socket socket, BoltServer server, string id)
    {
        _socket = socket;
        _socket.NoDelay = true;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _server = server;
        _id = id;
    }

    private bool V5 => _major >= 5;
    private GraphDatabase Db => _server.Database;

    public void Run()
    {
        try
        {
            if (Transport.Negotiate(_socket, ref _stream, _server.Options, out _) == Transport.Kind.Closed) return;
            if (!Handshake()) return;
            while (ReadMessage(out int length))
            {
                if (!Handle(_msg.AsSpan(0, length))) break;
                if (_inPos == _inLen && _socket.Available == 0) Flush();
            }
            Flush();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or BoltProtocolException
            or System.Security.Authentication.AuthenticationException)
        {
            // client went away or spoke garbage: just close
        }
        catch (Exception ex)
        {
            _server.Options.Log?.Invoke($"{_id}: {ex}");
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Called by the connection thread when it ends: rolls back an open transaction.</summary>
    public void Dispose()
    {
        try { DropResults(); } catch { }
        try { _tx?.Dispose(); } catch { }
        _tx = null;
        try { _stream.Dispose(); } catch { }
        _socket.Dispose();
        _server.Remove(this);
    }

    /// <summary>Closes the socket from another thread; the connection thread then ends and cleans up.</summary>
    public void Close()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket.Close(); } catch { }
    }

    // ================================================================== framing

    /// <summary>Version negotiation (the 4-byte magic preamble was consumed by <see cref="Transport"/>).</summary>
    private bool Handshake()
    {
        Span<byte> hs = stackalloc byte[16];
        ReadExactly(hs);
        for (int i = 0; i < 4; i++)
        {
            var p = hs.Slice(4 * i, 4);
            int major = p[3], minor = p[2], range = p[1];
            foreach (var (sMajor, sMinor) in Supported)
            {
                if (sMajor != major || sMinor > minor || sMinor < minor - range) continue;
                _major = sMajor;
                _minor = sMinor;
                // a single write: over WebSocket the reply must arrive as one message
                _stream.Write([0, 0, (byte)sMinor, (byte)sMajor]);
                _stream.Flush();
                return true;
            }
        }
        _stream.Write([0, 0, 0, 0]);
        return false;
    }

    private void Fill()
    {
        _inPos = 0;
        _inLen = _stream.Read(_in);
        if (_inLen == 0) throw new IOException("Connection closed");
    }

    private void ReadExactly(Span<byte> dst)
    {
        while (dst.Length > 0)
        {
            if (_inPos == _inLen) Fill();
            int n = Math.Min(dst.Length, _inLen - _inPos);
            _in.AsSpan(_inPos, n).CopyTo(dst);
            _inPos += n;
            dst = dst[n..];
        }
    }

    /// <summary>Reads one chunked message into <see cref="_msg"/>; false when the client closed the connection cleanly.</summary>
    private bool ReadMessage(out int length)
    {
        length = 0;
        Span<byte> header = stackalloc byte[2];
        while (true)
        {
            if (_inPos == _inLen)
            {
                if (length == 0)
                {
                    Flush();
                    _inPos = 0;
                    _inLen = _stream.Read(_in);
                    if (_inLen == 0) return false;
                }
                else Fill();
            }
            ReadExactly(header);
            int size = BinaryPrimitives.ReadUInt16BigEndian(header);
            if (size == 0)
            {
                if (length == 0) continue; // NOOP (keep-alive)
                return true;
            }
            if (length + size > _msg.Length) Array.Resize(ref _msg, Math.Max(_msg.Length * 2, length + size));
            ReadExactly(_msg.AsSpan(length, size));
            length += size;
            if (length > 256 << 20) throw new BoltProtocolException("Message too large");
        }
    }

    /// <summary>Appends the message currently in <see cref="_w"/> to the output buffer, chunked.</summary>
    private void Send()
    {
        var data = _w.Written;
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, ushort.MaxValue);
            var dst = OutReserve(n + 2);
            BinaryPrimitives.WriteUInt16BigEndian(dst, (ushort)n);
            data[..n].CopyTo(dst[2..]);
            data = data[n..];
        }
        OutReserve(2).Clear();
        _w.Reset();
        if (_outLen > 1 << 16) Flush();
    }

    private Span<byte> OutReserve(int n)
    {
        if (_outLen + n > _out.Length)
        {
            Flush();
            if (n > _out.Length) Array.Resize(ref _out, n);
        }
        var s = _out.AsSpan(_outLen, n);
        _outLen += n;
        return s;
    }

    private void Flush()
    {
        if (_outLen == 0) return;
        _stream.Write(_out, 0, _outLen);
        _stream.Flush();
        _outLen = 0;
    }

    // ================================================================== responses

    private void SendSuccess(IReadOnlyCollection<KeyValuePair<string, object?>>? meta = null)
    {
        _w.StructHeader(1, Success);
        _w.Meta(meta ?? []);
        Send();
    }

    private void SendFailure(string code, string message)
    {
        _w.Reset(); // drop a message interrupted by the error
        _w.StructHeader(1, Failure);
        _w.Meta(new Dictionary<string, object?> { ["code"] = code, ["message"] = message });
        Send();
        _failed = true;
    }

    private void SendIgnored()
    {
        _w.StructHeader(0, Ignored);
        Send();
    }

    // ================================================================== messages

    /// <summary>Handles one request; false to close the connection.</summary>
    private bool Handle(ReadOnlySpan<byte> message)
    {
        var r = new PackReader(message);
        var (count, tag) = r.StructHeader();
        var fields = new object?[count];
        for (int i = 0; i < count; i++) fields[i] = r.Read();
        var msg = (Msg)tag;

        if (msg == Msg.Goodbye) return false;
        if (msg == Msg.Reset)
        {
            ResetState();
            SendSuccess();
            return true;
        }
        if (_failed)
        {
            SendIgnored();
            return true;
        }
        try
        {
            if (!_hello)
            {
                if (msg != Msg.Hello) throw new BoltProtocolException("Expected HELLO");
                return Hello(Map(fields, 0));
            }
            if (msg == Msg.Logon)
            {
                if (_authenticated || _major < 5 || (_major == 5 && _minor == 0)) throw new BoltProtocolException("Unexpected LOGON");
                return Logon(Map(fields, 0));
            }
            if (!_authenticated) throw new BoltProtocolException("Not authenticated");
            switch (msg)
            {
                case Msg.Logoff:
                    if (_tx != null || _auto != null) throw new BoltProtocolException("LOGOFF inside a transaction");
                    _authenticated = false;
                    SendSuccess();
                    return true;
                case Msg.Run:
                    Run(fields[0] as string ?? throw new BoltProtocolException("RUN without query"), Map(fields, 1), Map(fields, 2));
                    return true;
                case Msg.Pull:
                    Pull(Map(fields, 0), discard: false);
                    return true;
                case Msg.Discard:
                    Pull(Map(fields, 0), discard: true);
                    return true;
                case Msg.Begin:
                    Begin(Map(fields, 0));
                    return true;
                case Msg.Commit:
                    Commit();
                    return true;
                case Msg.Rollback:
                    Rollback();
                    return true;
                case Msg.Route:
                    Route(Map(fields, 0), fields.Length > 2 ? fields[2] : null);
                    return true;
                case Msg.Telemetry:
                    SendSuccess();
                    return true;
            }
            throw new BoltProtocolException($"Unknown message 0x{tag:X2}");
        }
        catch (BoltClientException ex)
        {
            FailAutoCommit();
            SendFailure(ex.Code, ex.Message);
            return !ex.Code.StartsWith("Neo.ClientError.Security.", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is GraphQLiteException or OperationCanceledException or ArgumentException or InvalidCastException
            or InvalidOperationException or OverflowException or FormatException or KeyNotFoundException)
        {
            FailAutoCommit();
            var (code, text) = ErrorCode(ex);
            SendFailure(code, text);
            return true;
        }
    }

    /// <summary>An auto-commit query failed: its transaction is rolled back (the cursor already did it, this is a safety net).</summary>
    private void FailAutoCommit()
    {
        var auto = _auto;
        _auto = null;
        try { auto?.Dispose(); } catch { }
    }

    private static Dictionary<string, object?> Map(object?[] fields, int i) =>
        i < fields.Length && fields[i] is Dictionary<string, object?> m ? m : new Dictionary<string, object?>();

    private void DropResults()
    {
        var auto = _auto;
        _auto = null;
        auto?.Dispose();
        // results of an explicit transaction end with it
        _results.Clear();
    }

    private void ResetState()
    {
        _failed = false;
        try { DropResults(); } catch { }
        if (_tx != null)
        {
            try { _tx.Dispose(); } catch { }
            _tx = null;
        }
    }

    private bool Hello(Dictionary<string, object?> extra)
    {
        _hello = true;
        if (_major == 4 && extra.TryGetValue("patch_bolt", out var patches) && patches is List<object?> pl && pl.Contains("utc")) _utc = true;
        if (_major >= 5) _utc = true;
        var meta = new Dictionary<string, object?>
        {
            ["server"] = _server.Options.ServerAgent,
            ["connection_id"] = _id,
            ["hints"] = new Dictionary<string, object?>(),
        };
        if (_major == 4 && _utc) meta["patch_bolt"] = new List<object?> { "utc" };
        if (_major == 4 || _minor == 0)
        {
            // auth travels in HELLO up to Bolt 5.0
            if (!Authenticate(extra)) return false;
        }
        SendSuccess(meta);
        return true;
    }

    private bool Logon(Dictionary<string, object?> auth)
    {
        if (!Authenticate(auth)) return false;
        SendSuccess();
        return true;
    }

    private bool Authenticate(Dictionary<string, object?> auth)
    {
        var o = _server.Options;
        _user = auth.GetValueOrDefault("principal") as string;
        if (o.Username is null)
        {
            _authenticated = true;
            return true;
        }
        bool ok = auth.GetValueOrDefault("scheme") as string == "basic" &&
            auth.GetValueOrDefault("principal") as string == o.Username &&
            auth.GetValueOrDefault("credentials") as string == o.Password;
        if (!ok)
        {
            SendFailure("Neo.ClientError.Security.Unauthorized", "The client is unauthorized due to authentication failure.");
            Flush();
            return false;
        }
        _authenticated = true;
        return true;
    }

    /// <summary>Validates the requested database: the served one (default) or <c>system</c>. Returns its name.</summary>
    private string CheckDatabase(string? db)
    {
        var name = _server.Options.DatabaseName;
        if (string.IsNullOrEmpty(db) || string.Equals(db, name, StringComparison.OrdinalIgnoreCase)) return name;
        if (string.Equals(db, "system", StringComparison.OrdinalIgnoreCase)) return "system";
        throw new BoltClientException("Neo.ClientError.Database.DatabaseNotFound", $"Database does not exist. Database name: '{db}'.");
    }

    private static Dictionary<string, object?> Parameters(Dictionary<string, object?> raw)
    {
        var p = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
        foreach (var (k, v) in raw) p[k] = BoltValues.ToValue(v);
        return p;
    }

    private string Address => _server.Options.AdvertisedAddress ?? _socket.LocalEndPoint?.ToString() ?? "localhost:7687";

    private void Run(string query, Dictionary<string, object?> rawParams, Dictionary<string, object?> extra)
    {
        if (_auto != null || (_tx is null && _results.Count > 0)) throw new BoltProtocolException("RUN while a result is still open");
        long start = Stopwatch.GetTimestamp();
        string db = _tx != null ? _txDb : CheckDatabase(extra.GetValueOrDefault("db") as string);
        ResultStream rs;
        if (SystemQueries.TryAnswer(query, _server, _user, Address) is { } sys)
        {
            rs = new ResultStream(null, new Queue<Value[]>(sys.Rows), _tx != null ? _nextQid++ : -1, 0, db);
            Started(rs, sys.Columns);
            return;
        }
        var parameters = Parameters(rawParams);
        if (_tx != null)
        {
            var cursor = _tx.Stream(query, parameters);
            rs = new ResultStream(cursor, null, _nextQid++, Elapsed(start), db);
            Started(rs, cursor.Columns);
            return;
        }
        if (extra.GetValueOrDefault("mode") as string == "r" && Db.GetStatement(query).IsWrite)
            throw new BoltClientException("Neo.ClientError.Statement.AccessMode", "Writing in read access mode not allowed.");
        var auto = Db.Stream(query, parameters);
        rs = new ResultStream(auto, null, -1, Elapsed(start), db);
        Started(rs, auto.Columns);
    }

    private static long Elapsed(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    private void Started(ResultStream rs, IReadOnlyList<string> columns)
    {
        var meta = new Dictionary<string, object?> { ["fields"] = columns.ToList<object?>(), ["t_first"] = rs.TFirst };
        if (_tx != null)
        {
            _results.Add(rs);
            meta["qid"] = rs.Qid;
        }
        else _auto = rs;
        SendSuccess(meta);
    }

    private void Pull(Dictionary<string, object?> extra, bool discard)
    {
        long n = extra.GetValueOrDefault("n") is long ln ? ln : -1;
        long qid = extra.GetValueOrDefault("qid") is long lq ? lq : -1;
        ResultStream rs;
        if (_tx is null) rs = _auto ?? throw new BoltProtocolException("PULL without a result");
        else
        {
            int i = qid == -1 ? _results.Count - 1 : _results.FindIndex(x => x.Qid == qid);
            if (i < 0) throw new BoltProtocolException("PULL without a result");
            rs = _results[i];
        }
        bool more = false;
        if (discard)
        {
            rs.Dispose(); // stops a read query; an updating query still runs to the end
        }
        else
        {
            for (long sent = 0; n < 0 || sent < n; sent++)
            {
                var values = rs.Next();
                if (values is null) break;
                _w.StructHeader(1, Record);
                _w.ListHeader(values.Length);
                foreach (var v in values) BoltValues.Write(_w, v, V5, _utc);
                Send(); // flushes to the socket every 64 KB: rows are streamed, not accumulated
                if (n >= 0 && sent + 1 == n) more = !PeekEnd(rs);
            }
        }
        if (more)
        {
            SendSuccess(new Dictionary<string, object?> { ["has_more"] = true });
            return;
        }
        rs.Dispose();
        var stats = rs.Statistics;
        var meta = new Dictionary<string, object?>
        {
            ["type"] = stats.IndexesAdded + stats.IndexesRemoved > 0 ? "s" : stats.ContainsUpdates ? "rw" : "r",
            ["t_last"] = 0L,
            ["db"] = rs.Db,
        };
        if (stats.ContainsUpdates) meta["stats"] = Stats(stats);
        if (_tx is null)
        {
            meta["bookmark"] = _server.Bookmark();
            _auto = null;
        }
        else _results.Remove(rs);
        SendSuccess(meta);
    }

    /// <summary>True when the result has no more rows (the cursor reports completion once the last row was produced).</summary>
    private static bool PeekEnd(ResultStream rs) =>
        rs.Cursor is { } c ? c.IsCompleted && !c.HasBufferedRows : rs.Rows!.Count == 0;

    private static Dictionary<string, object?> Stats(QueryStatistics s)
    {
        var d = new Dictionary<string, object?>();
        void Add(string key, int v)
        {
            if (v != 0) d[key] = (long)v;
        }
        Add("nodes-created", s.NodesCreated);
        Add("nodes-deleted", s.NodesDeleted);
        Add("relationships-created", s.RelationshipsCreated);
        Add("relationships-deleted", s.RelationshipsDeleted);
        Add("properties-set", s.PropertiesSet);
        Add("labels-added", s.LabelsAdded);
        Add("labels-removed", s.LabelsRemoved);
        Add("indexes-added", s.IndexesAdded);
        Add("indexes-removed", s.IndexesRemoved);
        d["contains-updates"] = true;
        return d;
    }

    /// <summary>
    /// Explicit transaction. Write transactions are concurrent: clients do not wait for each other, and a transaction
    /// that conflicts with one committed in the meantime fails at COMMIT with a transient error that drivers retry.
    /// </summary>
    private void Begin(Dictionary<string, object?> extra)
    {
        if (_tx != null || _auto != null) throw new BoltProtocolException("BEGIN inside a transaction");
        _txDb = CheckDatabase(extra.GetValueOrDefault("db") as string);
        _tx = extra.GetValueOrDefault("mode") as string == "r" ? Db.BeginReadTransaction() : Db.BeginConcurrentTransaction();
        _results.Clear();
        _nextQid = 0;
        SendSuccess();
    }

    private void Commit()
    {
        var tx = _tx ?? throw new BoltProtocolException("COMMIT without a transaction");
        _tx = null;
        _results.Clear();
        tx.Commit();
        SendSuccess(new Dictionary<string, object?> { ["bookmark"] = _server.Bookmark() });
    }

    private void Rollback()
    {
        var tx = _tx ?? throw new BoltProtocolException("ROLLBACK without a transaction");
        _tx = null;
        _results.Clear();
        tx.Rollback();
        SendSuccess();
    }

    /// <summary>Routing table for <c>neo4j://</c> drivers: this single server has every role.</summary>
    private void Route(Dictionary<string, object?> routing, object? extraOrDb)
    {
        string? db = extraOrDb switch
        {
            Dictionary<string, object?> m => m.GetValueOrDefault("db") as string,
            string s => s,
            _ => null,
        };
        db = CheckDatabase(db);
        string address = _server.Options.AdvertisedAddress
            ?? routing.GetValueOrDefault("address") as string
            ?? Address;
        var addresses = new List<object?> { address };
        var servers = new List<object?>
        {
            new Dictionary<string, object?> { ["addresses"] = addresses, ["role"] = "WRITE" },
            new Dictionary<string, object?> { ["addresses"] = addresses, ["role"] = "READ" },
            new Dictionary<string, object?> { ["addresses"] = addresses, ["role"] = "ROUTE" },
        };
        SendSuccess(new Dictionary<string, object?>
        {
            ["rt"] = new Dictionary<string, object?> { ["ttl"] = 300L, ["db"] = db, ["servers"] = servers },
        });
    }

    /// <summary>Maps an exception to a Neo4j status code (drivers retry TransientErrors and report ClientErrors).</summary>
    private static (string Code, string Message) ErrorCode(Exception ex) => ex switch
    {
        CypherSyntaxException => ("Neo.ClientError.Statement.SyntaxError", ex.Message),
        CypherSemanticException when ex.Message.StartsWith("Expected parameter", StringComparison.Ordinal) =>
            ("Neo.ClientError.Statement.ParameterMissing", ex.Message),
        CypherSemanticException when ex.Message.Contains("procedure", StringComparison.OrdinalIgnoreCase) =>
            ("Neo.ClientError.Procedure.ProcedureNotFound", ex.Message),
        CypherSemanticException => ("Neo.ClientError.Statement.SemanticError", ex.Message),
        CypherRuntimeException => ("Neo.ClientError.Statement.ArgumentError", ex.Message),
        ConstraintViolationException => ("Neo.ClientError.Schema.ConstraintValidationFailed", ex.Message),
        TransactionConflictException => ("Neo.TransientError.Transaction.Outdated", ex.Message),
        GraphQLiteException when ex.Message.Contains("read-only transaction", StringComparison.Ordinal) =>
            ("Neo.ClientError.Statement.AccessMode", ex.Message),
        GraphQLiteException => ("Neo.ClientError.Statement.ExecutionFailed", ex.Message),
        OperationCanceledException => ("Neo.TransientError.Transaction.Terminated", ex.Message),
        _ => ("Neo.ClientError.Statement.ArgumentError", ex.Message),
    };
}
