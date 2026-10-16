using System.Text.Json.Serialization;

using SqlHarness.Core;

namespace SqlHarness.Mcp;

/// <summary>
/// MCP agent envelope v3. Required fields are <c>schemaVersion</c>,
/// <c>status</c>, and <c>result</c>. <c>error</c> and <c>truncation</c> are
/// omitted when empty. CLI envelope fields (<c>command</c>, <c>exitCode</c>,
/// <c>operationRef</c>, <c>parentOperationRef</c>) stay off the model text.
/// </summary>
public sealed record McpAgentEnvelope(
    int SchemaVersion,
    string Status,
    object? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpTruncationInfo? Truncation = null)
{
    /// <summary>MCP envelope schema version. Independent of the CLI envelope.</summary>
    public const int Version = 3;
}

/// <summary>
/// Model-facing MCP error object. Unsealed so later tasks can add fields such
/// as <c>matrixStop</c> without replacing the envelope.
/// </summary>
public record McpAgentError(
    string Code,
    string Phase,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SqlHarnessErrorLocation? Location = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MatrixExecutionCoverage? MatrixStop { get; init; }

    public const string InternalCode = "internal_error";
    public const string InternalPhase = "internal";
    public const string InternalMessage = "The tool failed without a safe error.";

    public static McpAgentError Internal(string? message = null) =>
        new(InternalCode, InternalPhase, string.IsNullOrWhiteSpace(message) ? InternalMessage : message);

    public static SqlHarnessError InternalOutcomeError(string? message = null) =>
        new(InternalCode, InternalPhase, string.IsNullOrWhiteSpace(message) ? InternalMessage : message);
}