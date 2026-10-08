using System.Text;

using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;

/// <summary>
/// Prepares setup SQL for execution on SQL Server. Setup must keep any
/// session-local #temp tables it creates visible to later measured batches on
/// the same connection. When the setup batch references supplied parameters,
/// SqlClient executes it through <c>sp_executesql</c>, and a #temp table created
/// inside that call is dropped when the call returns. This splitter moves
/// parameter-free temp-table declarations into a root-scope prefix command and
/// leaves the parameterized population for a second command on the same session.
/// </summary>
internal static class SetupSqlExecution
{
    /// <summary>
    /// Validates that the setup SQL can be executed without losing session-local
    /// temp tables. Throws <see cref="SqlHarnessSafetyException"/> for shapes
    /// that cannot be split safely (for example, a SELECT INTO #temp or a
    /// CREATE TABLE #temp whose definition references a parameter).
    /// </summary>
    internal static void Validate(SqlEngine engine, string? setupSql, IReadOnlyList<SqlHarnessParameter> parameters)
    {
        if (engine != SqlEngine.SqlServer)
            return;
        if (string.IsNullOrWhiteSpace(setupSql) || parameters.Count == 0)
            return;

        // Throws on unsupported shapes; the result is discarded.
        _ = PrepareCommands(setupSql, parameters, timeoutSeconds: 1);
    }

    /// <summary>
    /// Returns one or more commands that execute the setup SQL while keeping
    /// session-local #temp tables visible. The first command is parameter-free;
    /// any following command binds only the parameters the setup references.
    /// </summary>
    internal static IReadOnlyList<SqlExecutionCommand> PrepareCommands(
        string setupSql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(setupSql);
        ArgumentNullException.ThrowIfNull(parameters);

        var document = SqlServerDocument.Parse(setupSql);
        if (document.HasErrors || document.Fragment is not TSqlScript script)
        {
            // Setup has already been safety-classified, so a parse error here is
            // unexpected. Treat it as a safety rejection rather than an execution
            // failure to avoid leaking raw SQL on the error path.
            throw new SqlHarnessSafetyException("Setup SQL could not be parsed for execution.");
        }

        var requiredNames = SqlParameterReferences.Collect(SqlEngine.SqlServer, setupSql);
        var referencedParameters = parameters
            .Where(parameter => requiredNames.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        // Setup references no supplied parameters: execute it without bindings so
        // it runs in root scope and any #temp tables survive.
        if (referencedParameters.Length == 0)
        {
            return [new SqlExecutionCommand(setupSql, [], timeoutSeconds)];
        }

        var commands = new List<SqlExecutionCommand>();
        foreach (var batch in script.Batches)
        {
            var split = SplitBatch(batch, setupSql);
            if (split.PrefixStatements.Count > 0)
            {
                commands.Add(new SqlExecutionCommand(
                    BuildSql(split.PrefixStatements, setupSql),
                    [],
                    timeoutSeconds));
            }

            if (split.RemainderStatements.Count > 0)
            {
                var remainderSql = BuildSql(split.RemainderStatements, setupSql);
                var remainderParameters = referencedParameters
                    .Where(parameter =>
                        SqlParameterReferences.Collect(SqlEngine.SqlServer, remainderSql)
                            .Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                commands.Add(new SqlExecutionCommand(remainderSql, remainderParameters, timeoutSeconds));
            }
        }

        return commands;
    }

    private static BatchSplit SplitBatch(TSqlBatch batch, string originalSql)
    {
        var prefix = new List<TSqlStatement>();
        var remainder = new List<TSqlStatement>();

        foreach (var statement in batch.Statements)
        {
            if (IsTempCreatingStatement(statement))
            {
                // A temp table must be created without parameters so the creation
                // can run in root scope and survive past the command boundary.
                if (StatementReferencesParameter(statement, batch))
                {
                    throw new SqlHarnessSafetyException(
                        "Setup creates a session-local temp table and references parameters in the same statement. " +
                        "Create the temp table in a parameter-free CREATE TABLE or SELECT INTO statement, then populate it in a separate statement.");
                }

                prefix.Add(statement);
            }
            else
            {
                remainder.Add(statement);
            }
        }

        return new BatchSplit(prefix, remainder);
    }

    private static bool IsTempCreatingStatement(TSqlStatement statement) =>
        statement is CreateTableStatement create && IsLocalTemp(create.SchemaObjectName)
        || statement is SelectStatement { Into: not null } select && IsLocalTemp(select.Into);

    private static bool IsLocalTemp(SchemaObjectName? name) =>
        name?.Identifiers.Count == 1
        && name.BaseIdentifier.Value.StartsWith('#')
        && !name.BaseIdentifier.Value.StartsWith("##", StringComparison.Ordinal);

    private static bool StatementReferencesParameter(TSqlStatement statement, TSqlBatch batch)
    {
        var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in batch.Statements)
        {
            if (declaration is DeclareVariableStatement declare)
            {
                foreach (var element in declare.Declarations)
                {
                    if (element.VariableName?.Value is { } name)
                        locals.Add(name);
                }
            }
            else if (declaration is DeclareTableVariableStatement declareTable &&
                declareTable.Body?.VariableName?.Value is { } tableName)
            {
                locals.Add(tableName);
            }
        }

        var visitor = new ParameterReferenceVisitor(locals);
        statement.Accept(visitor);
        return visitor.HasParameterReference;
    }

    private static string BuildSql(IReadOnlyList<TSqlStatement> statements, string originalSql)
    {
        if (statements.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var statement in statements)
        {
            if (builder.Length > 0)
                builder.AppendLine();
            if (statement.StartOffset >= 0 && statement.FragmentLength > 0)
            {
                builder.Append(originalSql, statement.StartOffset, statement.FragmentLength);
            }
        }

        return builder.ToString();
    }

    private sealed class BatchSplit(
        IReadOnlyList<TSqlStatement> prefixStatements,
        IReadOnlyList<TSqlStatement> remainderStatements)
    {
        public IReadOnlyList<TSqlStatement> PrefixStatements { get; } = prefixStatements;
        public IReadOnlyList<TSqlStatement> RemainderStatements { get; } = remainderStatements;
    }

    private sealed class ParameterReferenceVisitor(IReadOnlySet<string> locals) : TSqlFragmentVisitor
    {
        internal bool HasParameterReference { get; private set; }

        public override void ExplicitVisit(VariableReference node)
        {
            if (!node.Name.StartsWith("@@", StringComparison.Ordinal)
                && !locals.Contains(node.Name))
            {
                HasParameterReference = true;
            }

            base.ExplicitVisit(node);
        }
    }
}