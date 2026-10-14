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
            var parsed = SqlParameterMatrixParser.Bind(matrix, fixedParameters.Select(parameter => parameter.Name));
            if (parsed.Values.Count > MaximumSetCount)
                throw new SqlHarnessSafetyException($"The SQL parameter matrix '{parsed.Name}' cannot contain more than {MaximumSetCount} values.");
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
                    fixedParameters.Concat(dialectBound).ToArray());
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
        string? baselineName = null;

        foreach (var set in sets)
        {
            ArgumentNullException.ThrowIfNull(set);
            if (set.Name is null || set.Name.Length is < 1 or > 64 || SafeSetLabel().Match(set.Name) is not { Success: true } match || match.Length != set.Name.Length)
                throw new SqlHarnessSafetyException("A parameter set has an invalid name.");
            if (!names.Add(set.Name))
                throw new SqlHarnessSafetyException($"Duplicate parameter set '{set.Name}'.");

            IReadOnlyList<SqlHarnessParameter> bound;
            try
            {
                bound = dialect.BindParameters(SqlParameterInputs.Resolve(set.Parameters, set.TypedParameters));
            }
            catch (SqlHarnessSafetyException exception) when (exception.IsParameterValue)
            {
                var message = $"Parameter set '{set.Name}' SQL parameter '{exception.ParameterName}' is invalid.";
                throw exception.WithParameterValue(message);
            }
            catch (SqlHarnessSafetyException exception)
            {
                throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' is invalid: {exception.Message}");
            }

            foreach (var parameter in bound)
            {
                if (fixedNames.Contains(parameter.Name))
                    throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' repeats fixed SQL parameter '{parameter.Name}'.");
            }

            var byName = bound.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
            if (baselineByName is null)
            {
                baselineByName = byName;
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
                }

                foreach (var parameter in bound)
                {
                    if (!baselineByName.ContainsKey(parameter.Name))
                        throw new SqlHarnessSafetyException($"Parameter set '{set.Name}' has unexpected SQL parameter '{parameter.Name}'.");
                }
            }

            resolved.Add(new ResolvedParameterSet(set.Name, null, fixedParameters.Concat(bound).ToArray()));
        }

        return resolved;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSetLabel();
}
