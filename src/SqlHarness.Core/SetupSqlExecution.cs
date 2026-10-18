using System.Text;

using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core.Dialect;
using SqlHarness.Core.Journal;

using JournalOperationPhase = SqlHarness.Core.Journal.OperationPhase;

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

        var tableValuedParameterNames = parameters
            .Where(parameter => parameter.Type == System.Data.SqlDbType.Structured)
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Setup references no supplied parameters: execute it without bindings so
        // it runs in root scope and any #temp tables survive.
        if (!ReferencesParameter(script, tableValuedParameterNames))
        {
            return [new SqlExecutionCommand(setupSql, [], timeoutSeconds)];
        }

        var commands = new List<SqlExecutionCommand>();
        foreach (var batch in script.Batches)
        {
            var split = SplitBatch(batch, setupSql, tableValuedParameterNames);
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
                var referencedTableParameters = CollectTableValuedParameterReferences(remainderSql, tableValuedParameterNames);
                var remainderParameters = parameters
                    .Where(parameter =>
                        SqlParameterReferences.Collect(SqlEngine.SqlServer, remainderSql)
                            .Contains(parameter.Name, StringComparer.OrdinalIgnoreCase)
                        || referencedTableParameters.Contains(parameter.Name))
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
    internal static async Task<IReadOnlyList<string>> ExecuteSetupAsync(
        SqlEngine engine,
        string? setupSql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        ISqlSession session,
        CanonicalResultAccumulator? raw,
        CancellationToken ct,
        SqlSafetyDecision? setupSafety = null)
    {
        if (string.IsNullOrWhiteSpace(setupSql))
            return setupSafety?.Notices ?? [];

        if (engine == SqlEngine.Postgres && setupSafety?.Allowed != true)
            throw new SqlHarnessSafetyException("PostgreSQL setup execution requires successful setup classification.");

        var commands = engine == SqlEngine.SqlServer
            ? PrepareCommands(setupSql, parameters, timeoutSeconds)
            : [new SqlExecutionCommand(setupSql, parameters, timeoutSeconds)];
        IReadOnlyList<string> warnings = [];
        try
        {
            foreach (var command in commands)
            {
                var execution = engine == SqlEngine.Postgres
                    ? command with { PostgresMode = PostgresExecutionMode.SessionSetup }
                    : command;
                using (OperationPhaseRecorder.Current?.Start(JournalOperationPhase.Setup))
                    await BenchmarkRunner.ExecuteRawAsync(session, execution, raw, ct, endLogicalBatch: false);
            }
        }
        finally
        {
            warnings = await SqlSessionLogicalBatch.EndAsync(session, ct);
        }
        return warnings.Concat(setupSafety?.Notices ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ReferencesParameter(TSqlScript script, IReadOnlySet<string> tableValuedParameterNames)
    {
        foreach (TSqlBatch batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                if (StatementReferencesParameter(statement, batch, tableValuedParameterNames))
                    return true;
            }
        }

        return false;
    }

    private static BatchSplit SplitBatch(TSqlBatch batch, string originalSql, IReadOnlySet<string> tableValuedParameterNames)
    {
        var statements = batch.Statements;
        var firstParameterReferenceIndex = -1;
        for (var index = 0; index < statements.Count; index++)
        {
            if (StatementReferencesParameter(statements[index], batch, tableValuedParameterNames))
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
            if (SqlServerControlFlowTraversal.Flatten([statements[index]])
                .Any(IsTempCreatingStatement))
            {
                throw new SetupSqlShapeException(
                    "Setup creates a session-local temp table at or after a statement that references a parameter. " +
                    "Place all parameter-free CREATE TABLE or SELECT INTO statements before any statement that references a parameter.");
            }
        }

        var prefix = statements.Take(firstParameterReferenceIndex).ToArray();
        var remainder = statements.Skip(firstParameterReferenceIndex).ToArray();
        if (SqlServerControlFlowTraversal.Flatten(prefix).Any(statement =>
                statement is BeginTransactionStatement or CommitTransactionStatement
                    or RollbackTransactionStatement or SaveTransactionStatement or ReturnStatement))
        {
            throw new SqlHarnessSafetyException(
                "Transaction control and RETURN are not allowed in a parameter-free setup prefix.");
        }

        if (HasCrossSplitLocalReference(prefix, remainder))
        {
            throw new SetupSqlShapeException(
                "Setup references a local variable declared before the parameterized setup boundary. " +
                "Declare and use local variables in the same parameterized batch, or pass the value as a typed parameter.");
        }

        return new BatchSplit(prefix, remainder);
    }

    private static bool HasCrossSplitLocalReference(
        IReadOnlyList<TSqlStatement> prefix,
        IReadOnlyList<TSqlStatement> remainder)
    {
        var declarations = new LocalDeclarationVisitor();
        foreach (var statement in prefix)
            statement.Accept(declarations);

        if (declarations.Locals.Count == 0)
            return false;

        var visitor = new LocalReferenceVisitor(declarations.Locals);
        foreach (var statement in remainder)
        {
            statement.Accept(visitor);
            if (visitor.Found)
                return true;
        }

        return false;
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

    private static bool StatementReferencesParameter(
        TSqlStatement statement,
        TSqlBatch batch,
        IReadOnlySet<string> tableValuedParameterNames)
    {
        var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in SqlServerControlFlowTraversal.Flatten(batch.Statements))
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

        var visitor = new ParameterReferenceVisitor(locals, tableValuedParameterNames);
        statement.Accept(visitor);
        return visitor.HasParameterReference;
    }

    private static IReadOnlySet<string> CollectTableValuedParameterReferences(
        string sql,
        IReadOnlySet<string> tableValuedParameterNames)
    {
        if (tableValuedParameterNames.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var document = SqlServerDocument.Parse(sql);
        if (document.HasErrors)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var visitor = new TableValuedParameterReferenceVisitor(tableValuedParameterNames);
        document.Fragment.Accept(visitor);
        return visitor.References;
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

    private sealed class ParameterReferenceVisitor(
        IReadOnlySet<string> locals,
        IReadOnlySet<string> tableValuedParameterNames) : TSqlFragmentVisitor
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

        public override void ExplicitVisit(VariableTableReference node)
        {
            if (node.Variable?.Name is { } name
                && tableValuedParameterNames.Contains(name)
                && !locals.Contains(name))
                HasParameterReference = true;
            base.ExplicitVisit(node);
        }
    }

    private sealed class TableValuedParameterReferenceVisitor(IReadOnlySet<string> tableValuedParameterNames) : TSqlFragmentVisitor
    {
        internal HashSet<string> References { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(VariableTableReference node)
        {
            if (node.Variable?.Name is { } name && tableValuedParameterNames.Contains(name))
                References.Add(name);
            base.ExplicitVisit(node);
        }
    }

    private sealed class LocalReferenceVisitor(IReadOnlySet<string> locals) : TSqlFragmentVisitor
    {
        internal bool Found { get; private set; }

        public override void ExplicitVisit(VariableReference node)
        {
            if (!node.Name.StartsWith("@@", StringComparison.Ordinal) && locals.Contains(node.Name))
                Found = true;

            base.ExplicitVisit(node);
        }
    }

    private sealed class LocalDeclarationVisitor : TSqlFragmentVisitor
    {
        internal HashSet<string> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                if (declaration.VariableName?.Value is { } name)
                    Locals.Add(name);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareTableVariableStatement node)
        {
            if (node.Body?.VariableName?.Value is { } name)
                Locals.Add(name);

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
