using System.Runtime.CompilerServices;
using System.Text;
using GraphQLite.Storage;

namespace GraphQLite.Runtime;

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
    public int Resolve(StoreTx store)
    {
        int id = _id;
        if (id >= 0) return id;
        id = store.G.TokensOf(_kind).Lookup(Name);
        if (id >= 0) _id = id;
        return id;
    }

    public int GetOrCreate(StoreTx store)
    {
        int id = _id;
        if (id >= 0) return id;
        id = store.G.TokensOf(_kind).GetOrCreate(Name);
        _id = id;
        return id;
    }

    public override string ToString() => Name;
}

/// <summary>Mutable statistics of updates performed by a query.</summary>
internal sealed class StatsCounter
{
    public int NodesCreated, NodesDeleted, RelationshipsCreated, RelationshipsDeleted, PropertiesSet, LabelsAdded, LabelsRemoved, IndexesAdded, IndexesRemoved;

    public QueryStatistics ToStats() => (NodesCreated | NodesDeleted | RelationshipsCreated | RelationshipsDeleted | PropertiesSet | LabelsAdded | LabelsRemoved | IndexesAdded | IndexesRemoved) == 0
        ? QueryStatistics.Empty
        : new(NodesCreated, NodesDeleted, RelationshipsCreated, RelationshipsDeleted, PropertiesSet,
        LabelsAdded, LabelsRemoved, IndexesAdded, IndexesRemoved);
}

internal sealed class ExecContext
{
    public readonly StoreTx Store;
    public readonly Value[] Row;
    public readonly Value[] Params;
    public readonly StatsCounter Stats = new();
    public HashSet<long>? PendingNodeDeletes;
    public readonly CancellationToken Cancellation;
    public readonly DatabaseOptions Options;
    /// <summary>Commits the work done so far and continues (CALL ... IN TRANSACTIONS); null inside explicit transactions.</summary>
    public Action? CommitBatch;
    private int _tick;

    private long _nowTicks;
    private int? _seed;
    private Random? _random;

    /// <summary>
    /// Statement clock (UTC): every time function of one statement sees the same instant, as in Neo4j. Read lazily,
    /// so statements that do not use it pay nothing.
    /// </summary>
    public System.DateTime Now
    {
        get
        {
            if (_nowTicks == 0) _nowTicks = System.DateTime.UtcNow.Ticks;
            return new System.DateTime(_nowTicks, DateTimeKind.Utc);
        }
        set => _nowTicks = value.Ticks;
    }

    /// <summary>Seed of <see cref="Random"/>: recorded so that a statement can be replayed with the same random values.</summary>
    public int Seed
    {
        get => _seed ??= System.Random.Shared.Next();
        set => _seed = value;
    }

    /// <summary>Source of rand() and randomUUID() (not thread-safe: parallel workers get their own).</summary>
    public Random Random => _random ??= new Random(Seed);

    /// <summary>Scan restricted to a node id range (parallel aggregation workers), see <see cref="PartitionOf"/>.</summary>
    private Operator? _partitionLeaf;
    private long _partitionLow, _partitionHigh;

    public ExecContext(StoreTx store, int slots, Value[] parameters, DatabaseOptions options, CancellationToken ct)
    {
        Store = store;
        Row = new Value[slots];
        Params = parameters;
        Options = options;
        Cancellation = ct;
    }

    /// <summary>
    /// Context of a parallel worker: same snapshot and parameters, private row (a copy of <paramref name="row"/>, or
    /// of ours), scan of <paramref name="leaf"/> restricted to [low, high). Thread-safe once <see cref="Now"/> and
    /// <see cref="Seed"/> have been read.
    /// </summary>
    public ExecContext ForPartition(Operator leaf, long low, long high, Value[]? row = null, CancellationToken? ct = null)
    {
        var w = new ExecContext(Store, Row.Length, Params, Options, ct ?? Cancellation)
        {
            _partitionLeaf = leaf,
            _partitionLow = low,
            _partitionHigh = high,
            Now = Now,
            Seed = HashCode.Combine(Seed, low),
        };
        (row ?? Row).AsSpan().CopyTo(w.Row);
        return w;
    }

    /// <summary>Work still running on other threads for this statement (see <see cref="StopBackground"/>).</summary>
    private List<Action>? _background;

    /// <summary>Registers how to stop work that runs on other threads while the statement is open.</summary>
    public void OnStop(Action stop) => (_background ??= []).Add(stop);

    /// <summary>
    /// Stops the work running on other threads and waits for it: called when the statement ends, before its
    /// transaction can release the snapshot.
    /// </summary>
    public void StopBackground()
    {
        var b = _background;
        _background = null;
        if (b is null) return;
        foreach (var stop in b) stop();
    }

    /// <summary>Node id range that the scan <paramref name="scan"/> must cover in this context.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (long Low, long High) PartitionOf(Operator scan) =>
        ReferenceEquals(scan, _partitionLeaf) ? (_partitionLow, _partitionHigh) : (0, long.MaxValue);

    /// <summary>Verifies that deleted nodes have no remaining relationships (deferred DELETE check).</summary>
    public void CheckPendingDeletes()
    {
        if (PendingNodeDeletes is null) return;
        foreach (var id in PendingNodeDeletes)
        {
            var (o, i) = Store.Degree(id);
            if (o > 0 || i > 0)
                throw new ConstraintViolationException(
                    $"Cannot delete node<{id}>, because it still has relationships. To delete this node, you must first delete its relationships (or use DETACH DELETE).");
        }
        PendingNodeDeletes = null;
    }

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

    /// <summary>
    /// True when every output row depends only on one input row and the operator has no side effects, so its
    /// input can be split into partitions processed independently (parallel aggregation).
    /// </summary>
    public virtual bool RowLocal => false;

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

    /// <summary>The same operator over another input, or null when it does not support it (plan rewriting).</summary>
    public virtual UnaryOperator? WithInput(Operator input) => null;
}

/// <summary>A node scan whose id space can be split into ranges (<see cref="ExecContext.PartitionOf"/>).</summary>
internal interface IPartitionedScan
{
    /// <summary>Estimated number of rows produced for one input row.</summary>
    long EstimateRows(StoreTx tx);
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
