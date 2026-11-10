using System.Globalization;
using System.Text;

namespace GraphDb.Cypher;

internal enum TokKind : byte
{
    Eof,
    Ident,      // identifier or keyword (Text holds the name, Escaped=true for `backtick` identifiers)
    String,
    Integer,
    Float,
    Param,      // $name
    LParen, RParen, LBracket, RBracket, LBrace, RBrace,
    Comma, Dot, DotDot, Colon, Semicolon, Pipe,
    Plus, Minus, Star, Slash, Percent, Caret,
    Eq, Neq, Lt, Gt, Le, Ge, RegexEq, PlusEq,
}

internal readonly struct Tok
{
    public readonly TokKind Kind;
    public readonly string Text;
    public readonly int Pos;
    public readonly int End;
    public readonly bool Escaped;
    public readonly long IntValue;
    public readonly double FloatValue;

    public Tok(TokKind kind, string text, int pos, int end, bool escaped = false, long i = 0, double f = 0)
    {
        Kind = kind;
        Text = text;
        Pos = pos;
        End = end;
        Escaped = escaped;
        IntValue = i;
        FloatValue = f;
    }

    public bool IsKeyword(string kw) => Kind == TokKind.Ident && !Escaped && Text.Equals(kw, StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Kind == TokKind.Eof ? "end of input" : $"'{Text}'";
}

/// <summary>Tokenizer working directly on a <see cref="ReadOnlySpan{T}"/> of the query text.</summary>
internal static class Lexer
{
    public static List<Tok> Tokenize(string text)
    {
        var toks = new List<Tok>(Math.Max(16, text.Length / 3));
        ReadOnlySpan<char> s = text;
        int i = 0;
        while (true)
        {
            // skip whitespace and comments
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    int nl = s[i..].IndexOfAny('\n', '\r');
                    i = nl < 0 ? s.Length : i + nl;
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = s[(i + 2)..].IndexOf("*/");
                    if (end < 0) throw new CypherSyntaxException("Unterminated comment", i);
                    i += end + 4;
                    continue;
                }
                break;
            }
            if (i >= s.Length)
            {
                toks.Add(new Tok(TokKind.Eof, "", i, i));
                return toks;
            }
            int start = i;
            char ch = s[i];
            if (char.IsLetter(ch) || ch == '_')
            {
                i++;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                toks.Add(new Tok(TokKind.Ident, text[start..i], start, i));
                continue;
            }
            if (ch == '`')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= s.Length) throw new CypherSyntaxException("Unterminated escaped identifier", start);
                    if (s[i] == '`')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '`') { sb.Append('`'); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                toks.Add(new Tok(TokKind.Ident, sb.ToString(), start, i, escaped: true));
                continue;
            }
            if (char.IsAsciiDigit(ch))
            {
                toks.Add(LexNumber(text, ref i));
                continue;
            }
            if (ch == '\'' || ch == '"')
            {
                toks.Add(LexString(text, ref i));
                continue;
            }
            if (ch == '$')
            {
                i++;
                if (i < s.Length && s[i] == '`')
                {
                    int e = s[(i + 1)..].IndexOf('`');
                    if (e < 0) throw new CypherSyntaxException("Unterminated parameter name", start);
                    toks.Add(new Tok(TokKind.Param, text.Substring(i + 1, e), start, i + e + 2));
                    i += e + 2;
                    continue;
                }
                int ps = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                if (ps == i) throw new CypherSyntaxException("Expected parameter name after '$'", start);
                toks.Add(new Tok(TokKind.Param, text[ps..i], start, i));
                continue;
            }
            i++;
            TokKind k;
            switch (ch)
            {
                case '(': k = TokKind.LParen; break;
                case ')': k = TokKind.RParen; break;
                case '[': k = TokKind.LBracket; break;
                case ']': k = TokKind.RBracket; break;
                case '{': k = TokKind.LBrace; break;
                case '}': k = TokKind.RBrace; break;
                case ',': k = TokKind.Comma; break;
                case ';': k = TokKind.Semicolon; break;
                case '|': k = TokKind.Pipe; break;
                case ':': k = TokKind.Colon; break;
                case '*': k = TokKind.Star; break;
                case '/': k = TokKind.Slash; break;
                case '%': k = TokKind.Percent; break;
                case '^': k = TokKind.Caret; break;
                case '-': k = TokKind.Minus; break;
                case '.':
                    if (i < s.Length && s[i] == '.') { i++; k = TokKind.DotDot; }
                    else k = TokKind.Dot;
                    break;
                case '+':
                    if (i < s.Length && s[i] == '=') { i++; k = TokKind.PlusEq; }
                    else k = TokKind.Plus;
                    break;
                case '=':
                    if (i < s.Length && s[i] == '~') { i++; k = TokKind.RegexEq; }
                    else k = TokKind.Eq;
                    break;
                case '!':
                    if (i < s.Length && s[i] == '=') { i++; k = TokKind.Neq; }
                    else throw new CypherSyntaxException("Unexpected character '!'", start);
                    break;
                case '<':
                    if (i < s.Length && s[i] == '=') { i++; k = TokKind.Le; }
                    else if (i < s.Length && s[i] == '>') { i++; k = TokKind.Neq; }
                    else k = TokKind.Lt;
                    break;
                case '>':
                    if (i < s.Length && s[i] == '=') { i++; k = TokKind.Ge; }
                    else k = TokKind.Gt;
                    break;
                default:
                    throw new CypherSyntaxException($"Unexpected character '{ch}'", start);
            }
            toks.Add(new Tok(k, text[start..i], start, i));
        }
    }

    private static Tok LexNumber(string text, ref int i)
    {
        int start = i;
        ReadOnlySpan<char> s = text;
        if (s[i] == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
        {
            i += 2;
            int hs = i;
            while (i < s.Length && char.IsAsciiHexDigit(s[i])) i++;
            long hv = long.Parse(s[hs..i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return new Tok(TokKind.Integer, text[start..i], start, i, i: hv);
        }
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        bool isFloat = false;
        if (i + 1 < s.Length && s[i] == '.' && char.IsAsciiDigit(s[i + 1]))
        {
            isFloat = true;
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int save = i;
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            if (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                isFloat = true;
                while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            }
            else i = save;
        }
        var span = s[start..i];
        if (isFloat)
            return new Tok(TokKind.Float, text[start..i], start, i, f: double.Parse(span, NumberStyles.Float, CultureInfo.InvariantCulture));
        if (!long.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            throw new CypherSyntaxException("Integer literal is too large", start);
        return new Tok(TokKind.Integer, text[start..i], start, i, i: v);
    }

    private static Tok LexString(string text, ref int i)
    {
        int start = i;
        char quote = text[i++];
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= text.Length) throw new CypherSyntaxException("Unterminated string literal", start);
            char c = text[i++];
            if (c == quote) break;
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= text.Length) throw new CypherSyntaxException("Unterminated string literal", start);
            char e = text[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '0': sb.Append('\0'); break;
                case '\\': sb.Append('\\'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case 'u':
                    if (i + 4 > text.Length) throw new CypherSyntaxException("Invalid unicode escape", i);
                    sb.Append((char)int.Parse(text.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: sb.Append('\\').Append(e); break;
            }
        }
        return new Tok(TokKind.String, sb.ToString(), start, i);
    }
}
