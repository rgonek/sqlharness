using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.SqlServer.Types;

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
    private static readonly IReadOnlySet<string> NoSessionTempTables =
        new HashSet<string>(StringComparer.Ordinal);

    internal bool HasSessionLocalWork { get; init; }

    internal IReadOnlySet<string> SessionTempTables { get; init; } = NoSessionTempTables;

    internal string RejectionDescription =>
        Detail is null ? $"{Reason}." : $"{Reason}. {Detail}";
}

internal sealed class SqlSafetyClassifier
{
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

        if (inspection.HasExternalAccess ||
            inspection.HasStatefulExpression ||
            inspection.HasStatefulTableSource ||
            inspection.HasExecuteInsertSource)
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
        var hasSessionLocal = false;
        foreach (var statement in statements)
        {
            var classification = ClassifyStatement(statement);
            if (classification.DenyReason is { } deny)
                return Denied(deny);

            hasMutation |= classification.HasPersistentWrite;
            hasSessionLocal |= classification.HasSessionLocalWrite;
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
        IReadOnlyList<TSqlStatement> statements,
        SafetyInspectionVisitor inspection)
    {
        if (inspection.HasNonLocalSelectInto || inspection.HasNonLocalOutputInto)
        {
            return Denied(SqlSafetyReason.NonTemporaryWrite);
        }

        var hasSessionLocal = false;
        foreach (var statement in statements)
        {
            if (statement is DeclareVariableStatement)
                continue;

            var classification = ClassifyStatement(statement);
            if (classification.DenyReason is { } deny)
            {
                // Setup never takes the mutation-approval path; any write outside local temps is NonTemporaryWrite.
                if (deny is SqlSafetyReason.MutationNotAllowed ||
                    (deny is SqlSafetyReason.UnsupportedStatement && IsWrite(statement)))
                {
                    return Denied(SqlSafetyReason.NonTemporaryWrite);
                }

                return Denied(deny);
            }

            if (classification.HasPersistentWrite)
                return Denied(SqlSafetyReason.NonTemporaryWrite);

            if (!classification.HasSessionLocalWrite && statement is not SelectStatement)
            {
                return Denied(IsWrite(statement)
                    ? SqlSafetyReason.NonTemporaryWrite
                    : SqlSafetyReason.UnsupportedStatement);
            }

            hasSessionLocal |= classification.HasSessionLocalWrite;
        }

        return Allowed(hasSessionLocal: hasSessionLocal);
    }

    private static StatementClassification ClassifyStatement(TSqlStatement statement)
    {
        switch (statement)
        {
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
                    ResolveDirectTarget(insert.InsertSpecification.Target),
                    insert.InsertSpecification.OutputIntoClause);

            case UpdateStatement update:
                return ClassifyDmlWrites(
                    ResolveTarget(update.UpdateSpecification.Target, update.UpdateSpecification.FromClause),
                    update.UpdateSpecification.OutputIntoClause);

            case DeleteStatement delete:
                return ClassifyDmlWrites(
                    ResolveTarget(delete.DeleteSpecification.Target, delete.DeleteSpecification.FromClause),
                    delete.DeleteSpecification.OutputIntoClause);

            case MergeStatement merge:
                return ClassifyDmlWrites(
                    ResolveDirectTarget(merge.MergeSpecification.Target),
                    merge.MergeSpecification.OutputIntoClause);

            default:
                return IsDirectMutation(statement)
                    ? StatementClassification.Persistent
                    : StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);
        }
    }

    private static StatementClassification ClassifyDmlWrites(
        TargetResolution primary,
        OutputIntoClause? outputInto)
    {
        if (primary.Kind == TargetResolutionKind.Ambiguous ||
            primary.Kind == TargetResolutionKind.Unsupported)
        {
            return StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);
        }

        var targets = new List<SchemaObjectName?>();
        if (primary.Kind == TargetResolutionKind.Resolved)
            targets.Add(primary.Name);
        else
            return StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);

        if (outputInto is not null)
        {
            var outputTarget = ResolveDirectTarget(outputInto.IntoTable);
            if (outputTarget.Kind != TargetResolutionKind.Resolved)
                return StatementClassification.Denied(SqlSafetyReason.UnsupportedStatement);
            targets.Add(outputTarget.Name);
        }

        var hasSessionLocal = false;
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

    private static TargetResolution ResolveTarget(TableReference? target, FromClause? fromClause)
    {
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

    private static TargetResolution ResolveDirectTarget(TableReference? target) =>
        target is NamedTableReference named
            ? TargetResolution.Resolved(named.SchemaObject)
            : TargetResolution.Unsupported;

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
            _ => AddNonNamedBinding(tableReference, bindings),
        };

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
        // Aliased derived/TVF sources are not durable named targets we can prove local.
        var alias = tableReference switch
        {
            QueryDerivedTable derived => derived.Alias?.Value,
            SchemaObjectFunctionTableReference function => function.Alias?.Value,
            VariableTableReference variable => variable.Alias?.Value,
            PivotedTableReference pivoted => pivoted.Alias?.Value,
            UnpivotedTableReference unpivoted => unpivoted.Alias?.Value,
            _ => null,
        };

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
    }

    private readonly record struct TargetResolution(TargetResolutionKind Kind, SchemaObjectName? Name)
    {
        internal static TargetResolution Resolved(SchemaObjectName name) =>
            new(TargetResolutionKind.Resolved, name);

        internal static TargetResolution Ambiguous { get; } =
            new(TargetResolutionKind.Ambiguous, null);

        internal static TargetResolution Unsupported { get; } =
            new(TargetResolutionKind.Unsupported, null);
    }

    private static SqlSafetyDecision Allowed(bool hasMutation = false, bool hasSessionLocal = false) =>
        new(true, SqlSafetyReason.Allowed, hasMutation) { HasSessionLocalWork = hasSessionLocal };

    private static SqlSafetyDecision Denied(SqlSafetyReason reason, string? detail = null) =>
        new(false, reason, Detail: detail);

    private sealed class SafetyInspectionVisitor : TSqlFragmentVisitor
    {
        internal bool HasCrossDatabaseReference { get; private set; }
        internal bool HasExternalAccess { get; private set; }
        internal bool HasStatefulExpression { get; private set; }
        internal bool HasStatefulTableSource { get; private set; }
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
    byte? Scale = null,
    string? UdtTypeName = null);

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