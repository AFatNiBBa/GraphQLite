using GraphQLite.Runtime;

namespace GraphQLite;

/// <summary>
/// Forward-only, streamed result of a query: rows are produced while they are read, so results of any size use
/// constant memory. Obtained from <see cref="Transaction.Stream"/> or <see cref="GraphDatabase.Stream"/>.
/// <para>Starting another statement in the same transaction buffers the rows not read yet. Disposing a cursor
/// before the end stops a read query; an updating query is always run to completion (its updates are applied
/// even if the rows are not read), as in Neo4j.</para>
/// </summary>
public sealed class QueryCursor : IDisposable
{
    private readonly Transaction _tx;
    private readonly ColumnSet _cols;
    private readonly bool _isWrite;
    private readonly bool _ownsTransaction;
    private ExecContext? _ctx;
    private Cursor? _cursor;
    private int[] _slots = [];
    private QueryResult.Materializer? _mat;
    private Queue<Value[]>? _buffer;
    private Exception? _pendingError;
    private QueryStatistics? _stats;
    private State _state;
    private Value[]? _current;
    internal readonly StatementLog? Log;

    private enum State : byte { Open, Done, Failed, Closed }

    /// <summary>Streams the rows of an executing plan.</summary>
    internal QueryCursor(Transaction tx, ColumnSet cols, bool isWrite, bool ownsTransaction, StatementLog? log, ExecContext ctx, Cursor cursor, int[] slots)
    {
        _tx = tx;
        _cols = cols;
        _isWrite = isWrite;
        _ownsTransaction = ownsTransaction;
        Log = log;
        _ctx = ctx;
        _cursor = cursor;
        _slots = slots;
        _mat = new QueryResult.Materializer(tx.Store);
    }

    /// <summary>Cursor over a result computed eagerly (schema commands, EXPLAIN); the statement is already over.</summary>
    internal QueryCursor(Transaction tx, QueryResult result, bool ownsTransaction, StatementLog? log)
    {
        _tx = tx;
        _cols = result.ColumnSet;
        _ownsTransaction = ownsTransaction;
        Log = log;
        _buffer = new Queue<Value[]>(result.Count);
        foreach (var r in result)
        {
            var values = r.Values.ToArray();
            _buffer.Enqueue(values);
            if (log != null) log.AddRow(values);
        }
        _stats = result.Statistics;
        _state = State.Done;
        log?.Complete(result.Statistics);
    }

    public IReadOnlyList<string> Columns => _cols.Columns;
    internal ColumnSet ColumnSet => _cols;

    /// <summary>Current row (after <see cref="MoveNext"/> returned true).</summary>
    public Record Current => new(_cols, _current ?? throw new InvalidOperationException("No current row"));

    /// <summary>Update counters: final once the cursor is exhausted or disposed.</summary>
    public QueryStatistics Statistics => _stats ?? _ctx?.Stats.ToStats() ?? QueryStatistics.Empty;

    /// <summary>True when the query produced all its rows (they may still be buffered).</summary>
    public bool IsCompleted => _state != State.Open;

    /// <summary>Rows (or an error) computed but not read yet.</summary>
    internal bool HasBufferedRows => _buffer is { Count: > 0 } || _pendingError != null;

    public bool MoveNext()
    {
        _current = NextValues();
        return _current != null;
    }

    /// <summary>Next row, or null at the end.</summary>
    internal Value[]? NextValues()
    {
        if (_buffer != null)
        {
            if (_buffer.TryDequeue(out var b)) return b;
            if (_pendingError != null)
            {
                var e = _pendingError;
                _pendingError = null;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e);
            }
            if (_state == State.Done && _ownsTransaction) EndOwnedTransaction(commit: true);
            return null;
        }
        if (_state != State.Open) return null;
        try
        {
            var cursor = _cursor!;
            var slots = _slots;
            while (cursor.Next())
            {
                if (slots.Length == 0) continue;
                var row = _ctx!.Row;
                var r = new Value[slots.Length];
                for (int i = 0; i < slots.Length; i++) r[i] = _mat!.Convert(row[slots[i]]);
                Log?.AddRow(r);
                return r;
            }
            Complete();
            if (_ownsTransaction) EndOwnedTransaction(commit: true);
            return null;
        }
        catch
        {
            Fail();
            throw;
        }
    }

    private void Complete()
    {
        _ctx!.CheckPendingDeletes();
        _stats = _ctx.Stats.ToStats();
        _tx.Store.EndStatement();
        _state = State.Done;
        Release();
        Log?.Complete(_stats);
    }

    private void Fail()
    {
        if (_state != State.Open) return;
        _state = State.Failed;
        try
        {
            _tx.Store.RollbackStatement();
        }
        finally
        {
            Release();
            if (Log != null) Log.Failed = true;
            if (_ownsTransaction) EndOwnedTransaction(commit: false);
        }
    }

    private void Release()
    {
        _cursor = null;
        _mat = null;
        _tx.CursorClosed(this);
    }

    private void EndOwnedTransaction(bool commit)
    {
        if (_state == State.Closed) return;
        var state = _state;
        _state = State.Closed;
        if (commit && state == State.Done) _tx.Commit();
        else _tx.Rollback();
    }

    /// <summary>
    /// Called when another statement starts in the same transaction: runs this one to the end, keeping the rows not
    /// read yet in memory. An error is reported when those rows have been read.
    /// </summary>
    internal void Detach()
    {
        if (_state != State.Open) return;
        var buffer = new Queue<Value[]>();
        try
        {
            while (NextValues() is { } r) buffer.Enqueue(r);
        }
        catch (Exception ex)
        {
            _pendingError = ex;
        }
        _buffer = buffer;
    }

    /// <summary>Stops a read query, finishes an updating one, and ends the transaction it owns.</summary>
    public void Dispose()
    {
        if (_state == State.Open)
        {
            if (_isWrite)
            {
                try
                {
                    while (NextValues() != null) { }
                }
                catch
                {
                    // the statement was rolled back; the caller did not read it
                }
            }
            else
            {
                _stats = _ctx!.Stats.ToStats();
                _tx.Store.EndStatement();
                _state = State.Done;
                Release();
                Log?.Stop(_stats);
            }
        }
        _buffer = null;
        _pendingError = null;
        if (_ownsTransaction && _state != State.Closed) EndOwnedTransaction(commit: _state == State.Done);
        _state = State.Closed;
    }
}

/// <summary>What a statement of a concurrent transaction returned, to verify a replay against it.</summary>
internal sealed class StatementLog(string query, IReadOnlyDictionary<string, object?>? parameters, System.DateTime now, int seed)
{
    public readonly string Query = query;
    public readonly IReadOnlyDictionary<string, object?>? Parameters = parameters;
    public readonly System.DateTime Now = now;
    public readonly int Seed = seed;
    public long Rows;
    public ulong Hash;
    /// <summary>The statement ran to the end (otherwise it was stopped after <see cref="Rows"/> rows).</summary>
    public bool Completed;
    public bool Failed;

    public void AddRow(Value[] row)
    {
        Rows++;
        Hash = ResultHash.Row(Hash, row);
    }

    public void Complete(QueryStatistics stats)
    {
        Completed = true;
        Hash = ResultHash.Stats(Hash, stats);
    }

    public void Stop(QueryStatistics stats) => Hash = ResultHash.Stats(Hash, stats);

    public bool SameOutcome(StatementLog other) =>
        Failed == other.Failed && (Failed || Completed == other.Completed && Rows == other.Rows && Hash == other.Hash);
}
