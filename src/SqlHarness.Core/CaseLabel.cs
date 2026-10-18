using System.Globalization;
using System.Text.Json;

namespace SqlHarness.Core;

public static class CaseLabel
{
    public static string Format(ConditionsCase conditionsCase, int total, bool valuesStored)
    {
        ArgumentNullException.ThrowIfNull(conditionsCase);
        if (conditionsCase.Varying.Count > 0 && conditionsCase.Varying.Any(parameter => parameter.Kind is "tvp" or "list"))
            return string.Join(", ", conditionsCase.Varying.Select(parameter => parameter.Kind is "tvp" or "list"
                ? FormatNonScalar(parameter, valuesStored)
                : parameter.Value.HasValue ? FormatParameter(parameter) : FormatMissingParameter(parameter)));
        if (valuesStored && conditionsCase.Varying.Count > 0 && conditionsCase.Varying.All(parameter => parameter.Value.HasValue))
            return string.Join(", ", conditionsCase.Varying.Select(FormatParameter));
        if (conditionsCase.SetName is not null)
            return conditionsCase.SetName;
        return $"Case {conditionsCase.Index + 1} of {total}";
    }

    private static string FormatNonScalar(ConditionsParameter parameter, bool valuesStored)
    {
        var name = parameter.Name.StartsWith('@') ? parameter.Name : "@" + parameter.Name;
        if (parameter.Kind == "tvp")
        {
            var type = parameter.EffectiveType.EndsWith(" READONLY", StringComparison.Ordinal)
                ? parameter.EffectiveType[..^" READONLY".Length]
                : parameter.EffectiveType;
            return $"{name} = {parameter.Count ?? 0} rows ({type.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal)})";
        }

        if (!valuesStored)
            return $"{name} IN ({parameter.Count ?? 0} values)";
        if (parameter.Values is not { } values || values.ValueKind != JsonValueKind.Array)
            return $"{name} IN ({parameter.Count ?? 0} values)";
        var items = values.EnumerateArray().ToArray();
        if (items.Length == 0)
            return $"{name} IN (0 values)";
        if (items.Length <= 3)
            return $"{name} IN ({string.Join(", ", items.Select(value => FormatListValue(value, parameter.EffectiveType)))})";
        return $"{name} IN (… {items.Length.ToString(CultureInfo.InvariantCulture)} values)";
    }

    private static string FormatListValue(JsonElement value, string effectiveType)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return "NULL";
        var type = effectiveType.StartsWith("list of ", StringComparison.Ordinal)
            ? effectiveType["list of ".Length..]
            : effectiveType;
        var baseType = type.Split('(')[0].ToLowerInvariant();
        if (value.ValueKind == JsonValueKind.String && baseType is not ("nvarchar" or "varchar" or "nchar" or "char" or "varbinary" or "binary"))
        {
            var raw = value.GetString()!;
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                return raw;
            if (bool.TryParse(raw, out var boolean))
                return boolean ? "1" : "0";
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var prefix = type.StartsWith('n') ? "N" : string.Empty;
            return $"{prefix}'{value.GetString()!.Replace("'", "''", StringComparison.Ordinal)}'";
        }
        return FormatParameter(new ConditionsParameter("@value", type, value)).Replace("@value = ", "", StringComparison.Ordinal);
    }

    private static string FormatMissingParameter(ConditionsParameter parameter)
    {
        var name = parameter.Name.StartsWith('@') ? parameter.Name : "@" + parameter.Name;
        return $"{name} = value not stored";
    }

    private static string FormatParameter(ConditionsParameter parameter)
    {
        var value = parameter.Value!.Value;
        var name = parameter.Name.StartsWith('@') ? parameter.Name : "@" + parameter.Name;
        if (value.ValueKind == JsonValueKind.Null)
            return $"{name} = NULL";
        if (value.ValueKind == JsonValueKind.String)
        {
            var type = parameter.EffectiveType;
            var prefix = type.StartsWith("n", StringComparison.OrdinalIgnoreCase) ? "N" : string.Empty;
            return $"{name} = {prefix}'{value.GetString()!.Replace("'", "''", StringComparison.Ordinal)}'";
        }
        if (value.ValueKind == JsonValueKind.True)
            return $"{name} = 1";
        if (value.ValueKind == JsonValueKind.False)
            return $"{name} = 0";
        if (value.ValueKind == JsonValueKind.Number)
            return $"{name} = {value.GetRawText()}";
        return $"{name} = {value.ToString().ToLower(CultureInfo.InvariantCulture)}";
    }
}
