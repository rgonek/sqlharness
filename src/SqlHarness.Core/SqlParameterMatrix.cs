using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

public sealed record SqlParameterMatrixSpec(
    string Name,
    string Type,
    IReadOnlyList<string> DisplayValues);

internal sealed record ParsedParameterMatrix(
    string Name,
    string Type,
    IReadOnlyList<string> DisplayValues,
    IReadOnlyList<SqlHarnessParameter> Values)
{
    public SqlParameterMatrixSpec Spec => new(Name, Type, DisplayValues);
}

internal static partial class SqlParameterMatrixParser
{
    internal static ParsedParameterMatrix Parse(string input, IReadOnlyList<string> fixedParameters)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(fixedParameters);

        var equalsIndex = input.IndexOf('=');
        if (equalsIndex < 0)
            throw new SqlHarnessSafetyException(MatrixError(TryCanonicalName(input), "must use name:type=value,value syntax."));

        var declaration = input[..equalsIndex];
        var valueText = input[(equalsIndex + 1)..];
        var parameterName = TryCanonicalName(declaration);
        var typeSeparator = declaration.IndexOf(':');
        if (typeSeparator <= 0 || typeSeparator == declaration.Length - 1)
            throw new SqlHarnessSafetyException(MatrixError(parameterName, "requires a type."));

        var type = declaration[(typeSeparator + 1)..];
        if (valueText.Length == 0)
            throw new SqlHarnessSafetyException(MatrixError(parameterName, "requires at least two values."));

        // Split only the value list. Declaration commas, such as decimal(10,2), belong to the type.
        var displayValues = valueText.Split(',');
        if (displayValues.Any(value => value.Length == 0))
            throw new SqlHarnessSafetyException(MatrixError(parameterName, "contains an empty value."));

        if (displayValues.Length < 2)
            throw new SqlHarnessSafetyException(MatrixError(parameterName, "requires at least two values."));

        var parsedValues = new List<SqlHarnessParameter>(displayValues.Length);
        var seen = new HashSet<MatrixValueKey>();
        foreach (var displayValue in displayValues)
        {
            SqlHarnessParameter parsed;
            try
            {
                parsed = SqlParameterParser.ParseOne($"{declaration}={displayValue}");
            }
            catch (SqlHarnessSafetyException)
            {
                throw new SqlHarnessSafetyException(MatrixError(parameterName, "is invalid."));
            }

            if (!seen.Add(MatrixValueKey.From(parsed)))
                throw new SqlHarnessSafetyException(MatrixError(parsed.Name, "contains a duplicate value."));

            parsedValues.Add(parsed);
        }

        var name = parsedValues[0].Name;
        foreach (var fixedParameter in fixedParameters)
        {
            var fixedName = SqlParameterParser.ParseOne(fixedParameter).Name;
            if (string.Equals(fixedName, name, StringComparison.OrdinalIgnoreCase))
                throw new SqlHarnessSafetyException(MatrixError(name, "duplicates a fixed parameter."));
        }

        return new ParsedParameterMatrix(name, type, displayValues, parsedValues);
    }

    // Same ASCII name rule as SqlParameterParser, so a rejection can name the parameter before ParseOne runs.
    private static string? TryCanonicalName(string text)
    {
        var typeSeparator = text.IndexOf(':');
        var name = typeSeparator < 0 ? text : text[..typeSeparator];
        if (name.Length == 0 || !NamePattern().IsMatch(name))
            return null;

        return "@" + name;
    }

    private static string MatrixError(string? parameterName, string statement) =>
        parameterName is null
            ? "The --matrix option is invalid."
            : $"The --matrix option for SQL parameter '{parameterName}' {statement}";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    // SqlDbType, precision, scale, size, and the invariant typed value. Not the raw display text.
    private readonly record struct MatrixValueKey(SqlDbType Type, byte? Precision, byte? Scale, int? Size, string InvariantValue)
    {
        public static MatrixValueKey From(SqlHarnessParameter parameter) =>
            new(parameter.Type, parameter.Precision, parameter.Scale, parameter.Size, InvariantText(parameter.Value));

        private static string InvariantText(object value) => value switch
        {
            DBNull => "null",
            byte[] bytes => Convert.ToBase64String(bytes),
            decimal number => CanonicalDecimal(number),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };

        // Parameter scale is already part of the key. Collapse numeric scale so 1.50 and 1.5 match.
        private static string CanonicalDecimal(decimal number)
        {
            var text = number.ToString(CultureInfo.InvariantCulture);
            var separator = text.IndexOf('.');
            if (separator < 0)
                return text;

            var trimmed = text.TrimEnd('0').TrimEnd('.');
            return trimmed.Length == 0 || trimmed == "-" ? "0" : trimmed;
        }
    }
}
