namespace GraphQLite.Runtime;

internal sealed class Procedure(string name, string[] outputs, Func<ExecContext, Value[], IEnumerable<Value[]>> body)
{
    public readonly string Name = name;
    public readonly string[] Outputs = outputs;
    public readonly Func<ExecContext, Value[], IEnumerable<Value[]>> Body = body;
}

internal static class Procedures
{
    private static readonly Dictionary<string, Procedure> All = new(StringComparer.OrdinalIgnoreCase);

    static Procedures()
    {
        Add("db.labels", ["label"], (ctx, _) =>
        {
            var s = ctx.Store;
            return Enumerable.Range(0, s.Labels.Count).Where(i => s.LabelCount(i) > 0)
                .Select(i => new[] { Value.FromString(s.Labels.Name(i)) });
        });
        Add("db.relationshiptypes", ["relationshipType"], (ctx, _) =>
        {
            var s = ctx.Store;
            return Enumerable.Range(0, s.RelTypes.Count).Where(i => s.RelTypeCount(i) > 0)
                .Select(i => new[] { Value.FromString(s.RelTypes.Name(i)) });
        });
        Add("db.propertykeys", ["propertyKey"], (ctx, _) =>
            ctx.Store.PropKeys.Names.Where(n => !n.StartsWith('\0')).Select(n => new[] { Value.FromString(n) }));
        Add("db.indexes", ["name", "label", "property", "unique", "type"], (ctx, _) =>
        {
            var s = ctx.Store;
            return s.Catalog.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal).Select(ix => new[]
            {
                Value.FromString(ix.Name), Value.FromString(s.Labels.Name(ix.Label)), Value.FromString(s.PropKeys.Name(ix.Key)),
                Value.FromBool(ix.Unique), Value.FromString("RANGE"),
            });
        });
        Add("db.constraints", ["name", "label", "property"], (ctx, _) =>
        {
            var s = ctx.Store;
            return s.Catalog.Indexes.Where(i => i.Unique).OrderBy(i => i.Name, StringComparer.Ordinal).Select(ix => new[]
            {
                Value.FromString(ix.Name), Value.FromString(s.Labels.Name(ix.Label)), Value.FromString(s.PropKeys.Name(ix.Key)),
            });
        });
        Add("db.stats", ["nodes", "relationships", "labels", "relationshipTypes", "propertyKeys", "dataPages"], (ctx, _) =>
        {
            var s = ctx.Store;
            return [[Value.FromInt(s.NodeCount), Value.FromInt(s.RelCount), Value.FromInt(s.Labels.Count), Value.FromInt(s.RelTypes.Count),
                Value.FromInt(s.PropKeys.Count), Value.FromInt(s.DataPages)]];
        });
        Add("dbms.components", ["name", "versions", "edition"], (ctx, _) =>
            [[Value.FromString("GraphQLite"), Value.FromList([Value.FromString(typeof(Procedures).Assembly.GetName().Version?.ToString() ?? "0")]), Value.FromString("embedded")]]);
    }

    private static void Add(string name, string[] outputs, Func<ExecContext, Value[], IEnumerable<Value[]>> body) =>
        All[name] = new Procedure(name, outputs, body);

    public static Procedure? Find(string name) => All.TryGetValue(name, out var p) ? p : null;

    public static IEnumerable<Procedure> List => All.Values;
}

internal sealed class ProcedureCallOp(Operator input, Procedure proc, Eval[] args, int[] outSlots) : UnaryOperator(input)
{
    private readonly Procedure proc = proc;
    private readonly Eval[] args = args;
    private readonly int[] outSlots = outSlots;
    public override string Name => "ProcedureCall";
    public override string Details => proc.Name;
    public override Cursor Open(ExecContext ctx) => new C(this, Input.Open(ctx), ctx);

    private sealed class C(ProcedureCallOp op, Cursor input, ExecContext ctx) : Cursor
    {
        private IEnumerator<Value[]>? _rows;

        public override bool Next()
        {
            while (true)
            {
                if (_rows != null && _rows.MoveNext())
                {
                    var r = _rows.Current;
                    for (int i = 0; i < op.outSlots.Length; i++)
                        if (op.outSlots[i] >= 0) ctx.Row[op.outSlots[i]] = r[i];
                    return true;
                }
                if (!input.Next()) return false;
                var argv = new Value[op.args.Length];
                for (int i = 0; i < argv.Length; i++) argv[i] = op.args[i](ctx);
                // materialize so the procedure observes a consistent state
                _rows = op.proc.Body(ctx, argv).ToList().GetEnumerator();
            }
        }
    }
}
