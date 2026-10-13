using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;

/// <summary>
/// Dry-run check shared by the CLI --dry-run switch and MCP dryRun: a parameter the measured
/// batch references but no input supplies is rejected offline, as the retired validate command did.
/// Execution paths are unchanged. Setup batches are not scanned (setup variables do not cross
/// into measured batches), and batch-local DECLAREs are already excluded by the collector.
/// </summary>
internal static class MissingParameterCheck
{
    internal static void RequireSupplied(SqlEngine engine, IReadOnlyList<SqlHarnessParameter> supplied, params string[] measuredBatches)
    {
        var names = supplied.Select(parameter => parameter.Name.TrimStart('@', ':')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = measuredBatches
            .SelectMany(batch => SqlParameterReferences.Collect(engine, batch))
            .Select(name => name.TrimStart('@', ':'))
            .Where(name => !names.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
            throw new SqlHarnessSafetyException($"SQL references parameters that were not supplied (missing_parameters): {string.Join(", ", missing)}.");
    }
}

internal static class SqlParameterReferences
{
    internal static IReadOnlyList<string> Collect(SqlEngine engine, string sql)
    {
        if (engine == SqlEngine.Postgres)
            return Postgres.PostgresParameterReferenceValidator.CollectReferences(sql);

        var document = SqlServerDocument.Parse(sql);
        if (document.HasErrors)
            return [];
        var collector = new SqlServerReferenceCollector();
        document.Fragment.Accept(collector);
        collector.CloseBatch();
        collector.Names.ExceptWith(collector.LocalNames);
        return collector.Names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed class SqlServerReferenceCollector : TSqlFragmentVisitor
    {
        internal HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> LocalNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        // 011/T3b: a table variable is local to its declaring GO batch only, so
        // its name is subtracted per batch. In any other batch the same @name
        // in scalar position is an ordinary required parameter.
        private readonly HashSet<string> _batchNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _batchTableVariables = new(StringComparer.OrdinalIgnoreCase);
        public override void ExplicitVisit(TSqlBatch node)
        {
            CloseBatch();
            base.ExplicitVisit(node);
            CloseBatch();
        }
        internal void CloseBatch()
        {
            _batchNames.ExceptWith(_batchTableVariables);
            Names.UnionWith(_batchNames);
            _batchNames.Clear();
            _batchTableVariables.Clear();
        }
        public override void ExplicitVisit(VariableReference node)
        {
            if (!node.Name.StartsWith("@@", StringComparison.Ordinal))
                _batchNames.Add(node.Name);
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
                LocalNames.Add(declaration.VariableName.Value);
            base.ExplicitVisit(node);
        }
        // 011/T3: table variables are batch locals, and a table-position @name
        // can never be satisfied by a scalar parameter, so neither is required.
        public override void ExplicitVisit(DeclareTableVariableBody node)
        {
            if (node.VariableName?.Value is { } name)
                _batchTableVariables.Add(name);
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(VariableTableReference node)
        {
        }
    }
}