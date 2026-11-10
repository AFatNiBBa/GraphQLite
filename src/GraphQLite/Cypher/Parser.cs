namespace GraphQLite.Cypher;

/// <summary>Recursive-descent parser for openCypher.</summary>
internal sealed class Parser
{
    private readonly string _src;
    private readonly List<Tok> _toks;
    private int _p;
    private int _anon;

    private Parser(string src)
    {
        _src = src;
        _toks = Lexer.Tokenize(src);
    }

    public static Statement Parse(string text)
    {
        var p = new Parser(text);
        var st = p.ParseStatement();
        return st;
    }

    // ------------------------------------------------------------------ token helpers

    private Tok Cur => _toks[_p];
    private Tok PeekTok(int n = 1) => _toks[Math.Min(_p + n, _toks.Count - 1)];
    private Tok Prev => _toks[_p - 1];
    private Tok Advance() => _toks[_p++];
    private bool Is(TokKind k) => Cur.Kind == k;
    private bool IsKw(string kw) => Cur.IsKeyword(kw);
    private bool IsKw(int offset, string kw) => PeekTok(offset).IsKeyword(kw);

    private bool Accept(TokKind k)
    {
        if (Cur.Kind != k) return false;
        _p++;
        return true;
    }

    private bool AcceptKw(string kw)
    {
        if (!Cur.IsKeyword(kw)) return false;
        _p++;
        return true;
    }

    private Tok Expect(TokKind k, string what)
    {
        if (Cur.Kind != k) throw Error($"Expected {what} but found {Cur}");
        return Advance();
    }

    private void ExpectKw(string kw)
    {
        if (!AcceptKw(kw)) throw Error($"Expected {kw} but found {Cur}");
    }

    private CypherSyntaxException Error(string msg) => new(msg, Cur.Pos);

    private string ExpectName(string what = "identifier")
    {
        if (Cur.Kind != TokKind.Ident) throw Error($"Expected {what} but found {Cur}");
        return Advance().Text;
    }

    private string SchemaName()
    {
        // label / type / property names may be keywords
        if (Cur.Kind == TokKind.Ident) return Advance().Text;
        throw Error($"Expected a name but found {Cur}");
    }

    private T Mark<T>(int startTok, T e) where T : Expr
    {
        int start = _toks[startTok].Pos;
        int end = _toks[Math.Max(_p - 1, startTok)].End;
        e.Text = _src[start..end];
        return e;
    }

    private string AnonName() => "  anon_" + (_anon++);

    // ------------------------------------------------------------------ statements

    private Statement ParseStatement()
    {
        bool explain = false;
        if (AcceptKw("EXPLAIN")) explain = true;
        else AcceptKw("PROFILE");

        Statement st;
        if (IsKw("CREATE") && (IsKw(1, "INDEX") || IsKw(1, "CONSTRAINT")) && PeekTok(2).Kind != TokKind.Eq)
            st = ParseCreateSchema();
        else if (IsKw("DROP") && (IsKw(1, "INDEX") || IsKw(1, "CONSTRAINT")))
        {
            Advance();
            Advance();
            var name = ExpectName("index name");
            bool ifExists = false;
            if (AcceptKw("IF")) { ExpectKw("EXISTS"); ifExists = true; }
            st = new DropIndexStatement(name, ifExists);
        }
        else st = ParseQuery();

        Accept(TokKind.Semicolon);
        if (!Is(TokKind.Eof)) throw Error($"Unexpected {Cur}");
        st.Explain = explain;
        return st;
    }

    private Statement ParseCreateSchema()
    {
        ExpectKw("CREATE");
        bool constraint = AcceptKw("CONSTRAINT");
        if (!constraint) ExpectKw("INDEX");
        string? name = null;
        if (Is(TokKind.Ident) && !IsKw("IF") && !IsKw("FOR") && !IsKw("ON")) name = Advance().Text;
        bool ifNotExists = false;
        if (AcceptKw("IF")) { ExpectKw("NOT"); ExpectKw("EXISTS"); ifNotExists = true; }

        if (!constraint && AcceptKw("ON"))
        {
            // legacy: CREATE INDEX ON :Label(prop)
            Expect(TokKind.Colon, "':'");
            var lbl = SchemaName();
            Expect(TokKind.LParen, "'('");
            var prop = SchemaName();
            Expect(TokKind.RParen, "')'");
            return new CreateIndexStatement(name, lbl, prop, ifNotExists, false);
        }
        bool legacyConstraint = false;
        if (constraint && AcceptKw("ON")) legacyConstraint = true;
        else ExpectKw("FOR");
        Expect(TokKind.LParen, "'('");
        var v = ExpectName();
        Expect(TokKind.Colon, "':'");
        var label = SchemaName();
        Expect(TokKind.RParen, "')'");
        string property;
        if (!constraint)
        {
            ExpectKw("ON");
            Expect(TokKind.LParen, "'('");
            if (ExpectName() != v) throw Error("Index property must reference the pattern variable");
            Expect(TokKind.Dot, "'.'");
            property = SchemaName();
            Expect(TokKind.RParen, "')'");
            return new CreateIndexStatement(name, label, property, ifNotExists, false);
        }
        if (legacyConstraint) ExpectKw("ASSERT"); else ExpectKw("REQUIRE");
        bool paren = Accept(TokKind.LParen);
        if (ExpectName() != v) throw Error("Constraint property must reference the pattern variable");
        Expect(TokKind.Dot, "'.'");
        property = SchemaName();
        if (paren) Expect(TokKind.RParen, "')'");
        ExpectKw("IS");
        if (!AcceptKw("UNIQUE"))
        {
            ExpectKw("NODE");
            ExpectKw("KEY");
        }
        return new CreateIndexStatement(name, label, property, ifNotExists, true);
    }

    private QueryStatement ParseQuery()
    {
        var parts = new List<List<Clause>> { ParseSingleQuery() };
        var all = new List<bool>();
        while (AcceptKw("UNION"))
        {
            all.Add(AcceptKw("ALL"));
            parts.Add(ParseSingleQuery());
        }
        return new QueryStatement(parts, all);
    }

    private List<Clause> ParseSingleQuery()
    {
        var clauses = new List<Clause>();
        while (true)
        {
            var c = TryParseClause();
            if (c is null) break;
            clauses.Add(c);
        }
        if (clauses.Count == 0) throw Error($"Expected a clause but found {Cur}");
        return clauses;
    }

    private Clause? TryParseClause()
    {
        if (Cur.Kind != TokKind.Ident) return null;
        if (IsKw("OPTIONAL"))
        {
            Advance();
            ExpectKw("MATCH");
            return ParseMatch(true);
        }
        if (AcceptKw("MATCH")) return ParseMatch(false);
        if (AcceptKw("UNWIND"))
        {
            var e = ParseExpr();
            ExpectKw("AS");
            return new UnwindClause(e, ExpectName());
        }
        if (AcceptKw("WITH"))
        {
            var proj = ParseProjection();
            Expr? where = AcceptKw("WHERE") ? ParseExpr() : null;
            return new WithClause(proj, where);
        }
        if (AcceptKw("RETURN")) return new ReturnClause(ParseProjection());
        if (AcceptKw("CREATE")) return new CreateClause(ParsePattern());
        if (AcceptKw("MERGE")) return ParseMerge();
        if (AcceptKw("SET")) return new SetClause(ParseSetItems());
        if (AcceptKw("REMOVE")) return ParseRemove();
        if (IsKw("DETACH"))
        {
            Advance();
            ExpectKw("DELETE");
            return new DeleteClause(ParseExprList(), true);
        }
        if (AcceptKw("DELETE")) return new DeleteClause(ParseExprList(), false);
        if (AcceptKw("CALL")) return Is(TokKind.LBrace) || Is(TokKind.LParen) ? ParseSubquery() : ParseCall();
        if (IsKw("LOAD") && IsKw(1, "CSV"))
        {
            Advance();
            Advance();
            bool headers = false;
            if (AcceptKw("WITH"))
            {
                ExpectKw("HEADERS");
                headers = true;
            }
            ExpectKw("FROM");
            var url = ParseExpr();
            ExpectKw("AS");
            var v = ExpectName();
            string? ft = null;
            if (AcceptKw("FIELDTERMINATOR")) ft = Expect(TokKind.String, "string").Text;
            return new LoadCsvClause(headers, url, v, ft);
        }
        if (AcceptKw("FOREACH")) return ParseForeach();
        return null;
    }

    private MatchClause ParseMatch(bool optional)
    {
        var pattern = ParsePattern();
        Expr? where = AcceptKw("WHERE") ? ParseExpr() : null;
        return new MatchClause(optional, pattern, where);
    }

    private MergeClause ParseMerge()
    {
        var part = ParsePatternPart();
        var onCreate = new List<SetItem>();
        var onMatch = new List<SetItem>();
        while (IsKw("ON"))
        {
            Advance();
            if (AcceptKw("CREATE")) { ExpectKw("SET"); onCreate.AddRange(ParseSetItems()); }
            else if (AcceptKw("MATCH")) { ExpectKw("SET"); onMatch.AddRange(ParseSetItems()); }
            else throw Error("Expected CREATE or MATCH after ON");
        }
        return new MergeClause(part, onCreate, onMatch);
    }

    private List<SetItem> ParseSetItems()
    {
        var items = new List<SetItem>();
        do
        {
            int start = _p;
            var target = ParsePostfix();
            if (target is LabelCheckExpr lc)
            {
                if (lc.Expression != null) throw Error("Only plain labels can be set");
                items.Add(new SetItem(SetKind.Labels, lc.Target, null, null, lc.Labels));
                continue;
            }
            if (target is PropertyExpr pe)
            {
                Expect(TokKind.Eq, "'='");
                items.Add(new SetItem(SetKind.Property, pe.Target, pe.Key, ParseExpr(), null));
                continue;
            }
            if (Accept(TokKind.Eq)) { items.Add(new SetItem(SetKind.ReplaceMap, target, null, ParseExpr(), null)); continue; }
            if (Accept(TokKind.PlusEq)) { items.Add(new SetItem(SetKind.MergeMap, target, null, ParseExpr(), null)); continue; }
            _p = start;
            throw Error("Invalid SET item");
        } while (Accept(TokKind.Comma));
        return items;
    }

    private RemoveClause ParseRemove()
    {
        var items = new List<SetItem>();
        do
        {
            var target = ParsePostfix();
            if (target is LabelCheckExpr { Expression: null } lc) items.Add(new SetItem(SetKind.Labels, lc.Target, null, null, lc.Labels));
            else if (target is PropertyExpr pe) items.Add(new SetItem(SetKind.Property, pe.Target, pe.Key, null, null));
            else throw Error("Invalid REMOVE item");
        } while (Accept(TokKind.Comma));
        return new RemoveClause(items);
    }

    private CallClause ParseCall()
    {
        var name = ExpectName("procedure name");
        while (Accept(TokKind.Dot)) name += "." + ExpectName();
        List<Expr>? args = null;
        if (Accept(TokKind.LParen))
        {
            args = new List<Expr>();
            if (!Is(TokKind.RParen)) args = ParseExprList();
            Expect(TokKind.RParen, "')'");
        }
        List<(string, string)>? yields = null;
        Expr? where = null;
        if (AcceptKw("YIELD"))
        {
            if (Accept(TokKind.Star)) yields = null;
            else
            {
                yields = new List<(string, string)>();
                do
                {
                    var f = ExpectName();
                    var alias = AcceptKw("AS") ? ExpectName() : f;
                    yields.Add((f, alias));
                } while (Accept(TokKind.Comma));
            }
            if (AcceptKw("WHERE")) where = ParseExpr();
        }
        return new CallClause(name.ToLowerInvariant(), args, yields, where);
    }

    private SubqueryClause ParseSubquery()
    {
        List<string>? imports = null;
        bool all = false;
        if (Accept(TokKind.LParen))
        {
            imports = new List<string>();
            if (Accept(TokKind.Star)) all = true;
            else if (!Is(TokKind.RParen))
            {
                do imports.Add(ExpectName()); while (Accept(TokKind.Comma));
            }
            Expect(TokKind.RParen, "')'");
        }
        Expect(TokKind.LBrace, "'{'");
        var q = ParseQuery();
        Expect(TokKind.RBrace, "'}'");
        bool inTx = false;
        Expr? batch = null;
        if (IsKw("IN") && IsKw(1, "TRANSACTIONS"))
        {
            Advance();
            Advance();
            inTx = true;
            if (AcceptKw("OF"))
            {
                batch = ParseExpr();
                if (!AcceptKw("ROWS")) AcceptKw("ROW");
            }
        }
        return new SubqueryClause(q, imports, all, inTx, batch);
    }

    private ForeachClause ParseForeach()
    {
        Expect(TokKind.LParen, "'('");
        var v = ExpectName();
        ExpectKw("IN");
        var list = ParseExpr();
        Expect(TokKind.Pipe, "'|'");
        var body = new List<Clause>();
        while (true)
        {
            var c = TryParseClause();
            if (c is null) break;
            if (c is not (CreateClause or MergeClause or SetClause or RemoveClause or DeleteClause or ForeachClause))
                throw Error("Only updating clauses are allowed inside FOREACH");
            body.Add(c);
        }
        Expect(TokKind.RParen, "')'");
        return new ForeachClause(v, list, body);
    }

    private Projection ParseProjection()
    {
        var proj = new Projection { Distinct = AcceptKw("DISTINCT") };
        if (Accept(TokKind.Star))
        {
            proj.Star = true;
            if (!Accept(TokKind.Comma)) goto tail;
        }
        do
        {
            var e = ParseExpr();
            string? alias = AcceptKw("AS") ? ExpectName("alias") : null;
            proj.Items.Add(new ReturnItem(e, alias, alias ?? e.Text));
        } while (Accept(TokKind.Comma));
    tail:
        if (IsKw("ORDER"))
        {
            Advance();
            ExpectKw("BY");
            proj.OrderBy = new List<SortItem>();
            do
            {
                var e = ParseExpr();
                bool desc = false;
                if (AcceptKw("DESC") || AcceptKw("DESCENDING")) desc = true;
                else if (!AcceptKw("ASC")) AcceptKw("ASCENDING");
                proj.OrderBy.Add(new SortItem(e, desc));
            } while (Accept(TokKind.Comma));
        }
        if (AcceptKw("SKIP") || AcceptKw("OFFSET")) proj.Skip = ParseExpr();
        if (AcceptKw("LIMIT")) proj.Limit = ParseExpr();
        return proj;
    }

    private List<Expr> ParseExprList()
    {
        var list = new List<Expr> { ParseExpr() };
        while (Accept(TokKind.Comma)) list.Add(ParseExpr());
        return list;
    }

    // ------------------------------------------------------------------ patterns

    private List<PatternPart> ParsePattern()
    {
        var parts = new List<PatternPart> { ParsePatternPart() };
        while (Accept(TokKind.Comma)) parts.Add(ParsePatternPart());
        return parts;
    }

    private PatternPart ParsePatternPart()
    {
        string? pathVar = null;
        if (Is(TokKind.Ident) && PeekTok().Kind == TokKind.Eq)
        {
            pathVar = Advance().Text;
            Advance();
        }
        var shortest = ShortestKind.None;
        if (Is(TokKind.Ident) && PeekTok().Kind == TokKind.LParen)
        {
            if (IsKw("shortestPath")) shortest = ShortestKind.Shortest;
            else if (IsKw("allShortestPaths")) shortest = ShortestKind.AllShortest;
            else throw Error($"Unexpected {Cur} in pattern");
            Advance();
            Advance();
        }
        var nodes = new List<NodePattern> { ParseNodePattern() };
        var rels = new List<RelPattern>();
        while (IsRelStart())
        {
            rels.Add(ParseRelPattern());
            nodes.Add(ParseNodePattern());
        }
        if (shortest != ShortestKind.None)
        {
            Expect(TokKind.RParen, "')'");
            if (rels.Count != 1) throw Error("shortestPath requires a pattern with exactly one relationship");
        }
        return new PatternPart(pathVar, nodes, rels, shortest);
    }

    private bool IsRelStart() =>
        Is(TokKind.Minus) || (Is(TokKind.Lt) && PeekTok().Kind == TokKind.Minus);

    private NodePattern ParseNodePattern()
    {
        Expect(TokKind.LParen, "'('");
        string? v = null;
        if (Is(TokKind.Ident)) v = Advance().Text;
        var labels = new List<string>();
        List<string>? any = null;
        LabelExpression? residual = null;
        if (Accept(TokKind.Colon))
        {
            int saved = _noPipe;
            _noPipe = 0;
            var le = ParseLabelOr();
            _noPipe = saved;
            foreach (var term in le.Conjuncts)
            {
                if (term is LabelName n) { if (!labels.Contains(n.Name)) labels.Add(n.Name); }
                else if (any is null && term is LabelOr o && o.Names() is { } names) any = names;
                else residual = residual is null ? term : new LabelAnd(residual, term);
            }
        }
        Expr? props = null;
        if (Is(TokKind.LBrace)) props = ParseMapLiteral();
        else if (Is(TokKind.Param)) { int s = _p; var t = Advance(); props = Mark(s, new ParamExpr(t.Text)); }
        Expect(TokKind.RParen, "')'");
        return new NodePattern(v, labels, props) { AnyLabels = any, Residual = residual };
    }

    // ------------------------------------------------------------------ label expressions

    /// <summary>When &gt; 0 a '|' ends a label expression (inside list comprehensions '|' starts the projection).</summary>
    private int _noPipe;

    private bool IsLabelAtomStart(Tok t) => t.Kind is TokKind.Ident or TokKind.LParen or TokKind.Bang or TokKind.Percent;

    private LabelExpression ParseLabelOr()
    {
        var l = ParseLabelAnd();
        while (_noPipe == 0 && Is(TokKind.Pipe) && IsLabelAtomStart(PeekTok()) || Is(TokKind.Pipe) && PeekTok().Kind == TokKind.Colon && _noPipe == 0)
        {
            Advance();
            Accept(TokKind.Colon);
            l = new LabelOr(l, ParseLabelAnd());
        }
        return l;
    }

    private LabelExpression ParseLabelAnd()
    {
        var l = ParseLabelNot();
        while ((Is(TokKind.Amp) || Is(TokKind.Colon)) && IsLabelAtomStart(PeekTok()))
        {
            Advance();
            l = new LabelAnd(l, ParseLabelNot());
        }
        return l;
    }

    private LabelExpression ParseLabelNot()
    {
        if (Accept(TokKind.Bang)) return new LabelNot(ParseLabelNot());
        if (Accept(TokKind.Percent)) return new LabelWildcard();
        if (Accept(TokKind.LParen))
        {
            int saved = _noPipe;
            _noPipe = 0;
            var e = ParseLabelOr();
            _noPipe = saved;
            Expect(TokKind.RParen, "')'");
            return e;
        }
        return new LabelName(SchemaName());
    }

    private RelPattern ParseRelPattern()
    {
        bool left = false, right = false;
        if (Accept(TokKind.Lt)) left = true;
        Expect(TokKind.Minus, "'-'");
        string? v = null;
        var types = new List<string>();
        Expr? props = null;
        bool varLength = false;
        int? min = null, max = null;
        if (Accept(TokKind.LBracket))
        {
            if (Is(TokKind.Ident)) v = Advance().Text;
            if (Accept(TokKind.Colon))
            {
                types.Add(SchemaName());
                while (Accept(TokKind.Pipe))
                {
                    Accept(TokKind.Colon);
                    types.Add(SchemaName());
                }
            }
            if (Accept(TokKind.Star))
            {
                varLength = true;
                if (Is(TokKind.Integer)) min = (int)Advance().IntValue;
                if (Accept(TokKind.DotDot))
                {
                    if (Is(TokKind.Integer)) max = (int)Advance().IntValue;
                }
                else if (min.HasValue) max = min;
            }
            if (Is(TokKind.LBrace)) props = ParseMapLiteral();
            else if (Is(TokKind.Param)) { int s = _p; var t = Advance(); props = Mark(s, new ParamExpr(t.Text)); }
            Expect(TokKind.RBracket, "']'");
        }
        Expect(TokKind.Minus, "'-'");
        if (Accept(TokKind.Gt)) right = true;
        var dir = left == right ? Direction.Both : (right ? Direction.Out : Direction.In);
        return new RelPattern(v, types, props, dir, varLength, min, max);
    }

    // ------------------------------------------------------------------ expressions

    public Expr ParseExpr() => ParseOr();

    private Expr ParseOr()
    {
        int s = _p;
        var e = ParseXor();
        while (AcceptKw("OR")) e = Mark(s, new BinaryExpr(BinOp.Or, e, ParseXor()));
        return e;
    }

    private Expr ParseXor()
    {
        int s = _p;
        var e = ParseAnd();
        while (AcceptKw("XOR")) e = Mark(s, new BinaryExpr(BinOp.Xor, e, ParseAnd()));
        return e;
    }

    private Expr ParseAnd()
    {
        int s = _p;
        var e = ParseNot();
        while (AcceptKw("AND")) e = Mark(s, new BinaryExpr(BinOp.And, e, ParseNot()));
        return e;
    }

    private Expr ParseNot()
    {
        int s = _p;
        if (AcceptKw("NOT")) return Mark(s, new UnaryExpr(UnOp.Not, ParseNot()));
        return ParseComparison();
    }

    private static BinOp? ComparisonOp(TokKind k) => k switch
    {
        TokKind.Eq => BinOp.Eq,
        TokKind.Neq => BinOp.Neq,
        TokKind.Lt => BinOp.Lt,
        TokKind.Gt => BinOp.Gt,
        TokKind.Le => BinOp.Le,
        TokKind.Ge => BinOp.Ge,
        _ => null,
    };

    private Expr ParseComparison()
    {
        int s = _p;
        var left = ParsePredicates();
        Expr? result = null;
        while (ComparisonOp(Cur.Kind) is { } op)
        {
            Advance();
            var right = ParsePredicates();
            var cmp = Mark(s, new BinaryExpr(op, left, right));
            result = result is null ? cmp : Mark(s, new BinaryExpr(BinOp.And, result, cmp));
            left = right;
        }
        return result ?? left;
    }

    private Expr ParsePredicates()
    {
        int s = _p;
        var e = ParseAddSub();
        while (true)
        {
            if (IsKw("IS"))
            {
                Advance();
                bool neg = AcceptKw("NOT");
                ExpectKw("NULL");
                e = Mark(s, new IsNullExpr(e, neg));
            }
            else if (IsKw("STARTS") && IsKw(1, "WITH"))
            {
                Advance(); Advance();
                e = Mark(s, new BinaryExpr(BinOp.StartsWith, e, ParseAddSub()));
            }
            else if (IsKw("ENDS") && IsKw(1, "WITH"))
            {
                Advance(); Advance();
                e = Mark(s, new BinaryExpr(BinOp.EndsWith, e, ParseAddSub()));
            }
            else if (AcceptKw("CONTAINS")) e = Mark(s, new BinaryExpr(BinOp.Contains, e, ParseAddSub()));
            else if (AcceptKw("IN")) e = Mark(s, new BinaryExpr(BinOp.In, e, ParseAddSub()));
            else if (Accept(TokKind.RegexEq)) e = Mark(s, new BinaryExpr(BinOp.Regex, e, ParseAddSub()));
            else return e;
        }
    }

    private Expr ParseAddSub()
    {
        int s = _p;
        var e = ParseMulDiv();
        while (true)
        {
            if (Accept(TokKind.Plus)) e = Mark(s, new BinaryExpr(BinOp.Add, e, ParseMulDiv()));
            else if (Accept(TokKind.Minus)) e = Mark(s, new BinaryExpr(BinOp.Sub, e, ParseMulDiv()));
            else return e;
        }
    }

    private Expr ParseMulDiv()
    {
        int s = _p;
        var e = ParsePow();
        while (true)
        {
            if (Accept(TokKind.Star)) e = Mark(s, new BinaryExpr(BinOp.Mul, e, ParsePow()));
            else if (Accept(TokKind.Slash)) e = Mark(s, new BinaryExpr(BinOp.Div, e, ParsePow()));
            else if (Accept(TokKind.Percent)) e = Mark(s, new BinaryExpr(BinOp.Mod, e, ParsePow()));
            else return e;
        }
    }

    private Expr ParsePow()
    {
        int s = _p;
        var e = ParseUnary();
        while (Accept(TokKind.Caret)) e = Mark(s, new BinaryExpr(BinOp.Pow, e, ParseUnary()));
        return e;
    }

    private Expr ParseUnary()
    {
        int s = _p;
        if (Accept(TokKind.Minus))
        {
            if (Is(TokKind.Integer) && PeekTok().Kind != TokKind.Caret)
            {
                var t = Advance();
                return ParsePostfixOps(s, Mark(s, new LiteralExpr(Value.FromInt(-t.IntValue))));
            }
            if (Is(TokKind.Float) && PeekTok().Kind != TokKind.Caret)
            {
                var t = Advance();
                return ParsePostfixOps(s, Mark(s, new LiteralExpr(Value.FromFloat(-t.FloatValue))));
            }
            return Mark(s, new UnaryExpr(UnOp.Neg, ParseUnary()));
        }
        if (Accept(TokKind.Plus)) return Mark(s, new UnaryExpr(UnOp.Plus, ParseUnary()));
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        int s = _p;
        var e = ParseAtom();
        return ParsePostfixOps(s, e);
    }

    private Expr ParsePostfixOps(int s, Expr e)
    {
        while (true)
        {
            if (Is(TokKind.Dot) && PeekTok().Kind == TokKind.Ident)
            {
                Advance();
                var key = Advance().Text;
                e = Mark(s, new PropertyExpr(e, key));
            }
            else if (Is(TokKind.LBracket))
            {
                Advance();
                if (Accept(TokKind.DotDot))
                {
                    Expr? to = Is(TokKind.RBracket) ? null : ParseExpr();
                    Expect(TokKind.RBracket, "']'");
                    e = Mark(s, new SliceExpr(e, null, to));
                    continue;
                }
                var idx = ParseExpr();
                if (Accept(TokKind.DotDot))
                {
                    Expr? to = Is(TokKind.RBracket) ? null : ParseExpr();
                    Expect(TokKind.RBracket, "']'");
                    e = Mark(s, new SliceExpr(e, idx, to));
                    continue;
                }
                Expect(TokKind.RBracket, "']'");
                e = Mark(s, new IndexExpr(e, idx));
            }
            else if (Is(TokKind.Colon) && IsLabelAtomStart(PeekTok()))
            {
                Advance();
                var le = ParseLabelOr();
                var conj = le.Conjuncts.ToList();
                if (conj.All(x => x is LabelName))
                    e = Mark(s, new LabelCheckExpr(e, conj.Select(x => ((LabelName)x).Name).ToList()));
                else
                    e = Mark(s, new LabelCheckExpr(e, [], le));
            }
            else return e;
        }
    }

    private Expr ParseAtom()
    {
        int s = _p;
        var t = Cur;
        switch (t.Kind)
        {
            case TokKind.Integer: Advance(); return Mark(s, new LiteralExpr(Value.FromInt(t.IntValue)));
            case TokKind.Float: Advance(); return Mark(s, new LiteralExpr(Value.FromFloat(t.FloatValue)));
            case TokKind.String: Advance(); return Mark(s, new LiteralExpr(Value.FromString(t.Text)));
            case TokKind.Param: Advance(); return Mark(s, new ParamExpr(t.Text));
            case TokKind.LBrace: return ParseMapLiteral();
            case TokKind.LBracket: return ParseBracket();
            case TokKind.LParen:
                {
                    var pat = TryParsePatternExpr();
                    if (pat != null) return pat;
                    Advance();
                    var e = ParseExpr();
                    Expect(TokKind.RParen, "')'");
                    return e;
                }
            case TokKind.Ident:
                return ParseIdentAtom();
        }
        throw Error($"Unexpected {t} in expression");
    }

    private Expr? TryParsePatternExpr()
    {
        int save = _p;
        try
        {
            ParseNodePattern();
            if (!IsRelStart()) { _p = save; return null; }
            _p = save;
            var part = ParsePatternPart();
            return Mark(save, new PatternSubqueryExpr(SubqueryKind.Exists, [part], null, null));
        }
        catch (CypherSyntaxException)
        {
            _p = save;
            return null;
        }
    }

    private Expr ParseMapLiteral()
    {
        int s = _p;
        Expect(TokKind.LBrace, "'{'");
        var keys = new List<string>();
        var vals = new List<Expr>();
        if (!Is(TokKind.RBrace))
        {
            do
            {
                if (Cur.Kind is not (TokKind.Ident or TokKind.String)) throw Error($"Expected map key but found {Cur}");
                keys.Add(Advance().Text);
                Expect(TokKind.Colon, "':'");
                vals.Add(ParseExpr());
            } while (Accept(TokKind.Comma));
        }
        Expect(TokKind.RBrace, "'}'");
        return Mark(s, new MapExpr(keys, vals));
    }

    private Expr ParseBracket()
    {
        int s = _p;
        Expect(TokKind.LBracket, "'['");
        // list comprehension: [x IN list WHERE .. | ..]
        if (Is(TokKind.Ident) && IsKw(1, "IN"))
        {
            var v = Advance().Text;
            Advance();
            var list = ParseExpr();
            Expr? where = null;
            if (AcceptKw("WHERE"))
            {
                _noPipe++;
                where = ParseExpr();
                _noPipe--;
            }
            Expr? map = Accept(TokKind.Pipe) ? ParseExpr() : null;
            Expect(TokKind.RBracket, "']'");
            return Mark(s, new ListCompExpr(v, list, where, map));
        }
        // pattern comprehension: [p = (a)-->(b) WHERE .. | expr]
        if (Is(TokKind.LParen) || (Is(TokKind.Ident) && PeekTok().Kind == TokKind.Eq && PeekTok(2).Kind == TokKind.LParen))
        {
            int save = _p;
            try
            {
                var part = ParsePatternPart();
                if (part.Rels.Count > 0 && (IsKw("WHERE") || Is(TokKind.Pipe)))
                {
                    Expr? where = null;
                    if (AcceptKw("WHERE"))
                    {
                        _noPipe++;
                        where = ParseExpr();
                        _noPipe--;
                    }
                    Expect(TokKind.Pipe, "'|'");
                    var map = ParseExpr();
                    Expect(TokKind.RBracket, "']'");
                    return Mark(s, new PatternSubqueryExpr(SubqueryKind.Collect, [part], where, map));
                }
            }
            catch (CypherSyntaxException) { }
            _p = save;
        }
        var items = new List<Expr>();
        if (!Is(TokKind.RBracket)) items = ParseExprList();
        Expect(TokKind.RBracket, "']'");
        return Mark(s, new ListExpr(items));
    }

    private Expr ParseIdentAtom()
    {
        int s = _p;
        var t = Cur;
        if (!t.Escaped)
        {
            if (t.IsKeyword("true")) { Advance(); return Mark(s, new LiteralExpr(Value.True)); }
            if (t.IsKeyword("false")) { Advance(); return Mark(s, new LiteralExpr(Value.False)); }
            if (t.IsKeyword("null")) { Advance(); return Mark(s, new LiteralExpr(Value.Null)); }
            if (t.IsKeyword("CASE")) return ParseCase();
            if ((t.IsKeyword("EXISTS") || t.IsKeyword("COUNT")) && PeekTok().Kind == TokKind.LBrace)
            {
                var kind = t.IsKeyword("EXISTS") ? SubqueryKind.Exists : SubqueryKind.Count;
                Advance();
                Advance();
                AcceptKw("MATCH");
                var pattern = ParsePattern();
                Expr? where = AcceptKw("WHERE") ? ParseExpr() : null;
                Expect(TokKind.RBrace, "'}'");
                return Mark(s, new PatternSubqueryExpr(kind, pattern, where, null));
            }
            if (PeekTok().Kind == TokKind.LParen)
            {
                if (t.IsKeyword("COUNT") && PeekTok(2).Kind == TokKind.Star)
                {
                    Advance(); Advance(); Advance();
                    Expect(TokKind.RParen, "')'");
                    return Mark(s, new CountStarExpr());
                }
                if ((t.IsKeyword("ALL") || t.IsKeyword("ANY") || t.IsKeyword("NONE") || t.IsKeyword("SINGLE"))
                    && PeekTok(2).Kind == TokKind.Ident && IsKw(3, "IN"))
                {
                    var kind = t.Text.ToUpperInvariant() switch
                    {
                        "ALL" => QuantKind.All,
                        "ANY" => QuantKind.Any,
                        "NONE" => QuantKind.None,
                        _ => QuantKind.Single,
                    };
                    Advance(); Advance();
                    var v = Advance().Text;
                    Advance();
                    var list = ParseExpr();
                    ExpectKw("WHERE");
                    var pred = ParseExpr();
                    Expect(TokKind.RParen, "')'");
                    return Mark(s, new QuantifierExpr(kind, v, list, pred));
                }
                if (t.IsKeyword("REDUCE"))
                {
                    Advance(); Advance();
                    var acc = ExpectName();
                    Expect(TokKind.Eq, "'='");
                    var init = ParseExpr();
                    Expect(TokKind.Comma, "','");
                    var v = ExpectName();
                    ExpectKw("IN");
                    var list = ParseExpr();
                    Expect(TokKind.Pipe, "'|'");
                    var body = ParseExpr();
                    Expect(TokKind.RParen, "')'");
                    return Mark(s, new ReduceExpr(acc, init, v, list, body));
                }
                if (t.IsKeyword("EXISTS"))
                {
                    Advance(); Advance();
                    var pat = TryParsePatternExpr();
                    if (pat != null)
                    {
                        Expect(TokKind.RParen, "')'");
                        return Mark(s, new PatternSubqueryExpr(SubqueryKind.Exists, ((PatternSubqueryExpr)pat).Pattern, null, null));
                    }
                    var arg = ParseExpr();
                    Expect(TokKind.RParen, "')'");
                    return Mark(s, new FuncExpr("exists", [arg], false));
                }
            }
        }
        // function call, possibly namespaced: a.b.c(
        int look = 0;
        while (PeekTok(look).Kind == TokKind.Ident && PeekTok(look + 1).Kind == TokKind.Dot) look += 2;
        if (PeekTok(look).Kind == TokKind.Ident && PeekTok(look + 1).Kind == TokKind.LParen)
        {
            var name = Advance().Text;
            while (Accept(TokKind.Dot)) name += "." + Advance().Text;
            Expect(TokKind.LParen, "'('");
            bool distinct = AcceptKw("DISTINCT");
            var args = new List<Expr>();
            if (!Is(TokKind.RParen)) args = ParseExprList();
            Expect(TokKind.RParen, "')'");
            return Mark(s, new FuncExpr(name.ToLowerInvariant(), args, distinct));
        }
        Advance();
        var variable = Mark(s, new VarExpr(t.Text));
        if (Is(TokKind.LBrace)) return ParseMapProjection(s, t.Text);
        return variable;
    }

    private Expr ParseMapProjection(int s, string variable)
    {
        Expect(TokKind.LBrace, "'{'");
        var items = new List<MapProjItem>();
        if (!Is(TokKind.RBrace))
        {
            do
            {
                if (Accept(TokKind.Dot))
                {
                    if (Accept(TokKind.Star)) items.Add(new MapProjItem(MapProjKind.AllProperties, "*", null));
                    else items.Add(new MapProjItem(MapProjKind.Property, SchemaName(), null));
                }
                else
                {
                    var key = ExpectName();
                    if (Accept(TokKind.Colon)) items.Add(new MapProjItem(MapProjKind.Literal, key, ParseExpr()));
                    else items.Add(new MapProjItem(MapProjKind.Variable, key, null));
                }
            } while (Accept(TokKind.Comma));
        }
        Expect(TokKind.RBrace, "'}'");
        return Mark(s, new MapProjectionExpr(variable, items));
    }

    private Expr ParseCase()
    {
        int s = _p;
        ExpectKw("CASE");
        Expr? test = null;
        if (!IsKw("WHEN")) test = ParseExpr();
        var whens = new List<Expr>();
        var thens = new List<Expr>();
        while (AcceptKw("WHEN"))
        {
            whens.Add(ParseExpr());
            ExpectKw("THEN");
            thens.Add(ParseExpr());
        }
        if (whens.Count == 0) throw Error("CASE requires at least one WHEN");
        Expr? @else = AcceptKw("ELSE") ? ParseExpr() : null;
        ExpectKw("END");
        return Mark(s, new CaseExpr(test, whens, thens, @else));
    }
}
