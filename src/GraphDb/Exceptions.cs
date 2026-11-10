namespace GraphDb;

/// <summary>Base exception for all database errors.</summary>
public class GraphDbException : Exception
{
    public GraphDbException(string message) : base(message) { }
    public GraphDbException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The query text is not valid Cypher.</summary>
public sealed class CypherSyntaxException : GraphDbException
{
    public int Position { get; }
    public CypherSyntaxException(string message, int position) : base($"{message} (at offset {position})") => Position = position;
}

/// <summary>Semantic error detected while planning a query (unknown variable, invalid aggregation, ...).</summary>
public sealed class CypherSemanticException : GraphDbException
{
    public CypherSemanticException(string message) : base(message) { }
}

/// <summary>Error raised while executing a query (type errors, invalid arguments, ...).</summary>
public sealed class CypherRuntimeException : GraphDbException
{
    public CypherRuntimeException(string message) : base(message) { }
}

/// <summary>A schema constraint was violated, or a node with relationships was deleted without DETACH.</summary>
public sealed class ConstraintViolationException : GraphDbException
{
    public ConstraintViolationException(string message) : base(message) { }
}

/// <summary>
/// A concurrent transaction could not commit because another transaction changed data it depends on. Nothing was
/// written: retry the whole transaction (Neo4j drivers do it automatically for managed transactions).
/// </summary>
public sealed class TransactionConflictException : GraphDbException
{
    public TransactionConflictException(string message) : base(message) { }
}
