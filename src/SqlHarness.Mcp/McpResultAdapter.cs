using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Mutations;

namespace SqlHarness.Mcp;

/// <summary>
/// Call-level result budget for one MCP tool response. The byte budget is the
/// UTF-8 size of the text block the model reads. Accepted range is
/// 4096..1048576 bytes; the cell limit is 0..4096 characters.
/// </summary>
public sealed record McpResultBudget(
    int MaximumBytes = McpLimits.DefaultCallToolResultBudgetBytes,
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
    /// Resolves the effective budget from the process ceiling and an optional
    /// per-call request. A call defaults to 65536 bytes and is clamped to the
    /// process ceiling. Both inputs must sit inside the accepted ranges.
    /// </summary>
    public static McpResultBudget Resolve(
        int processCeilingBytes,
        int? callMaximumBytes,
        int? callMaximumCellCharacters = null,
        int processMaximumCellCharacters = McpLimits.DefaultMaximumCellCharacters)
    {
        var ceiling = new McpResultBudget(processCeilingBytes, processMaximumCellCharacters);
        ceiling.Validate();
        if (callMaximumCellCharacters is < 0 or > McpLimits.MaxCellCharactersLimit)
            throw new ArgumentOutOfRangeException(
                nameof(callMaximumCellCharacters),
                callMaximumCellCharacters,
                $"Cell limit must be 0..{McpLimits.MaxCellCharactersLimit} characters.");
        var requestedBytes = callMaximumBytes ?? McpLimits.DefaultCallToolResultBudgetBytes;
        var effectiveBytes = Math.Min(requestedBytes, ceiling.MaximumBytes);
        var effectiveCells = Math.Min(
            callMaximumCellCharacters ?? McpLimits.DefaultMaximumCellCharacters,
            ceiling.MaximumCellCharacters);
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
/// The single <see cref="TextContentBlock"/> carries the agent envelope
/// (schemaVersion/command/status/exitCode/result/error/truncation) as compact
/// JSON. There is no structured or third descriptive/table representation.
///
/// Projection and secret handling reuse the shared Core pieces: reports go
/// through <see cref="AgentOutputProjection"/> (never a second projector)
/// (the plan tool keeps or omits statement text and predicates via
/// <see cref="McpPlanTextPolicy"/>), and error text goes through
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
    public const int SchemaVersion = AgentEnvelopeSchema.Version;

    /// <summary>
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

        var report = outcome.Report is SqlHarnessArtifactReadReport artifactRead
            ? artifactRead.Section
            : outcome.Report;
        foreach (var detailLimit in AgentOutputProjection.CandidateDetailLimits)
        {
            var projected = AgentOutputProjection.Project(
                report, maximumCellCharacters, detailLimit, out var omitted, maximumBytes);
            var totalOmitted = omitted + errorOmitted;

            // Reports the shared projection does not know (capabilities,
            // validation) travel raw when they fit, instead of degrading to a
            // bounded placeholder while bytes remain.
            if (projected is AgentBoundedOutput && report is not null &&
                TryEnvelopeBytes(boundedCommand, status, exitCode, report, error, null, outcome.OperationRef, outcome.ParentOperationRef, out var rawBytes) &&
                TryBuildWithinBudget(rawBytes, isError, maximumBytes, out var rawResult))
                return rawResult;

            var truncation = totalOmitted > 0
                ? new McpTruncationInfo(totalOmitted, detailLimit, maximumCellCharacters)
                : null;
            if (TryEnvelopeBytes(boundedCommand, status, exitCode, projected, error, truncation, outcome.OperationRef, outcome.ParentOperationRef, out var envelopeBytes) &&
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
        var fallback =
            /*lang=json*/ $"{{\"schemaVersion\":{SchemaVersion},\"command\":\"unknown\",\"status\":\"error\",\"exitCode\":2,\"result\":null,\"error\":{{\"code\":\"output_budget_too_small\",\"phase\":\"render\",\"message\":\"Agent output unavailable.\"}},\"truncation\":null}}";
        return Build(Encoding.UTF8.GetBytes(fallback), isError: true);
    }

    /// <summary>
    /// Measures the UTF-8 byte count of the result text blocks the model reads.
    /// </summary>
    public static int MeasureBytes(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Content.OfType<TextContentBlock>().Sum(block => Encoding.UTF8.GetByteCount(block.Text));
    }

    /// <summary>
    /// True when <paramref name="report"/> travels raw as a success result
    /// within <paramref name="maximumBytes"/>: the same envelope and text
    /// measurement <see cref="Adapt"/> uses for its raw path, so a caller can
    /// size a report (for example a capabilities page) before adapting it.
    /// </summary>
    internal static bool FitsRawSuccess(object report, string command, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(report);
        TryEnvelopeBytes(command, "success", (int)SqlHarnessExitCode.Success, report, null, null, null, null, out var bytes);
        return TryBuildWithinBudget(bytes, isError: false, maximumBytes, out _);
    }

    /// <summary>
    /// Emitted footprint of a result for activity-journal accounting: measured text bytes
    /// and line count. Journal token math reuses the Core estimator
    /// (<see cref="OutputFootprint.EstimateTokens"/>).
    /// </summary>
    public static OutputFootprint EmittedFootprint(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var byteCount = 0;
        var lines = 0;
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            var bytes = Encoding.UTF8.GetBytes(block.Text);
            byteCount += bytes.Length;
            foreach (var value in bytes)
                if (value == (byte)'\n')
                    lines++;
        }

        return new OutputFootprint(byteCount, byteCount == 0 ? 0 : lines + 1);
    }

    /// <summary>
    /// Builds the final budgeted MCP result and completes its operation
    /// emission receipt once its text block is known. Receipt completion is
    /// best-effort for journaling; the result
    /// remains the outcome produced by Core.
    /// </summary>
    internal static async Task<CallToolResult> AdaptAndCompleteAsync(
        SqlHarnessOutcome outcome,
        string command,
        McpResultBudget? budget = null)
    {
        var result = Adapt(outcome, command, budget);
        if (outcome.EmissionReceipt is { } receipt)
            await receipt.CompleteAsync(EmittedFootprint(result), CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Validates a parsed envelope against the fixed agent envelope structure.
    /// Returns human-readable violations; empty means the envelope conforms.
    /// A focused structural check, not a general schema evaluator: it covers
    /// exactly the fixed envelope shape described above.
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
            violations.Add($"schemaVersion must be {SchemaVersion}");

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
        if (outcome.Report is MutationBrokerResult mutation)
            return mutation.Status;
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
        // The three fields share one secret set, so they share one compiled pattern.
        var redaction = SecretRedactor.For(secrets);
        var redacted = error with
        {
            Message = Clip(redaction.Apply(error.Message), maximumCellCharacters, ref omitted),
            Hint = error.Hint is null
                ? null
                : Clip(redaction.Apply(error.Hint), maximumCellCharacters, ref omitted),
            Location = error.Location is null
                ? null
                : error.Location with
                {
                    Path = error.Location.Path is null
                        ? null
                        : Clip(redaction.Apply(error.Location.Path), maximumCellCharacters, ref omitted),
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
        string? operationRef,
        string? parentOperationRef,
        out byte[] bytes)
    {
        var envelope = new SqlHarnessAgentEnvelope(SchemaVersion, command, status, exitCode, result, error, truncation, operationRef, parentOperationRef);
        bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        return true;
    }

    /// Builds a result from envelope bytes and accepts it only when the UTF-8
    /// text the model reads fits the budget.
    private static bool TryBuildWithinBudget(
        byte[] envelopeUtf8, bool isError, int maximumBytes, out CallToolResult result)
    {
        result = Build(envelopeUtf8, isError);
        return envelopeUtf8.Length <= maximumBytes;
    }

    private static CallToolResult Build(byte[] envelopeUtf8, bool isError)
    {
        var text = Encoding.UTF8.GetString(envelopeUtf8);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = isError,
        };
    }
}