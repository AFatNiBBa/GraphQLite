using System.Runtime.CompilerServices;
using System.Text;
using GraphDb.Storage;

namespace GraphDb.Runtime;

/// <summary>Compiled expression: evaluates against the current row of the context.</summary>
internal delegate Value Eval(ExecContext ctx);

/// <summary>Lazily resolved token id (label / type / key). Plans are cached, tokens may appear later.</summary>
internal sealed class TokenRef
{
    public readonly string Name;
    private readonly TokenKind _kind;
    private int _id = -1;

    public TokenRef(string name, TokenKind kind)
    {
        Name = name;
        _kind = kind;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Resolve(GraphStore store)
    {
        int id = _id;
        if (id >= 0) return id;
        id = store.TokensOf(_kind).Lookup(Name);
        if (id >= 0) _id = id;
        return id;
    }

    public int GetOrCreate(GraphStore store)
    {
        int id = _id;
        if (id >= 0) return id;
        id = store.TokensOf(_kind).GetOrCreate(Name);
        _id = id;
        return id;
    }

    public override string ToString() => Name;
}

/// <summary>Mutable statistics of updates performed by a query.</summary>
internal sealed class StatsCounter
{
    public int NodesCreated, NodesDeleted, RelationshipsCreated, RelationshipsDeleted, PropertiesSet, LabelsAdded, LabelsRemoved, IndexesAdded, IndexesRemoved;

    public QueryStatistics ToStats() => new(NodesCreated, NodesDeleted, RelationshipsCreated, RelationshipsDeleted, PropertiesSet,
        LabelsAdded, LabelsRemoved, IndexesAdded, IndexesRemoved);
}

internal sealed class ExecContext
{
    public readonly GraphStore Store;
    public readonly Value[] Row;
    public readonly Value[] Params;
    /// <summary>Null for read-only execution.</summary>
    public readonly TxLog? Log;
    public readonly StatsCounter Stats = new();
    public HashSet<int>? PendingNodeDeletes;
    public readonly CancellationToken Cancellation;
    private int _tick;

    public ExecContext(GraphStore store, int slots, Value[] parameters, TxLog? log, CancellationToken ct)
    {
        Store = store;
        Row = new Value[slots];
        Params = parameters;
        Log = log;
        Cancellation = ct;
    }

    public TxLog WriteLog => Log ?? throw new GraphDbException("Cannot perform updates in a read-only transaction");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CheckCancel()
    {
        if ((++_tick & 0x3FFF) == 0) Cancellation.ThrowIfCancellationRequested();
    }
}

internal abstract class Cursor
{
    /// <summary>Advances to the next row (written in-place into ctx.Row).</summary>
    public abstract bool Next();
}

internal abstract class Operator
{
    public abstract Cursor Open(ExecContext ctx);
    public abstract string Name { get; }
    public virtual string Details => "";
    public virtual IEnumerable<Operator> Children => [];

    public void Explain(StringBuilder sb, int depth)
    {
        sb.Append(' ', depth * 2).Append("+").Append(Name);
        var d = Details;
        if (d.Length > 0) sb.Append("  ").Append(d);
        sb.AppendLine();
        foreach (var c in Children) c.Explain(sb, depth + 1);
    }
}

/// <summary>Operator with a single input.</summary>
internal abstract class UnaryOperator(Operator input) : Operator
{
    public readonly Operator Input = input;
    public override IEnumerable<Operator> Children => [Input];
}

/// <summary>Produces exactly one (current) row. Used as the leaf of every pipeline and nested plan.</summary>
internal sealed class StartOp : Operator
{
    public override string Name => "Argument";
    public override Cursor Open(ExecContext ctx) => new C();

    private sealed class C : Cursor
    {
        private bool _done;

        public override bool Next()
        {
            if (_done) return false;
            _done = true;
            return true;
        }
    }
}

internal static class RowOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value[] Snapshot(Value[] row) => (Value[])row.Clone();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Restore(Value[] src, Value[] row) => src.AsSpan().CopyTo(row);
}
