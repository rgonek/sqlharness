using System.Collections;
using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlHarness.Core;


internal sealed class SqlHarnessSafetyException(string message, Exception? innerException = null) : Exception(message, innerException);

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
    internal string RejectionDescription =>
        Detail is null ? $"{Reason}." : $"{Reason}. {Detail}";
}

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
        typeof(IdentifierLiteral),
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
        typeof(OverClause),
        typeof(WindowFrameClause),
        typeof(WindowDelimiter),
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
        typeof(NullableConstraintDefinition),
        typeof(UniqueConstraintDefinition),
        typeof(CreateIndexStatement),
        typeof(ColumnWithSortOrder),
        typeof(DropTableStatement),
        typeof(DeclareVariableStatement),
        typeof(DeclareVariableElement),
        typeof(WhereClause),
    ];

    private sealed record UnsupportedSyntax(
        IReadOnlyList<string> StatementTypes,
        IReadOnlyList<string> FragmentTypes)
    {
        internal bool Any => StatementTypes.Count > 0 || FragmentTypes.Count > 0;
    }

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

        var unsupported = CollectUnsupportedSyntax(script);
        if (unsupported.Any)
        {
            return Denied(SqlSafetyReason.UnsupportedStatement, FormatUnsupported(unsupported));
        }

        var statements = script.Batches.SelectMany(batch => batch.Statements).ToArray();
        if (statements.Length == 0)
        {
            return Denied(SqlSafetyReason.UnsupportedStatement);
        }

        return usage switch
        {
            SqlUsage.Query => ClassifyQuery(statements, inspection, database, allowMutation, confirmDatabase),
            SqlUsage.CompareSetup => ClassifyCompareSetup(statements, inspection),
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

    private static SqlSafetyDecision ClassifyCompareSetup(
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

    private static UnsupportedSyntax CollectUnsupportedSyntax(TSqlScript script)
    {
        var topLevelStatements = new HashSet<TSqlFragment>(ReferenceEqualityComparer.Instance);
        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                topLevelStatements.Add(statement);
            }
        }

        var statementTypes = new HashSet<string>(StringComparer.Ordinal);
        var fragmentTypes = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<TSqlFragment>();
        var visited = new HashSet<TSqlFragment>(ReferenceEqualityComparer.Instance);
        pending.Push(script);

        while (pending.TryPop(out var fragment))
        {
            if (!visited.Add(fragment))
            {
                continue;
            }

            var type = fragment.GetType();
            if (!AllowedFragmentTypes.Contains(type))
            {
                if (topLevelStatements.Contains(fragment))
                {
                    statementTypes.Add(type.Name);
                }
                else
                {
                    fragmentTypes.Add(type.Name);
                }

                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
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
                    fragmentTypes.Add(type.Name);
                    continue;
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

        return new UnsupportedSyntax(
            statementTypes.Order(StringComparer.Ordinal).ToArray(),
            fragmentTypes.Order(StringComparer.Ordinal).ToArray());
    }

    private static string FormatUnsupported(UnsupportedSyntax unsupported)
    {
        var parts = new List<string>(2);
        if (unsupported.StatementTypes.Count > 0)
        {
            parts.Add($"Unsupported SQL statement types: {string.Join(", ", unsupported.StatementTypes)}.");
        }

        if (unsupported.FragmentTypes.Count > 0)
        {
            parts.Add($"Unsupported AST fragment types: {string.Join(", ", unsupported.FragmentTypes)}.");
        }

        return string.Join(" ", parts);
    }

    private static SqlSafetyDecision Allowed(bool hasMutation = false) =>
        new(true, SqlSafetyReason.Allowed, hasMutation);

    private static SqlSafetyDecision Denied(SqlSafetyReason reason, string? detail = null) =>
        new(false, reason, Detail: detail);

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

internal sealed record SqlHarnessParameter(
    string Name,
    SqlDbType Type,
    object Value,
    int? Size,
    byte? Precision = null,
    byte? Scale = null);

internal static partial class SqlParameterParser
{
    private const int MaximumNVarCharSize = 4000;
    private const int MaximumVarCharSize = 8000;
    private const int SqlMaxSize = -1;
    private static readonly DateTime SmallDateTimeMin = new(1900, 1, 1);
    private static readonly DateTime SmallDateTimeMax = new(2079, 6, 6, 23, 59, 0);

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

    internal static SqlHarnessParameter ParseOne(string input)
    {
        var equalsIndex = input.IndexOf('=');
        if (equalsIndex < 0)
        {
            return ParseNullDeclaration(input);
        }

        var declaration = input[..equalsIndex];
        var value = input[(equalsIndex + 1)..];
        var typeSeparator = declaration.IndexOf(':');
        var name = typeSeparator < 0 ? declaration : declaration[..typeSeparator];
        var type = typeSeparator < 0 ? null : declaration[(typeSeparator + 1)..];
        ValidateName(name);

        if (type is null)
        {
            return CreateUnicodeString(name, value, max: false);
        }

        try
        {
            return BindTyped(name, type, value);
        }
        catch (SqlHarnessSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException or ArgumentException)
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.", exception);
        }
    }

    private static SqlHarnessParameter ParseNullDeclaration(string input)
    {
        var lastColon = input.LastIndexOf(':');
        if (lastColon <= 0 || input[(lastColon + 1)..] != "null")
        {
            throw new SqlHarnessSafetyException(
                "SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.");
        }

        var left = input[..lastColon];
        var typeSeparator = left.IndexOf(':');
        if (typeSeparator < 0)
        {
            ValidateName(left);
            return CreateNull(left, SqlDbType.NVarChar, size: null);
        }

        var name = left[..typeSeparator];
        var type = left[(typeSeparator + 1)..];
        ValidateName(name);
        try
        {
            return CreateTypedNull(name, type);
        }
        catch (SqlHarnessSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException or ArgumentException)
        {
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.", exception);
        }
    }

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
            "hierarchyid" or "geography" or "geometry" => CreateSpatialOrHierarchy(name, value),
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
            "hierarchyid" or "geography" or "geometry" => CreateNull(name, SqlDbType.NVarChar, null),
            _ => throw new SqlHarnessSafetyException($"Unsupported SQL parameter type '{type}'."),
        };
    }

    private static SqlHarnessParameter CreateNull(string name, SqlDbType type, int? size) =>
        new($"@{name}", type, DBNull.Value, size);

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

    private static SqlHarnessParameter CreateSpatialOrHierarchy(string name, string value)
    {
        // Bind path/WKT as nvarchar; SQL can convert or CAST to the native type.
        if (value.Length == 0)
            throw new SqlHarnessSafetyException($"Invalid value for SQL parameter '{name}'.");

        return CreateUnicodeString(name, value, max: value.Length > MaximumNVarCharSize);
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
        if (!NamePattern().IsMatch(name))
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