using GraphQLite.Cypher;
using GraphQLite.Storage;

namespace GraphQLite.Runtime;

/// <summary>A <see cref="LabelExpression"/> (AND / OR / NOT of labels, % wildcard) compiled to test nodes of the store.</summary>
internal abstract class LabelMatcher
{
    public abstract bool Matches(StoreTx tx, long node);

    public static LabelMatcher Compile(LabelExpression e) => e switch
    {
        LabelName n => new Name(new TokenRef(n.Name, TokenKind.Label)),
        LabelWildcard => new Wildcard(),
        LabelNot n => new Not(Compile(n.Inner)),
        LabelAnd a => new And(Compile(a.Left), Compile(a.Right)),
        LabelOr o => new Or(Compile(o.Left), Compile(o.Right)),
        _ => throw new CypherSemanticException($"Unsupported label expression {e}"),
    };

    private sealed class Name(TokenRef token) : LabelMatcher
    {
        private readonly TokenRef _token = token;

        public override bool Matches(StoreTx tx, long node)
        {
            int id = _token.Resolve(tx);
            return id >= 0 && tx.HasLabel(node, id);
        }
    }

    private sealed class Wildcard : LabelMatcher
    {
        public override bool Matches(StoreTx tx, long node) => tx.NodeLabels(node).Length > 0;
    }

    private sealed class Not(LabelMatcher inner) : LabelMatcher
    {
        private readonly LabelMatcher _inner = inner;
        public override bool Matches(StoreTx tx, long node) => !_inner.Matches(tx, node);
    }

    private sealed class And(LabelMatcher left, LabelMatcher right) : LabelMatcher
    {
        private readonly LabelMatcher _left = left, _right = right;
        public override bool Matches(StoreTx tx, long node) => _left.Matches(tx, node) && _right.Matches(tx, node);
    }

    private sealed class Or(LabelMatcher left, LabelMatcher right) : LabelMatcher
    {
        private readonly LabelMatcher _left = left, _right = right;
        public override bool Matches(StoreTx tx, long node) => _left.Matches(tx, node) || _right.Matches(tx, node);
    }
}
