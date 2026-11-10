using System.Text;
using GraphDb.Cypher;
using GraphDb.Runtime;
using GraphDb.Storage;

namespace GraphDb;

/// <summary>
/// A database transaction. Read-only transactions share the database with other readers;
/// a write transaction has exclusive access until it is committed, rolled back or disposed.
/// A failing statement is rolled back on its own: the transaction stays usable.
/// Instances are not thread-safe; use one transaction per thread.
/// </summary>
public sealed class Transaction : IDisposable
{
    private readonly GraphDatabase _db;
    private readonly TxLog? _log;
    private bool _finished;

    public bool IsReadOnly { get; }

    internal Transaction(GraphDatabase db, bool readOnly)
    {
        _db = db;
        IsReadOnly = readOnly;
        _log = readOnly ? null : db.NewLog();
    }

    public QueryResult Execute(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var cached = _db.GetStatement(query);
        if (cached.IsWrite && IsReadOnly)
            throw new GraphDbException("Cannot execute an updating query in a read-only transaction");

        int undoMark = _log?.Undo.Count ?? 0;
        int redoMark = _log?.Redo?.Length ?? 0;
        try
        {
            return cached.Ast switch
            {
                CreateIndexStatement ci => CreateIndex(ci),
                DropIndexStatement di => DropIndex(di),
                _ => Run(_db.GetPlan(cached), parameters, cancellationToken),
            };
        }
        catch
        {
            if (_log != null) _db.Store.RollbackTo(_log, undoMark, redoMark);
            throw;
        }
    }

    private QueryResult Run(PlannedQuery plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        var store = _db.Store;
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
        var ctx = new ExecContext(store, plan.SlotCount, pvals, _log, ct);
        var cursor = plan.Root.Open(ctx);
        var rows = new List<Value[]>();
        var slots = plan.ColumnSlots;
        var mat = new QueryResult.Materializer(store);
        var row = ctx.Row;
        while (cursor.Next())
        {
            if (slots.Length == 0) continue;
            var r = new Value[slots.Length];
            for (int i = 0; i < slots.Length; i++) r[i] = mat.Convert(row[slots[i]]);
            rows.Add(r);
        }
        if (ctx.PendingNodeDeletes != null)
        {
            foreach (var id in ctx.PendingNodeDeletes)
            {
                ref var n = ref store.Nodes[id];
                if (n.Out.Count > 0 || n.In.Count > 0)
                    throw new ConstraintViolationException(
                        $"Cannot delete node<{id}>, because it still has relationships. To delete this node, you must first delete its relationships (or use DETACH DELETE).");
            }
        }
        return new QueryResult(plan.Columns, rows, ctx.Stats.ToStats());
    }

    private QueryResult CreateIndex(CreateIndexStatement ci)
    {
        var store = _db.Store;
        int label = store.Labels.GetOrCreate(ci.Label);
        int key = store.PropKeys.GetOrCreate(ci.Property);
        var existing = store.FindIndex(label, key);
        string name = ci.Name ?? (ci.Unique ? $"constraint_{ci.Label}_{ci.Property}" : $"index_{ci.Label}_{ci.Property}");
        if (existing != null || store.IndexesByName.ContainsKey(name))
        {
            if (ci.IfNotExists) return new QueryResult([], [], QueryStatistics.Empty);
            if (existing != null && existing.Unique != ci.Unique)
                throw new GraphDbException($"An index already exists on :{ci.Label}({ci.Property}); drop it first");
        }
        store.CreateIndex(name, label, key, ci.Unique, _log!);
        return new QueryResult([], [], QueryStatistics.Empty with { IndexesAdded = 1 });
    }

    private QueryResult DropIndex(DropIndexStatement di)
    {
        if (!_db.Store.DropIndex(di.Name, _log!))
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
            _db.CommitWrite(_log!);
        }
        finally
        {
            _db.Lock.ExitWrite();
            try { _db.AfterWriteReleased(); }
            finally { _db.WriterGate.Release(); }
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
            _db.Store.Rollback(_log!);
        }
        finally
        {
            _db.Lock.ExitWrite();
            _db.WriterGate.Release();
        }
    }

    /// <summary>Rolls back if neither Commit nor Rollback was called.</summary>
    public void Dispose() => Rollback();
}
