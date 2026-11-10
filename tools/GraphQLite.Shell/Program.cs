using System.Diagnostics;
using System.Text;
using GraphQLite;

// Interactive Cypher shell, similar to the sqlite3 CLI.
// Usage: GraphQLite.Shell [database-file | :memory:] [-c "query"]
string path = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : ":memory:";
using var db = GraphDatabase.Open(path);
var parameters = new Dictionary<string, object?>();

int ci = Array.IndexOf(args, "-c");
if (ci >= 0 && ci + 1 < args.Length)
{
    return Run(args[ci + 1]) ? 0 : 1;
}

Console.WriteLine($"GraphQLite shell — {(db.IsInMemory ? "in-memory database" : Path.GetFullPath(path))}");
Console.WriteLine("Terminate statements with ';'. Type :help for commands.");
var buffer = new StringBuilder();
while (true)
{
    Console.Write(buffer.Length == 0 ? "gdb> " : "...> ");
    var line = Console.ReadLine();
    if (line is null) break;
    if (buffer.Length == 0 && line.TrimStart().StartsWith(':'))
    {
        if (!Command(line.Trim())) break;
        continue;
    }
    buffer.AppendLine(line);
    var text = buffer.ToString().TrimEnd();
    if (!text.EndsWith(';')) continue;
    buffer.Clear();
    Run(text);
}
return 0;

bool Run(string query)
{
    try
    {
        var sw = Stopwatch.StartNew();
        var result = db.Execute(query, parameters);
        sw.Stop();
        if (result.Columns is ["plan"] && result.Count == 1) Console.WriteLine(result[0][0].AsString());
        else if (result.Columns.Count > 0) Console.WriteLine(result.ToTable());
        var s = result.Statistics;
        if (s.ContainsUpdates)
        {
            var parts = new List<string>();
            if (s.NodesCreated > 0) parts.Add($"{s.NodesCreated} nodes created");
            if (s.NodesDeleted > 0) parts.Add($"{s.NodesDeleted} nodes deleted");
            if (s.RelationshipsCreated > 0) parts.Add($"{s.RelationshipsCreated} relationships created");
            if (s.RelationshipsDeleted > 0) parts.Add($"{s.RelationshipsDeleted} relationships deleted");
            if (s.PropertiesSet > 0) parts.Add($"{s.PropertiesSet} properties set");
            if (s.LabelsAdded > 0) parts.Add($"{s.LabelsAdded} labels added");
            if (s.LabelsRemoved > 0) parts.Add($"{s.LabelsRemoved} labels removed");
            if (s.IndexesAdded > 0) parts.Add($"{s.IndexesAdded} indexes added");
            if (s.IndexesRemoved > 0) parts.Add($"{s.IndexesRemoved} indexes removed");
            Console.WriteLine(string.Join(", ", parts));
        }
        Console.WriteLine($"({sw.Elapsed.TotalMilliseconds:N2} ms)");
        return true;
    }
    catch (GraphQLiteException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
        Console.ResetColor();
        return false;
    }
}

bool Command(string cmd)
{
    var parts = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    switch (parts[0].ToLowerInvariant())
    {
        case ":q":
        case ":quit":
        case ":exit":
            return false;
        case ":help":
            Console.WriteLine("""
                :param <name> <cypher expression>   set a query parameter, e.g. :param ids [1, 2, 3]
                :params                             list parameters
                :clear                              remove all parameters
                :stats                              node / relationship counts
                :checkpoint                         write a snapshot and truncate the WAL
                :quit                               exit
                EXPLAIN <query>;                    show the execution plan
                """);
            return true;
        case ":param" when parts.Length == 3:
            try
            {
                parameters[parts[1]] = db.Execute("RETURN " + parts[2], parameters)[0][0];
                Console.WriteLine($"${parts[1]} = {parameters[parts[1]]}");
            }
            catch (GraphQLiteException ex) { Console.WriteLine(ex.Message); }
            return true;
        case ":params":
            foreach (var kv in parameters) Console.WriteLine($"${kv.Key} = {kv.Value}");
            return true;
        case ":clear":
            parameters.Clear();
            return true;
        case ":stats":
            Console.WriteLine($"nodes: {db.NodeCount:N0}, relationships: {db.RelationshipCount:N0}");
            return true;
        case ":checkpoint":
            db.Checkpoint();
            Console.WriteLine("checkpoint done");
            return true;
        default:
            Console.WriteLine("Unknown command, type :help");
            return true;
    }
}
