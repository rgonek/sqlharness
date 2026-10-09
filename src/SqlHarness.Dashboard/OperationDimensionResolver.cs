using System.Text.Json;

namespace SqlHarness.Dashboard;

/// <summary>Resolves journaled scope variables without consulting current profile rules.</summary>
public static class OperationDimensionResolver
{
    public const string RecordedSource = "recorded";
    public const string UnknownSource = "unknown";
    public const string UnknownLabel = "Unknown";

    /// <summary>
    /// Reads string properties from the stored variables object. Invalid JSON and
    /// individual non-string values are treated as absent while valid siblings survive.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadRecordedValues(string? variablesJson)
    {
        if (string.IsNullOrWhiteSpace(variablesJson))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(variablesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var invalidNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (invalidNames.Contains(property.Name))
                    continue;
                if (values.ContainsKey(property.Name))
                {
                    values.Remove(property.Name);
                    invalidNames.Add(property.Name);
                }
                else if (property.Value.ValueKind == JsonValueKind.String)
                    values.Add(property.Name, property.Value.GetString()!);
                else
                    invalidNames.Add(property.Name);
            }
            return values;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Reads dimension names even when an individual stored value is malformed.</summary>
    public static IReadOnlyList<string> ReadDimensionNames(string? variablesJson)
    {
        if (string.IsNullOrWhiteSpace(variablesJson))
            return [];
        try
        {
            using var document = JsonDocument.Parse(variablesJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Builds stable labels for configured and historically recorded dimensions.</summary>
    public static IReadOnlyList<DimensionAttribution> Resolve(
        IEnumerable<string> dimensionNames,
        IReadOnlyDictionary<string, string> recordedValues)
    {
        return dimensionNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(name => recordedValues.TryGetValue(name, out var value)
                ? new DimensionAttribution(name, value, false, RecordedSource)
                : new DimensionAttribution(name, UnknownLabel, true, UnknownSource))
            .ToArray();
    }

    public static bool Matches(
        IReadOnlyDictionary<string, string?>? filters,
        IReadOnlyDictionary<string, string> recordedValues)
    {
        if (filters is null || filters.Count == 0)
            return true;

        foreach (var (name, expected) in filters)
        {
            if (expected is null)
            {
                if (recordedValues.ContainsKey(name))
                    return false;
            }
            else if (!recordedValues.TryGetValue(name, out var value)
                     || !string.Equals(value, expected, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}