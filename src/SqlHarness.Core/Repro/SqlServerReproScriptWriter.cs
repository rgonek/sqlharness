using System.Data;
using System.Globalization;
using System.Text;

using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlHarness.Core;

internal static partial class SqlServerReproScriptWriter
{
    private const string AsExecutedLabel = "As executed (sp_executesql)";
    private const string LocalVariablesLabel = "Local variables — estimates may differ";
    private const string LocalVariablesEquivalentLabel = "Local variables — equivalent (every statement uses OPTION (RECOMPILE))";
    private const string ValuesMissingLabel = "Incomplete — values not stored";

    internal static ReproScript Write(ConditionsModel conditions, string sideSql, string side, string caseId, ReproScriptForm form)
    {
        var selectedCase = conditions.Cases.FirstOrDefault(item => string.Equals(item.Id, caseId, StringComparison.Ordinal));
        if (conditions.Cases.Count > 0 && selectedCase is null)
            return new ReproScript(null, ReproCompleteness.Unavailable, "Script not available — enable storeSensitive");

        var originalParameters = conditions.Fixed.Concat(selectedCase?.Varying ?? []).ToArray();
        var lists = originalParameters.Where(parameter => parameter.Kind == "list").Select(ToListValues).ToArray();
        var expandedSql = ListParameterExpander.Expand(sideSql, lists);
        var setupListParameters = string.IsNullOrWhiteSpace(conditions.Setup?.Text)
            ? []
            : ListParameterExpander.Expand(conditions.Setup.Text, lists).Parameters;
        var parameters = ExpandConditionsParameters(originalParameters,
            setupListParameters.Concat(expandedSql.Parameters).DistinctBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        var missing = originalParameters.Any(IsMissingValue) || parameters.Any(IsMissingValue);
        var tableParameters = originalParameters.Where(parameter => parameter.Kind == "tvp").ToArray();
        var requery = tableParameters.Any(parameter => parameter.SourceText is not null);
        var label = missing ? ValuesMissingLabel : form switch
        {
            ReproScriptForm.AsExecuted => AsExecutedLabel,
            ReproScriptForm.LocalVariables when EveryStatementRecompiles(sideSql, conditions.Setup?.Text) => LocalVariablesEquivalentLabel,
            _ => LocalVariablesLabel,
        };
        if (requery)
            label += " — rows re-queried; data may have changed";

        var builder = new StringBuilder();
        builder.AppendLine($"-- SQLHarness reproduction for side: {SafeComment(side)}");
        builder.AppendLine($"-- case: {SafeComment(selectedCase?.Label ?? "Case 1 of 1")}");
        builder.AppendLine("-- new connection; setup runs per case");
        builder.AppendLine("-- SQLHarness discards one warm-up and reports medians; this script runs once");
        builder.AppendLine("-- ARITHABORT OFF reflects SqlClient's documented client default; this session setting was not journaled");
        builder.AppendLine("SET ARITHABORT OFF;");
        builder.AppendLine();

        AppendTableParameters(builder, tableParameters);

        if (!string.IsNullOrWhiteSpace(conditions.Setup?.Text))
        {
            var setupSql = ListParameterExpander.Expand(conditions.Setup.Text, lists).Text;
            var setupCommands = SetupSqlExecution.PrepareCommands(setupSql, parameters.Select(ToBoundParameter).ToArray(), conditions.Execution.TimeoutSeconds);
            foreach (var command in setupCommands)
            {
                if (command.Parameters.Count == 0)
                {
                    builder.AppendLine(command.Sql.TrimEnd());
                    builder.AppendLine("GO");
                }
                else if (form == ReproScriptForm.AsExecuted)
                {
                    AppendSpExecuteSql(builder, command.Sql, command.Parameters.Select(parameter => parameters.Single(item => string.Equals(item.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))).ToArray());
                    builder.AppendLine("GO");
                }
                else
                {
                    AppendLocalDeclarations(builder, command.Parameters.Select(parameter => parameters.Single(item => string.Equals(item.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))).ToArray());
                    builder.AppendLine(command.Sql.TrimEnd());
                    builder.AppendLine("GO");
                }
            }
        }

        if (form == ReproScriptForm.LocalVariables)
            AppendLocalDeclarations(builder, parameters);

        builder.AppendLine("SET STATISTICS IO, TIME ON;");
        if (form == ReproScriptForm.AsExecuted)
        {
            AppendSpExecuteSql(builder, expandedSql.Text, parameters);
        }
        else
        {
            builder.AppendLine(expandedSql.Text.TrimEnd());
        }
        builder.AppendLine("GO");
        builder.AppendLine("SET STATISTICS IO, TIME OFF;");
        return new ReproScript(builder.ToString(), missing ? ReproCompleteness.ValuesMissing : ReproCompleteness.Complete, label);
    }

    private static void AppendSpExecuteSql(StringBuilder builder, string sql, IReadOnlyList<ConditionsParameter> parameters)
    {
        builder.Append("EXEC sys.sp_executesql N'").Append(EscapeSqlString(sql)).Append('\'');
        if (parameters.Count > 0)
        {
            builder.Append(", N'").Append(EscapeSqlString(string.Join(", ", parameters.Select(parameter => $"{parameter.Name} {parameter.EffectiveType}")))).Append('\'');
            foreach (var parameter in parameters)
                builder.Append(", ").Append(parameter.Name).Append(" = ").Append(SqlServerLiteral.Format(parameter));
        }
        builder.AppendLine(";");
    }

    private static void AppendLocalDeclarations(StringBuilder builder, IReadOnlyList<ConditionsParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Kind == "tvp")
                continue;
            var value = parameter.Value is null || parameter.Value.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? "/* value not stored */"
                : SqlServerLiteral.Format(parameter);
            builder.Append("DECLARE ").Append(parameter.Name).Append(' ').Append(parameter.EffectiveType).Append(" = ").Append(value).AppendLine(";");
        }
        if (parameters.Count > 0)
            builder.AppendLine();
    }

    private static string EscapeSqlString(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string SafeComment(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static SqlHarnessParameter ToBoundParameter(ConditionsParameter parameter)
    {
        var type = parameter.EffectiveType;
        if (parameter.Kind == "tvp")
        {
            var tableType = type.EndsWith(" READONLY", StringComparison.Ordinal) ? type[..^" READONLY".Length] : type;
            return new SqlHarnessParameter(parameter.Name, SqlDbType.Structured, DBNull.Value, null, TableTypeName: tableType);
        }
        var open = type.IndexOf('(');
        var baseType = (open < 0 ? type : type[..open]).ToLowerInvariant();
        var sqlType = baseType switch
        {
            "nvarchar" => SqlDbType.NVarChar,
            "varchar" => SqlDbType.VarChar,
            "nchar" => SqlDbType.NChar,
            "char" => SqlDbType.Char,
            "varbinary" => SqlDbType.VarBinary,
            "binary" => SqlDbType.Binary,
            "decimal" or "numeric" => SqlDbType.Decimal,
            "datetime2" => SqlDbType.DateTime2,
            "datetimeoffset" => SqlDbType.DateTimeOffset,
            _ => Enum.TryParse<SqlDbType>(baseType, true, out var parsed) ? parsed : SqlDbType.Variant,
        };
        var size = ParseSize(type);
        var (precision, scale) = ParsePrecisionScale(type);
        // PrepareCommands uses these parameters only to find referenced names; it
        // never binds or inspects their values.
        return new SqlHarnessParameter(parameter.Name, sqlType, DBNull.Value, size, precision, scale);
    }

    private static void AppendTableParameters(StringBuilder builder, IReadOnlyList<ConditionsParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            var type = parameter.EffectiveType.EndsWith(" READONLY", StringComparison.Ordinal)
                ? parameter.EffectiveType[..^" READONLY".Length]
                : parameter.EffectiveType;
            builder.Append("DECLARE ").Append(parameter.Name).Append(' ').Append(type).AppendLine(";");
            if (parameter.SourceText is { } source)
            {
                builder.Append("INSERT INTO ").Append(parameter.Name).Append(" (")
                    .Append(string.Join(", ", parameter.Columns ?? [])).Append(") ")
                    .AppendLine(source.TrimEnd());
                continue;
            }

            if (parameter.Rows is not { } rows || rows.ValueKind != System.Text.Json.JsonValueKind.Array || rows.GetArrayLength() == 0)
                continue;
            var tuples = rows.EnumerateArray().Select(row => "(" + string.Join(", ", row.EnumerateArray().Select((value, index) =>
                FormatTableValue(value, parameter.ColumnTypes is { Count: > 0 } types && index < types.Count ? types[index] : "nvarchar(max)"))) + ")").ToArray();
            var columns = string.Join(", ", parameter.Columns ?? []);
            for (var offset = 0; offset < tuples.Length; offset += 1000)
            {
                var batch = tuples.Skip(offset).Take(1000);
                builder.Append("INSERT INTO ").Append(parameter.Name).Append(" (").Append(columns).Append(") VALUES ")
                    .AppendLine(string.Join(", ", batch) + ";");
            }
        }
        if (parameters.Count > 0)
            builder.AppendLine();
    }

    private static string FormatTableValue(System.Text.Json.JsonElement value, string type)
    {
        if (value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var raw = value.GetString()!;
            var baseType = type.Split('(')[0].ToLowerInvariant();
            if (baseType is "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real")
                return raw;
            if (baseType == "bit" && bool.TryParse(raw, out var boolean))
                return boolean ? "1" : "0";
            if (baseType is "varchar" or "char" or "nvarchar" or "nchar" or "text" or "ntext" or "xml" or "date" or "datetime" or "datetime2" or "smalldatetime" or "time" or "datetimeoffset")
                return (baseType.StartsWith('n') || baseType == "xml" ? "N" : string.Empty) + "'" + raw.Replace("'", "''", StringComparison.Ordinal) + "'";
        }
        return SqlServerLiteral.Format(new ConditionsParameter("@Value", type, value));
    }

    private static ListValues ToListValues(ConditionsParameter parameter)
    {
        var values = parameter.Values is { } json && json.ValueKind == System.Text.Json.JsonValueKind.Array
            ? json.EnumerateArray().Select(value => value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Null => null,
                System.Text.Json.JsonValueKind.String => value.GetString(),
                _ => value.GetRawText(),
            }).ToArray()
            : [];
        var type = parameter.EffectiveType.StartsWith("list of ", StringComparison.Ordinal)
            ? parameter.EffectiveType["list of ".Length..]
            : parameter.EffectiveType;
        return new ListValues(parameter.Name, type, values);
    }

    private static ConditionsParameter[] ExpandConditionsParameters(
        IReadOnlyList<ConditionsParameter> original,
        IReadOnlyList<SqlHarnessParameter> expanded)
    {
        var scalar = original.Where(parameter => parameter.Kind != "list").ToList();
        foreach (var parameter in expanded)
        {
            var source = FindListSource(original, parameter.Name);
            System.Text.Json.JsonElement? value = parameter.Value is DBNull
                ? System.Text.Json.JsonSerializer.SerializeToElement<object?>(null)
                : System.Text.Json.JsonSerializer.SerializeToElement(parameter.Value);
            scalar.Add(new ConditionsParameter(parameter.Name, source.EffectiveType["list of ".Length..], value));
        }
        return scalar.ToArray();
    }

    private static ConditionsParameter FindListSource(IReadOnlyList<ConditionsParameter> original, string expandedName)
    {
        var normalizedExpandedName = SqlParameterNames.Normalize(expandedName);
        var source = original
            .Where(parameter => parameter.Kind == "list")
            .Select(parameter => new
            {
                Parameter = parameter,
                Name = SqlParameterNames.Normalize(parameter.Name),
            })
            .Where(item => string.Equals(normalizedExpandedName, item.Name, StringComparison.OrdinalIgnoreCase)
                || normalizedExpandedName.StartsWith(item.Name, StringComparison.OrdinalIgnoreCase)
                    && normalizedExpandedName[item.Name.Length..].All(char.IsDigit))
            .OrderByDescending(item => item.Name.Length)
            .FirstOrDefault();
        return source?.Parameter ?? throw new InvalidOperationException($"Expanded list parameter '{expandedName}' has no source list.");
    }

    private static bool IsMissingValue(ConditionsParameter parameter) => parameter.Kind switch
    {
        "tvp" => parameter.Rows is null && parameter.SourceText is null,
        "list" => parameter.Values is null || parameter.Values.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined,
        _ => parameter.Value is null || parameter.Value.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined,
    };

    private static int? ParseSize(string type)
    {
        var open = type.IndexOf('(');
        var close = type.IndexOf(')', open + 1);
        if (open < 0 || close < 0)
            return null;
        var size = type[(open + 1)..close];
        if (size.Contains(','))
            return null;
        return string.Equals(size, "max", StringComparison.OrdinalIgnoreCase) ? -1 : int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static (byte? Precision, byte? Scale) ParsePrecisionScale(string type)
    {
        var open = type.IndexOf('(');
        var close = type.IndexOf(')', open + 1);
        if (open < 0 || close < 0)
            return (null, null);
        var pieces = type[(open + 1)..close].Split(',');
        return pieces.Length == 2 && byte.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var precision)
            && byte.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var scale)
            ? (precision, scale)
            : (null, null);
    }

    private static bool EveryStatementRecompiles(params string?[] sqlTexts)
    {
        var foundAny = false;
        foreach (var sql in sqlTexts.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql!);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count > 0 || fragment is not TSqlScript script)
                return false;
            var hints = new QueryHintVisitor();
            script.Accept(hints);
            foundAny |= hints.QueryCount > 0;
            if (!hints.AllQueriesRecompile)
                return false;
        }
        return foundAny;
    }

    private sealed class QueryHintVisitor : TSqlFragmentVisitor
    {
        internal int QueryCount { get; private set; }
        internal bool AllQueriesRecompile { get; private set; } = true;

        public override void ExplicitVisit(SelectStatement node) => Add(node);
        public override void ExplicitVisit(InsertStatement node) => Add(node);
        public override void ExplicitVisit(UpdateStatement node) => Add(node);
        public override void ExplicitVisit(DeleteStatement node) => Add(node);
        public override void ExplicitVisit(MergeStatement node) => Add(node);

        private void Add(StatementWithCtesAndXmlNamespaces statement)
        {
            QueryCount++;
            if (statement.OptimizerHints?.Any(hint => hint.HintKind == OptimizerHintKind.Recompile) != true)
                AllQueriesRecompile = false;
        }
    }
}
