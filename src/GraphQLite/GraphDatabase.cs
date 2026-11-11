using System.Collections.Concurrent;
using GraphQLite.Cypher;
using GraphQLite.Storage;

namespace GraphQLite;

/// <summary>Durability of commits (same meaning as SQLite's PRAGMA synchronous in WAL mode).</summary>
public enum SyncMode
{
    /// <summary>Same as <see cref="Normal"/> (the WAL is written straight to the OS): kept for compatibility.</summary>
    Off,
    /// <summary>The WAL is written to the OS on commit: survives process crashes; a power loss may lose recent commits
    /// but never corrupts the database (data files are only written after the WAL is fsync'ed).</summary>
    Normal,
    /// <summary>A commit returns once the WAL is fsync'ed: committed transactions survive power loss. Commits that
    /// arrive together share one fsync (group commit), and other transactions see a commit only once it is durable.</summary>
    Full,
}

public sealed class DatabaseOptions
{
    public SyncMode Sync { get; init; } = SyncMode.Normal;

    /// <summary>Committed pages kept in memory before they are checkpointed into the data files (bytes).</summary>
    public long CheckpointThresholdBytes { get; init; } = 256L << 20;

    /// <summary>Maximum number of cached query plans.</summary>
    public int PlanCacheSize { get; init; } = 1024;

    /// <summary>If set, LOAD CSV file paths are resolved inside this directory and cannot escape it.</summary>
    public string? CsvImportDirectory { get; init; }

    /// <summary>Allows LOAD CSV FROM 'http(s)://...'.</summary>
    public bool AllowRemoteCsv { get; init; }

    /// <summary>Maximum threads used by one read-only query (parallel aggregation over large scans); 1 disables it.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
}

/// <summary>
/// Embedded graph database queried with openCypher. Runs in-process, like SQLite. The graph lives in a
/// directory of memory-mapped files and can be much larger than RAM.
/// <para>Concurrency: any number of concurrent readers, each seeing a consistent snapshot; one exclusive writer
/// at a time, or any number of concurrent (optimistic) writers, see <see cref="BeginConcurrentTransaction"/>. Readers are never blocked by writers (multi-version pages); only the short checkpoint phase
/// needs exclusive access.</para>
/// </summary>
public sealed class GraphDatabase : IDisposable
{
    internal readonly GraphStore Store;
    internal readonly RwLock Lock = new();
    internal readonly SemaphoreSlim WriterGate = new(1, 1);
    internal readonly DatabaseOptions Options;
    private readonly ConcurrentDictionary<string, CachedStatement> _cache = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Database directory, or null for an in-memory database.</summary>
    public string? Path { get; }
    public bool IsInMemory => Path is null;

    private GraphDatabase(string? path, DatabaseOptions options)
    {
        Path = path is null ? null : System.IO.Path.GetFullPath(path);
        Options = options;
        Store = GraphStore.Open(Path, options.Sync);
    }

    /// <summary>Opens (or creates) a database directory. Use ":memory:" for a transient in-memory database.</summary>
    public static GraphDatabase Open(string path, DatabaseOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new GraphDatabase(path == ":memory:" ? null : path, options ?? new DatabaseOptions());
    }

    public static GraphDatabase OpenInMemory(DatabaseOptions? options = null) => new(null, options ?? new DatabaseOptions());

    public long NodeCount
    {
        get
        {
            using var tx = BeginReadTransaction();
            return tx.Store.NodeCount;
        }
    }

    public long RelationshipCount
    {
        get
        {
            using var tx = BeginReadTransaction();
            return tx.Store.RelCount;
        }
    }

    // ------------------------------------------------------------------ transactions

    /// <summary>Begins a read-write transaction. Writers are serialized; readers are not blocked.</summary>
    public Transaction BeginTransaction(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        WriterGate.Wait(cancellationToken);
        try
        {
            return new Transaction(this, Store.BeginWrite(), autoCommit: false);
        }
        catch
        {
            WriterGate.Release();
            throw;
        }
    }

    /// <summary>
    /// Begins a concurrent (optimistic) write transaction: it does not wait for other writers and does not block them.
    /// It works on a snapshot; at commit, if other transactions committed in the meantime, its statements are
    /// replayed on the latest state and it commits only if they return the same results, otherwise
    /// <see cref="Transaction.Commit"/> throws <see cref="TransactionConflictException"/> and the transaction must be
    /// retried. Suited to interactive transactions (e.g. a client that keeps a transaction open between requests);
    /// for throughput an exclusive <see cref="BeginTransaction"/> is cheaper, since it never replays.
    /// </summary>
    public Transaction BeginConcurrentTransaction()
    {
        ThrowIfDisposed();
        Lock.EnterRead(); // keeps the snapshot's page versions alive until commit
        try
        {
            return new Transaction(this, Store.BeginConcurrentWrite(), autoCommit: false, concurrent: true);
        }
        catch
        {
            Lock.ExitRead();
            throw;
        }
    }

    /// <summary>Begins a read-only transaction on a consistent snapshot. Many can run concurrently.</summary>
    public Transaction BeginReadTransaction()
    {
        ThrowIfDisposed();
        Lock.EnterRead();
        try
        {
            return new Transaction(this, Store.BeginRead(), autoCommit: false);
        }
        catch
        {
            Lock.ExitRead();
            throw;
        }
    }

    /// <summary>Executes a query in its own transaction (read-only queries run concurrently with everything).</summary>
    public QueryResult Execute(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        var cached = GetStatement(query);
        Transaction tx;
        if (cached.IsWrite)
        {
            ThrowIfDisposed();
            WriterGate.Wait(cancellationToken);
            try { tx = new Transaction(this, Store.BeginWrite(), autoCommit: true); }
            catch { WriterGate.Release(); throw; }
        }
        else tx = BeginReadTransaction();
        using (tx)
        {
            var result = tx.Execute(query, parameters, cancellationToken);
            tx.Commit();
            return result;
        }
    }

    /// <summary>
    /// Executes a query in its own transaction and streams the rows (constant memory for results of any size). The
    /// transaction commits when the cursor is exhausted or disposed; an updating query holds the writer gate until
    /// then, so read the cursor promptly.
    /// </summary>
    public QueryCursor Stream(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        var cached = GetStatement(query);
        Transaction tx;
        if (cached.IsWrite)
        {
            ThrowIfDisposed();
            WriterGate.Wait(cancellationToken);
            try { tx = new Transaction(this, Store.BeginWrite(), autoCommit: true); }
            catch { WriterGate.Release(); throw; }
        }
        else tx = BeginReadTransaction();
        try
        {
            return tx.Open(query, parameters, cancellationToken, ownsTransaction: true, null);
        }
        catch
        {
            tx.Dispose();
            throw;
        }
    }

    /// <summary>Executes a query and returns the first column of the first row.</summary>
    public T? ExecuteScalar<T>(string query, IReadOnlyDictionary<string, object?>? parameters = null) => Execute(query, parameters).Scalar<T>();

    /// <summary>Copies committed pages into the data files and truncates the WAL.</summary>
    public void Checkpoint()
    {
        ThrowIfDisposed();
        WriterGate.Wait();
        try
        {
            CheckpointCore(force: true);
        }
        finally
        {
            WriterGate.Release();
        }
    }

    /// <summary>Caller holds the writer gate and has no uncommitted pages.</summary>
    internal void CheckpointCore(bool force)
    {
        var pager = Store.Pager;
        if (pager.OverlayBytes == 0) return;
        if (force) Lock.EnterWrite();
        else
        {
            // regular checkpoints only wait briefly for active readers; they are retried at the next commit
            bool urgent = pager.OverlayBytes > 8 * Options.CheckpointThresholdBytes;
            if (urgent) Lock.EnterWrite();
            else if (!Lock.TryEnterWrite(25)) return;
        }
        try
        {
            pager.MergeOverlay();
        }
        finally
        {
            Lock.ExitWrite();
        }
        pager.FlushAndTruncateWal(); // readers may run again; writers are still excluded by the gate
    }

    internal void AfterCommit()
    {
        if (Store.Pager.OverlayBytes > Options.CheckpointThresholdBytes) CheckpointCore(force: false);
    }

    // ------------------------------------------------------------------ plan cache

    internal sealed class CachedStatement(string text, Statement ast, bool isWrite)
    {
        public readonly string Text = text;
        public readonly Statement Ast = ast;
        public readonly bool IsWrite = isWrite;
        /// <summary>Plan together with the catalog it was made for (replaced atomically).</summary>
        public volatile Tuple<PlannedQuery, long>? Entry;
        public bool AstConsumed;
    }

    internal CachedStatement GetStatement(string query)
    {
        if (_cache.TryGetValue(query, out var c)) return c;
        var ast = Parser.Parse(query);
        c = new CachedStatement(query, ast, IsUpdating(ast));
        if (_cache.Count >= Options.PlanCacheSize) _cache.Clear();
        return _cache.GetOrAdd(query, c);
    }

    /// <summary>Returns a plan valid for the schema seen by the transaction.</summary>
    internal PlannedQuery GetPlan(CachedStatement c, StoreTx tx)
    {
        if (tx.SchemaChanged)
            return Planner.Plan(tx, (QueryStatement)Parser.Parse(c.Text)); // uncommitted schema: never cached
        long catalog = tx.CatalogAddress;
        var e = c.Entry;
        if (e != null && e.Item2 == catalog) return e.Item1;
        lock (c)
        {
            e = c.Entry;
            if (e != null && e.Item2 == catalog) return e.Item1;
            // planning annotates the AST, so plan from a fresh parse after the first time
            var ast = c.AstConsumed ? Parser.Parse(c.Text) : c.Ast;
            c.AstConsumed = true;
            var plan = Planner.Plan(tx, (QueryStatement)ast);
            c.Entry = Tuple.Create(plan, catalog);
            return plan;
        }
    }

    private static bool IsUpdating(Statement st)
    {
        if (st is not QueryStatement q) return true;
        return q.Parts.Any(p => p.Any(IsUpdating));

        static bool IsUpdating(Clause c) => c switch
        {
            CreateClause or MergeClause or SetClause or RemoveClause or DeleteClause or ForeachClause => true,
            SubqueryClause s => s.InTransactions || s.Query.Parts.Any(p => p.Any(IsUpdating)),
            _ => false,
        };
    }

    /// <summary>Test hook: releases the files without checkpointing, as if the process had crashed.</summary>
    internal void SimulateCrash()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Store.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        WriterGate.Wait();
        Lock.EnterWrite();
        try
        {
            try
            {
                Store.Pager.MergeOverlay();
                Store.Pager.FlushAndTruncateWal();
            }
            catch
            {
                // the WAL stays authoritative: recovery will replay it
            }
            Store.Dispose();
        }
        finally
        {
            Lock.ExitWrite();
            WriterGate.Release();
        }
    }
}
