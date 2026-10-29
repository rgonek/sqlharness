using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Data.SqlClient;

using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

internal sealed record ConditionsCaseInput(string Id, string? SetName, IReadOnlyList<SqlHarnessParameter> Varying);

internal static class ConditionsModelBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static ConditionsModel Build(
        SqlHarnessOperation operation,
        ResolvedTarget target,
        ISqlDialect dialect,
        IReadOnlyList<SqlHarnessParameter> fixedParameters,
        IReadOnlyList<ConditionsCaseInput> cases,
        bool storeSensitive)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(fixedParameters);
        ArgumentNullException.ThrowIfNull(cases);

        var (kind, setup, timeout, repeat, comparisonMode, compareNullability) = operation switch
        {
            SqlHarnessMeasureOperation measure => ("measure", measure.SetupSql, measure.TimeoutSeconds, measure.Repeat, (string?)null, (bool?)null),
            SqlHarnessCompareOperation compare => ("compare", compare.SetupSql, compare.TimeoutSeconds, compare.Repeat,
                compare.CompareResults.ToString(), compare.CompareNullability),
            SqlHarnessCompareMatrixOperation matrix => ("compare", matrix.SetupSql, matrix.TimeoutSeconds, matrix.Repeat,
                matrix.CompareResults.ToString(), matrix.CompareNullability),
            _ => throw new ArgumentException("Conditions are available only for measure and compare operations.", nameof(operation)),
        };

        var fixedValues = fixedParameters.Select(parameter => ToCondition(parameter, storeSensitive)).ToArray();
        var modelCases = cases.Select((item, index) =>
        {
            var varyingForFingerprint = item.Varying.Select(parameter => ToCondition(parameter, true)).ToArray();
            var varying = item.Varying.Select(parameter => ToCondition(parameter, storeSensitive)).ToArray();
            var shell = new ConditionsCase(item.Id, index, string.Empty, item.SetName, varying, string.Empty)
            {
                VaryingForFingerprint = varyingForFingerprint,
            };
            return shell with { Label = CaseLabel.Format(shell, cases.Count, storeSensitive) };
        }).ToArray();
        var execution = new ConditionsExecution(kind, "per-case", "per-case", 1, repeat, timeout, comparisonMode, compareNullability);
        return new ConditionsModel(2, dialect.Engine == SqlEngine.SqlServer ? "sqlserver" : "postgres", target.Server, target.Database,
            execution, setup is null ? null : new ConditionsSetup(string.Empty, storeSensitive ? setup : null), fixedValues, modelCases,
            string.Empty, [])
        {
            SetupTextForFingerprint = setup,
            FixedForFingerprint = fixedParameters.Select(parameter => ToCondition(parameter, true)).ToArray(),
        };
    }

    internal static ConditionsModel WithMaterializedParameters(
        ConditionsModel model,
        int caseIndex,
        IReadOnlyList<MaterializedParameterInfo> details,
        bool storeSensitive,
        IReadOnlySet<string>? fixedParameterNames = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(details);
        if ((uint)caseIndex >= (uint)model.Cases.Count)
            throw new ArgumentOutOfRangeException(nameof(caseIndex));

        var cases = model.Cases.ToArray();
        var conditionsCase = cases[caseIndex];
        var parameters = conditionsCase.Varying.ToList();
        var fixedParameters = model.Fixed.ToList();
        foreach (var detail in details)
        {
            var prefix = detail.Name.StartsWith('@') ? detail.Name : "@" + detail.Name;
            var rows = detail.Kind == "tvp" ? JsonSerializer.SerializeToElement(detail.Rows, JsonOptions) : (JsonElement?)null;
            var values = detail.Kind == "list" ? JsonSerializer.SerializeToElement(detail.Values, JsonOptions) : (JsonElement?)null;
            var content = JsonSerializer.SerializeToUtf8Bytes(new { detail.Kind, detail.Rows, detail.Values, detail.SourceText }, JsonOptions);
            var parameter = new ConditionsParameter(
                prefix,
                detail.Kind == "tvp" ? detail.ResolvedType?.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal) + " READONLY" : "list of " + detail.DeclaredType,
                null,
                detail.Kind,
                detail.Count,
                detail.Columns.Select(column => column.Name).ToArray(),
                detail.Columns.Select(column => column.ParameterType).ToArray(),
                null,
                storeSensitive ? rows : null,
                storeSensitive ? values : null,
                storeSensitive ? detail.SourceText : null)
            {
                ContentForFingerprint = content,
            };
            var isFixed = fixedParameterNames?.Contains(detail.Name) == true || fixedParameterNames?.Contains(prefix) == true;
            var target = isFixed ? fixedParameters : parameters;
            var existingIndex = target.FindIndex(item => string.Equals(item.Name, prefix, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                target[existingIndex] = parameter;
            else
                target.Add(parameter);
        }

        var updated = conditionsCase with { Varying = parameters, VaryingForFingerprint = parameters };
        var labelCase = updated with { Varying = fixedParameters.Concat(parameters).ToArray() };
        updated = updated with { Label = CaseLabel.Format(labelCase, model.Cases.Count, storeSensitive) };
        cases[caseIndex] = updated;
        return model with
        {
            Fixed = fixedParameters,
            FixedForFingerprint = fixedParameters,
            Cases = cases,
        };
    }

    internal static byte[] SetupContentBytes(ConditionsModel model) => Encoding.UTF8.GetBytes(model.SetupTextForFingerprint ?? string.Empty);

    internal static byte[] CaseFingerprintPayload(ConditionsModel model, string setupHash, ConditionsCase conditionsCase) =>
        FingerprintPayload(model, setupHash, conditionsCase.VaryingForFingerprint);

    internal static byte[] OperationFingerprintPayload(ConditionsModel model, string setupHash) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            model.Engine,
            model.Server,
            model.Database,
            setup = new { hash = setupHash },
            @fixed = model.FixedForFingerprint,
            varying = model.Cases.Select(item => item.VaryingForFingerprint),
            execution = new
            {
                model.Execution.SessionPolicy,
                model.Execution.SetupBinding,
                model.Execution.ComparisonMode,
            },
        }, JsonOptions);

    internal static byte[] OperationMeasurementFingerprintPayload(ConditionsModel model, string setupHash) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            model.Engine,
            model.Server,
            model.Database,
            setup = new { hash = setupHash },
            @fixed = model.FixedForFingerprint,
            varying = model.Cases.Select(item => item.VaryingForFingerprint),
            execution = new
            {
                model.Execution.SessionPolicy,
                model.Execution.SetupBinding,
            },
        }, JsonOptions);

    internal static byte[] CaseMeasurementFingerprintPayload(ConditionsModel model, string setupHash, ConditionsCase conditionsCase) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            model.Engine,
            model.Server,
            model.Database,
            setup = new { hash = setupHash },
            @fixed = model.FixedForFingerprint,
            varying = conditionsCase.VaryingForFingerprint,
            execution = new
            {
                model.Execution.SessionPolicy,
                model.Execution.SetupBinding,
            },
        }, JsonOptions);

    internal static byte[] CaseVaryingFingerprintPayload(ConditionsModel model, string setupHash, ConditionsCase conditionsCase) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            model.Engine,
            model.Server,
            model.Database,
            setup = new { hash = setupHash },
            varying = conditionsCase.VaryingForFingerprint,
            execution = new
            {
                model.Execution.SessionPolicy,
                model.Execution.SetupBinding,
            },
        }, JsonOptions);

    private static byte[] FingerprintPayload(ConditionsModel model, string setupHash, IReadOnlyList<ConditionsParameter> varying) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            model.Engine,
            model.Server,
            model.Database,
            setup = new { hash = setupHash },
            @fixed = model.FixedForFingerprint,
            varying,
            execution = new
            {
                model.Execution.SessionPolicy,
                model.Execution.SetupBinding,
                model.Execution.ComparisonMode,
            },
        }, JsonOptions);

    private static ConditionsParameter ToCondition(SqlHarnessParameter parameter, bool includeValue)
    {
        var value = IsNull(parameter.Value) ? "null" : JsonSerializer.Serialize(parameter.Value, JsonOptions);
        using var document = JsonDocument.Parse(value);
        return new ConditionsParameter(parameter.Name, SqlTypes.Effective(parameter), includeValue ? document.RootElement.Clone() : null);
    }

    private static bool IsNull(object value) => value is DBNull or null;

    internal static string EffectiveType(SqlHarnessParameter parameter) => SqlTypes.Effective(parameter);
}

internal static class SqlTypes
{
    internal static string Effective(SqlHarnessParameter parameter)
    {
        var type = parameter.Type switch
        {
            SqlDbType.NVarChar => "nvarchar",
            SqlDbType.VarChar => "varchar",
            SqlDbType.NChar => "nchar",
            SqlDbType.Char => "char",
            SqlDbType.VarBinary => "varbinary",
            SqlDbType.Binary => "binary",
            SqlDbType.Decimal => "decimal",
            SqlDbType.DateTime2 => "datetime2",
            SqlDbType.DateTimeOffset => "datetimeoffset",
            SqlDbType.SmallDateTime => "smalldatetime",
            SqlDbType.Udt => parameter.UdtTypeName?.ToLowerInvariant() ?? "udt",
            SqlDbType.Variant => "sql_variant",
            _ => parameter.Type.ToString().ToLowerInvariant(),
        };
        if (parameter.Type is SqlDbType.NVarChar or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.Char or SqlDbType.VarBinary or SqlDbType.Binary)
        {
            var size = parameter.Size ?? (parameter.Type is SqlDbType.NVarChar or SqlDbType.NChar ? 4000 : 8000);
            return $"{type}({(size == -1 ? "max" : size.ToString(CultureInfo.InvariantCulture))})";
        }
        if (parameter.Type == SqlDbType.Decimal)
        {
            var inferred = new SqlParameter(parameter.Name, SqlDbType.Decimal);
            if (parameter.Precision is { } explicitPrecision)
                inferred.Precision = explicitPrecision;
            if (parameter.Scale is { } explicitScale)
                inferred.Scale = explicitScale;
            inferred.Value = parameter.Value;
            var precision = parameter.Precision ?? (inferred.Precision == 0 ? (byte)18 : inferred.Precision);
            var scale = parameter.Scale ?? inferred.Scale;
            return $"decimal({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})";
        }
        if (parameter.Type == SqlDbType.DateTime2)
            return $"datetime2({(parameter.Scale ?? (byte)7).ToString(CultureInfo.InvariantCulture)})";
        if (parameter.Type == SqlDbType.Time)
            return $"time({(parameter.Scale ?? (byte)7).ToString(CultureInfo.InvariantCulture)})";
        return type;
    }
}
