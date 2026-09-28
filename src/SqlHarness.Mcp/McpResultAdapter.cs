using System.Text;
using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;

namespace SqlHarness.Mcp;

/// <summary>
/// Call-level result budget for one MCP tool response. The byte budget is the
/// whole serialized <see cref="CallToolResult"/> in UTF-8 (structured content,
/// text content, JSON escaping, and SDK metadata), never the result object
/// alone. Accepted range is 4096..1048576 bytes; the cell limit is 0..4096
/// characters.
/// </summary>
public sealed record McpResultBudget(
    int MaximumBytes = (int)McpLimits.CallToolResultBudgetBytes,
    int MaximumCellCharacters = McpLimits.DefaultMaximumCellCharacters)
{
    /// <summary>
    /// Rejects budgets outside 4096..1048576 bytes or with a cell limit
    /// outside 0..4096 characters.
    /// </summary>
    public void Validate()
    {
        if (MaximumBytes < McpLimits.MinCallToolResultBudgetBytes ||
            MaximumBytes > McpLimits.MaxCallToolResultBudgetBytes)
            throw new ArgumentOutOfRangeException(
                nameof(MaximumBytes),
                MaximumBytes,
                $"Result budget must be {McpLimits.MinCallToolResultBudgetBytes}..{McpLimits.MaxCallToolResultBudgetBytes} bytes.");
        if (MaximumCellCharacters < 0 || MaximumCellCharacters > McpLimits.MaxCellCharactersLimit)
            throw new ArgumentOutOfRangeException(
                nameof(MaximumCellCharacters),
                MaximumCellCharacters,
                $"Cell limit must be 0..{McpLimits.MaxCellCharactersLimit} characters.");
    }

    /// <summary>
    /// Resolves the effective budget from the process-operator maximum and an
    /// optional per-call request. A call may only lower the process maximum;
    /// a request above it is refused (the process value wins). Both inputs
    /// must sit inside the accepted ranges.
    /// </summary>
    public static McpResultBudget Resolve(
        int processMaximumBytes,
        int? callMaximumBytes,
        int? callMaximumCellCharacters = null,
        int processMaximumCellCharacters = McpLimits.DefaultMaximumCellCharacters)
    {
        var process = new McpResultBudget(processMaximumBytes, processMaximumCellCharacters);
        process.Validate();
        var effectiveBytes = callMaximumBytes.HasValue
            ? Math.Min(callMaximumBytes.Value, process.MaximumBytes)
            : process.MaximumBytes;
        var effectiveCells = callMaximumCellCharacters.HasValue
            ? Math.Min(callMaximumCellCharacters.Value, process.MaximumCellCharacters)
            : process.MaximumCellCharacters;
        var resolved = new McpResultBudget(effectiveBytes, effectiveCells);
        resolved.Validate();
        return resolved;
    }
}

/// <summary>Truncation marker carried by the agent envelope when content was bounded.</summary>
public sealed record McpTruncationInfo(int OmittedItems, int DetailLimit, int MaxCellChars);

/// <summary>
/// Maps a Core <see cref="SqlHarnessOutcome"/> onto an SDK
/// <see cref="CallToolResult"/> inside a caller-supplied byte budget.
///
/// Both representations carry the same small agent envelope
/// (schemaVersion/command/status/exitCode/result/error/truncation):
/// <see cref="CallToolResult.StructuredContent"/> holds the parsed envelope
/// and the single <see cref="TextContentBlock"/> holds its compact JSON.
/// There is no third descriptive/table representation.
///
/// Projection and secret handling reuse the shared Core pieces: reports go
/// through <see cref="AgentOutputProjection"/> (never a second projector)
/// after <see cref="McpResultSanitizer"/> strips plan statement text and
/// literal predicates, and error text goes through
/// <see cref="SecretRedactor"/> with caller-supplied secrets.
///
/// Semantics: only failures set <c>IsError</c>. The controlled outcomes
/// watch deadline (exit 7) and snapshot diff (exit 8) are valid results with
/// an explicit status and <c>IsError=false</c>; a failed matrix cell batch
/// keeps status <c>partial</c> with <c>IsError=true</c>. Protocol errors
/// (unknown method, malformed frame) stay with the SDK and never reach this
/// adapter. JSON is never truncated: detail levels descend until the envelope
/// fits, ending in a minimum valid error envelope.
/// </summary>
public static class McpResultAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Envelope schema version shared with the CLI agent output.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// JSON Schema (2020-12) for the agent envelope. It covers success,
    /// controlled outcomes (watch_max_duration, snapshot_differences,
    /// partial), and failures: result is free-form, error is null on success
    /// and a stable code/phase/message object otherwise.
    /// </summary>
    public static JsonDocument OutputSchema { get; } = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "SqlHarness MCP agent envelope",
          "type": "object",
          "required": ["schemaVersion", "command", "status", "exitCode", "result", "error", "truncation"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "command": { "type": "string", "minLength": 1 },
            "status": { "type": "string", "enum": ["success", "partial", "error", "watch_max_duration", "snapshot_differences"] },
            "exitCode": { "type": "integer" },
            "result": true,
            "error": {
              "type": ["object", "null"],
              "required": ["code", "phase", "message"],
              "properties": {
                "code": { "type": "string", "minLength": 1 },
                "phase": { "type": "string", "minLength": 1 },
                "message": { "type": "string" },
                "hint": { "type": ["string", "null"] },
                "location": { "type": ["object", "null"] }
              }
            },
            "truncation": {
              "type": ["object", "null"],
              "required": ["omittedItems", "detailLimit", "maxCellChars"],
              "properties": {
                "omittedItems": { "type": "integer", "minimum": 0 },
                "detailLimit": { "type": "integer", "minimum": 0 },
                "maxCellChars": { "type": "integer", "minimum": 0 }
              }
            }
          }
        }
        """);

    /// <summary>
    /// Adapts one outcome to a budgeted SDK result. The returned content is
    /// one text block; <c>IsError</c> is true only for failures.
    /// </summary>
    public static CallToolResult Adapt(
        SqlHarnessOutcome outcome,
        string command,
        McpResultBudget? budget = null,
        IReadOnlyList<string>? knownSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var active = budget ?? new McpResultBudget();
        active.Validate();
        var secrets = knownSecrets ?? [];
        var maximumBytes = active.MaximumBytes;
        var maximumCellCharacters = active.MaximumCellCharacters;

        var error = RedactError(outcome.MachineError, secrets, maximumCellCharacters, out var errorOmitted);
        var boundedCommand = Clip(command, maximumCellCharacters, ref errorOmitted);
        var status = StatusFor(outcome, error);
        var isError = outcome.ExitCode is not (
            SqlHarnessExitCode.Success or
            SqlHarnessExitCode.WatchMaxDuration or
            SqlHarnessExitCode.SnapshotDifferences);
        var exitCode = (int)outcome.ExitCode;

        var sanitized = McpResultSanitizer.Sanitize(outcome.Report);
        var level = AgentOutputProjection.CalculateDetailLimit(maximumBytes, maximumCellCharacters);
        var levels = new List<int>();
        while (level > 0)
        {
            levels.Add(level);
            if (level == 1)
                break;
            level = Math.Max(1, level / 2);
        }

        levels.Add(0);
        foreach (var detailLimit in levels.Distinct())
        {
            var projected = AgentOutputProjection.Project(
                sanitized, maximumCellCharacters, detailLimit, out var omitted, maximumBytes);
            var totalOmitted = omitted + errorOmitted;

            // Reports the shared projection does not know (capabilities,
            // validation) travel raw when they fit, instead of degrading to a
            // bounded placeholder while bytes remain.
            if (projected is AgentBoundedOutput && sanitized is not null &&
                TryEnvelopeBytes(boundedCommand, status, exitCode, sanitized, error, null, out var rawBytes) &&
                TryBuildWithinBudget(rawBytes, isError, maximumBytes, out var rawResult))
                return rawResult;

            var truncation = totalOmitted > 0
                ? new McpTruncationInfo(totalOmitted, detailLimit, maximumCellCharacters)
                : null;
            if (TryEnvelopeBytes(boundedCommand, status, exitCode, projected, error, truncation, out var envelopeBytes) &&
                TryBuildWithinBudget(envelopeBytes, isError, maximumBytes, out var built))
                return built;
        }

        var minimal = new SqlHarnessAgentEnvelope(
            SchemaVersion,
            boundedCommand,
            "error",
            (int)SqlHarnessExitCode.Safety,
            null,
            new SqlHarnessError(
                "output_budget_too_small",
                "render",
                "The minimum agent response does not fit the configured byte budget."),
            new McpTruncationInfo(1, 0, maximumCellCharacters));
        var minimalBytes = JsonSerializer.SerializeToUtf8Bytes(minimal, Json);
        if (TryBuildWithinBudget(minimalBytes, isError: true, maximumBytes, out var minimalResult))
            return minimalResult;

        // Defensive only: the minimum budget (4096) always fits the envelope
        // above, so reaching here means misconfiguration, not content size.
        const string Fallback =
            /*lang=json*/ "{\"schemaVersion\":1,\"command\":\"unknown\",\"status\":\"error\",\"exitCode\":2,\"result\":null,\"error\":{\"code\":\"output_budget_too_small\",\"phase\":\"render\",\"message\":\"Agent output unavailable.\"},\"truncation\":null}";
        return Build(Encoding.UTF8.GetBytes(Fallback), isError: true);
    }

    /// <summary>
    /// Measures the real wire cost of a result: the actual
    /// <see cref="CallToolResult"/> serialized with the SDK JSON options, in
    /// UTF-8 bytes. Escaping, both representations, and metadata are included
    /// by definition.
    /// </summary>
    public static int MeasureBytes(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).Length;
    }

    /// <summary>
    /// Emitted footprint of a result for gain accounting: measured wire bytes
    /// and line count. Gain token math reuses the Core estimator
    /// (<see cref="OutputFootprint.EstimateTokens"/>).
    /// </summary>
    public static OutputFootprint EmittedFootprint(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions);
        var lines = 0;
        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
                lines++;
        }

        return new OutputFootprint(bytes.Length, bytes.Length == 0 ? 0 : lines + 1);
    }

    /// <summary>
    /// Validates a parsed envelope against <see cref="OutputSchema"/>.
    /// Returns human-readable violations; empty means the envelope conforms.
    /// A focused structural check, not a general schema evaluator: it covers
    /// exactly the fixed envelope shape above.
    /// </summary>
    public static IReadOnlyList<string> ValidateEnvelope(JsonElement envelope)
    {
        var violations = new List<string>();
        if (envelope.ValueKind != JsonValueKind.Object)
            return ["envelope must be a JSON object"];

        Check(envelope, "schemaVersion", JsonValueKind.Number, violations);
        Check(envelope, "command", JsonValueKind.String, violations);
        Check(envelope, "status", JsonValueKind.String, violations);
        Check(envelope, "exitCode", JsonValueKind.Number, violations);
        if (!envelope.TryGetProperty("result", out _))
            violations.Add("missing required property 'result'");
        if (!envelope.TryGetProperty("error", out var error))
            violations.Add("missing required property 'error'");
        if (!envelope.TryGetProperty("truncation", out var truncation))
            violations.Add("missing required property 'truncation'");

        if (envelope.TryGetProperty("schemaVersion", out var version) &&
            (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != SchemaVersion))
            violations.Add("schemaVersion must be 1");

        string? status = null;
        if (envelope.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
            status = statusElement.GetString();
        if (status is not ("success" or "partial" or "error" or "watch_max_duration" or "snapshot_differences"))
            violations.Add($"status '{status}' is not a known envelope status");

        if (envelope.TryGetProperty("exitCode", out var exitElement) &&
            (!exitElement.TryGetInt32(out var exitCode) || !Enum.IsDefined(typeof(SqlHarnessExitCode), exitCode)))
            violations.Add("exitCode must be a known SqlHarnessExitCode value");

        if (envelope.TryGetProperty("error", out error) && error.ValueKind != JsonValueKind.Null)
        {
            if (error.ValueKind != JsonValueKind.Object)
                violations.Add("error must be an object or null");
            else
            {
                Check(error, "code", JsonValueKind.String, violations);
                Check(error, "phase", JsonValueKind.String, violations);
                Check(error, "message", JsonValueKind.String, violations);
            }
        }

        if (envelope.TryGetProperty("truncation", out truncation) && truncation.ValueKind != JsonValueKind.Null)
        {
            if (truncation.ValueKind != JsonValueKind.Object)
                violations.Add("truncation must be an object or null");
            else
            {
                Check(truncation, "omittedItems", JsonValueKind.Number, violations);
                Check(truncation, "detailLimit", JsonValueKind.Number, violations);
                Check(truncation, "maxCellChars", JsonValueKind.Number, violations);
            }
        }

        return violations;

        static void Check(JsonElement parent, string name, JsonValueKind kind, List<string> violations)
        {
            if (!parent.TryGetProperty(name, out var value))
                violations.Add($"missing required property '{name}'");
            else if (value.ValueKind != kind)
                violations.Add($"property '{name}' must be {kind}");
        }
    }

    private static string StatusFor(SqlHarnessOutcome outcome, SqlHarnessError? error)
    {
        if (error is null)
            return "success";
        return outcome.ExitCode switch
        {
            SqlHarnessExitCode.WatchMaxDuration => "watch_max_duration",
            SqlHarnessExitCode.SnapshotDifferences => "snapshot_differences",
            _ => outcome.Report is SqlHarnessCompareMatrixReport ? "partial" : "error",
        };
    }

    private static SqlHarnessError? RedactError(
        SqlHarnessError? error,
        IReadOnlyList<string> secrets,
        int maximumCellCharacters,
        out int omitted)
    {
        omitted = 0;
        if (error is null)
            return null;
        var redacted = error with
        {
            Message = Clip(SecretRedactor.Redact(error.Message, secrets), maximumCellCharacters, ref omitted),
            Hint = error.Hint is null
                ? null
                : Clip(SecretRedactor.Redact(error.Hint, secrets), maximumCellCharacters, ref omitted),
            Location = error.Location is null
                ? null
                : error.Location with
                {
                    Path = error.Location.Path is null
                        ? null
                        : Clip(SecretRedactor.Redact(error.Location.Path, secrets), maximumCellCharacters, ref omitted),
                },
        };
        return redacted;
    }

    private static string Clip(string value, int maximumCellCharacters, ref int omitted)
    {
        if (value.Length <= maximumCellCharacters)
            return value;
        omitted++;
        var length = maximumCellCharacters;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length];
    }

    private static bool TryEnvelopeBytes(
        string command,
        string status,
        int exitCode,
        object? result,
        SqlHarnessError? error,
        McpTruncationInfo? truncation,
        out byte[] bytes)
    {
        var envelope = new SqlHarnessAgentEnvelope(SchemaVersion, command, status, exitCode, result, error, truncation);
        bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        return true;
    }

    /// Builds a result from envelope bytes and accepts it only when the real
    /// wire cost (both representations, JSON escaping, SDK metadata) fits the
    /// budget. The envelope alone is roughly half the wire cost, so accepting
    /// on envelope bytes would admit over-budget responses; callers keep
    /// descending detail levels (down to the minimum envelope) until the wire
    /// fits.
    private static bool TryBuildWithinBudget(
        byte[] envelopeUtf8, bool isError, int maximumBytes, out CallToolResult result)
    {
        result = Build(envelopeUtf8, isError);
        return MeasureBytes(result) <= maximumBytes;
    }

    private static CallToolResult Build(byte[] envelopeUtf8, bool isError)
    {
        var text = Encoding.UTF8.GetString(envelopeUtf8);
        using var document = JsonDocument.Parse(envelopeUtf8);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = document.RootElement.Clone(),
            IsError = isError,
        };
    }
}
