using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.SqlServer.Types;

using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;


// ParameterName marks a value-validation failure. Diagnostic is not InnerException, so ToString cannot inherit the rejected value.
internal class SqlHarnessSafetyException(string message, Exception? innerException = null) : Exception(message, innerException)
{
    internal string? ParameterName { get; init; }

    internal string? ExpectedType { get; init; }

    internal Exception? Diagnostic { get; init; }

    internal bool IsParameterValue => ParameterName is not null && ExpectedType is not null;

    internal static SqlHarnessSafetyException InvalidParameter(string name, string type, Exception? diagnostic = null) =>
        new($"Invalid value for SQL parameter '{name}' of type '{type}'.")
        {
            ParameterName = name,
            ExpectedType = type,
            Diagnostic = diagnostic,
        };

    internal SqlHarnessSafetyException WithParameterValue(string? message = null) =>
        new(message ?? Message)
        {
            ParameterName = ParameterName,
            ExpectedType = ExpectedType,
            Diagnostic = Diagnostic,
        };

    internal IReadOnlyList<string> PreservedTokens()
    {
        var tokens = new List<string>(8);
        Add(tokens, ParameterName);
        Add(tokens, ExpectedType);
        if (!string.IsNullOrEmpty(ParameterName) && !ParameterName.StartsWith('@'))
            Add(tokens, "@" + ParameterName);
        return tokens;
    }

    private static void Add(List<string> tokens, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        tokens.Add(value);
        tokens.Add("'" + value + "'");
    }
}

internal static class SqlParameterSecrets
{
    private const int MinimumRedactionLength = 4;

    internal static void AddParameterValue(ICollection<string> knownSecrets, string? value)
    {
        if (value is { Length: >= MinimumRedactionLength })
            knownSecrets.Add(value);
    }

    internal static void AddValues(ICollection<string> knownSecrets, IEnumerable<string>? declarations)
    {
        if (declarations is null)
            return;
        foreach (var declaration in declarations)
            AddValue(knownSecrets, declaration);
    }

    // Typed values are registered whole: a comma, '=' or ':' inside one is part of the secret.
    internal static void AddValues(ICollection<string> knownSecrets, IEnumerable<SqlHarnessParameterInput>? inputs)
    {
        if (inputs is null)
            return;
        foreach (var input in inputs)
            AddParameterValue(knownSecrets, input?.Value);
    }

    internal static void AddMatrixValues(ICollection<string> knownSecrets, SqlHarnessParameterMatrixInput? matrix)
    {
        if (matrix?.Values is null)
            return;
        foreach (var value in matrix.Values)
            AddParameterValue(knownSecrets, value);
    }

    internal static void AddMatrixValues(ICollection<string> knownSecrets, string? matrix)
    {
        if (string.IsNullOrEmpty(matrix))
            return;
        var equals = matrix.IndexOf('=');
        if (equals < 0 || equals >= matrix.Length - 1)
            return;
        foreach (var value in matrix[(equals + 1)..].Split(','))
            AddParameterValue(knownSecrets, value);
    }

    private static void AddValue(ICollection<string> knownSecrets, string declaration)
    {
        if (string.IsNullOrEmpty(declaration))
            return;
        var equals = declaration.IndexOf('=');
        if (equals < 0 || equals >= declaration.Length - 1)
            return;
        var value = declaration[(equals + 1)..];
        AddParameterValue(knownSecrets, value);
    }
}

/// <summary>
/// Compatibility adapter (012): an operation carries declaration text or the typed model, never both.
/// Legacy text is turned into the same model, so every caller ends in the one binder.
/// </summary>
internal static class SqlParameterInputs
{
    internal static IEnumerable<SqlHarnessParameterInput> Resolve(
        IReadOnlyList<string> declarations,
        IReadOnlyList<SqlHarnessParameterInput>? typed)
    {
        // Lazy on purpose: see SqlParameterParser.Parse.
        if (typed is null)
            return declarations.Select(SqlParameterParser.ToInput);
        if (declarations is { Count: > 0 })
        {
            throw new SqlHarnessSafetyException(
                "SQL parameters must be supplied either as declarations or as typed inputs, not both.");
        }

        return typed;
    }

    internal static SqlHarnessParameterMatrixInput ResolveMatrix(string matrix, SqlHarnessParameterMatrixInput? typed)
    {
        if (typed is null)
            return SqlParameterMatrixParser.ToInput(matrix);
        if (!string.IsNullOrEmpty(matrix))
        {
            throw new SqlHarnessSafetyException(
                "The --matrix option must be supplied either as text or as a typed matrix, not both.");
        }

        return typed;
    }
}

internal enum SqlUsage
{
    Query,
    CompareSetup,
}

internal enum SqlSafetyReason
{
    Allowed,
    ParseError,
    UnsupportedStatement,
    MutationNotAllowed,
    DatabaseConfirmationRequired,
    DatabaseConfirmationMismatch,
    CrossDatabaseReference,
    SelectIntoNotAllowed,
    NonTemporaryWrite,
}

internal sealed record SqlSafetyDecision(
    bool Allowed,
    SqlSafetyReason Reason,
    bool HasMutation = false,
    string? Detail = null)
{
    private static readonly IReadOnlySet<string> NoSessionTempTables =
        new HashSet<string>(StringComparer.Ordinal);

    internal bool HasSessionLocalWork { get; init; }

    internal IReadOnlySet<string> SessionTempTables { get; init; } = NoSessionTempTables;

    internal string RejectionDescription =>
        Detail is null ? $"{Reason}." : $"{Reason}. {Detail}";
}

internal sealed class SqlSafetyClassifier
{
    private const string UnprovenTableVariableDetail =
        "Table-position variables require an earlier inline TABLE declaration in the same batch. Named user-defined types are not proven as table variables. Setup variables do not cross into benchmark batches.";

    internal SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase = null)
    {
        var document = SqlServerDocument.Parse(sql);
        if (document.HasErrors || document.Fragment is not TSqlScript script)
        {
            return Denied(SqlSafetyReason.ParseError);
        }

        var inspection = new SafetyInspectionVisitor();
        document.Fragment.Accept(inspection);
        if (inspection.HasCrossDatabaseReference)
        {
            return Denied(SqlSafetyReason.CrossDatabaseReference);
        }

        if (inspection.HasExternalAccess ||
            inspection.HasStatefulExpression ||
            inspection.HasStatefulTableSource ||
            inspection.HasExecuteInsertSource ||
            inspection.HasHashNamedCommonTableExpression)
        {
            return Denied(SqlSafetyReason.UnsupportedStatement);
        }

        if (script.Batches.All(batch => batch.Statements.Count == 0))
        {
            return Denied(SqlSafetyReason.UnsupportedStatement);
        }

        return usage switch
        {
            SqlUsage.Query => ClassifyQuery(script.Batches, inspection, database, allowMutation, confirmDatabase),
            SqlUsage.CompareSetup => ClassifyCompareSetup(script.Batches, inspection),
            _ => Denied(SqlSafetyReason.UnsupportedStatement),
        };
    }

    private static SqlSafetyDecision ClassifyQuery(
        IList<TSqlBatch> batches,
        SafetyInspectionVisitor inspection,
        string? database,
        bool allowMutation,
        string? confirmDatabase)
    {
        if (inspection.HasNonLocalSelectInto)
        {
            return Denied(SqlSafetyReason.SelectIntoNotAllowed);
        }

        var hasMutation = false;
        var hasSessionLocal = false;
        foreach (var batch in batches)
        {
            // 011/T2, 011/T3: SET targets and table variables are proven
            // against same-batch declarations (Ruling R3).
            var scope = inspection.ScopeOf(batch);
            foreach (var statement in batch.Statements)
            {
                var hasUnprovenUse = HasUnprovenTableVariableUse(statement, scope);
                var classification = ClassifyStatement(statement, scope);
                if (classification.DenyReason is { } deny)
                    return Denied(deny, hasUnprovenUse ? UnprovenTableVariableDetail : null);

                hasMutation |= classification.HasPersistentWrite;
                hasSessionLocal |= classification.HasSessionLocalWrite;
            }
        }

        if (!hasMutation)
        {
            return Allowed(hasSessionLocal: hasSessionLocal);
        }

        if (!allowMutation)
        {
            return Denied(SqlSafetyReason.MutationNotAllowed);
        }

        if (confirmDatabase is null)
        {
            return Denied(SqlSafetyReason.DatabaseConfirmationRequired);
        }

        if (!string.Equals(database, confirmDatabase, StringComparison.Ordinal))
        {
            return Denied(SqlSafetyReason.DatabaseConfirmationMismatch);
        }

        return Allowed(hasMutation: true, hasSessionLocal: hasSessionLocal);
    }

    private static SqlSafetyDecision ClassifyCompareSetup(
        IList<TSqlBatch> batches,
        SafetyInspectionVisitor inspection)
    {
        if (inspection.HasNonLocalSelectInto || inspection.HasNonLocalOutputInto)
        {
            return Denied(SqlSafetyReason.NonTemporaryWrite);
        }

        var hasSessionLocal = false;
        foreach (var batch in batches)
        {
            // 011/T3: table variables are proven against same-batch
            // declarations. A SET never reaches an allow here: it is neither a
            // SELECT nor session-local work, so setup denies it below.
            var scope = inspection.ScopeOf(batch);
            foreach (var statement in batch.Statements)
            {
                // One walk per statement: a DECLARE is skipped only when it
                // hides no unproven table-position name.
                var hasUnprovenUse = HasUnprovenTableVariableUse(statement, scope);
                if (statement is DeclareVariableStatement && !hasUnprovenUse)
                    continue;

                var classification = hasUnprovenUse
                    ? StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement)
                    : ClassifyProvenStatement(statement, scope);
                if (classification.DenyReason is { } deny)
                {
                    // Setup never takes the mutation-approval path; any write outside local temps is NonTemporaryWrite.
                    if (deny is SqlSafetyReason.MutationNotAllowed ||
                        (deny is SqlSafetyReason.UnsupportedStatement && IsWrite(statement)))
                    {
                        return Denied(
                            SqlSafetyReason.NonTemporaryWrite,
                            hasUnprovenUse ? UnprovenTableVariableDetail : null);
                    }

                    return Denied(deny, hasUnprovenUse ? UnprovenTableVariableDetail : null);
                }

                if (classification.HasPersistentWrite)
                    return Denied(SqlSafetyReason.NonTemporaryWrite);

                if (!classification.HasSessionLocalWrite && statement is not SelectStatement)
                {
                    return Denied(
                        IsWrite(statement) ? SqlSafetyReason.NonTemporaryWrite : SqlSafetyReason.UnsupportedStatement,
                        hasUnprovenUse ? UnprovenTableVariableDetail : null);
                }

                hasSessionLocal |= classification.HasSessionLocalWrite;
            }
        }

        return Allowed(hasSessionLocal: hasSessionLocal);
    }

    private static StatementClassification ClassifyStatement(TSqlStatement statement, BatchVariableScope scope)
    {
        // 011/T3: every table-position @name anywhere in the statement (DML
        // target, FROM, subquery, OUTPUT INTO) must be a proven same-batch
        // table variable, and a table variable never appears as a scalar.
        return HasUnprovenTableVariableUse(statement, scope)
            ? StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement)
            : ClassifyProvenStatement(statement, scope);
    }

    // Callers have already established that the statement holds no unproven table-variable use.
    private static StatementClassification ClassifyProvenStatement(TSqlStatement statement, BatchVariableScope scope)
    {
        switch (statement)
        {
            case DeclareTableVariableStatement declareTable:
                // 011/T3: session-local declaration. Column defaults and
                // computed columns ride on the global inspection.
                return declareTable.Body is { VariableName.Value: { Length: > 0 }, Definition: not null }
                    ? StatementClassification.SessionLocal
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

            case SetVariableStatement setVariable:
                // 011/T2: plain scalar assignment to a proven same-batch scalar
                // local only (Ruling R3, fail closed). The RHS rides on the
                // global inspection (external/stateful/cross-db/EXEC sources).
                return IsProvenScalarAssignment(setVariable, scope)
                    ? StatementClassification.ReadOnly
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

            case DeclareVariableStatement declare:
                // Scalar variables only; initializers are covered by the global
                // inspection (cross-database, external, stateful, EXEC sources).
                // Table variables parse as DeclareTableVariableStatement (011/T3 case above).
                return IsScalarDeclaration(declare)
                    ? StatementClassification.ReadOnly
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

            case TruncateTableStatement truncate:
                return IsLocalTemp(truncate.TableName)
                    ? StatementClassification.SessionLocal
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

            case AlterTableAddTableElementStatement addElement:
                return IsSupportedLocalTempAlter(addElement.SchemaObjectName, addElement.Definition)
                    ? StatementClassification.SessionLocal
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

            case AlterTableDropTableElementStatement dropElement:
                return IsLocalTemp(dropElement.SchemaObjectName) &&
                    dropElement.AlterTableDropTableElements.All(e =>
                        e.TableElementType is TableElementType.Column or TableElementType.Constraint)
                    ? StatementClassification.SessionLocal
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

            case SelectStatement select:
                if (select.Into is null)
                    return StatementClassification.ReadOnly;
                return IsLocalTemp(select.Into)
                    ? StatementClassification.SessionLocal
                    : StatementClassification.Denied(SqlSafetyReason.SelectIntoNotAllowed);

            case CreateTableStatement create:
                return IsLocalTemp(create.SchemaObjectName)
                    ? StatementClassification.SessionLocal
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

            case CreateIndexStatement createIndex:
                return IsLocalTemp(createIndex.OnName)
                    ? StatementClassification.SessionLocal
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

            case DropTableStatement drop when drop.Objects.Count > 0 && drop.Objects.All(IsLocalTemp):
                return StatementClassification.SessionLocal;

            case InsertStatement insert:
                return ClassifyDmlWrites(
                    ResolveDirectTarget(insert.InsertSpecification.Target, scope),
                    insert.InsertSpecification.OutputIntoClause,
                    insert,
                    scope);

            case UpdateStatement update:
                return ClassifyDmlWrites(
                    ResolveTarget(update.UpdateSpecification.Target, update.UpdateSpecification.FromClause, scope),
                    update.UpdateSpecification.OutputIntoClause,
                    update,
                    scope);

            case DeleteStatement delete:
                return ClassifyDmlWrites(
                    ResolveTarget(delete.DeleteSpecification.Target, delete.DeleteSpecification.FromClause, scope),
                    delete.DeleteSpecification.OutputIntoClause,
                    delete,
                    scope);

            case MergeStatement merge:
                return ClassifyDmlWrites(
                    ResolveDirectTarget(merge.MergeSpecification.Target, scope),
                    merge.MergeSpecification.OutputIntoClause,
                    merge,
                    scope);

            default:
                return IsDirectMutation(statement)
                    ? StatementClassification.Persistent
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);
        }
    }

    // 011/T2: same-batch proof for SET targets (Ruling R3, fail closed).
    // A name counts as a scalar local only when a same-batch
    // DeclareVariableStatement declares it with a scalar-looking type.
    // Table-variable names (DeclareTableVariableStatement, owned by T3)
    // never qualify, even if otherwise declared.
    //
    // 011/T3: a name is a table variable only when a same-batch top-level
    // DeclareTableVariableStatement declares it AND no DeclareVariableStatement
    // of any type (scalar, cursor, user type) reuses the name. Ambiguous
    // scalar-vs-table names prove nothing and stay denied on both paths.
    //
    // 011/T3b: declaration must precede use. ScalarLocals and TableVariables
    // map a name to the end offset of its first declaring statement; a SET
    // target or a table-position @name proves nothing unless it starts at or
    // after that offset. The disqualifying checks (dual declaration, table
    // name in scalar position) stay whole-batch, so order never widens an allow.
    //
    // The collections are read-only views: the shared Empty instance and a
    // scope handed to several visitors cannot be changed after construction.
    private sealed class BatchVariableScope(
        IReadOnlyDictionary<string, int> scalarLocals,
        IReadOnlyDictionary<string, int> tableVariables,
        IReadOnlySet<string> nonTableDeclarations)
    {
        internal static BatchVariableScope Empty { get; } = new(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        internal bool IsTableVariableName(string? name) =>
            !string.IsNullOrEmpty(name) && tableVariables.ContainsKey(name);

        internal bool IsProvenTableVariable(VariableTableReference? reference) =>
            reference?.Variable?.Name is { Length: > 0 } name &&
            IsDeclaredBefore(tableVariables, name, reference) &&
            !nonTableDeclarations.Contains(name);

        internal bool IsProvenScalarLocal(VariableReference? variable) =>
            variable?.Name is { Length: > 0 } name &&
            IsDeclaredBefore(scalarLocals, name, variable) &&
            !tableVariables.ContainsKey(name);

        private static bool IsDeclaredBefore(IReadOnlyDictionary<string, int> declarations, string name, TSqlFragment use) =>
            declarations.TryGetValue(name, out var declarationEnd) &&
            use.StartOffset >= 0 &&
            use.StartOffset >= declarationEnd;
    }

    // Unknown fragment positions prove nothing: no use can follow int.MaxValue.
    private static int DeclarationEnd(TSqlFragment statement) =>
        statement.StartOffset >= 0 && statement.FragmentLength >= 0
            ? statement.StartOffset + statement.FragmentLength
            : int.MaxValue;

    // The one definition of "scalar DECLARE": the statement verdict and the
    // SET-target scope can never disagree.
    private static bool IsScalarDeclaration(DeclareVariableStatement declare) =>
        declare.Declarations.All(d =>
            d is DeclareVariableElement element &&
            element.DataType is (SqlDataTypeReference or UserDataTypeReference or XmlDataTypeReference) and
                not SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Cursor });

    private static BatchVariableScope CollectBatchScope(TSqlBatch batch)
    {
        var scalars = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nonTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in batch.Statements)
        {
            if (statement is DeclareVariableStatement declare)
            {
                var isScalar = IsScalarDeclaration(declare);
                foreach (var declaration in declare.Declarations)
                {
                    if (declaration?.VariableName?.Value is not { } declaredName)
                        continue;

                    nonTables.Add(declaredName);
                    if (isScalar)
                        scalars.TryAdd(declaredName, DeclarationEnd(statement));
                }
            }
            else if (statement is DeclareTableVariableStatement declareTable &&
                declareTable.Body?.VariableName?.Value is { } tableName)
            {
                tables.TryAdd(tableName, DeclarationEnd(statement));
            }
        }

        return new BatchVariableScope(scalars, tables, nonTables);
    }

    private static bool HasUnprovenTableVariableUse(TSqlFragment statement, BatchVariableScope scope)
    {
        var visitor = new TableVariableUseVisitor(scope);
        statement.Accept(visitor);
        return visitor.HasUnprovenUse;
    }

    private sealed class TableVariableUseVisitor(BatchVariableScope scope) : TSqlFragmentVisitor
    {
        internal bool HasUnprovenUse { get; private set; }

        public override void ExplicitVisit(VariableTableReference node)
        {
            // Table position: undeclared, scalar, parameter, other-batch,
            // not-yet-declared, or dual-declared names are not proven local.
            // The inner VariableReference is the table name, not a scalar use.
            if (!scope.IsProvenTableVariable(node))
                HasUnprovenUse = true;
        }

        public override void ExplicitVisit(VariableReference node)
        {
            // Scalar position: a declared table variable is never a scalar.
            if (scope.IsTableVariableName(node.Name))
                HasUnprovenUse = true;
            base.ExplicitVisit(node);
        }
    }

    private static bool IsProvenScalarAssignment(SetVariableStatement set, BatchVariableScope scope)
    {
        // Only plain `SET @v = <rhs>`; cursor assignments and compound
        // operators (+=, ...) stay denied.
        if (set.AssignmentKind != AssignmentKind.Equals || set.Expression is null)
            return false;
        // The target is the variable itself: a member (@v.Member), a static
        // member (@v::Member) or a method call (@v.Method(...)) is another shape.
        if (set.Identifier is not null ||
            set.SeparatorType != SeparatorType.NotSpecified ||
            set.FunctionCallExists)
            return false;
        return scope.IsProvenScalarLocal(set.Variable);
    }

    private static StatementClassification ClassifyDmlWrites(
        TargetResolution primary,
        OutputIntoClause? outputInto,
        TSqlFragment statement,
        BatchVariableScope scope)
    {
        // 011/T3: a proven table-variable write is session-local; it carries
        // no SchemaObjectName and never enters the #temp name check below.
        // Any other resolution kind (ambiguous, unsupported) is denied.
        var hasSessionLocal = false;
        var targets = new List<SchemaObjectName?>();
        if (primary.Kind == TargetResolutionKind.Resolved)
            targets.Add(primary.Name);
        else if (primary.Kind == TargetResolutionKind.TableVariable)
            hasSessionLocal = true;
        else
            return StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

        if (outputInto is not null)
        {
            var outputTarget = ResolveDirectTarget(outputInto.IntoTable, scope);
            if (outputTarget.Kind == TargetResolutionKind.Resolved)
                targets.Add(outputTarget.Name);
            else if (outputTarget.Kind == TargetResolutionKind.TableVariable)
                hasSessionLocal = true;
            else
                return StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);
        }

        // INSERT ... SELECT (UPDATE/DELETE/INSERT/MERGE ... OUTPUT) hides writes that are not the statement target.
        var nested = new NestedDmlWriteVisitor(scope);
        statement.Accept(nested);
        if (nested.Unsupported)
            return StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);
        targets.AddRange(nested.Targets);
        hasSessionLocal |= nested.HasTableVariableTarget;

        var hasPersistent = false;
        foreach (var target in targets)
        {
            if (IsLocalTemp(target))
                hasSessionLocal = true;
            else
                hasPersistent = true;
        }

        return new StatementClassification(null, hasSessionLocal, hasPersistent);
    }

    private static TargetResolution ResolveTarget(TableReference? target, FromClause? fromClause, BatchVariableScope scope)
    {
        // 011/T3: an @name target is the variable itself (never a FROM alias).
        if (target is VariableTableReference)
            return ResolveDirectTarget(target, scope);

        if (target is not NamedTableReference named)
            return TargetResolution.Unsupported;

        if (fromClause is null)
            return TargetResolution.Resolved(named.SchemaObject);

        var bindings = new Dictionary<string, List<SchemaObjectName?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tableReference in fromClause.TableReferences)
        {
            if (!CollectCorrelationBindings(tableReference, bindings))
                return TargetResolution.Unsupported;
        }

        var key = CorrelationKey(named.SchemaObject);
        if (key is not null && bindings.TryGetValue(key, out var matches))
        {
            if (matches.Count != 1)
                return TargetResolution.Ambiguous;
            return matches[0] is null
                ? TargetResolution.Unsupported
                : TargetResolution.Resolved(matches[0]!);
        }

        return TargetResolution.Resolved(named.SchemaObject);
    }

    private static TargetResolution ResolveDirectTarget(TableReference? target, BatchVariableScope scope) =>
        target switch
        {
            NamedTableReference named => TargetResolution.Resolved(named.SchemaObject),
            // 011/T3: only a proven same-batch table variable is a local target.
            VariableTableReference variable when scope.IsProvenTableVariable(variable) =>
                TargetResolution.TableVariable,
            _ => TargetResolution.Unsupported,
        };

    private static bool CollectCorrelationBindings(
        TableReference tableReference,
        IDictionary<string, List<SchemaObjectName?>> bindings) =>
        tableReference switch
        {
            NamedTableReference named => AddNamedBinding(named, bindings),
            JoinTableReference join =>
                CollectCorrelationBindings(join.FirstTableReference, bindings) &&
                CollectCorrelationBindings(join.SecondTableReference, bindings),
            JoinParenthesisTableReference paren =>
                CollectCorrelationBindings(paren.Join, bindings),
            OdbcQualifiedJoinTableReference odbc =>
                CollectCorrelationBindings(odbc.TableReference, bindings),
            PivotedTableReference pivoted =>
                CollectPivotBindings(pivoted.TableReference, pivoted, bindings),
            UnpivotedTableReference unpivoted =>
                CollectPivotBindings(unpivoted.TableReference, unpivoted, bindings),
            _ => AddNonNamedBinding(tableReference, bindings),
        };

    // The pivot alias names the result, not a base table. An unproven inner must not fall through to the DML token.
    private static bool CollectPivotBindings(
        TableReference? inner,
        TableReference pivot,
        IDictionary<string, List<SchemaObjectName?>> bindings) =>
        inner is not null &&
        CollectCorrelationBindings(inner, bindings) &&
        AddNonNamedBinding(pivot, bindings);

    private static bool AddNamedBinding(
        NamedTableReference named,
        IDictionary<string, List<SchemaObjectName?>> bindings)
    {
        var key = named.Alias?.Value ?? CorrelationKey(named.SchemaObject);
        if (key is null)
            return false;

        if (!bindings.TryGetValue(key, out var matches))
        {
            matches = [];
            bindings[key] = matches;
        }

        matches.Add(named.SchemaObject);
        return true;
    }

    private static bool AddNonNamedBinding(
        TableReference tableReference,
        IDictionary<string, List<SchemaObjectName?>> bindings)
    {
        // Any aliased non-named source is unprovable as a real #temp object.
        if (tableReference is TableReferenceWithAlias withAlias)
        {
            var alias = withAlias.Alias?.Value;
            if (alias is null)
                return true;

            if (!bindings.TryGetValue(alias, out var matches))
            {
                matches = [];
                bindings[alias] = matches;
            }

            matches.Add(null);
            return true;
        }

        // Unknown FROM shape: fail closed rather than treating the target token as local.
        return false;
    }

    private static string? CorrelationKey(SchemaObjectName name) =>
        name.Identifiers.Count == 0 ? null : name.BaseIdentifier.Value;

    private static bool IsDirectMutation(TSqlStatement statement) =>
        statement is InsertStatement or UpdateStatement or DeleteStatement or MergeStatement;

    private static bool IsWrite(TSqlStatement statement) =>
        IsDirectMutation(statement) ||
        statement is CreateTableStatement or AlterTableStatement or DropTableStatement or
            TruncateTableStatement or CreateIndexStatement;

    private static SchemaObjectName? GetName(TableReference? target) => target switch
    {
        NamedTableReference named => named.SchemaObject,
        _ => null,
    };

    private static bool IsSupportedLocalTempAlter(SchemaObjectName? name, TableDefinition? definition) =>
        IsLocalTemp(name) &&
        definition is not null &&
        definition.Indexes.Count == 0 &&
        definition.SystemTimePeriod is null &&
        definition.ColumnDefinitions.Count + definition.TableConstraints.Count > 0 &&
        definition.TableConstraints.All(c =>
            c is CheckConstraintDefinition ||
            c is DefaultConstraintDefinition ||
            c is NullableConstraintDefinition ||
            c is UniqueConstraintDefinition);

    private static bool IsLocalTemp(SchemaObjectName? name) =>
        name?.Identifiers.Count == 1 &&
        name.BaseIdentifier.Value.StartsWith('#') &&
        !name.BaseIdentifier.Value.StartsWith("##", StringComparison.Ordinal);

    private readonly record struct StatementClassification(
        SqlSafetyReason? DenyReason,
        bool HasSessionLocalWrite,
        bool HasPersistentWrite)
    {
        internal static StatementClassification ReadOnly { get; } = new(null, false, false);
        internal static StatementClassification SessionLocal { get; } = new(null, true, false);
        internal static StatementClassification Persistent { get; } = new(null, false, true);
        internal static StatementClassification Denied(SqlSafetyReason reason) => new(reason, false, false);
    }

    private enum TargetResolutionKind
    {
        Resolved,
        Ambiguous,
        Unsupported,
        TableVariable,
    }

    private readonly record struct TargetResolution(TargetResolutionKind Kind, SchemaObjectName? Name)
    {
        internal static TargetResolution Resolved(SchemaObjectName name) =>
            new(TargetResolutionKind.Resolved, name);

        internal static TargetResolution Ambiguous { get; } =
            new(TargetResolutionKind.Ambiguous, null);

        internal static TargetResolution Unsupported { get; } =
            new(TargetResolutionKind.Unsupported, null);

        internal static TargetResolution TableVariable { get; } =
            new(TargetResolutionKind.TableVariable, null);
    }

    private static SqlSafetyDecision Allowed(bool hasMutation = false, bool hasSessionLocal = false) =>
        new(true, SqlSafetyReason.Allowed, hasMutation) { HasSessionLocalWork = hasSessionLocal };

    private static SqlSafetyDecision Denied(SqlSafetyReason reason, string? detail = null) =>
        new(false, reason, Detail: detail);

    private sealed class NestedDmlWriteVisitor(BatchVariableScope scope) : TSqlFragmentVisitor
    {
        internal bool Unsupported { get; private set; }

        internal bool HasTableVariableTarget { get; private set; }

        internal List<SchemaObjectName?> Targets { get; } = [];

        public override void ExplicitVisit(DataModificationTableReference node)
        {
            var spec = node.DataModificationSpecification;
            if (spec is null)
            {
                Unsupported = true;
                return;
            }

            var resolution = spec switch
            {
                UpdateSpecification update => ResolveTarget(update.Target, update.FromClause, scope),
                DeleteSpecification delete => ResolveTarget(delete.Target, delete.FromClause, scope),
                InsertSpecification or MergeSpecification => ResolveDirectTarget(spec.Target, scope),
                _ => TargetResolution.Unsupported,
            };

            if (!TryRecord(resolution))
                return;

            if (spec.OutputIntoClause is { } outputInto &&
                !TryRecord(ResolveDirectTarget(outputInto.IntoTable, scope)))
            {
                return;
            }

            base.ExplicitVisit(node);
        }

        private bool TryRecord(TargetResolution resolution)
        {
            if (resolution.Kind == TargetResolutionKind.TableVariable)
            {
                HasTableVariableTarget = true;
                return true;
            }

            if (resolution.Kind != TargetResolutionKind.Resolved || resolution.Name is null)
            {
                Unsupported = true;
                return false;
            }

            Targets.Add(resolution.Name);
            return true;
        }
    }

    private sealed class SafetyInspectionVisitor : TSqlFragmentVisitor
    {
        // 011/T3: OUTPUT INTO @t is local only for a table variable proven in the batch being walked.
        private BatchVariableScope _scope = BatchVariableScope.Empty;
        private readonly Dictionary<TSqlBatch, BatchVariableScope> _scopes = [];
        private readonly Stack<HashSet<string>> _queryAliases = new();
        private readonly Stack<SchemaObjectName> _xmlMethodNames = new();

        internal bool HasCrossDatabaseReference { get; private set; }
        internal bool HasExternalAccess { get; private set; }
        internal bool HasStatefulExpression { get; private set; }
        internal bool HasStatefulTableSource { get; private set; }
        internal bool HasExecuteInsertSource { get; private set; }
        internal bool HasHashNamedCommonTableExpression { get; private set; }
        internal bool HasSelectInto { get; private set; }
        internal bool HasNonLocalSelectInto { get; private set; }
        internal bool HasNonLocalOutputInto { get; private set; }

        // The scope built during the inspection walk, reused by statement
        // classification. Classify walks the whole script before it classifies
        // any batch, so every batch has one.
        internal BatchVariableScope ScopeOf(TSqlBatch batch) => _scopes[batch];

        public override void ExplicitVisit(TSqlBatch node)
        {
            _scope = CollectBatchScope(node);
            _scopes[node] = _scope;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into is not null)
            {
                HasSelectInto = true;
                HasNonLocalSelectInto |= !IsLocalTemp(node.Into);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            _queryAliases.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            try
            {
                base.ExplicitVisit(node);
            }
            finally
            {
                _queryAliases.Pop();
            }
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (_queryAliases.TryPeek(out var aliases))
            {
                var alias = node.Alias?.Value ?? node.SchemaObject.BaseIdentifier.Value;
                if (!string.IsNullOrEmpty(alias))
                    aliases.Add(alias);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            var name = node.SchemaObject;
            var isXmlNodesMethod = name is not null &&
                name.Identifiers.Count == 3 &&
                string.Equals(name.BaseIdentifier.Value, "nodes", StringComparison.OrdinalIgnoreCase) &&
                _queryAliases.TryPeek(out var aliases) &&
                aliases.Contains(name.Identifiers[0].Value);

            if (isXmlNodesMethod)
                _xmlMethodNames.Push(name!);

            base.ExplicitVisit(node);

            if (isXmlNodesMethod)
                _xmlMethodNames.Pop();
        }

        public override void ExplicitVisit(SchemaObjectName node)
        {
            var isRecognizedXmlMethodName = _xmlMethodNames.TryPeek(out var methodName) &&
                ReferenceEquals(methodName, node);
            if (!isRecognizedXmlMethodName && node.Identifiers.Count > 2)
            {
                HasCrossDatabaseReference = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OpenRowsetTableReference node)
        {
            HasExternalAccess = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AdHocTableReference node)
        {
            HasExternalAccess = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OpenQueryTableReference node)
        {
            HasExternalAccess = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BulkOpenRowset node)
        {
            HasExternalAccess = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OpenXmlTableReference node)
        {
            HasStatefulTableSource = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NextValueForExpression node)
        {
            HasStatefulExpression = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecuteInsertSource node)
        {
            HasExecuteInsertSource = true;
            base.ExplicitVisit(node);
        }

        // 017: a CTE named like a temp table could stand in for one as a DML
        // target and write through to its base table. No session-local
        // workflow needs such a name, so the shape is denied outright.
        public override void ExplicitVisit(CommonTableExpression node)
        {
            if (node.ExpressionName?.Value is { } name && name.StartsWith('#'))
                HasHashNamedCommonTableExpression = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OutputIntoClause node)
        {
            var isProvenTableVariable =
                _scope.IsProvenTableVariable(node.IntoTable as VariableTableReference);
            HasNonLocalOutputInto |= !isProvenTableVariable && !IsLocalTemp(GetName(node.IntoTable));
            base.ExplicitVisit(node);
        }
    }
}

internal sealed record SqlHarnessParameter(
    string Name,
    SqlDbType Type,
    object Value,
    int? Size,
    byte? Precision = null,
    byte? Scale = null,
    string? UdtTypeName = null);

internal static partial class SqlParameterParser
{
    private const int MaximumNVarCharSize = 4000;
    private const int MaximumVarCharSize = 8000;
    private const int SqlMaxSize = -1;
    private static readonly DateTime SmallDateTimeMin = new(1900, 1, 1);
    private static readonly DateTime SmallDateTimeMax = new(2079, 6, 6, 23, 59, 0);

    /// <summary>
    /// Legacy text adapter kept for compatibility and characterization tests only (012/final
    /// F3): no production call site uses it. Production composes
    /// <see cref="SqlParameterInputs.Resolve"/> and <see cref="Bind(IEnumerable{SqlHarnessParameterInput})"/>
    /// itself (see <see cref="SqlHarnessModule"/>).
    /// </summary>
    internal static IReadOnlyList<SqlHarnessParameter> Parse(IReadOnlyList<string> inputs) =>
        // Lazy on purpose: each declaration is read and bound before the next one is read,
        // so the first failing declaration decides the error, whatever kind of failure it is.
        Bind(inputs.Select(ToInput));

    internal static SqlHarnessParameter ParseOne(string input) => Bind(ToInput(input));

    /// <summary>The one binder. Declaration text and structured callers both end here.</summary>
    internal static IReadOnlyList<SqlHarnessParameter> Bind(IEnumerable<SqlHarnessParameterInput> inputs)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameters = new List<SqlHarnessParameter>();

        foreach (var input in inputs)
        {
            var parameter = Bind(input);
            if (!names.Add(parameter.Name))
            {
                throw new SqlHarnessSafetyException($"Duplicate SQL parameter '{parameter.Name}'.");
            }

            parameters.Add(parameter);
        }

        return parameters;
    }

    /// <summary>
    /// Legacy declaration text to the model: name=value, name:type=value, name:null, name:type:null.
    /// Only the grammar is decided here. Name, type and value are validated by <see cref="Bind(SqlHarnessParameterInput)"/>.
    /// </summary>
    internal static SqlHarnessParameterInput ToInput(string input)
    {
        var equalsIndex = input.IndexOf('=');
        if (equalsIndex < 0)
        {
            var lastColon = input.LastIndexOf(':');
            if (lastColon <= 0 || input[(lastColon + 1)..] != "null")
            {
                throw new SqlHarnessSafetyException(
                    "SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.");
            }

            return SplitDeclaration(input[..lastColon], value: null);
        }

        return SplitDeclaration(input[..equalsIndex], input[(equalsIndex + 1)..]);
    }

    // The name ends at the first ':'. Everything after it is the type token, so decimal(10,2) stays whole.
    private static SqlHarnessParameterInput SplitDeclaration(string declaration, string? value)
    {
        var typeSeparator = declaration.IndexOf(':');
        return typeSeparator < 0
            ? new(declaration, null, value)
            : new(declaration[..typeSeparator], declaration[(typeSeparator + 1)..], value);
    }

    internal static SqlHarnessParameter Bind(SqlHarnessParameterInput input)
    {
        // A null entry has no name. Declaration text cannot produce one.
        if (input is null)
            throw new SqlHarnessSafetyException("Invalid SQL parameter name.");
        var name = input.Name;
        var type = input.Type;
        var value = input.Value;
        ValidateName(name);

        if (type is null)
        {
            return value is null
                ? CreateNull(name, SqlDbType.NVarChar, size: null)
                : CreateUnicodeString(name, value, max: false);
        }

        try
        {
            return value is null ? CreateTypedNull(name, type) : BindTyped(name, type, value);
        }
        catch (SqlHarnessSafetyException exception) when (exception.IsParameterValue)
        {
            throw;
        }
        catch (SqlHarnessSafetyException exception) when (IsInvalidParameterValue(exception))
        {
            throw SqlHarnessSafetyException.InvalidParameter(name, type, exception.InnerException ?? exception.Diagnostic);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException or ArgumentException)
        {
            throw SqlHarnessSafetyException.InvalidParameter(name, type, exception);
        }
    }

    private static bool IsInvalidParameterValue(SqlHarnessSafetyException exception) =>
        !exception.IsParameterValue
        && exception.Message.StartsWith("Invalid value for SQL parameter '", StringComparison.Ordinal);

    private static SqlHarnessParameter BindTyped(string name, string type, string value)
    {
        if (type is "decimal" or "numeric")
        {
            return CreateDecimal(name, value, precision: null, scale: null);
        }

        var decimalMatch = DecimalOrNumericTypePattern().Match(type);
        if (decimalMatch.Success)
        {
            var precision = byte.Parse(decimalMatch.Groups["precision"].Value, CultureInfo.InvariantCulture);
            var scale = byte.Parse(decimalMatch.Groups["scale"].Value, CultureInfo.InvariantCulture);
            if (precision is < 1 or > 38 || scale > precision)
            {
                throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{type}'.");
            }

            return CreateDecimal(name, value, precision, scale);
        }

        var sizedMatch = SizedTypePattern().Match(type);
        if (sizedMatch.Success)
        {
            return CreateSizedType(name, sizedMatch.Groups["base"].Value, sizedMatch.Groups["size"].Value, value);
        }

        return type switch
        {
            "nvarchar" => CreateUnicodeString(name, value, max: false),
            "varchar" => CreateAnsiString(name, value, SqlDbType.VarChar, max: false),
            "char" => CreateFixedString(name, value, SqlDbType.Char, MaximumVarCharSize),
            "nchar" => CreateFixedString(name, value, SqlDbType.NChar, MaximumNVarCharSize),
            "varbinary" => CreateBinary(name, value, max: false),
            "int" => new($"@{name}", SqlDbType.Int, int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), null),
            "bigint" => new($"@{name}", SqlDbType.BigInt, long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), null),
            "smallint" => new($"@{name}", SqlDbType.SmallInt, short.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), null),
            "tinyint" => new($"@{name}", SqlDbType.TinyInt, byte.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture), null),
            "bit" => new($"@{name}", SqlDbType.Bit, ParseBit(value), null),
            "float" => new($"@{name}", SqlDbType.Float, ParseFiniteDouble(value), null),
            "real" => new($"@{name}", SqlDbType.Real, ParseFiniteSingle(value), null),
            "money" => new($"@{name}", SqlDbType.Money, ParseMoney(value), null),
            "smallmoney" => new($"@{name}", SqlDbType.SmallMoney, ParseSmallMoney(value), null),
            "date" => new($"@{name}", SqlDbType.Date, DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None), null),
            "time" => new($"@{name}", SqlDbType.Time, ParseTime(value), null),
            "datetime" => CreateDateTime(name, value),
            "datetime2" => new($"@{name}", SqlDbType.DateTime2, DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), null),
            "smalldatetime" => CreateSmallDateTime(name, value),
            "datetimeoffset" => CreateDateTimeOffset(name, value),
            "uniqueidentifier" => new($"@{name}", SqlDbType.UniqueIdentifier, Guid.Parse(value), null),
            "hierarchyid" => CreateHierarchyId(name, value),
            "geography" => CreateGeography(name, value),
            "geometry" => CreateGeometry(name, value),
            _ => throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{type}'."),
        };
    }

    private static SqlHarnessParameter CreateTypedNull(string name, string type)
    {
        if (type is "decimal" or "numeric")
            return CreateNull(name, SqlDbType.Decimal, null);

        var decimalMatch = DecimalOrNumericTypePattern().Match(type);
        if (decimalMatch.Success)
        {
            var precision = byte.Parse(decimalMatch.Groups["precision"].Value, CultureInfo.InvariantCulture);
            var scale = byte.Parse(decimalMatch.Groups["scale"].Value, CultureInfo.InvariantCulture);
            if (precision is < 1 or > 38 || scale > precision)
                throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{type}'.");
            return new($"@{name}", SqlDbType.Decimal, DBNull.Value, null, precision, scale);
        }

        var sizedMatch = SizedTypePattern().Match(type);
        if (sizedMatch.Success)
        {
            var (sqlType, size) = ResolveSizedTypeMetadata(sizedMatch.Groups["base"].Value, sizedMatch.Groups["size"].Value);
            return CreateNull(name, sqlType, size);
        }

        return type switch
        {
            "nvarchar" => CreateNull(name, SqlDbType.NVarChar, null),
            "varchar" => CreateNull(name, SqlDbType.VarChar, null),
            "char" => CreateNull(name, SqlDbType.Char, null),
            "nchar" => CreateNull(name, SqlDbType.NChar, null),
            "varbinary" => CreateNull(name, SqlDbType.VarBinary, null),
            "int" => CreateNull(name, SqlDbType.Int, null),
            "bigint" => CreateNull(name, SqlDbType.BigInt, null),
            "smallint" => CreateNull(name, SqlDbType.SmallInt, null),
            "tinyint" => CreateNull(name, SqlDbType.TinyInt, null),
            "bit" => CreateNull(name, SqlDbType.Bit, null),
            "float" => CreateNull(name, SqlDbType.Float, null),
            "real" => CreateNull(name, SqlDbType.Real, null),
            "money" => CreateNull(name, SqlDbType.Money, null),
            "smallmoney" => CreateNull(name, SqlDbType.SmallMoney, null),
            "date" => CreateNull(name, SqlDbType.Date, null),
            "time" => CreateNull(name, SqlDbType.Time, null),
            "datetime" => CreateNull(name, SqlDbType.DateTime, null),
            "datetime2" => CreateNull(name, SqlDbType.DateTime2, null),
            "smalldatetime" => CreateNull(name, SqlDbType.SmallDateTime, null),
            "datetimeoffset" => CreateNull(name, SqlDbType.DateTimeOffset, null),
            "uniqueidentifier" => CreateNull(name, SqlDbType.UniqueIdentifier, null),
            "hierarchyid" => CreateUdtNull(name, "HierarchyId"),
            "geography" => CreateUdtNull(name, "Geography"),
            "geometry" => CreateUdtNull(name, "Geometry"),
            _ => throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{type}'."),
        };
    }

    private static SqlHarnessParameter CreateNull(string name, SqlDbType type, int? size) =>
        new($"@{name}", type, DBNull.Value, size);

    private static SqlHarnessParameter CreateUdtNull(string name, string udtTypeName) =>
        new($"@{name}", SqlDbType.Udt, DBNull.Value, null, UdtTypeName: udtTypeName);

    private static SqlHarnessParameter CreateUnicodeString(string name, string value, bool max)
    {
        if (max || value.Length > MaximumNVarCharSize)
            return new($"@{name}", SqlDbType.NVarChar, value, SqlMaxSize);

        return new($"@{name}", SqlDbType.NVarChar, value, Math.Max(1, value.Length));
    }

    private static SqlHarnessParameter CreateAnsiString(string name, string value, SqlDbType type, bool max)
    {
        if (max || value.Length > MaximumVarCharSize)
            return new($"@{name}", type, value, SqlMaxSize);

        return new($"@{name}", type, value, Math.Max(1, value.Length));
    }

    private static SqlHarnessParameter CreateFixedString(string name, string value, SqlDbType type, int maximum)
    {
        if (value.Length is 0 || value.Length > maximum)
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");

        return new($"@{name}", type, value, value.Length);
    }

    private static SqlHarnessParameter CreateSizedType(string name, string typeBase, string sizeToken, string value)
    {
        var (sqlType, size) = ResolveSizedTypeMetadata(typeBase, sizeToken);
        if (sqlType is SqlDbType.VarBinary or SqlDbType.Binary)
        {
            var bytes = DecodeBase64(name, value);
            if (size != SqlMaxSize && bytes.Length > size)
                throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");

            var bindSize = size == SqlMaxSize
                ? SqlMaxSize
                : sqlType == SqlDbType.Binary ? size : Math.Max(bytes.Length, 1);
            return new($"@{name}", sqlType, bytes, bindSize);
        }

        if (size == SqlMaxSize)
        {
            return sqlType == SqlDbType.NVarChar
                ? CreateUnicodeString(name, value, max: true)
                : CreateAnsiString(name, value, sqlType, max: true);
        }

        if (value.Length > size)
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");

        return new($"@{name}", sqlType, value, size);
    }

    private static (SqlDbType Type, int Size) ResolveSizedTypeMetadata(string typeBase, string sizeToken)
    {
        var isMax = sizeToken == "max";
        return typeBase switch
        {
            "nvarchar" => (SqlDbType.NVarChar, isMax ? SqlMaxSize : ParseBoundedSize(sizeToken, 1, MaximumNVarCharSize)),
            "varchar" => (SqlDbType.VarChar, isMax ? SqlMaxSize : ParseBoundedSize(sizeToken, 1, MaximumVarCharSize)),
            "nchar" => (SqlDbType.NChar, isMax
                ? throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{typeBase}({sizeToken})'.")
                : ParseBoundedSize(sizeToken, 1, MaximumNVarCharSize)),
            "char" => (SqlDbType.Char, isMax
                ? throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{typeBase}({sizeToken})'.")
                : ParseBoundedSize(sizeToken, 1, MaximumVarCharSize)),
            "varbinary" => (SqlDbType.VarBinary, isMax ? SqlMaxSize : ParseBoundedSize(sizeToken, 1, MaximumVarCharSize)),
            "binary" => (SqlDbType.Binary, isMax
                ? throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{typeBase}({sizeToken})'.")
                : ParseBoundedSize(sizeToken, 1, MaximumVarCharSize)),
            _ => throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{typeBase}({sizeToken})'."),
        };
    }

    private static int ParseBoundedSize(string sizeToken, int min, int max)
    {
        if (!int.TryParse(sizeToken, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < min || size > max)
            throw new SqlHarnessSafetyException($"Unsupported SQL parameter type size '{sizeToken}'.");
        return size;
    }

    private static SqlHarnessParameter CreateBinary(string name, string value, bool max)
    {
        var bytes = DecodeBase64(name, value);
        if (max || bytes.Length > MaximumVarCharSize)
            return new($"@{name}", SqlDbType.VarBinary, bytes, SqlMaxSize);

        return new($"@{name}", SqlDbType.VarBinary, bytes, Math.Max(1, bytes.Length));
    }

    private static byte[] DecodeBase64(string name, string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.", exception);
        }
    }

    private static SqlHarnessParameter CreateHierarchyId(string name, string value)
    {
        if (value.Length == 0)
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");

        try
        {
            var hierarchy = SqlHierarchyId.Parse(value);
            return new($"@{name}", SqlDbType.Udt, hierarchy, null, UdtTypeName: "HierarchyId");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or ArgumentNullException)
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.", exception);
        }
    }

    private static SqlHarnessParameter CreateGeography(string name, string value)
    {
        var (srid, wkt) = SplitSridAndWkt(name, value, defaultSrid: 4326);
        try
        {
            var geography = SqlGeography.STGeomFromText(new SqlChars(wkt), srid);
            if (geography.IsNull)
                throw new FormatException("Geography value is null.");
            return new($"@{name}", SqlDbType.Udt, geography, null, UdtTypeName: "Geography");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or ArgumentNullException)
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.", exception);
        }
    }

    private static SqlHarnessParameter CreateGeometry(string name, string value)
    {
        var (srid, wkt) = SplitSridAndWkt(name, value, defaultSrid: 0);
        try
        {
            var geometry = SqlGeometry.STGeomFromText(new SqlChars(wkt), srid);
            if (geometry.IsNull)
                throw new FormatException("Geometry value is null.");
            return new($"@{name}", SqlDbType.Udt, geometry, null, UdtTypeName: "Geometry");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or ArgumentNullException)
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.", exception);
        }
    }

    private static (int Srid, string Wkt) SplitSridAndWkt(string name, string value, int defaultSrid)
    {
        if (value.Length == 0)
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");

        // Optional "srid;WKT" form, e.g. 4326;POINT(-122.3 47.6). Bare WKT uses the type default.
        var separator = value.IndexOf(';');
        if (separator <= 0)
            return (defaultSrid, value);

        var sridText = value[..separator];
        var wkt = value[(separator + 1)..];
        if (wkt.Length == 0
            || !int.TryParse(sridText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid < 0)
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");
        }

        return (srid, wkt);
    }

    private static SqlHarnessParameter CreateDecimal(string name, string value, byte? precision, byte? scale)
    {
        var parsed = decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (precision is { } p && scale is { } s)
        {
            EnsureDecimalFits(name, parsed, p, s);
            return new($"@{name}", SqlDbType.Decimal, parsed, null, p, s);
        }

        return new($"@{name}", SqlDbType.Decimal, parsed, null);
    }

    private static void EnsureDecimalFits(string name, decimal value, byte precision, byte scale)
    {
        var absolute = Math.Abs(value);
        var bits = decimal.GetBits(absolute);
        var valueScale = (bits[3] >> 16) & 0x7F;
        if (valueScale > scale)
        {
            // Extra fractional digits beyond scale are only allowed when they are trailing zeros.
            var factor = 1m;
            for (var i = 0; i < scale; i++)
                factor *= 10m;

            var shifted = absolute * factor;
            if (shifted != decimal.Truncate(shifted))
            {
                throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");
            }
        }

        var integerPart = decimal.Truncate(absolute);
        var maxIntegerDigits = precision - scale;
        if (integerPart == 0m)
            return;

        var digits = 0;
        var remaining = integerPart;
        while (remaining >= 1m)
        {
            remaining = decimal.Truncate(remaining / 10m);
            digits++;
            if (digits > maxIntegerDigits)
            {
                throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");
            }
        }
    }

    private static SqlHarnessParameter CreateDateTime(string name, string value)
    {
        var parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (parsed < SqlDateTime.MinValue.Value || parsed > SqlDateTime.MaxValue.Value)
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");
        }

        return new($"@{name}", SqlDbType.DateTime, parsed, null);
    }

    private static SqlHarnessParameter CreateSmallDateTime(string name, string value)
    {
        var parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (parsed < SmallDateTimeMin || parsed > SmallDateTimeMax)
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");

        return new($"@{name}", SqlDbType.SmallDateTime, parsed, null);
    }

    private static SqlHarnessParameter CreateDateTimeOffset(string name, string value)
    {
        if (!HasExplicitDateTimeOffset(value))
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");
        }

        var parsed = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new($"@{name}", SqlDbType.DateTimeOffset, parsed, null);
    }

    private static bool HasExplicitDateTimeOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
            return true;

        return ExplicitOffsetPattern().IsMatch(value);
    }

    private static TimeSpan ParseTime(string value)
    {
        if (TimeSpan.TryParseExact(
                value,
                ["c", @"hh\:mm\:ss", @"hh\:mm\:ss\.FFFFFFF", @"h\:mm\:ss", @"h\:mm\:ss\.FFFFFFF"],
                CultureInfo.InvariantCulture,
                out var exact))
        {
            return exact;
        }

        return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
    }

    private static double ParseFiniteDouble(string value)
    {
        var parsed = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(parsed))
            throw new FormatException("Non-finite float value.");
        return parsed;
    }

    private static float ParseFiniteSingle(string value)
    {
        var parsed = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!float.IsFinite(parsed))
            throw new FormatException("Non-finite real value.");
        return parsed;
    }

    private static decimal ParseMoney(string value)
    {
        var parsed = decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return new SqlMoney(parsed).Value;
    }

    private static decimal ParseSmallMoney(string value)
    {
        var parsed = decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        // smallmoney: -214,748.3648 to 214,748.3647
        if (parsed < -214748.3648m || parsed > 214748.3647m)
            throw new OverflowException("Smallmoney value out of range.");
        return parsed;
    }

    private static bool ParseBit(string value) => value switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => throw new FormatException("Invalid bit value."),
    };

    private static void ValidateName(string name)
    {
        // A structured caller can hand over a null name; declaration text cannot.
        if (name is null || !NamePattern().IsMatch(name))
        {
            throw new SqlHarnessSafetyException("Invalid SQL parameter name.");
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(decimal|numeric)\((?<precision>[0-9]{1,2}),(?<scale>[0-9]{1,2})\)$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalOrNumericTypePattern();

    [GeneratedRegex(@"^(?<base>nvarchar|varchar|nchar|char|varbinary|binary)\((?<size>max|[0-9]{1,5})\)$", RegexOptions.CultureInvariant)]
    private static partial Regex SizedTypePattern();

    [GeneratedRegex(@"[+-][0-9]{2}:[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffsetPattern();
}

internal static class SqlParameterReferenceValidator
{
    internal static void Validate(
        IReadOnlyList<SqlHarnessParameter> parameters,
        params string?[] batches)
    {
        if (parameters.Count == 0)
            return;

        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in batches.Where(batch => !string.IsNullOrWhiteSpace(batch)))
        {
            var document = SqlServerDocument.Parse(batch!);
            if (document.HasErrors)
                throw new SqlHarnessSafetyException("SQL parameter references could not be parsed.");
            var visitor = new ParameterReferenceVisitor(references);
            document.Fragment.Accept(visitor);
        }

        foreach (var parameter in parameters)
        {
            if (!references.Contains(parameter.Name))
                throw new SqlHarnessSafetyException($"SQL parameter '{parameter.Name}' is not referenced by the applicable batch.");
        }
    }

    private sealed class ParameterReferenceVisitor(HashSet<string> references) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(VariableReference node)
        {
            references.Add(node.Name);
            base.ExplicitVisit(node);
        }

        // 011/T3: a table-position @name is never a reference to a supplied scalar parameter.
        public override void ExplicitVisit(VariableTableReference node)
        {
        }
    }
}

internal sealed class SqlSetupVariableScopeException() : SqlHarnessSafetyException(
    "A variable declared in setup is referenced by a benchmark batch. Setup variables do not cross into benchmark batches.");

internal static class SqlSetupVariableReferenceValidator
{
    internal static void Validate(
        string? setupSql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        params string?[] variants)
    {
        if (string.IsNullOrWhiteSpace(setupSql) || variants.Length == 0)
            return;

        var setupDocument = SqlServerDocument.Parse(setupSql);
        if (setupDocument.HasErrors || setupDocument.Fragment is not TSqlScript setupScript)
            throw new SqlHarnessSafetyException("SQL parameter references could not be parsed.");

        var setupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in setupScript.Batches)
        {
            foreach (var name in CollectDeclaredVariablePositions(batch).Keys)
                setupNames.Add(name);
        }
        if (setupNames.Count == 0)
            return;

        var parameterNames = parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in variants.Where(text => !string.IsNullOrWhiteSpace(text)))
        {
            var document = SqlServerDocument.Parse(variant!);
            if (document.HasErrors || document.Fragment is not TSqlScript script)
                throw new SqlHarnessSafetyException("SQL parameter references could not be parsed.");

            foreach (var batch in script.Batches)
            {
                var localDeclarations = CollectDeclaredVariablePositions(batch);
                var references = new VariableReferenceCollector();
                batch.Accept(references);
                if (references.Positions.Any(reference => setupNames.Contains(reference.Name) &&
                    !parameterNames.Contains(reference.Name) &&
                    (!localDeclarations.TryGetValue(reference.Name, out var declarationOffset) ||
                     reference.Offset < 0 || declarationOffset >= reference.Offset)))
                {
                    throw new SqlSetupVariableScopeException();
                }
            }
        }
    }

    private static Dictionary<string, int> CollectDeclaredVariablePositions(TSqlBatch batch)
    {
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in batch.Statements)
        {
            if (statement is DeclareVariableStatement declare)
            {
                foreach (var declaration in declare.Declarations)
                {
                    if (declaration?.VariableName is { Value: { } name } variableName)
                        positions.TryAdd(name, variableName.StartOffset);
                }
            }
            else if (statement is DeclareTableVariableStatement declareTable &&
                declareTable.Body?.VariableName is { Value: { } tableName } tableVariableName)
            {
                positions.TryAdd(tableName, tableVariableName.StartOffset);
            }
        }

        return positions;
    }

    private sealed class VariableReferenceCollector : TSqlFragmentVisitor
    {
        internal List<VariableReferencePosition> Positions { get; } = [];

        public override void ExplicitVisit(VariableReference node)
        {
            Positions.Add(new VariableReferencePosition(node.Name, node.StartOffset));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(VariableTableReference node)
        {
            if (node.Variable?.Name is { } name)
                Positions.Add(new VariableReferencePosition(name, node.StartOffset));
            base.ExplicitVisit(node);
        }
    }

    private sealed record VariableReferencePosition(string Name, int Offset);
}