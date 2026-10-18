using System.Globalization;
using System.Text.RegularExpressions;

using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;

internal sealed record ResolvedParameterSet(
    string Name,
    string? ParameterValue,
    IReadOnlyList<SqlHarnessParameter> Parameters);

internal static partial class ParameterSetResolver
{
    private const int MaximumSetCount = 32;
    internal const int MaximumNonScalarParameters = 8;
    internal const int MaximumCombinedRowsAndValues = 100_000;
    internal const int MaximumMaterializedBytes = 32 * 1024 * 1024;
    internal const int MaximumCommandParameters = 2_100;

    internal static IReadOnlySet<string> ValidateInputs(
        IReadOnlyList<SqlHarnessParameterInput> inputs,
        SqlEngine engine,
        bool mutation = false)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var tableValuedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nonScalarCount = 0;
        long rowsAndValues = 0;
        long materializedBytes = 0;
        long commandParameterCount = 0;
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            var kind = ParameterKindParser.Parse(input);
            if (kind == ParameterKind.Scalar)
            {
                if (input.Rows is not null || input.Values is not null || input.SourceSql is not null)
                    throw new SqlHarnessSafetyException($"SQL parameter '@{input.Name}' is scalar and cannot include rows, values, or source SQL.");
                commandParameterCount++;
                if (commandParameterCount > MaximumCommandParameters)
                    throw new SqlHarnessSafetyException($"SQL parameter '@{input.Name}' exceeds the {MaximumCommandParameters} final command parameter limit.");
                continue;
            }

            if (engine != SqlEngine.SqlServer)
                throw new SqlHarnessSafetyException("table-valued and list parameters are SQL Server only");
            if (mutation)
                throw new SqlHarnessSafetyException("table-valued and list parameters are not supported by mutation");
            nonScalarCount++;
            if (nonScalarCount > MaximumNonScalarParameters)
                throw new SqlHarnessSafetyException($"SQL parameter '@{input.Name}' exceeds the limit of {MaximumNonScalarParameters} table-valued and list parameters.");

            commandParameterCount++;
            if (kind == ParameterKind.Tvp)
            {
                if (string.IsNullOrWhiteSpace(input.Type))
                    throw new SqlHarnessSafetyException($"Table-valued SQL parameter '@{input.Name}' requires a type name.");
                if ((input.Rows is null) == (input.SourceSql is null))
                    throw new SqlHarnessSafetyException($"Table-valued SQL parameter '@{input.Name}' requires exactly one of rows or rowsFrom.");
                if (input.Values is not null)
                    throw new SqlHarnessSafetyException($"Table-valued SQL parameter '@{input.Name}' cannot include values or valuesFrom.");
                if (input.Rows is { } rows)
                {
                    var arity = rows.Count == 0 ? (int?)null : rows[0]?.Count;
                    for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                    {
                        var row = rows[rowIndex] ?? throw new SqlHarnessSafetyException($"Table-valued SQL parameter '@{input.Name}' contains a null row.");
                        if (arity is not null && row.Count != arity)
                            throw new SqlHarnessSafetyException($"Table-valued SQL parameter '@{input.Name}' has inline rows with unequal arity.");
                        rowsAndValues++;
                        foreach (var value in row)
                            AddMaterializedValue(input.Name, value, ref materializedBytes);
                    }
                }
                if (input.SourceSql is { Length: 0 })
                    throw new SqlHarnessSafetyException($"Table-valued SQL parameter '@{input.Name}' has empty rowsFrom SQL.");
                tableValuedNames.Add(input.Name.StartsWith('@') ? input.Name : "@" + input.Name);
            }
            else
            {
                if ((input.Values is null) == (input.SourceSql is null))
                    throw new SqlHarnessSafetyException($"List SQL parameter '@{input.Name}' requires exactly one of values or valuesFrom.");
                if (input.Rows is not null)
                    throw new SqlHarnessSafetyException($"List SQL parameter '@{input.Name}' cannot include rows or rowsFrom.");
                if (input.Values is { } values)
                {
                    rowsAndValues += values.Count;
                    if (values.Count > 0)
                        commandParameterCount += values.Count - 1;
                    foreach (var value in values)
                        AddMaterializedValue(input.Name, value, ref materializedBytes);
                }
                if (input.SourceSql is { Length: 0 })
                    throw new SqlHarnessSafetyException($"List SQL parameter '@{input.Name}' has empty valuesFrom SQL.");
            }

            if (rowsAndValues > MaximumCombinedRowsAndValues)
                throw new SqlHarnessSafetyException($"SQL parameter '@{input.Name}' exceeds the {MaximumCombinedRowsAndValues} combined rows/values limit.");
            if (materializedBytes > MaximumMaterializedBytes)
                throw new SqlHarnessSafetyException($"SQL parameter '@{input.Name}' exceeds the {MaximumMaterializedBytes}-byte materialized input limit.");
            if (commandParameterCount > MaximumCommandParameters)
                throw new SqlHarnessSafetyException($"SQL parameter '@{input.Name}' exceeds the {MaximumCommandParameters} final command parameter limit.");
        }

        if (mutation && inputs.Any(input => ParameterKindParser.Parse(input) != ParameterKind.Scalar))
            throw new SqlHarnessSafetyException("table-valued and list parameters are not supported by mutation");
        return tableValuedNames;
    }

    internal static void RequireScalarExecution(IReadOnlyList<SqlHarnessParameterInput> inputs, SqlEngine engine)
    {
        ValidateInputs(inputs, engine);
        if (inputs.Any(input => ParameterKindParser.Parse(input) != ParameterKind.Scalar))
            throw new SqlHarnessSafetyException("Table-valued and list parameter execution is not available in this operation yet.");
    }

    private static void AddMaterializedValue(string name, string? value, ref long totalBytes)
    {
        if (value is not null)
            totalBytes += System.Text.Encoding.UTF8.GetByteCount(value);
    }

    internal static IReadOnlyList<ResolvedParameterSet> Resolve(
        ISqlDialect dialect,
        IReadOnlyList<SqlHarnessParameter> fixedParameters,
        SqlHarnessParameterMatrixInput? matrix,
        IReadOnlyList<SqlHarnessParameterSetInput>? sets)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(fixedParameters);
        if (matrix is not null && sets is not null)
            throw new SqlHarnessSafetyException("A parameter matrix and parameter sets cannot be supplied together.");

        if (matrix is not null)
        {
            if (matrix.Values is { Count: > MaximumSetCount })
                throw new SqlHarnessSafetyException($"The SQL parameter matrix cannot contain more than {MaximumSetCount} values.");

            var parsed = SqlParameterMatrixParser.Bind(matrix, fixedParameters.Select(parameter => parameter.Name));
            var output = new ResolvedParameterSet[parsed.Values.Count];
            for (var index = 0; index < parsed.Values.Count; index++)
            {
                IReadOnlyList<SqlHarnessParameter> dialectBound;
                try
                {
                    dialectBound = dialect.BindParameters(
                        [new SqlHarnessParameterInput(parsed.Name, parsed.Type, parsed.DisplayValues[index])]);
                }
                catch (SqlHarnessSafetyException exception) when (exception.IsParameterValue)
                {
                    throw exception.WithParameterValue(
                        $"The SQL parameter matrix '{parsed.Name}' has an invalid value for SQL parameter '{exception.ParameterName}'.");
                }
                catch (SqlHarnessSafetyException exception)
                {
                    throw new SqlHarnessSafetyException($"The SQL parameter matrix '{parsed.Name}' is invalid: {exception.Message}");
                }

                if (dialectBound.Count != 1)
                    throw new SqlHarnessSafetyException($"The SQL parameter matrix '{parsed.Name}' is invalid.");

                output[index] = new ResolvedParameterSet(
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    parsed.DisplayValues[index],
                    dialectBound);
            }

            return output;
        }

        if (sets is null)
            return [];
        if (sets.Count is < 2 or > MaximumSetCount)
            throw new SqlHarnessSafetyException("Parameter sets must contain between 2 and 32 sets.");

        var fixedNames = new HashSet<string>(fixedParameters.Select(parameter => parameter.Name), StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ResolvedParameterSet>(sets.Count);
        IReadOnlyDictionary<string, SqlHarnessParameter>? baselineByName = null;
        IReadOnlyDictionary<string, DeclaredParameterShape>? baselineShapes = null;
        string? baselineName = null;

        foreach (var set in sets)
        {
            ArgumentNullException.ThrowIfNull(set);
            if (set.Name is null || set.Name.Length is < 1 or > 64 || SafeSetLabel().Match(set.Name) is not { Success: true } match || match.Length != set.Name.Length)
                throw new SqlHarnessSafetyException("A parameter set has an invalid name.");
            if (!names.Add(set.Name))
                throw new SqlHarnessSafetyException($"Duplicate parameter set '{set.Name}'.");

            IReadOnlyList<SqlHarnessParameter> bound;
            IReadOnlyList<SqlHarnessParameterInput> inputs;
            try
            {
                inputs = SqlParameterInputs.Resolve(set.Parameters, set.TypedParameters).ToArray();
                RequireScalarExecution(inputs, dialect.Engine);
                bound = dialect.BindParameters(inputs);
            }
            catch (SqlHarnessSafetyException exception) when (exception.IsParameterValue)
            {
                var message = set.SourcePath is not null
                    ? $"Parameter set file '{set.SourcePath}' has an invalid entry for parameter '{exception.ParameterName}': {exception.Message}"
                    : $"Parameter set '{set.Name}' is invalid: {exception.Message}";
                throw exception.WithParameterValue(message);
            }
            catch (SqlHarnessSafetyException exception)
            {
                var message = set.SourcePath is not null
                    ? $"Parameter set file '{set.SourcePath}' has an invalid parameter entry: {exception.Message}"
                    : $"Parameter set '{set.Name}' is invalid: {exception.Message}";
                throw new SqlHarnessSafetyException(message);
            }

            foreach (var parameter in bound)
            {
                if (fixedNames.Contains(parameter.Name))
                    throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' repeats fixed SQL parameter '{parameter.Name}'.");
            }
            if (bound.Count != inputs.Count)
                throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' is invalid.");

            var byName = bound.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
            var shapes = new Dictionary<string, DeclaredParameterShape>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < bound.Count; index++)
                shapes.Add(bound[index].Name, DeclaredParameterShape.From(inputs[index].Type));

            if (baselineByName is null)
            {
                baselineByName = byName;
                baselineShapes = shapes;
                baselineName = set.Name;
            }
            else
            {
                foreach (var parameter in baselineByName.Values)
                {
                    if (!byName.TryGetValue(parameter.Name, out var candidate))
                        throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' is missing SQL parameter '{parameter.Name}'.");
                    if (candidate.Type != parameter.Type)
                        throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' SQL parameter '{candidate.Name}' has a different type than parameter set '{baselineName}'.");

                    var baselineShape = baselineShapes![parameter.Name];
                    var candidateShape = shapes[candidate.Name];
                    if (baselineShape.Type != candidateShape.Type)
                        throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' SQL parameter '{candidate.Name}' has a different type than parameter set '{baselineName}'.");
                    if (baselineShape.Size != candidateShape.Size)
                        throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' SQL parameter '{candidate.Name}' has a different size than parameter set '{baselineName}'.");
                    if (baselineShape.Precision != candidateShape.Precision)
                        throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' SQL parameter '{candidate.Name}' has a different precision than parameter set '{baselineName}'.");
                    if (baselineShape.Scale != candidateShape.Scale)
                        throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' SQL parameter '{candidate.Name}' has a different scale than parameter set '{baselineName}'.");
                }

                foreach (var parameter in bound)
                {
                    if (!baselineByName.ContainsKey(parameter.Name))
                        throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' has unexpected SQL parameter '{parameter.Name}'.");
                }
            }

            resolved.Add(new ResolvedParameterSet(set.Name, null, bound));
        }

        return resolved;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSetLabel();

    // Compare declared type tokens, not bound size. Unsized strings can produce different
    // bound sizes from their values while retaining the same declared SQL shape.
    private readonly record struct DeclaredParameterShape(string Type, int? Size, byte? Precision, byte? Scale)
    {
        public static DeclaredParameterShape From(string? token)
        {
            if (string.IsNullOrEmpty(token))
                return new("nvarchar", null, null, null);

            token = token.ToLowerInvariant();
            var open = token.IndexOf('(');
            if (open < 0)
                return new(token is "numeric" ? "decimal" : token, null, null, null);

            if (!token.EndsWith(')') || open == 0)
                throw new SqlHarnessSafetyException("SQL parameter shape is invalid.");

            var head = token[..open];
            var body = token[(open + 1)..^1];
            if (head is "decimal" or "numeric")
            {
                var comma = body.IndexOf(',');
                if (comma <= 0
                    || !byte.TryParse(body[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out var precision)
                    || !byte.TryParse(body[(comma + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var scale))
                {
                    throw new SqlHarnessSafetyException("SQL parameter shape is invalid.");
                }

                return new("decimal", null, precision, scale);
            }

            if (body == "max")
                return new(head, -1, null, null);

            if (!int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                throw new SqlHarnessSafetyException("SQL parameter shape is invalid.");

            return new(head, size, null, null);
        }
    }
}
