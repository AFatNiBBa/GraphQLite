using System.Text;

namespace GraphDb.Runtime;

/// <summary>LOAD CSV [WITH HEADERS] FROM url AS row [FIELDTERMINATOR c]: streams records from a file (or http(s) when enabled).</summary>
internal sealed class LoadCsvOp(Operator input, Eval url, int slot, bool headers, char separator) : UnaryOperator(input)
{
    public override string Name => headers ? "LoadCSV(WITH HEADERS)" : "LoadCSV";
    public override Cursor Open(ExecContext ctx) => new C(Input.Open(ctx), ctx, url, slot, headers, separator);

    internal static TextReader OpenSource(ExecContext ctx, string url)
    {
        var o = ctx.Options;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!o.AllowRemoteCsv) throw new GraphDbException("Remote LOAD CSV is disabled (DatabaseOptions.AllowRemoteCsv)");
            var http = new HttpClient();
            var stream = http.GetStreamAsync(url, ctx.Cancellation).GetAwaiter().GetResult();
            return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        }
        string path = url;
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            path = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : path[5..].TrimStart('/');
            if (OperatingSystem.IsWindows() && path.Length > 2 && path[0] == '/' && path[2] == ':') path = path[1..];
        }
        if (o.CsvImportDirectory != null)
        {
            var root = System.IO.Path.GetFullPath(o.CsvImportDirectory);
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path.TrimStart('/', '\\')));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new GraphDbException($"LOAD CSV path '{url}' is outside the import directory");
            path = full;
        }
        if (!File.Exists(path)) throw new GraphDbException($"Couldn't load the external resource at: {url}");
        return new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
    }

    /// <summary>Reads one RFC 4180 record (quoted fields, doubled quotes, embedded newlines). Null at EOF.</summary>
    internal static List<(string Text, bool Quoted)>? ReadRecord(TextReader r, char sep, StringBuilder sb)
    {
        int c = r.Read();
        if (c < 0) return null;
        var fields = new List<(string, bool)>();
        while (true)
        {
            sb.Clear();
            bool quoted = false;
            if (c == '"')
            {
                quoted = true;
                while (true)
                {
                    c = r.Read();
                    if (c < 0) break;
                    if (c == '"')
                    {
                        if (r.Peek() == '"')
                        {
                            r.Read();
                            sb.Append('"');
                            continue;
                        }
                        c = r.Read();
                        break;
                    }
                    sb.Append((char)c);
                }
                // skip garbage until the separator / end of line
                while (c >= 0 && c != sep && c != '\n' && c != '\r') c = r.Read();
            }
            else
            {
                while (c >= 0 && c != sep && c != '\n' && c != '\r')
                {
                    sb.Append((char)c);
                    c = r.Read();
                }
            }
            fields.Add((sb.ToString(), quoted));
            if (c == sep)
            {
                c = r.Read();
                continue;
            }
            if (c == '\r' && r.Peek() == '\n') r.Read();
            return fields;
        }
    }

    private sealed class C(Cursor input, ExecContext ctx, Eval url, int slot, bool headers, char sep) : Cursor
    {
        private TextReader? _reader;
        private string[]? _header;
        private readonly StringBuilder _sb = new();

        public override bool Next()
        {
            while (true)
            {
                if (_reader != null)
                {
                    var rec = ReadRecord(_reader, sep, _sb);
                    if (rec is null)
                    {
                        _reader.Dispose();
                        _reader = null;
                        continue;
                    }
                    if (rec.Count == 1 && rec[0].Text.Length == 0 && !rec[0].Quoted) continue; // blank line
                    ctx.CheckCancel();
                    if (!headers)
                    {
                        var arr = new Value[rec.Count];
                        for (int i = 0; i < arr.Length; i++) arr[i] = Value.FromString(rec[i].Text);
                        ctx.Row[slot] = Value.FromList(arr);
                        return true;
                    }
                    if (_header is null)
                    {
                        _header = rec.Select(f => f.Text).ToArray();
                        continue;
                    }
                    var map = new Dictionary<string, Value>(_header.Length);
                    for (int i = 0; i < _header.Length; i++)
                    {
                        // missing or empty unquoted fields are null (as in Neo4j)
                        map[_header[i]] = i < rec.Count && (rec[i].Text.Length > 0 || rec[i].Quoted) ? Value.FromString(rec[i].Text) : Value.Null;
                    }
                    ctx.Row[slot] = Value.FromMap(map);
                    return true;
                }
                if (!input.Next()) return false;
                var u = url(ctx);
                if (u.Kind != ValueKind.String) throw new CypherRuntimeException("LOAD CSV requires a string url");
                _reader = OpenSource(ctx, u.Str);
                _header = null;
            }
        }
    }
}
