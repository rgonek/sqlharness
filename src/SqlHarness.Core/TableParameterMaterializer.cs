using System.Data;
using System.Globalization;
using System.Text;

using Microsoft.Data.SqlClient.Server;
using Microsoft.SqlServer.Server;

using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;

internal sealed record MaterializedParameterInfo(
    string Name,
    string Kind,
    string DeclaredType,
    string? ResolvedType,
    IReadOnlyList<TableTypeColumn> Columns,
    int Count,
    string? SourceText,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    IReadOnlyList<string?> Values);

internal sealed record MaterializedParameters(
    IReadOnlyList<SqlHarnessParameter> Parameters,
    IReadOnlyList<SqlHarnessParameterInput> Inputs,
    IReadOnlyList<MaterializedParameterInfo> Details)
{
    internal ExpandedSql Expand(string sql) => ListParameterExpander.Expand(sql,
        Inputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.List)
            .Select(input => new ListValues(input.Name, input.Type!, input.Values ?? []))
            .ToArray());
}

internal static class TableParameterMaterializer
{
    internal static async Task<MaterializedParameters> MaterializeAsync(
        ISqlSession session,
        IReadOnlyList<SqlHarnessParameterInput> inputs,
        ISqlDialect dialect,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(dialect);
        ParameterSetResolver.ValidateInputs(inputs, dialect.Engine);
        if (dialect.Engine != SqlEngine.SqlServer && inputs.Any(input => ParameterKindParser.Parse(input) != ParameterKind.Scalar))
            throw new SqlHarnessSafetyException("table-valued and list parameters are SQL Server only");

        var scalarInputs = inputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar).ToArray();
        var parameters = new List<SqlHarnessParameter>(dialect.BindParameters(scalarInputs));
        var materializedInputs = new List<SqlHarnessParameterInput>(inputs.Count);
        var details = new List<MaterializedParameterInfo>();
        long totalValues = 0;
        long totalBytes = 0;

        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            var kind = ParameterKindParser.Parse(input);
            if (kind == ParameterKind.Scalar)
            {
                materializedInputs.Add(input);
                continue;
            }

            IReadOnlyList<IReadOnlyList<object?>> rows;
            IReadOnlyList<string?> values;
            var tableShape = kind == ParameterKind.Tvp
                ? await SqlServerTableTypes.ResolveAsync(session, input.Type!, ct)
                : null;
            var fromSource = input.SourceSql is not null;
            if (input.SourceSql is { } sourceSql)
            {
                await using var reader = await session.ExecuteReaderAsync(new SqlExecutionCommand(sourceSql, [], 30), ct);
                if (kind == ParameterKind.Tvp)
                {
                    EnsureColumnShape(input.Name, input.Type!, tableShape!.Columns, reader);
                    var collected = new List<IReadOnlyList<object?>>();
                    while (await reader.ReadAsync(ct))
                    {
                        var row = new object?[reader.FieldCount];
                        for (var index = 0; index < row.Length; index++)
                            row[index] = reader.GetValue(index) is DBNull ? null : reader.GetValue(index);
                        collected.Add(row);
                        totalValues++;
                        foreach (var value in row)
                            totalBytes += value is null ? 0 : EstimateBytes(value);
                        EnforceLimits(input.Name, totalValues, totalBytes);
                    }
                    rows = collected;
                    values = [];
                }
                else
                {
                    if (reader.FieldCount != 1 || !SourceMatchesScalarType(input.Type!, reader.GetColumnMetadata(0)))
                        throw new SqlHarnessSafetyException($"values for @{input.Name} do not match declared type '{input.Type}'.");
                    var collected = new List<string?>();
                    while (await reader.ReadAsync(ct))
                    {
                        var value = reader.GetValue(0);
                        var materializedValue = value is DBNull ? null : FormatSourceValue(value);
                        collected.Add(materializedValue);
                        totalValues++;
                        totalBytes += materializedValue is null ? 0 : Encoding.UTF8.GetByteCount(materializedValue);
                        EnforceLimits(input.Name, totalValues, totalBytes);
                    }
                    values = collected;
                    rows = [];
                }
            }
            else
            {
                rows = input.Rows?.Select(row => (IReadOnlyList<object?>)row.Cast<object?>().ToArray()).ToArray() ?? [];
                values = input.Values ?? [];
            }

            if (kind == ParameterKind.Tvp)
            {
                var shape = tableShape!;
                var records = new List<SqlDataRecord>(rows.Count);
                var metadata = shape.Columns.Select(ToMetaData).ToArray();
                foreach (var row in rows)
                {
                    if (row.Count != shape.Columns.Count)
                        throw new SqlHarnessSafetyException($"rows for @{input.Name} do not match {input.Type}: expected {shape.Columns.Count} values, found {row.Count}");
                    var record = new SqlDataRecord(metadata);
                    for (var index = 0; index < row.Count; index++)
                    {
                        var column = shape.Columns[index];
                        if (row[index] is null && !column.Nullable)
                            throw new SqlHarnessSafetyException($"rows for @{input.Name} do not match {input.Type}: column '{column.Name}' does not allow NULL");
                        var value = row[index] is null ? DBNull.Value : row[index] is string text
                            ? SqlParameterParser.Bind(new SqlHarnessParameterInput(column.Name, column.ParameterType, text)).Value
                            : row[index]!;
                        record.SetValue(index, value);
                        if (!fromSource)
                            totalBytes += EstimateBytes(value);
                    }
                    records.Add(record);
                }
                if (!fromSource)
                    totalValues += records.Count;
                parameters.Add(new SqlHarnessParameter("@" + input.Name.TrimStart('@'), SqlDbType.Structured, DBNull.Value, null,
                    Rows: records, TableTypeName: shape.QualifiedName));
                materializedInputs.Add(input with { Rows = rows.Select(row => (IReadOnlyList<string?>)row.Select(value => value is null ? null : FormatSourceValue(value)).ToArray()).ToArray(), SourceSql = null });
                details.Add(new MaterializedParameterInfo(input.Name, "tvp", input.Type!, shape.QualifiedName, shape.Columns,
                    records.Count, input.SourceSql, materializedInputs[^1].Rows!, []));
            }
            else
            {
                var bound = new List<string?>(values.Count);
                foreach (var value in values)
                {
                    _ = value is null ? null : SqlParameterParser.Bind(new SqlHarnessParameterInput(input.Name, input.Type, value));
                    bound.Add(value);
                    if (!fromSource)
                        totalBytes += value is null ? 0 : Encoding.UTF8.GetByteCount(value);
                }
                if (!fromSource)
                    totalValues += bound.Count;
                materializedInputs.Add(input with { Values = bound, SourceSql = null });
                details.Add(new MaterializedParameterInfo(input.Name, "list", input.Type!, null, [], bound.Count,
                    input.SourceSql, [], bound));
            }
            EnforceLimits(input.Name, totalValues, totalBytes);
        }

        return new MaterializedParameters(parameters, materializedInputs, details);
    }

    private static SqlMetaData ToMetaData(TableTypeColumn column) => column.Type switch
    {
        SqlDbType.NVarChar or SqlDbType.NChar or SqlDbType.VarChar or SqlDbType.Char or SqlDbType.VarBinary or SqlDbType.Binary =>
            new SqlMetaData(column.Name, column.Type, column.Size),
        SqlDbType.Decimal => new SqlMetaData(column.Name, column.Type, column.Precision, column.Scale),
        SqlDbType.Time or SqlDbType.DateTime2 or SqlDbType.DateTimeOffset => new SqlMetaData(column.Name, column.Type, (byte)0, column.Scale),
        _ => new SqlMetaData(column.Name, column.Type),
    };

    private static void EnsureColumnShape(string name, string type, IReadOnlyList<TableTypeColumn> expected, ISqlReader reader)
    {
        if (reader.FieldCount != expected.Count)
            throw new SqlHarnessSafetyException($"rows for @{name} do not match {type}: expected {expected.Count} columns, found {reader.FieldCount}");
        for (var index = 0; index < expected.Count; index++)
        {
            var expectedColumn = expected[index];
            var actual = reader.GetColumnMetadata(index);
            var expectedSize = expectedColumn.Type is SqlDbType.NVarChar or SqlDbType.NChar ? expectedColumn.Size / 2 : expectedColumn.Size;
            var sizedType = expectedColumn.Type is SqlDbType.NVarChar or SqlDbType.NChar or SqlDbType.VarChar or SqlDbType.Char or SqlDbType.VarBinary or SqlDbType.Binary;
            var precisionType = expectedColumn.Type == SqlDbType.Decimal;
            var scaleType = precisionType || expectedColumn.Type is SqlDbType.Time or SqlDbType.DateTime2 or SqlDbType.DateTimeOffset;
            if (!TypeMatches(expectedColumn.ParameterType, actual.TypeName) ||
                sizedType && actual.Size is { } actualSize && expectedSize != -1 && expectedSize != actualSize ||
                precisionType && actual.Precision is { } actualPrecision && expectedColumn.Precision != actualPrecision ||
                scaleType && actual.Scale is { } actualScale && expectedColumn.Scale != actualScale)
                throw new SqlHarnessSafetyException($"rows for @{name} do not match {type}: column {index + 1} has type '{actual.TypeName}', expected '{expectedColumn.ParameterType}'");
        }
    }

    private static bool TypeMatches(string declared, string source) =>
        string.Equals(declared.Split('(')[0], source, StringComparison.OrdinalIgnoreCase) ||
        declared.StartsWith("decimal(", StringComparison.OrdinalIgnoreCase) && source.Equals("numeric", StringComparison.OrdinalIgnoreCase);

    private static bool SourceMatchesScalarType(string declared, SqlReaderColumnMetadata actual)
    {
        var expected = SqlParameterParser.Bind(new SqlHarnessParameterInput("Value", declared, null));
        if (!TypeMatches(expected.Type.ToString(), actual.TypeName))
            return false;
        if (expected.Size is { } size && actual.Size is { } actualSize && size != -1 && size != actualSize)
            return false;
        if (expected.Precision is { } precision && actual.Precision is { } actualPrecision && precision != actualPrecision)
            return false;
        if (expected.Scale is { } scale && actual.Scale is { } actualScale && scale != actualScale)
            return false;
        return true;
    }

    private static string FormatSourceValue(object value) => value switch
    {
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "1" : "0",
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static long EstimateBytes(object value) => value switch
    {
        byte[] bytes => bytes.Length,
        string text => Encoding.UTF8.GetByteCount(text),
        _ => Encoding.UTF8.GetByteCount(FormatSourceValue(value)),
    };

    private static void EnforceLimits(string name, long values, long bytes)
    {
        if (values > ParameterSetResolver.MaximumCombinedRowsAndValues)
            throw new SqlHarnessSafetyException($"SQL parameter '@{name}' exceeds the {ParameterSetResolver.MaximumCombinedRowsAndValues} combined rows/values limit.");
        if (bytes > ParameterSetResolver.MaximumMaterializedBytes)
            throw new SqlHarnessSafetyException($"SQL parameter '@{name}' exceeds the {ParameterSetResolver.MaximumMaterializedBytes}-byte materialized input limit.");
    }
}
