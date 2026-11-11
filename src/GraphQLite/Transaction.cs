using System.Text;
using GraphQLite.Cypher;
using GraphQLite.Runtime;
using GraphQLite.Storage;

namespace GraphQLite;

/// <summary>
/// A database transaction. Read-only transactions see an immutable snapshot taken when they start;
/// a write transaction sees its own changes and is invisible to others until it commits.
/// A failing statement is rolled back on its own: the transaction stays usable.
/// Instances are not thread-safe; use one transaction per thread.
/// <para>Write transactions are either <b>exclusive</b> (<see cref="GraphDatabase.BeginTransaction"/>: writers run one
/// at a time and never conflict) or <b>concurrent</b> (<see cref="GraphDatabase.BeginConcurrentTransaction"/>: they
/// run in parallel on a snapshot and may fail at commit with a <see cref="TransactionConflictException"/>).</para>
/// </summary>
public sealed class Transaction : IDisposable
{
    private readonly GraphDatabase _db;
    internal readonly StoreTx Store;
    private readonly bool _autoCommit;
    private readonly bool _concurrent;
    private bool _finished;
    private QueryCursor? _open;
    /// <summary>Statements of a concurrent transaction, replayed if others committed in the meantime.</summary>
    private readonly List<StatementLog>? _log;
    /// <summary>
    /// Exclusive writers read the newest commits, which with SyncMode.Full may not be durable yet (group commit):
    /// before showing a row to the caller they wait for the commits they read (up to this sequence) to be durable.
    /// 0 when there is nothing to wait for.
    /// </summary>
    private long _exposeAfter;

    public bool IsReadOnly => !Store.IsWriter;

    /// <summary>True for a concurrent (optimistic) write transaction.</summary>
    public bool IsConcurrent => _concurrent;

    internal Transaction(GraphDatabase db, StoreTx store, bool autoCommit, bool concurrent = false)
    {
        _db = db;
        Store = store;
        _autoCommit = autoCommit;
        _concurrent = concurrent;
        if (concurrent) _log = new List<StatementLog>();
        else if (store.IsWriter) _exposeAfter = db.Store.Pager.CommittedSeq; // under the writer gate
    }

    /// <summary>Called before a row reaches the caller (see <see cref="_exposeAfter"/>).</summary>
    internal void BeforeExposingRows()
    {
        if (_exposeAfter == 0) return;
        _db.Store.Pager.WaitDurable(_exposeAfter);
        _exposeAfter = 0;
    }

    /// <summary>Executes a query and returns all its rows.</summary>
    public QueryResult Execute(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        using var cursor = Open(query, parameters, cancellationToken, ownsTransaction: false, null);
        // an auto-commit query hands its rows over only after its own (durable) commit: no need to wait before
        cursor.ExposesRows = !_autoCommit;
        var rows = new List<Value[]>();
        while (cursor.NextValues() is { } r) rows.Add(r);
        return new QueryResult(cursor.ColumnSet, rows, cursor.Statistics);
    }

    /// <summary>
    /// Executes a query and streams its rows: they are produced while the cursor is read, so a result of any size uses
    /// constant memory. Dispose the cursor when done.
    /// </summary>
    public QueryCursor Stream(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
        Open(query, parameters, cancellationToken, ownsTransaction: false, null);

    internal QueryCursor Open(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct, bool ownsTransaction, StatementLog? replay)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _open?.Detach();
        var cached = _db.GetStatement(query);
        if (cached.IsWrite && IsReadOnly)
            throw new GraphQLiteException("Cannot execute an updating query in a read-only transaction");
        var log = replay;
        if (log is null && _log != null)
        {
            log = new StatementLog(query, parameters is null ? null : new Dictionary<string, object?>(parameters),
                System.DateTime.UtcNow, System.Random.Shared.Next());
            _log.Add(log);
        }
        Store.BeginStatement();
        try
        {
            QueryCursor cursor;
            switch (cached.Ast)
            {
                case CreateIndexStatement ci:
                    cursor = new QueryCursor(this, CreateIndex(ci), ownsTransaction, log);
                    Store.EndStatement();
                    return cursor;
                case DropIndexStatement di:
                    cursor = new QueryCursor(this, DropIndex(di), ownsTransaction, log);
                    Store.EndStatement();
                    return cursor;
            }
            var plan = _db.GetPlan(cached, Store);
            if (plan.Explain)
            {
                var sb = new StringBuilder();
                plan.Root.Explain(sb, 0);
                cursor = new QueryCursor(this, new QueryResult(["plan"], [[Value.FromString(sb.ToString().TrimEnd())]], QueryStatistics.Empty), ownsTransaction, log);
                Store.EndStatement();
                return cursor;
            }
            var pvals = new Value[plan.ParamNames.Length];
            for (int i = 0; i < pvals.Length; i++)
            {
                var name = plan.ParamNames[i];
                if (parameters is null || !parameters.TryGetValue(name, out var o))
                    throw new CypherSemanticException($"Expected parameter: ${name}");
                pvals[i] = Value.FromObject(o);
            }
            var ctx = new ExecContext(Store, plan.SlotCount, pvals, _db.Options, ct);
            if (log != null)
            {
                // a statement that may be replayed fixes its clock and random seed up front
                ctx.Now = log.Now;
                ctx.Seed = log.Seed;
            }
            if (_autoCommit && Store.IsWriter) ctx.CommitBatch = CommitBatch;
            var c = plan.Root.Open(ctx);
            cursor = new QueryCursor(this, new ColumnSet(plan.Columns), cached.IsWrite, ownsTransaction, log, ctx, c, plan.ColumnSlots);
            _open = cursor;
            return cursor;
        }
        catch
        {
            Store.RollbackStatement();
            if (log != null) log.Failed = true;
            throw;
        }
    }

    /// <summary>The statement of <paramref name="cursor"/> ended.</summary>
    internal void CursorClosed(QueryCursor cursor)
    {
        if (ReferenceEquals(_open, cursor)) _open = null;
    }

    /// <summary>CALL { } IN TRANSACTIONS: commits the work so far and keeps going.</summary>
    private void CommitBatch()
    {
        Store.Commit();
        // durable right away (under the gate): batches are large anyway, and if the query fails later its earlier
        // batches must not stay committed but invisible
        _db.Store.Pager.WaitDurable(_db.Store.Pager.CommittedSeq);
        _db.AfterCommit();
        Store.BeginStatement();
    }

    private QueryResult CreateIndex(CreateIndexStatement ci)
    {
        int label = Store.Labels.GetOrCreate(ci.Label);
        int key = Store.PropKeys.GetOrCreate(ci.Property);
        var catalog = Store.Catalog;
        var existing = catalog.Find(label, key);
        string name = ci.Name ?? (ci.Unique ? $"constraint_{ci.Label}_{ci.Property}" : $"index_{ci.Label}_{ci.Property}");
        if (existing != null || catalog.ByName(name) != null)
        {
            if (ci.IfNotExists) return new QueryResult([], [], QueryStatistics.Empty);
            if (existing != null && existing.Unique != ci.Unique)
                throw new GraphQLiteException($"An index already exists on :{ci.Label}({ci.Property}); drop it first");
        }
        Store.CreateIndex(name, label, key, ci.Unique);
        return new QueryResult([], [], QueryStatistics.Empty with { IndexesAdded = 1 });
    }

    private QueryResult DropIndex(DropIndexStatement di)
    {
        if (!Store.DropIndex(di.Name))
        {
            if (di.IfExists) return new QueryResult([], [], QueryStatistics.Empty);
            throw new GraphQLiteException($"No index or constraint named '{di.Name}'");
        }
        return new QueryResult([], [], QueryStatistics.Empty with { IndexesRemoved = 1 });
    }

    /// <summary>
    /// Commits. A concurrent transaction is published as is when nobody committed since it started; otherwise its
    /// statements are replayed on the latest state and it commits only if every statement returns what it returned
    /// the first time, else it throws <see cref="TransactionConflictException"/> (retry the whole transaction).
    /// </summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var open = _open;
        _open = null;
        open?.Dispose();
        _finished = true;
        if (IsReadOnly)
        {
            _db.Lock.ExitRead();
            return;
        }
        if (_concurrent)
        {
            CommitConcurrent();
            return;
        }
        long seq = -1;
        try
        {
            Store.Commit();
            seq = _db.Store.Pager.CommittedSeq;
            _db.AfterCommit();
        }
        catch
        {
            if (seq < 0) Store.Abort();
            throw;
        }
        finally
        {
            _db.WriterGate.Release();
            // group commit: wait for the fsync outside the gate, so the next writers can join it
            if (seq >= 0) _db.Store.Pager.WaitDurable(seq);
        }
    }

    private void CommitConcurrent()
    {
        // the snapshot is no longer needed: under the gate nobody else commits, and private pages are copies
        _db.Lock.ExitRead();
        if (Store.W!.DirtyCount == 0)
        {
            Store.Abort(); // only reads: serializable at its snapshot
            return;
        }
        _db.WriterGate.Wait();
        long seq = -1;
        try
        {
            if (_db.Store.Pager.CommittedSeq == Store.Snapshot)
            {
                try
                {
                    Store.Commit();
                }
                catch
                {
                    Store.Abort();
                    throw;
                }
            }
            else
            {
                Store.Abort();
                Replay();
            }
            seq = _db.Store.Pager.CommittedSeq;
            _db.AfterCommit();
        }
        finally
        {
            _db.WriterGate.Release();
            if (seq >= 0) _db.Store.Pager.WaitDurable(seq);
        }
    }

    /// <summary>Re-executes the statements on the latest state (caller holds the writer gate).</summary>
    private void Replay()
    {
        var store = _db.Store.BeginWrite();
        store.ReplayNodes = new Queue<long>(Store.ReservedNodes!);
        store.ReplayRels = new Queue<long>(Store.ReservedRels!);
        var tx = new Transaction(_db, store, autoCommit: false) { _exposeAfter = 0 }; // its rows reach nobody
        try
        {
            foreach (var original in _log!)
            {
                var log = new StatementLog(original.Query, original.Parameters, original.Now, original.Seed);
                try
                {
                    var cursor = tx.Open(original.Query, original.Parameters, CancellationToken.None, ownsTransaction: false, log);
                    if (original.Completed)
                    {
                        while (cursor.NextValues() != null) { }
                    }
                    else
                    {
                        for (long i = 0; i < original.Rows && cursor.NextValues() != null; i++) { }
                    }
                    cursor.Dispose();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    log.Failed = true;
                }
                if (!original.SameOutcome(log))
                    throw new TransactionConflictException(
                        "The transaction conflicts with a concurrent one: some statement returned a different result on the latest state. Retry it.");
            }
            store.Commit();
        }
        catch
        {
            store.Abort();
            throw;
        }
    }

    public void Rollback()
    {
        if (_finished) return;
        _finished = true;
        _open?.StopBackground();
        _open = null;
        if (IsReadOnly)
        {
            _db.Lock.ExitRead();
            return;
        }
        if (_concurrent)
        {
            Store.Abort();
            _db.Lock.ExitRead();
            return;
        }
        try
        {
            Store.Abort();
        }
        finally
        {
            _db.WriterGate.Release();
        }
    }

    /// <summary>Rolls back if neither Commit nor Rollback was called.</summary>
    public void Dispose() => Rollback();
}
