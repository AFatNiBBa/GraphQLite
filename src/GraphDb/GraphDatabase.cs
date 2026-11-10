using System.Collections.Concurrent;
using GraphDb.Cypher;
using GraphDb.Runtime;
using GraphDb.Storage;

namespace GraphDb;

/// <summary>Durability of commits (same meaning as SQLite's PRAGMA synchronous in WAL mode).</summary>
public enum SyncMode
{
    /// <summary>The WAL is not flushed on commit: fastest, data may be lost if the process crashes.</summary>
    Off,
    /// <summary>The WAL is flushed to the OS on commit: survives process crashes, not power loss.</summary>
    Normal,
    /// <summary>The WAL is fsync'ed on every commit: survives power loss.</summary>
    Full,
}

public sealed class DatabaseOptions
{
    public SyncMode Sync { get; init; } = SyncMode.Normal;
    /// <summary>Automatic checkpoint when the WAL grows beyond this size (bytes). 0 disables.</summary>
    public long AutoCheckpointBytes { get; init; } = 64L << 20;
    /// <summary>Maximum number of cached query plans.</summary>
    public int PlanCacheSize { get; init; } = 1024;
    /// <summary>Checkpoint when the database is disposed.</summary>
    public bool CheckpointOnClose { get; init; } = true;
}

/// <summary>
/// Embedded graph database queried with openCypher. Runs in-process, like SQLite.
/// <para>Concurrency: any number of concurrent read transactions; one write transaction at a time
/// (writers block readers only while their transaction is open).</para>
/// </summary>
public sealed class GraphDatabase : IDisposable
{
    internal readonly GraphStore Store = new();
    internal readonly RwLock Lock = new();
    internal readonly SemaphoreSlim WriterGate = new(1, 1);
    private readonly DurableStorage? _storage;
    private readonly DatabaseOptions _options;
    private readonly ConcurrentDictionary<string, CachedStatement> _cache = new(StringComparer.Ordinal);
    private int _disposed;

    public string? Path { get; }
    public bool IsInMemory => _storage is null;

    private GraphDatabase(string? path, DatabaseOptions options)
    {
        Path = path;
        _options = options;
        if (path != null) _storage = DurableStorage.Open(path, Store, options.Sync);
    }

    /// <summary>Opens (or creates) a database file. Use ":memory:" for a transient in-memory database.</summary>
    public static GraphDatabase Open(string path, DatabaseOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new GraphDatabase(path == ":memory:" ? null : path, options ?? new DatabaseOptions());
    }

    public static GraphDatabase OpenInMemory(DatabaseOptions? options = null) => new(null, options ?? new DatabaseOptions());

    public long NodeCount => Store.NodeCount;
    public long RelationshipCount => Store.RelCount;

    // ------------------------------------------------------------------ transactions

    /// <summary>Begins a read-write transaction (exclusive among writers; blocks readers until it ends).</summary>
    public Transaction BeginTransaction(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        WriterGate.Wait(cancellationToken);
        try
        {
            Lock.EnterWrite();
        }
        catch
        {
            WriterGate.Release();
            throw;
        }
        return new Transaction(this, readOnly: false);
    }

    /// <summary>Begins a read-only transaction. Many read transactions can run concurrently.</summary>
    public Transaction BeginReadTransaction()
    {
        ThrowIfDisposed();
        Lock.EnterRead();
        return new Transaction(this, readOnly: true);
    }

    /// <summary>Executes a query in its own transaction (read-only queries run concurrently).</summary>
    public QueryResult Execute(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        var cached = GetStatement(query);
        using var tx = cached.IsWrite ? BeginTransaction(cancellationToken) : BeginReadTransaction();
        var result = tx.Execute(query, parameters, cancellationToken);
        tx.Commit();
        return result;
    }

    /// <summary>Executes a query and returns the first column of the first row.</summary>
    public T? ExecuteScalar<T>(string query, IReadOnlyDictionary<string, object?>? parameters = null) => Execute(query, parameters).Scalar<T>();

    /// <summary>Writes a full snapshot and truncates the WAL. Readers keep running meanwhile.</summary>
    public void Checkpoint()
    {
        ThrowIfDisposed();
        if (_storage is null) return;
        WriterGate.Wait();
        try
        {
            _storage.Checkpoint(Store);
        }
        finally
        {
            WriterGate.Release();
        }
    }

    // ------------------------------------------------------------------ internals used by Transaction

    internal sealed class CachedStatement(string text, Statement ast, bool isWrite)
    {
        public readonly string Text = text;
        public readonly Statement Ast = ast;
        public readonly bool IsWrite = isWrite;
        public PlannedQuery? Plan;
        public long SchemaVersion = -1;
        public bool AstConsumed;
    }

    internal CachedStatement GetStatement(string query)
    {
        if (_cache.TryGetValue(query, out var c)) return c;
        var ast = Parser.Parse(query);
        c = new CachedStatement(query, ast, IsUpdating(ast));
        if (_cache.Count >= _options.PlanCacheSize) _cache.Clear();
        return _cache.GetOrAdd(query, c);
    }

    /// <summary>Called with the lock held.</summary>
    internal PlannedQuery GetPlan(CachedStatement c)
    {
        var plan = c.Plan;
        long version = Store.SchemaVersion;
        if (plan != null && Volatile.Read(ref c.SchemaVersion) == version) return plan;
        lock (c)
        {
            if (c.Plan != null && c.SchemaVersion == version) return c.Plan;
            // planning annotates the AST, so always plan from a fresh parse
            var ast = c.AstConsumed ? Parser.Parse(c.Text) : c.Ast;
            c.AstConsumed = true;
            plan = Planner.Plan(Store, (QueryStatement)ast);
            c.Plan = plan;
            Volatile.Write(ref c.SchemaVersion, version);
            return plan;
        }
    }

    private static bool IsUpdating(Statement st)
    {
        if (st is not QueryStatement q) return true;
        foreach (var part in q.Parts)
            foreach (var c in part)
                if (c is CreateClause or MergeClause or SetClause or RemoveClause or DeleteClause or ForeachClause) return true;
        return false;
    }

    internal TxLog NewLog() => new(durable: _storage != null);

    internal void CommitWrite(TxLog log)
    {
        // caller holds WriterGate + write lock
        if (_storage != null && log.HasChanges)
        {
            long seq = Store.LastTxSeq + 1;
            try
            {
                _storage.Append(Store, log, seq);
            }
            catch
            {
                Store.Rollback(log);
                throw;
            }
            Store.LastTxSeq = seq;
        }
    }

    internal void AfterWriteReleased()
    {
        // caller still holds WriterGate (no writers), readers may run concurrently
        if (_storage != null && _options.AutoCheckpointBytes > 0 && _storage.WalSize > _options.AutoCheckpointBytes)
        {
            try { _storage.Checkpoint(Store); }
            catch { /* checkpoint is an optimisation; WAL remains authoritative */ }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        WriterGate.Wait();
        try
        {
            if (_storage != null)
            {
                if (_options.CheckpointOnClose && _storage.WalSize > 0)
                {
                    try { _storage.Checkpoint(Store); } catch { }
                }
                _storage.Dispose();
            }
        }
        finally
        {
            WriterGate.Release();
        }
    }
}
