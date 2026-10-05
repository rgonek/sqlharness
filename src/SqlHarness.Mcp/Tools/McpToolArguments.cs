using System.Text.Json.Serialization;

namespace SqlHarness.Mcp.Tools;

/// <summary>
/// MCP tool argument shapes (spec section 4). These DTOs carry only tool-level
/// inputs: SQL sources, typed parameters, bounds, and modes. They never carry
/// target, auth, mutation, or credential fields, not even as hidden members:
/// the process scope owns the closed profile and mutation stays unavailable.
/// Unknown JSON properties are rejected at binding time via
/// <see cref="JsonUnmappedMemberHandling.Disallow"/>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpParameterArgument
{
    /// <summary>Parameter name without any prefix.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Optional Core type token (for example "int", "decimal(19,4)",
    /// "nvarchar(max)"). The Core binder owns type validation.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Culture-invariant value text, or JSON null for a typed NULL.
    /// Values are never echoed in errors.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>
/// Small shared SQL source object: exactly one of inline sql or a file under
/// an operator input root. Used for query/setup/baseline/candidate inputs.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpSqlSourceArgument
{
    /// <summary>Inline SQL (at most 1 MiB; larger batches go through file).</summary>
    [JsonPropertyName("sql")]
    public string? Sql { get; init; }

    /// <summary>Path under an operator input root. No file inputs when roots are empty.</summary>
    [JsonPropertyName("file")]
    public string? File { get; init; }
}

/// <summary>
/// One compare matrix dimension: a typed parameter name plus at least two
/// values in caller order. Each value travels to Core whole: a comma is an
/// ordinary character, an empty string is a text value, and JSON null is a
/// typed NULL of the matrix type. The Core binder owns value validation.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpMatrixArgument
{
    /// <summary>Matrix parameter name without any prefix.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Core type token for the matrix parameter.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// At least two distinct values: culture-invariant value text, or JSON
    /// null for a typed NULL. Values are never echoed in errors.
    /// </summary>
    [JsonPropertyName("values")]
    public required IReadOnlyList<string?> Values { get; init; }
}