namespace GraphQLite.Runtime;

/// <summary>
/// Parallel execution of a chain of row-local operators over a partitioned scan: the node id space is split into
/// contiguous ranges, each run by a worker with its own <see cref="ExecContext"/> (see <see cref="ExecContext.ForPartition"/>).
/// The results of the ranges are combined in range order, so they match a sequential run.
/// </summary>
internal static class Partitioning
{
    /// <summary>Minimum estimated input rows per worker.</summary>
    public const long RowsPerWorker = 16_384;

    /// <summary>
    /// The scan at the bottom of <paramref name="op"/> when <paramref name="op"/> is a chain of row-local operators
    /// that starts from a partitioned scan of the argument row, otherwise null.
    /// </summary>
    public static Operator? FindLeaf(Operator op)
    {
        while (op.RowLocal && op is UnaryOperator u)
        {
            if (op is IPartitionedScan && u.Input is StartOp) return op;
            op = u.Input;
        }
        return null;
    }

    /// <summary>Number of workers worth using (1 = sequential). Only read-only transactions run in parallel.</summary>
    public static int Workers(ExecContext ctx, Operator? leaf)
    {
        if (leaf is null || ctx.Store.IsWriter) return 1;
        int max = ctx.Options.MaxDegreeOfParallelism;
        if (max <= 1) return 1;
        long rows = ((IPartitionedScan)leaf).EstimateRows(ctx.Store);
        return (int)Math.Min(max, rows / RowsPerWorker);
    }

    /// <summary>
    /// Ranges of node ids for <paramref name="workers"/> workers: about 8 each (load balancing), at least 4096 ids
    /// and aligned to 64 (label bitmap words).
    /// </summary>
    public static (long Size, int Count) Ranges(ExecContext ctx, int workers)
    {
        long high = ctx.Store.NodeHigh;
        long size = Math.Max(4096, ((high + workers * 8 - 1) / (workers * 8) + 63) & ~63L);
        return (size, (int)((high + size - 1) / size));
    }

    /// <summary>Runs <paramref name="body"/> for every range on up to <paramref name="workers"/> threads, rethrowing the first error as is.</summary>
    public static void For(int count, int workers, Action<int> body)
    {
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers }, body);
        }
        catch (AggregateException ae)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }
    }
}
