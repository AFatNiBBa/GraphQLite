using System.Text;
using GraphDb.Cypher;
using GraphDb.Runtime;
using GraphDb.Storage;

namespace GraphDb;

/// <summary>
/// A database transaction. Read-only transactions see an immutable snapshot taken when they start;
/// a write transaction sees its own changes and is invisible to others until it commits.
/// A failing statement is rolled back on its own: the transaction stays usable.
/// Instances are not thread-safe; use one transaction per thread.
/// </summary>
public sealed class Transaction : IDisposable
{
    private readonly GraphDatabase _db;
    internal readonly StoreTx Store;
    private readonly bool _autoCommit;
    private bool _finished;

    public bool IsReadOnly => !Store.IsWriter;

    internal Transaction(GraphDatabase db, StoreTx store, bool autoCommit)
    {
        _db = db;
        Store = store;
        _autoCommit = autoCommit;
    }

    public QueryResult Execute(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var cached = _db.GetStatement(query);
        if (cached.IsWrite && IsReadOnly)
            throw new GraphDbException("Cannot execute an updating query in a read-only transaction");
        Store.BeginStatement();
        try
        {
            var result = cached.Ast switch
            {
                CreateIndexStatement ci => CreateIndex(ci),
                DropIndexStatement di => DropIndex(di),
                _ => Run(_db.GetPlan(cached, Store), parameters, cancellationToken),
            };
            Store.EndStatement();
            return result;
        }
        catch
        {
            Store.RollbackStatement();
            throw;
        }
    }

    private QueryResult Run(PlannedQuery plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        if (plan.Explain)
        {
            var sb = new StringBuilder();
            plan.Root.Explain(sb, 0);
            return new QueryResult(["plan"], [[Value.FromString(sb.ToString().TrimEnd())]], QueryStatistics.Empty);
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
        if (_autoCommit && Store.IsWriter) ctx.CommitBatch = CommitBatch;
        var cursor = plan.Root.Open(ctx);
        var rows = new List<Value[]>();
        var slots = plan.ColumnSlots;
        var mat = new QueryResult.Materializer(Store);
        var row = ctx.Row;
        while (cursor.Next())
        {
            if (slots.Length == 0) continue;
            var r = new Value[slots.Length];
            for (int i = 0; i < slots.Length; i++) r[i] = mat.Convert(row[slots[i]]);
            rows.Add(r);
        }
        ctx.CheckPendingDeletes();
        return new QueryResult(plan.Columns, rows, ctx.Stats.ToStats());
    }

    /// <summary>CALL { } IN TRANSACTIONS: commits the work so far and keeps going.</summary>
    private void CommitBatch()
    {
        Store.Commit();
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
                throw new GraphDbException($"An index already exists on :{ci.Label}({ci.Property}); drop it first");
        }
        Store.CreateIndex(name, label, key, ci.Unique);
        return new QueryResult([], [], QueryStatistics.Empty with { IndexesAdded = 1 });
    }

    private QueryResult DropIndex(DropIndexStatement di)
    {
        if (!Store.DropIndex(di.Name))
        {
            if (di.IfExists) return new QueryResult([], [], QueryStatistics.Empty);
            throw new GraphDbException($"No index or constraint named '{di.Name}'");
        }
        return new QueryResult([], [], QueryStatistics.Empty with { IndexesRemoved = 1 });
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;
        if (IsReadOnly)
        {
            _db.Lock.ExitRead();
            return;
        }
        try
        {
            Store.Commit();
            _db.AfterCommit();
        }
        catch
        {
            Store.Abort();
            throw;
        }
        finally
        {
            _db.WriterGate.Release();
        }
    }

    public void Rollback()
    {
        if (_finished) return;
        _finished = true;
        if (IsReadOnly)
        {
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
