using System.Globalization;
using System.Text.Json;

namespace SqlHarness.Core;

public static class CaseLabel
{
    public static string Format(ConditionsCase conditionsCase, int total, bool valuesStored)
    {
        ArgumentNullException.ThrowIfNull(conditionsCase);
        if (valuesStored && conditionsCase.Varying.Count > 0 && conditionsCase.Varying.All(parameter => parameter.Value.HasValue))
            return string.Join(", ", conditionsCase.Varying.Select(FormatParameter));
        if (conditionsCase.SetName is not null)
            return conditionsCase.SetName;
        return $"Case {conditionsCase.Index + 1} of {total}";
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
