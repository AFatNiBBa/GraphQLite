using GraphQLite.Cypher;

namespace GraphQLite.Tests;

/// <summary>The parser (GraphQLite.Cypher) used on its own, without a database.</summary>
public class ParserTests
{
    [Fact]
    public void Parser_Builds_The_Syntax_Tree_Without_A_Database()
    {
        var st = Assert.IsType<QueryStatement>(Parser.Parse("MATCH (a:Person:!Robot)-[r:KNOWS*1..3]->(b) WHERE a.age > 30 RETURN b.name AS name, -2.5, null, 'x' LIMIT 10"));
        var clauses = Assert.Single(st.Parts);
        var match = Assert.IsType<MatchClause>(clauses[0]);
        var part = Assert.Single(match.Pattern);
        Assert.Equal("a", part.Nodes[0].Variable);
        Assert.Equal(["Person"], part.Nodes[0].Labels);
        Assert.Equal("!Robot", part.Nodes[0].Residual?.ToString());
        Assert.Equal(Direction.Out, part.Rels[0].Dir);
        Assert.True(part.Rels[0].VarLength);
        Assert.Equal((1, 3), (part.Rels[0].Min, part.Rels[0].Max));

        var where = Assert.IsType<BinaryExpr>(match.Where);
        Assert.Equal(BinOp.Gt, where.Op);
        Assert.Equal(Value.FromInt(30), Assert.IsType<LiteralExpr>(where.Right).Value);

        var ret = Assert.IsType<ReturnClause>(clauses[1]);
        Assert.Equal(["name", "-2.5", "null", "'x'"], ret.Projection.Items.Select(i => i.Name));
        Assert.Equal([Value.FromFloat(-2.5), Value.Null, Value.FromString("x")], ret.Projection.Items.Skip(1).Select(i => ((LiteralExpr)i.Expr).Value));
        Assert.Equal(Value.FromInt(10), Assert.IsType<LiteralExpr>(ret.Projection.Limit).Value);

        var error = Assert.Throws<CypherSyntaxException>(() => Parser.Parse("MATCH (n RETURN n"));
        Assert.IsAssignableFrom<GraphQLiteException>(error);
    }
}
