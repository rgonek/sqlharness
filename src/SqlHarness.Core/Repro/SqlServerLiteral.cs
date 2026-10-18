using System.Text.Json;

namespace SqlHarness.Core;

internal static class SqlServerLiteral
{
    internal static string Format(ConditionsParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter.Kind == "tvp")
            return parameter.Name;
        if (parameter.Value is not { } value || value.ValueKind == JsonValueKind.Undefined)
            return "/* value not stored */";
        if (value.ValueKind == JsonValueKind.Null)
            return "NULL";

        var type = BaseType(parameter.EffectiveType);
        return value.ValueKind switch
        {
            JsonValueKind.String => FormatString(value.GetString()!, type),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => throw new ArgumentException($"Unsupported stored value for SQL type '{parameter.EffectiveType}'.", nameof(parameter)),
        };
    }

    private static string FormatString(string value, string type)
    {
        if (type is "varbinary" or "binary" or "image" or "timestamp" or "rowversion")
        {
            var bytes = Convert.FromBase64String(value);
            return "0x" + Convert.ToHexString(bytes);
        }

        var escaped = value.Replace("'", "''", StringComparison.Ordinal);
        var isUnicode = type.StartsWith('n') || type is "xml";
        return (isUnicode ? "N" : string.Empty) + "'" + escaped + "'";
    }

    private static string BaseType(string effectiveType)
    {
        var openParen = effectiveType.IndexOf('(');
        return (openParen < 0 ? effectiveType : effectiveType[..openParen]).Trim().ToLowerInvariant();
    }
}
