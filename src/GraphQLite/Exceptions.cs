namespace GraphQLite;

/// <summary>Base exception for all database errors.</summary>
public class GraphQLiteException : Exception
{
    public GraphQLiteException(string message) : base(message) { }
    public GraphQLiteException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The query text is not valid Cypher.</summary>
public sealed class CypherSyntaxException : GraphQLiteException
{
    public int Position { get; }
    public CypherSyntaxException(string message, int position) : base($"{message} (at offset {position})") => Position = position;
}

/// <summary>Semantic error detected while planning a query (unknown variable, invalid aggregation, ...).</summary>
public sealed class CypherSemanticException : GraphQLiteException
{
    public CypherSemanticException(string message) : base(message) { }
}

/// <summary>Error raised while executing a query (type errors, invalid arguments, ...).</summary>
public sealed class CypherRuntimeException : GraphQLiteException
{
    public CypherRuntimeException(string message) : base(message) { }
}

/// <summary>A schema constraint was violated, or a node with relationships was deleted without DETACH.</summary>
public sealed class ConstraintViolationException : GraphQLiteException
{
    public ConstraintViolationException(string message) : base(message) { }
}

/// <summary>
/// A concurrent transaction could not commit because another transaction changed data it depends on. Nothing was
/// written: retry the whole transaction (Neo4j drivers do it automatically for managed transactions).
/// </summary>
public sealed class TransactionConflictException : GraphQLiteException
{
    public TransactionConflictException(string message) : base(message) { }
}
