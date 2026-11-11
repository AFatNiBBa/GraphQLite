using System.Text.RegularExpressions;
using GraphQLite.Runtime;

namespace GraphQLite.Server.Bolt;

/// <summary>
/// Administration queries that Neo4j answers from its <c>system</c> database and that tools such as Neo4j Browser
/// send right after connecting (server version, databases, current user, configuration, procedure list).
/// They are answered by the server, which knows the served database name and the authentication settings.
/// </summary>
internal static partial class SystemQueries
{
    public sealed record Result(string[] Columns, List<Value[]> Rows);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^show\s+(?:(?<all>databases)|database\s+`?(?<one>[^`\s]+)`?|(?<def>default|home)\s+database)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShowDatabases();

    [GeneratedRegex(@"^show\s+(?:all\s+|built\s+in\s+|user\s+defined\s+)?(?<what>procedures|functions)\b(?:.*?\byield\s+(?<yield>.+?))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowRoutines();

    /// <summary>Answers <paramref name="query"/> if it is one of the system queries, else returns null.</summary>
    public static Result? TryAnswer(string query, BoltServer server, string? user, string address)
    {
        var q = Spaces().Replace(query.Trim().TrimEnd(';').Trim(), " ");
        var lower = q.ToLowerInvariant();
        var o = server.Options;
        string version = o.ServerAgent.Split('/').Last();

        if (lower.StartsWith("call dbms.components()", StringComparison.Ordinal))
            return Rows(["name", "versions", "edition"], [S("Neo4j Kernel"), L(S(version)), S("community")]);

        if (ShowDatabases().Match(q) is { Success: true } m)
        {
            var rows = new List<Value[]>();
            foreach (var (name, isDefault) in new[] { (o.DatabaseName, true), ("system", false) })
            {
                if (m.Groups["one"].Success && !name.Equals(m.Groups["one"].Value, StringComparison.OrdinalIgnoreCase)) continue;
                if (m.Groups["def"].Success && !isDefault) continue;
                rows.Add(
                [
                    S(name), S(isDefault ? "standard" : "system"), L(), S("read-write"), S(address), S("primary"), Value.FromBool(true),
                    S("online"), S("online"), S(""), Value.FromBool(isDefault), Value.FromBool(isDefault), L(),
                ]);
            }
            return new Result(
                ["name", "type", "aliases", "access", "address", "role", "writer", "requestedStatus", "currentStatus", "statusMessage",
                    "default", "home", "constituents"], rows);
        }

        if (lower.StartsWith("call dbms.showcurrentuser()", StringComparison.Ordinal))
            return Rows(["username", "roles", "flags"], [S(user ?? "neo4j"), L(S("admin")), L()]);

        if (lower.StartsWith("show current user", StringComparison.Ordinal))
            return Rows(["user", "roles", "passwordChangeRequired", "suspended", "home"],
                [S(user ?? "neo4j"), L(S("admin")), Value.FromBool(false), Value.FromBool(false), S(o.DatabaseName)]);

        if (lower.StartsWith("call dbms.clientconfig()", StringComparison.Ordinal) || lower.StartsWith("call dbms.listconfig(", StringComparison.Ordinal))
        {
            var settings = new (string, Value)[]
            {
                ("browser.allow_outgoing_connections", Value.FromBool(true)),
                ("browser.credential_timeout", S("0s")),
                ("browser.post_connect_cmd", S("")),
                ("browser.remote_content_hostname_whitelist", S("guides.neo4j.com,localhost")),
                ("browser.retain_connection_credentials", Value.FromBool(true)),
                ("browser.retain_editor_history", Value.FromBool(true)),
                ("client.allow_telemetry", Value.FromBool(false)),
                ("dbms.security.auth_enabled", Value.FromBool(o.Username != null)),
            };
            return new Result(["name", "description", "value", "dynamic"],
                settings.Select(s => new[] { S(s.Item1), S(""), s.Item2, Value.FromBool(false) }).ToList());
        }

        if (ShowRoutines().Match(q) is { Success: true } r)
        {
            bool procedures = r.Groups["what"].Value.Equals("procedures", StringComparison.OrdinalIgnoreCase);
            var all = new[] { "name", "description", "mode", "worksOnSystem", "signature", "category", "isBuiltIn" };
            var cols = all;
            if (r.Groups["yield"].Success && r.Groups["yield"].Value.Trim() != "*")
            {
                cols = r.Groups["yield"].Value.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0)
                    .Select(c => all.FirstOrDefault(a => a.Equals(c, StringComparison.OrdinalIgnoreCase)) ?? c).ToArray();
            }
            var rows = new List<Value[]>();
            if (procedures)
            {
                foreach (var p in Procedures.List.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    var values = new Dictionary<string, Value>
                    {
                        ["name"] = S(p.Name),
                        ["description"] = S(""),
                        ["mode"] = S("READ"),
                        ["worksOnSystem"] = Value.FromBool(false),
                        ["signature"] = S($"{p.Name}() :: ({string.Join(", ", p.Outputs.Select(x => x + " :: ANY"))})"),
                        ["category"] = S(""),
                        ["isBuiltIn"] = Value.FromBool(true),
                    };
                    rows.Add(cols.Select(c => values.GetValueOrDefault(c)).ToArray());
                }
            }
            return new Result(cols, rows);
        }

        if (lower.StartsWith("call dbms.info()", StringComparison.Ordinal))
            return Rows(["id", "name", "creationDate"], [S("graphqlite"), S(o.DatabaseName), S("")]);

        if (ShowSchema().Match(q) is { Success: true } sm)
        {
            string type = sm.Groups["type"].Success ? sm.Groups["type"].Value.ToUpperInvariant() : "ALL";
            string? yield = sm.Groups["yield"].Success ? sm.Groups["yield"].Value : null;
            bool indexes = sm.Groups["what"].Value.StartsWith("index", StringComparison.OrdinalIgnoreCase);
            var rows = new List<Dictionary<string, Value>>();
            using (var tx = server.Database.BeginReadTransaction())
            {
                var st = tx.Store;
                long id = 1;
                foreach (var ix in st.Catalog.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal))
                {
                    var label = L(S(st.Labels.Name(ix.Label)));
                    var prop = L(S(st.PropKeys.Name(ix.Key)));
                    string constraint = ix.Unique ? ix.Name : "";
                    if (indexes && type is "ALL" or "RANGE")
                    {
                        rows.Add(new()
                        {
                            ["id"] = Value.FromInt(id), ["name"] = S(ix.Name), ["state"] = S("ONLINE"), ["populationPercent"] = Value.FromFloat(100),
                            ["type"] = S("RANGE"), ["entityType"] = S("NODE"), ["labelsOrTypes"] = label, ["properties"] = prop,
                            ["indexProvider"] = S("range-1.0"), ["owningConstraint"] = ix.Unique ? S(constraint) : Value.Null,
                            ["lastRead"] = Value.Null, ["readCount"] = Value.FromInt(0), ["trackedSince"] = Value.Null,
                            ["options"] = Value.FromMap(new()), ["failureMessage"] = S(""),
                            ["createStatement"] = S(ix.Unique
                                ? $"CREATE CONSTRAINT `{ix.Name}` FOR (n:`{st.Labels.Name(ix.Label)}`) REQUIRE (n.`{st.PropKeys.Name(ix.Key)}`) IS UNIQUE"
                                : $"CREATE RANGE INDEX `{ix.Name}` FOR (n:`{st.Labels.Name(ix.Label)}`) ON (n.`{st.PropKeys.Name(ix.Key)}`)"),
                        });
                    }
                    else if (!indexes && ix.Unique && type is "ALL" or "UNIQUE" or "NODE UNIQUE" or "UNIQUENESS" or "NODE UNIQUENESS")
                    {
                        rows.Add(new()
                        {
                            ["id"] = Value.FromInt(id), ["name"] = S(ix.Name), ["type"] = S("UNIQUENESS"), ["entityType"] = S("NODE"),
                            ["labelsOrTypes"] = label, ["properties"] = prop, ["ownedIndex"] = S(ix.Name), ["propertyType"] = Value.Null,
                            ["options"] = Value.FromMap(new()),
                            ["createStatement"] = S($"CREATE CONSTRAINT `{ix.Name}` FOR (n:`{st.Labels.Name(ix.Label)}`) REQUIRE (n.`{st.PropKeys.Name(ix.Key)}`) IS UNIQUE"),
                        });
                    }
                    id++;
                }
            }
            string[] defaults = indexes
                ? ["id", "name", "state", "populationPercent", "type", "entityType", "labelsOrTypes", "properties", "indexProvider", "owningConstraint", "lastRead", "readCount"]
                : ["id", "name", "type", "entityType", "labelsOrTypes", "properties", "ownedIndex", "propertyType"];
            string[] all = indexes
                ? [.. defaults, "trackedSince", "options", "failureMessage", "createStatement"]
                : [.. defaults, "options", "createStatement"];
            var cols = yield is null ? defaults : Columns(yield, all);
            return new Result(cols, rows.Select(r => cols.Select(c => r.GetValueOrDefault(c)).ToArray()).ToList());
        }
        return null;
    }

    [GeneratedRegex(@"^show\s+(?:(?<type>all|range|text|point|fulltext|vector|lookup|unique|node\s+unique|uniqueness|node\s+uniqueness|existence|node\s+key|key)\s+)?(?<what>indexes|index|constraints|constraint)\b(?:.*?\byield\s+(?<yield>.+?))?(?:\s+where\s+.*)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowSchema();

    /// <summary>Columns of a YIELD list (<c>*</c> = all), with the canonical casing.</summary>
    private static string[] Columns(string yield, string[] all) =>
        yield.Trim() == "*"
            ? all
            : yield.Split(',')
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .Select(c => all.FirstOrDefault(a => a.Equals(c, StringComparison.OrdinalIgnoreCase)) ?? c)
                .ToArray();

    private static Result Rows(string[] columns, params Value[][] rows) => new(columns, rows.ToList());
    private static Value S(string s) => Value.FromString(s);
    private static Value L(params Value[] items) => Value.FromList(items);
}
