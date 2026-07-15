using System.Data;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlHarness.Core;


internal sealed class SqlHarnessSafetyException(string message, Exception? innerException = null) : Exception(message, innerException);

internal enum SqlUsage
{
    Query,
    PerfSetup,
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

internal sealed record SqlSafetyDecision(bool Allowed, SqlSafetyReason Reason, bool HasMutation = false);

internal sealed class SqlSafetyClassifier
{
    private static readonly HashSet<Type> AllowedFragmentTypes =
    [
        typeof(TSqlScript),
        typeof(TSqlBatch),
        typeof(SelectStatement),
        typeof(QuerySpecification),
        typeof(SelectScalarExpression),
        typeof(SelectStarExpression),
        typeof(FromClause),
        typeof(NamedTableReference),
        typeof(SchemaObjectName),
        typeof(MultiPartIdentifier),
        typeof(Identifier),
        typeof(IdentifierOrValueExpression),
        typeof(ColumnReferenceExpression),
        typeof(IntegerLiteral),
        typeof(NumericLiteral),
        typeof(RealLiteral),
        typeof(MoneyLiteral),
        typeof(StringLiteral),
        typeof(BinaryLiteral),
        typeof(NullLiteral),
        typeof(VariableReference),
        typeof(GlobalVariableExpression),
        typeof(BinaryExpression),
        typeof(UnaryExpression),
        typeof(ParenthesisExpression),
        typeof(BooleanComparisonExpression),
        typeof(BooleanBinaryExpression),
        typeof(BooleanNotExpression),
        typeof(BooleanParenthesisExpression),
        typeof(BooleanIsNullExpression),
        typeof(InPredicate),
        typeof(LikePredicate),
        typeof(BooleanTernaryExpression),
        typeof(ExistsPredicate),
        typeof(ScalarSubquery),
        typeof(FunctionCall),
        typeof(CastCall),
        typeof(ConvertCall),
        typeof(CoalesceExpression),
        typeof(NullIfExpression),
        typeof(SearchedCaseExpression),
        typeof(SearchedWhenClause),
        typeof(SimpleCaseExpression),
        typeof(SimpleWhenClause),
        typeof(WithCtesAndXmlNamespaces),
        typeof(CommonTableExpression),
        typeof(QueryDerivedTable),
        typeof(InlineDerivedTable),
        typeof(QualifiedJoin),
        typeof(UnqualifiedJoin),
        typeof(JoinParenthesisTableReference),
        typeof(SchemaObjectFunctionTableReference),
        typeof(OrderByClause),
        typeof(ExpressionWithSortOrder),
        typeof(GroupByClause),
        typeof(ExpressionGroupingSpecification),
        typeof(HavingClause),
        typeof(TopRowFilter),
        typeof(BinaryQueryExpression),
        typeof(QueryParenthesisExpression),
        typeof(InsertStatement),
        typeof(InsertSpecification),
        typeof(ValuesInsertSource),
        typeof(SelectInsertSource),
        typeof(RowValue),
        typeof(UpdateStatement),
        typeof(UpdateSpecification),
        typeof(AssignmentSetClause),
        typeof(DeleteStatement),
        typeof(DeleteSpecification),
        typeof(MergeStatement),
        typeof(MergeSpecification),
        typeof(MergeActionClause),
        typeof(UpdateMergeAction),
        typeof(OutputIntoClause),
        typeof(CreateTableStatement),
        typeof(TableDefinition),
        typeof(ColumnDefinition),
        typeof(SqlDataTypeReference),
        typeof(CreateIndexStatement),
        typeof(ColumnWithSortOrder),
        typeof(DropTableStatement),
        typeof(DeclareVariableStatement),
        typeof(DeclareVariableElement),
        typeof(WhereClause),
    ];

    internal SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase = null)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
        {
            return Denied(SqlSafetyReason.ParseError);
        }

        var inspection = new SafetyInspectionVisitor();
        fragment.Accept(inspection);
        if (inspection.HasCrossDatabaseReference)
        {
            return Denied(SqlSafetyReason.CrossDatabaseReference);
        }

        if (inspection.HasExternalAccess || inspection.HasStatefulExpression || inspection.HasExecuteInsertSource)
        {
            return Denied(SqlSafetyReason.UnsupportedStatement);
        }

        if (HasUnallowlistedFragment(fragment))
        {
            return Denied(SqlSafetyReason.UnsupportedStatement);
        }

        var statements = script.Batches.SelectMany(batch => batch.Statements).ToArray();
        if (statements.Length == 0)
        {
            return Denied(SqlSafetyReason.UnsupportedStatement);
        }

        return usage switch
        {
            SqlUsage.Query => ClassifyQuery(statements, inspection, database, allowMutation, confirmDatabase),
            SqlUsage.PerfSetup => ClassifyPerfSetup(statements, inspection),
            _ => Denied(SqlSafetyReason.UnsupportedStatement),
        };
    }

    private static SqlSafetyDecision ClassifyQuery(
        IReadOnlyList<TSqlStatement> statements,
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
        foreach (var statement in statements)
        {
            if (statement is SelectStatement)
            {
                continue;
            }

            if (IsSessionOnlyWork(statement))
            {
                continue;
            }

            if (IsDirectMutation(statement))
            {
                hasMutation = true;
                continue;
            }

            return Denied(SqlSafetyReason.UnsupportedStatement);
        }

        if (!hasMutation)
        {
            return Allowed();
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

        return Allowed(hasMutation: true);
    }

    private static bool IsSessionOnlyWork(TSqlStatement statement) => statement switch
    {
        SelectStatement select => select.Into is not null && IsLocalTemp(select.Into),
        CreateTableStatement create => IsLocalTemp(create.SchemaObjectName),
        InsertStatement insert => IsLocalTemp(GetName(insert.InsertSpecification.Target)),
        UpdateStatement update => IsLocalTemp(GetName(update.UpdateSpecification.Target)),
        DeleteStatement delete => IsLocalTemp(GetName(delete.DeleteSpecification.Target)),
        MergeStatement merge => IsLocalTemp(GetName(merge.MergeSpecification.Target)),
        CreateIndexStatement createIndex => IsLocalTemp(createIndex.OnName),
        DropTableStatement drop => drop.Objects.Count > 0 && drop.Objects.All(IsLocalTemp),
        _ => false,
    };

    private static SqlSafetyDecision ClassifyPerfSetup(
        IReadOnlyList<TSqlStatement> statements,
        SafetyInspectionVisitor inspection)
    {
        if (inspection.HasNonLocalSelectInto || inspection.HasNonLocalOutputInto)
        {
            return Denied(SqlSafetyReason.NonTemporaryWrite);
        }

        foreach (var statement in statements)
        {
            var allowed = statement switch
            {
                SelectStatement => true,
                DeclareVariableStatement => true,
                CreateTableStatement create => IsLocalTemp(create.SchemaObjectName),
                InsertStatement insert => IsLocalTemp(GetName(insert.InsertSpecification.Target)),
                UpdateStatement update => IsLocalTemp(GetName(update.UpdateSpecification.Target)),
                DeleteStatement delete => IsLocalTemp(GetName(delete.DeleteSpecification.Target)),
                CreateIndexStatement createIndex => IsLocalTemp(createIndex.OnName),
                DropTableStatement drop => drop.Objects.Count > 0 && drop.Objects.All(IsLocalTemp),
                _ => false,
            };

            if (!allowed)
            {
                return Denied(IsWrite(statement)
                    ? SqlSafetyReason.NonTemporaryWrite
                    : SqlSafetyReason.UnsupportedStatement);
            }
        }

        return Allowed();
    }

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

    private static bool IsLocalTemp(SchemaObjectName? name) =>
        name?.Identifiers.Count == 1 &&
        name.BaseIdentifier.Value.StartsWith('#') &&
        !name.BaseIdentifier.Value.StartsWith("##", StringComparison.Ordinal);

    private static bool HasUnallowlistedFragment(TSqlFragment root)
    {
        var pending = new Stack<TSqlFragment>();
        var visited = new HashSet<TSqlFragment>(ReferenceEqualityComparer.Instance);
        pending.Push(root);

        while (pending.TryPop(out var fragment))
        {
            if (!visited.Add(fragment))
            {
                continue;
            }

            if (!AllowedFragmentTypes.Contains(fragment.GetType()))
            {
                return true;
            }

            foreach (var property in fragment.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = property.GetValue(fragment);
                }
                catch (Exception)
                {
                    return true;
                }

                if (value is TSqlFragment child)
                {
                    pending.Push(child);
                }
                else if (value is IEnumerable children and not string)
                {
                    foreach (var item in children)
                    {
                        if (item is TSqlFragment childItem)
                        {
                            pending.Push(childItem);
                        }
                    }
                }
            }
        }

        return false;
    }

    private static SqlSafetyDecision Allowed(bool hasMutation = false) =>
        new(true, SqlSafetyReason.Allowed, hasMutation);

    private static SqlSafetyDecision Denied(SqlSafetyReason reason) => new(false, reason);

    private sealed class SafetyInspectionVisitor : TSqlFragmentVisitor
    {
        internal bool HasCrossDatabaseReference { get; private set; }
        internal bool HasExternalAccess { get; private set; }
        internal bool HasStatefulExpression { get; private set; }
        internal bool HasExecuteInsertSource { get; private set; }
        internal bool HasSelectInto { get; private set; }
        internal bool HasNonLocalSelectInto { get; private set; }
        internal bool HasNonLocalOutputInto { get; private set; }

        public override void ExplicitVisit(SchemaObjectName node)
        {
            if (node.Identifiers.Count > 2)
            {
                HasCrossDatabaseReference = true;
            }

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

        public override void ExplicitVisit(OutputIntoClause node)
        {
            HasNonLocalOutputInto |= !IsLocalTemp(GetName(node.IntoTable));
            base.ExplicitVisit(node);
        }
    }
}

internal sealed record SqlHarnessParameter(string Name, SqlDbType Type, object Value, int? Size);

internal static partial class SqlParameterParser
{
    private const int MaximumNVarCharSize = 4000;

    internal static IReadOnlyList<SqlHarnessParameter> Parse(IReadOnlyList<string> inputs)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameters = new List<SqlHarnessParameter>(inputs.Count);

        foreach (var input in inputs)
        {
            var parameter = ParseOne(input);
            if (!names.Add(parameter.Name))
            {
                throw new SqlHarnessSafetyException($"Duplicate SQL parameter '{parameter.Name}'.");
            }

            parameters.Add(parameter);
        }

        return parameters;
    }

    private static SqlHarnessParameter ParseOne(string input)
    {
        var equalsIndex = input.IndexOf('=');
        if (equalsIndex < 0)
        {
            var nullSeparator = input.IndexOf(':');
            if (nullSeparator > 0 && input[(nullSeparator + 1)..] == "null")
            {
                return CreateNull(input[..nullSeparator]);
            }

            throw new SqlHarnessSafetyException("SQL parameter must use name=value, name:type=value, or name:null syntax.");
        }

        var declaration = input[..equalsIndex];
        var value = input[(equalsIndex + 1)..];
        var typeSeparator = declaration.IndexOf(':');
        var name = typeSeparator < 0 ? declaration : declaration[..typeSeparator];
        var type = typeSeparator < 0 ? null : declaration[(typeSeparator + 1)..];
        ValidateName(name);

        if (type is null || type == "nvarchar")
        {
            return CreateNVarChar(name, value);
        }

        try
        {
            return type switch
            {
                "int" => new($"@{name}", SqlDbType.Int, int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), null),
                "bigint" => new($"@{name}", SqlDbType.BigInt, long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), null),
                "decimal" => new($"@{name}", SqlDbType.Decimal, decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), null),
                "bit" => new($"@{name}", SqlDbType.Bit, ParseBit(value), null),
                "date" => new($"@{name}", SqlDbType.Date, DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None), null),
                "datetime2" => new($"@{name}", SqlDbType.DateTime2, DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), null),
                "uniqueidentifier" => new($"@{name}", SqlDbType.UniqueIdentifier, Guid.ParseExact(value, "D"), null),
                _ => throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{type}'."),
            };
        }
        catch (SqlHarnessSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.", exception);
        }
    }

    private static SqlHarnessParameter CreateNull(string name)
    {
        ValidateName(name);
        return new SqlHarnessParameter($"@{name}", SqlDbType.NVarChar, DBNull.Value, null);
    }

    private static SqlHarnessParameter CreateNVarChar(string name, string value)
    {
        if (value.Length > MaximumNVarCharSize)
        {
            throw new SqlHarnessSafetyException($"SQL parameter '{name}' exceeds the nvarchar limit.");
        }

        return new SqlHarnessParameter($"@{name}", SqlDbType.NVarChar, value, Math.Max(1, value.Length));
    }

    private static bool ParseBit(string value) => value switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => throw new FormatException("Invalid bit value."),
    };

    private static void ValidateName(string name)
    {
        if (!NamePattern().IsMatch(name))
        {
            throw new SqlHarnessSafetyException("Invalid SQL parameter name.");
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
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
            var parser = new TSql170Parser(initialQuotedIdentifiers: true);
            var fragment = parser.Parse(new StringReader(batch!), out var errors);
            if (errors.Count > 0)
                throw new SqlHarnessSafetyException("SQL parameter references could not be parsed.");
            var visitor = new ParameterReferenceVisitor(references);
            fragment.Accept(visitor);
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
    }
}
