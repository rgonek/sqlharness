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
    /// temp tables. Throws <see cref="SetupSqlShapeException"/> for shapes
    /// that cannot be split safely (for example, a SELECT INTO #temp or a
    /// CREATE TABLE #temp whose definition references a parameter, or any
    /// temp-creating statement placed at or after the first parameter-referencing
    /// statement).
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

        // Setup references no supplied parameters: execute it without bindings so
        // it runs in root scope and any #temp tables survive.
        if (!ReferencesParameter(script))
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
                var remainderParameters = parameters
                    .Where(parameter =>
                        SqlParameterReferences.Collect(SqlEngine.SqlServer, remainderSql)
                            .Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                commands.Add(new SqlExecutionCommand(remainderSql, remainderParameters, timeoutSeconds));
            }
        }

        return commands;
    }

    /// <summary>
    /// Executes optional setup SQL once per session, applying the SQL Server
    /// split when required. SQL Server setup that references parameters is split
    /// into a parameter-free root-scope prefix followed by a parameterized
    /// remainder; Postgres setup runs unchanged.
    /// </summary>
    internal static async Task ExecuteSetupAsync(
        SqlEngine engine,
        string? setupSql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        ISqlSession session,
        CanonicalResultAccumulator raw,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(setupSql))
            return;

        var commands = engine == SqlEngine.SqlServer
            ? PrepareCommands(setupSql, parameters, timeoutSeconds)
            : [new SqlExecutionCommand(setupSql, parameters, timeoutSeconds)];
        foreach (var command in commands)
        {
            await BenchmarkRunner.ExecuteRawAsync(session, command, raw, ct);
        }
    }

    private static bool ReferencesParameter(TSqlScript script)
    {
        foreach (TSqlBatch batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                if (StatementReferencesParameter(statement, batch))
                    return true;
            }
        }

        return false;
    }

    private static BatchSplit SplitBatch(TSqlBatch batch, string originalSql)
    {
        var statements = batch.Statements;
        var firstParameterReferenceIndex = -1;
        for (var index = 0; index < statements.Count; index++)
        {
            if (StatementReferencesParameter(statements[index], batch))
            {
                firstParameterReferenceIndex = index;
                break;
            }
        }

        if (firstParameterReferenceIndex < 0)
        {
            // The whole batch is parameter-free; it will run as a single root-scope
            // command from the caller when no statement in the entire setup references
            // a parameter. Returning an empty remainder keeps this helper consistent.
            return new BatchSplit(statements.ToArray(), []);
        }

        for (var index = firstParameterReferenceIndex; index < statements.Count; index++)
        {
            if (IsTempCreatingStatement(statements[index]))
            {
                throw new SetupSqlShapeException(
                    "Setup creates a session-local temp table at or after a statement that references a parameter. " +
                    "Place all parameter-free CREATE TABLE or SELECT INTO statements before any statement that references a parameter.");
            }
        }

        var prefix = statements.Take(firstParameterReferenceIndex).ToArray();
        var remainder = statements.Skip(firstParameterReferenceIndex).ToArray();
        return new BatchSplit(prefix, remainder);
    }

    /// <summary>
    /// A statement needs root scope to survive if and only if it creates a new
    /// session-local #temp table: CREATE TABLE #t or SELECT ... INTO #t. Other
    /// statements (including INSERT, UPDATE, DELETE, ALTER TABLE, CREATE INDEX,
    /// DROP TABLE, and TRUNCATE against an existing #temp) do not create the
    /// table and can run parameterized after the prefix.
    /// </summary>
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

/// <summary>
/// Safety rejection for a setup SQL shape that cannot keep session-local #temp
/// tables visible across the parameterized command boundary. The message is
/// safe to surface on validation paths because it never contains SQL text,
/// supplied parameter names, or supplied values.
/// </summary>
internal sealed class SetupSqlShapeException : SqlHarnessSafetyException
{
    internal SetupSqlShapeException(string message)
        : base(message)
    {
    }
}