namespace GraphDb.Cypher;

// ============================================================ expressions

internal abstract class Expr
{
    /// <summary>Original source text (used for column names and structural matching).</summary>
    public string Text = "";
    public override string ToString() => Text;

    /// <summary>Visits direct children.</summary>
    public virtual IEnumerable<Expr> Children => [];
}

internal sealed class LiteralExpr(Value value) : Expr
{
    public readonly Value Value = value;
}

internal sealed class ParamExpr(string name) : Expr
{
    public readonly string Name = name;
}

internal sealed class VarExpr(string name) : Expr
{
    public readonly string Name = name;
}

internal sealed class PropertyExpr(Expr target, string key) : Expr
{
    public readonly Expr Target = target;
    public readonly string Key = key;
    public override IEnumerable<Expr> Children => [Target];
}

internal sealed class IndexExpr(Expr target, Expr index) : Expr
{
    public readonly Expr Target = target;
    public readonly Expr Index = index;
    public override IEnumerable<Expr> Children => [Target, Index];
}

internal sealed class SliceExpr(Expr target, Expr? from, Expr? to) : Expr
{
    public readonly Expr Target = target;
    public readonly Expr? From = from;
    public readonly Expr? To = to;
    public override IEnumerable<Expr> Children => new[] { Target, From, To }.Where(e => e != null)!;
}

internal enum BinOp
{
    Add, Sub, Mul, Div, Mod, Pow,
    Eq, Neq, Lt, Gt, Le, Ge,
    And, Or, Xor,
    In, StartsWith, EndsWith, Contains, Regex,
}

internal sealed class BinaryExpr(BinOp op, Expr left, Expr right) : Expr
{
    public readonly BinOp Op = op;
    public readonly Expr Left = left;
    public readonly Expr Right = right;
    public override IEnumerable<Expr> Children => [Left, Right];
}

internal enum UnOp { Not, Neg, Plus }

internal sealed class UnaryExpr(UnOp op, Expr operand) : Expr
{
    public readonly UnOp Op = op;
    public readonly Expr Operand = operand;
    public override IEnumerable<Expr> Children => [Operand];
}

internal sealed class IsNullExpr(Expr operand, bool negated) : Expr
{
    public readonly Expr Operand = operand;
    public readonly bool Negated = negated;
    public override IEnumerable<Expr> Children => [Operand];
}

internal sealed class LabelCheckExpr(Expr target, List<string> labels, LabelExpression? expression = null) : Expr
{
    public readonly Expr Target = target;
    /// <summary>Plain conjunction of labels (n:A:B); empty when <see cref="Expression"/> is used.</summary>
    public readonly List<string> Labels = labels;
    /// <summary>General label expression (n:A|B, n:!A, n:%, ...).</summary>
    public readonly LabelExpression? Expression = expression;
    public override IEnumerable<Expr> Children => [Target];
}

// ============================================================ label expressions

internal abstract class LabelExpression
{
    public abstract bool Matches(Storage.StoreTx tx, long node);
    public abstract override string ToString();

    /// <summary>Top-level conjuncts (A&amp;B&amp;C → [A, B, C]).</summary>
    public virtual IEnumerable<LabelExpression> Conjuncts => [this];
}

internal sealed class LabelName(string name) : LabelExpression
{
    public readonly string Name = name;
    private readonly Runtime.TokenRef _token = new(name, Storage.TokenKind.Label);

    public override bool Matches(Storage.StoreTx tx, long node)
    {
        int id = _token.Resolve(tx);
        return id >= 0 && tx.HasLabel(node, id);
    }

    public override string ToString() => Name;
}

internal sealed class LabelWildcard : LabelExpression
{
    public override bool Matches(Storage.StoreTx tx, long node) => tx.NodeLabels(node).Length > 0;
    public override string ToString() => "%";
}

internal sealed class LabelNot(LabelExpression inner) : LabelExpression
{
    public readonly LabelExpression Inner = inner;
    public override bool Matches(Storage.StoreTx tx, long node) => !Inner.Matches(tx, node);
    public override string ToString() => "!" + Inner;
}

internal sealed class LabelAnd(LabelExpression left, LabelExpression right) : LabelExpression
{
    public readonly LabelExpression Left = left, Right = right;
    public override bool Matches(Storage.StoreTx tx, long node) => Left.Matches(tx, node) && Right.Matches(tx, node);
    public override string ToString() => $"({Left}&{Right})";
    public override IEnumerable<LabelExpression> Conjuncts => Left.Conjuncts.Concat(Right.Conjuncts);
}

internal sealed class LabelOr(LabelExpression left, LabelExpression right) : LabelExpression
{
    public readonly LabelExpression Left = left, Right = right;
    public override bool Matches(Storage.StoreTx tx, long node) => Left.Matches(tx, node) || Right.Matches(tx, node);
    public override string ToString() => $"({Left}|{Right})";

    /// <summary>When the expression is a disjunction of plain names, returns them.</summary>
    public List<string>? Names()
    {
        var acc = new List<string>();
        return Collect(this, acc) ? acc : null;

        static bool Collect(LabelExpression e, List<string> acc)
        {
            switch (e)
            {
                case LabelName n: acc.Add(n.Name); return true;
                case LabelOr o: return Collect(o.Left, acc) && Collect(o.Right, acc);
                default: return false;
            }
        }
    }
}

internal sealed class ListExpr(List<Expr> items) : Expr
{
    public readonly List<Expr> Items = items;
    public override IEnumerable<Expr> Children => Items;
}

internal sealed class MapExpr(List<string> keys, List<Expr> values) : Expr
{
    public readonly List<string> Keys = keys;
    public readonly List<Expr> Values = values;
    public override IEnumerable<Expr> Children => Values;
}

internal sealed class FuncExpr(string name, List<Expr> args, bool distinct) : Expr
{
    public readonly string Name = name; // lower-cased, possibly namespaced
    public readonly List<Expr> Args = args;
    public readonly bool Distinct = distinct;
    public override IEnumerable<Expr> Children => Args;
}

internal sealed class CountStarExpr : Expr;

internal sealed class CaseExpr(Expr? test, List<Expr> whens, List<Expr> thens, Expr? @else) : Expr
{
    public readonly Expr? Test = test;
    public readonly List<Expr> Whens = whens;
    public readonly List<Expr> Thens = thens;
    public readonly Expr? Else = @else;

    public override IEnumerable<Expr> Children
    {
        get
        {
            if (Test != null) yield return Test;
            foreach (var w in Whens) yield return w;
            foreach (var t in Thens) yield return t;
            if (Else != null) yield return Else;
        }
    }
}

internal sealed class ListCompExpr(string variable, Expr list, Expr? where, Expr? map) : Expr
{
    public readonly string Variable = variable;
    public readonly Expr List = list;
    public readonly Expr? Where = where;
    public readonly Expr? Map = map;
}

internal enum QuantKind { All, Any, None, Single }

internal sealed class QuantifierExpr(QuantKind kind, string variable, Expr list, Expr where) : Expr
{
    public readonly QuantKind Kind = kind;
    public readonly string Variable = variable;
    public readonly Expr List = list;
    public readonly Expr Where = where;
}

internal sealed class ReduceExpr(string acc, Expr init, string variable, Expr list, Expr body) : Expr
{
    public readonly string Acc = acc;
    public readonly Expr Init = init;
    public readonly string Variable = variable;
    public readonly Expr List = list;
    public readonly Expr Body = body;
}

internal enum SubqueryKind { Exists, Count, Collect }

/// <summary>Pattern predicate / EXISTS {..} / COUNT {..} / pattern comprehension.</summary>
internal sealed class PatternSubqueryExpr(SubqueryKind kind, List<PatternPart> pattern, Expr? where, Expr? map) : Expr
{
    public readonly SubqueryKind Kind = kind;
    public readonly List<PatternPart> Pattern = pattern;
    public readonly Expr? Where = where;
    public readonly Expr? Map = map;
}

internal enum MapProjKind { Property, AllProperties, Literal, Variable }

internal sealed class MapProjItem(MapProjKind kind, string key, Expr? value)
{
    public readonly MapProjKind Kind = kind;
    public readonly string Key = key;
    public readonly Expr? Value = value;
}

internal sealed class MapProjectionExpr(string variable, List<MapProjItem> items) : Expr
{
    public readonly string Variable = variable;
    public readonly List<MapProjItem> Items = items;
}

// ============================================================ patterns

internal enum Direction { Out, In, Both }

internal sealed class NodePattern(string? variable, List<string> labels, Expr? props)
{
    public string? Variable = variable;
    /// <summary>Labels the node must all have.</summary>
    public readonly List<string> Labels = labels;
    public readonly Expr? Props = props;
    /// <summary>Optional disjunction: the node must have at least one of these labels.</summary>
    public List<string>? AnyLabels;
    /// <summary>Remaining label expression evaluated as a predicate.</summary>
    public LabelExpression? Residual;
}

internal sealed class RelPattern(string? variable, List<string> types, Expr? props, Direction dir, bool varLength, int? min, int? max)
{
    public string? Variable = variable;
    public readonly List<string> Types = types;
    public readonly Expr? Props = props;
    public readonly Direction Dir = dir;
    public readonly bool VarLength = varLength;
    public readonly int? Min = min;
    public readonly int? Max = max;
}

internal enum ShortestKind { None, Shortest, AllShortest }

internal sealed class PatternPart(string? pathVar, List<NodePattern> nodes, List<RelPattern> rels, ShortestKind shortest)
{
    public readonly string? PathVariable = pathVar;
    public readonly List<NodePattern> Nodes = nodes;
    public readonly List<RelPattern> Rels = rels;
    public readonly ShortestKind Shortest = shortest;
}

// ============================================================ clauses

internal abstract class Clause;

internal sealed class MatchClause(bool optional, List<PatternPart> pattern, Expr? where) : Clause
{
    public readonly bool Optional = optional;
    public readonly List<PatternPart> Pattern = pattern;
    public readonly Expr? Where = where;
}

internal sealed class UnwindClause(Expr list, string variable) : Clause
{
    public readonly Expr List = list;
    public readonly string Variable = variable;
}

internal sealed class ReturnItem(Expr expr, string? alias, string name)
{
    public readonly Expr Expr = expr;
    public readonly string? Alias = alias;
    /// <summary>Column name: alias or source text.</summary>
    public readonly string Name = name;
}

internal sealed class SortItem(Expr expr, bool descending)
{
    public readonly Expr Expr = expr;
    public readonly bool Descending = descending;
}

internal sealed class Projection
{
    public bool Distinct;
    public bool Star;
    public List<ReturnItem> Items = new();
    public List<SortItem>? OrderBy;
    public Expr? Skip;
    public Expr? Limit;
}

internal sealed class WithClause(Projection proj, Expr? where) : Clause
{
    public readonly Projection Projection = proj;
    public readonly Expr? Where = where;
}

internal sealed class ReturnClause(Projection proj) : Clause
{
    public readonly Projection Projection = proj;
}

internal sealed class CreateClause(List<PatternPart> pattern) : Clause
{
    public readonly List<PatternPart> Pattern = pattern;
}

internal enum SetKind { Property, ReplaceMap, MergeMap, Labels }

internal sealed class SetItem(SetKind kind, Expr target, string? key, Expr? value, List<string>? labels)
{
    public readonly SetKind Kind = kind;
    /// <summary>Entity expression (variable or any expression).</summary>
    public readonly Expr Target = target;
    public readonly string? Key = key;
    public readonly Expr? Value = value;
    public readonly List<string>? Labels = labels;
}

internal sealed class MergeClause(PatternPart part, List<SetItem> onCreate, List<SetItem> onMatch) : Clause
{
    public readonly PatternPart Part = part;
    public readonly List<SetItem> OnCreate = onCreate;
    public readonly List<SetItem> OnMatch = onMatch;
}

internal sealed class SetClause(List<SetItem> items) : Clause
{
    public readonly List<SetItem> Items = items;
}

internal sealed class RemoveClause(List<SetItem> items) : Clause
{
    public readonly List<SetItem> Items = items; // Property (Value null) or Labels
}

internal sealed class DeleteClause(List<Expr> exprs, bool detach) : Clause
{
    public readonly List<Expr> Exprs = exprs;
    public readonly bool Detach = detach;
}

internal sealed class CallClause(string proc, List<Expr>? args, List<(string Field, string Alias)>? yields, Expr? where) : Clause
{
    public readonly string Procedure = proc;
    public readonly List<Expr>? Args = args;
    public readonly List<(string Field, string Alias)>? Yields = yields;
    public readonly Expr? Where = where;
}

internal sealed class SubqueryClause(QueryStatement query, List<string>? imports, bool importAll, bool inTransactions, Expr? batchSize) : Clause
{
    public readonly QueryStatement Query = query;
    /// <summary>Variables imported with the scope clause CALL (a, b) { ... }; null for the legacy form.</summary>
    public readonly List<string>? Imports = imports;
    public readonly bool ImportAll = importAll;
    public readonly bool InTransactions = inTransactions;
    public readonly Expr? BatchSize = batchSize;
}

internal sealed class LoadCsvClause(bool headers, Expr url, string variable, string? fieldTerminator) : Clause
{
    public readonly bool Headers = headers;
    public readonly Expr Url = url;
    public readonly string Variable = variable;
    public readonly string? FieldTerminator = fieldTerminator;
}

internal sealed class ForeachClause(string variable, Expr list, List<Clause> body) : Clause
{
    public readonly string Variable = variable;
    public readonly Expr List = list;
    public readonly List<Clause> Body = body;
}

// ============================================================ statements

internal abstract class Statement
{
    public bool Explain;
}

internal sealed class QueryStatement(List<List<Clause>> parts, List<bool> unionAll) : Statement
{
    /// <summary>Single queries combined by UNION.</summary>
    public readonly List<List<Clause>> Parts = parts;
    public readonly List<bool> UnionAll = unionAll;
}

internal sealed class CreateIndexStatement(string? name, string label, string property, bool ifNotExists, bool unique) : Statement
{
    public readonly string? Name = name;
    public readonly string Label = label;
    public readonly string Property = property;
    public readonly bool IfNotExists = ifNotExists;
    public readonly bool Unique = unique;
}

internal sealed class DropIndexStatement(string name, bool ifExists) : Statement
{
    public readonly string Name = name;
    public readonly bool IfExists = ifExists;
}
