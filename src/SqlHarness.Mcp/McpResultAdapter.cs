using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
public sealed record McpTruncationInfo(int OmittedItems, int MaxCellChars);

/// <summary>
/// Maps a Core <see cref="SqlHarnessOutcome"/> onto an SDK
/// <see cref="CallToolResult"/> inside a caller-supplied byte budget.
///
/// The single <see cref="TextContentBlock"/> carries the MCP v3 agent envelope
/// (schemaVersion/status/result, plus error and truncation when populated) as
/// compact JSON. There is no structured or third descriptive/table representation.
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
    private static readonly JsonSerializerOptions Json = CreateJson();

    /// <summary>MCP envelope schema version. The CLI envelope stays on <see cref="AgentEnvelopeSchema.Version"/>.</summary>
    public const int SchemaVersion = McpAgentEnvelope.Version;

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter<ResultComparisonMode>(JsonNamingPolicy.KebabCaseLower));
        options.Converters.Add(new JsonStringEnumConverter<WatchExitReason>(JsonNamingPolicy.KebabCaseLower));
        options.Converters.Add(new JsonStringEnumConverter<SnapshotVerdict>(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    internal static bool FitsCompleteResult(SqlHarnessOutcome outcome, string command, McpResultBudget budget)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        budget.Validate();
        var report = outcome.Report is SqlHarnessArtifactReadReport artifactRead ? artifactRead.Section : outcome.Report;
        var error = ToMcpError(RedactError(outcome.MachineError, [], budget.MaximumCellCharacters, out _));
        var status = StatusFor(outcome, error);
        var operationRef = outcome.OperationRefEligible && outcome.Journaled && IsAnnotatableCommand(command) && !string.IsNullOrWhiteSpace(outcome.OperationRef)
            ? outcome.OperationRef : null;
        return TryEnvelopeBytes(status, report, error, null, operationRef, out var bytes) &&
            TryBuildWithinBudget(bytes, outcome.ExitCode is not (SqlHarnessExitCode.Success or SqlHarnessExitCode.WatchMaxDuration or SqlHarnessExitCode.SnapshotDifferences), budget.MaximumBytes, out _);
    }

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
        var operationRef = outcome.OperationRefEligible && outcome.Journaled && IsAnnotatableCommand(command) && !string.IsNullOrWhiteSpace(outcome.OperationRef)
            ? outcome.OperationRef
            : null;

        var redactedError = RedactError(outcome.MachineError, secrets, maximumCellCharacters, out var errorClippedFields);
        var errorOmitted = errorClippedFields.Count;
        var error = ToMcpError(redactedError);
        var status = StatusFor(outcome, error);
        var isError = outcome.ExitCode is not (
            SqlHarnessExitCode.Success or
            SqlHarnessExitCode.WatchMaxDuration or
            SqlHarnessExitCode.SnapshotDifferences);

        var report = outcome.Report is SqlHarnessArtifactReadReport artifactRead
            ? artifactRead.Section
            : outcome.Report;
        var statementEvidence = AgentOutputProjection.StatementEvidenceFor(outcome);
        foreach (var detailLimit in AgentOutputProjection.CandidateDetailLimits)
        {
            var projected = PlaceCoverage(
                AgentOutputProjection.Project(
                    report, maximumCellCharacters, detailLimit, out var omitted, maximumBytes, AgentProjectionProfile.Mcp, statementEvidence),
                outcome,
                ref error);
            var totalOmitted = omitted + errorOmitted;

            // Reports the shared projection does not know (capabilities,
            // validation) travel raw when they fit, instead of degrading to a
            // bounded placeholder while bytes remain.
            if (projected is AgentBoundedOutput && report is not null &&
                TryEnvelopeBytes(status, report, error, null, operationRef, out var rawBytes) &&
                TryBuildWithinBudget(rawBytes, isError, maximumBytes, out var rawResult))
                return rawResult;

            var truncation = totalOmitted > 0
                ? new McpTruncationInfo(totalOmitted, maximumCellCharacters)
                : null;
            if (TryEnvelopeBytes(status, projected, error, truncation, operationRef, out var envelopeBytes) &&
                TryBuildWithinBudget(envelopeBytes, isError, maximumBytes, out var built))
                return built;
        }

        if (error is not null)
        {
            var low = 0;
            var high = Math.Max(Math.Max(error.Message.Length, error.Hint?.Length ?? 0), error.Location?.Path?.Length ?? 0);
            byte[]? bestErrorBytes = null;
            while (low <= high)
            {
                var characterLimit = low + (high - low) / 2;
                var message = AgentOutputLimits.PrefixErrorText(error.Message, characterLimit);
                var hint = error.Hint is null ? null : AgentOutputLimits.PrefixErrorText(error.Hint, characterLimit);
                var locationPath = error.Location?.Path is null
                    ? null
                    : AgentOutputLimits.PrefixErrorText(error.Location.Path, characterLimit);
                var omittedItems = (errorClippedFields.Message || message.Length < error.Message.Length ? 1 : 0) +
                    (errorClippedFields.Hint || (hint is not null && hint.Length < error.Hint!.Length) ? 1 : 0) +
                    (errorClippedFields.LocationPath || (locationPath is not null && locationPath.Length < error.Location!.Path!.Length) ? 1 : 0) +
                    (report is null ? 0 : 1);
                var budgetedError = error with
                {
                    Message = message,
                    Hint = hint,
                    Location = error.Location is null ? null : error.Location with { Path = locationPath },
                };
                var truncation = omittedItems > 0
                    ? new McpTruncationInfo(omittedItems, maximumCellCharacters)
                    : null;
                TryEnvelopeBytes("error", null, budgetedError, truncation, operationRef, out var candidateBytes);
                if (candidateBytes.Length <= maximumBytes)
                {
                    bestErrorBytes = candidateBytes;
                    low = characterLimit + 1;
                }
                else
                {
                    high = characterLimit - 1;
                }
            }

            if (bestErrorBytes is not null)
                return Build(bestErrorBytes, isError);

            var minimumError = error with
            {
                Message = string.Empty,
                Hint = null,
                Location = error.Location is null ? null : error.Location with { Path = null },
            };
            var minimumOmittedItems = (errorClippedFields.Message || error.Message.Length > 0 ? 1 : 0) +
                (errorClippedFields.Hint || error.Hint is { Length: > 0 } ? 1 : 0) +
                (errorClippedFields.LocationPath || error.Location?.Path is not null ? 1 : 0) + (report is null ? 0 : 1);
            var minimumTruncation = minimumOmittedItems > 0
                ? new McpTruncationInfo(minimumOmittedItems, maximumCellCharacters)
                : null;
            TryEnvelopeBytes("error", null, minimumError, minimumTruncation, operationRef, out var minimumErrorBytes);
            if (TryBuildWithinBudget(minimumErrorBytes, isError, maximumBytes, out var preservedErrorResult))
                return preservedErrorResult;
        }

        var minimal = new McpAgentEnvelope(
            SchemaVersion,
            "error",
            null,
            new McpAgentError(
                "output_budget_too_small",
                "render",
                "The minimum agent response does not fit the configured byte budget."),
            new McpTruncationInfo(1, maximumCellCharacters),
            operationRef);
        var minimalBytes = JsonSerializer.SerializeToUtf8Bytes(minimal, Json);
        if (TryBuildWithinBudget(minimalBytes, isError: true, maximumBytes, out var minimalResult))
            return minimalResult;

        // Defensive only: the minimum budget (4096) always fits the envelope
        // above, so reaching here means misconfiguration, not content size.
        var fallback = JsonSerializer.SerializeToUtf8Bytes(new McpAgentEnvelope(
            SchemaVersion, "error", null,
            new McpAgentError("output_budget_too_small", "render", "Agent output unavailable."),
            OperationRef: operationRef), Json);
        return Build(fallback, isError: true);
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
        TryEnvelopeBytes("success", report, null, null, null, out var bytes);
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
        Check(envelope, "status", JsonValueKind.String, violations);
        if (!envelope.TryGetProperty("result", out _))
            violations.Add("missing required property 'result'");
        foreach (var removed in new[] { "command", "exitCode", "parentOperationRef" })
        {
            if (envelope.TryGetProperty(removed, out _))
                violations.Add($"MCP v3 envelope must not include '{removed}'");
        }
        if (envelope.TryGetProperty("operationRef", out var operationRef) &&
            (operationRef.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(operationRef.GetString())))
            violations.Add("property 'operationRef' must be a non-empty string");

        if (envelope.TryGetProperty("schemaVersion", out var version) &&
            (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != SchemaVersion))
            violations.Add($"schemaVersion must be {SchemaVersion}");

        string? status = null;
        if (envelope.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
            status = statusElement.GetString();
        if (status is not ("success" or "partial" or "error" or "watch_max_duration" or "snapshot_differences"))
            violations.Add($"status '{status}' is not a known envelope status");

        if (envelope.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Null)
                violations.Add("empty error must be omitted");
            else if (error.ValueKind != JsonValueKind.Object)
                violations.Add("error must be an object");
            else
            {
                Check(error, "code", JsonValueKind.String, violations);
                Check(error, "phase", JsonValueKind.String, violations);
                Check(error, "message", JsonValueKind.String, violations);
            }
        }

        if (envelope.TryGetProperty("truncation", out var truncation))
        {
            if (truncation.ValueKind == JsonValueKind.Null)
                violations.Add("empty truncation must be omitted");
            else if (truncation.ValueKind != JsonValueKind.Object)
                violations.Add("truncation must be an object");
            else
            {
                Check(truncation, "omittedItems", JsonValueKind.Number, violations);
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

    private static string StatusFor(SqlHarnessOutcome outcome, McpAgentError? error)
    {
        if (outcome.Report is MutationBrokerResult mutation)
            return mutation.Status;
        if (error is null)
            return "success";
        return outcome.ExitCode switch
        {
            SqlHarnessExitCode.WatchMaxDuration => "watch_max_duration",
            SqlHarnessExitCode.SnapshotDifferences => "snapshot_differences",
            _ => outcome.Report is SqlHarnessCompareMatrixReport ||
                outcome.Report is SqlHarnessMeasureSetReport && outcome.Coverage is { CompletedCases: > 0 }
                    ? "partial"
                    : "error",
        };
    }

    private static bool IsAnnotatableCommand(string command) => command is
        "query" or "sqlharness_query" or
        "measure" or "sqlharness_measure" or
        "compare" or "sqlharness_compare" or
        "watch" or "sqlharness_watch" or
        "snapshot" or "sqlharness_snapshot";

    private readonly record struct ClippedErrorFields(bool Message, bool Hint, bool LocationPath)
    {
        public int Count => (Message ? 1 : 0) + (Hint ? 1 : 0) + (LocationPath ? 1 : 0);
    }

    private static SqlHarnessError? RedactError(
        SqlHarnessError? error,
        IReadOnlyList<string> secrets,
        int maximumCellCharacters,
        out ClippedErrorFields clippedFields)
    {
        clippedFields = default;
        if (error is null)
            return null;
        // The three fields share one secret set, so they share one compiled pattern.
        var redaction = SecretRedactor.For(secrets);
        var errorCellCharacters = AgentOutputLimits.ErrorCellCharacters(maximumCellCharacters);
        var message = redaction.Apply(error.Message);
        var hint = error.Hint is null ? null : redaction.Apply(error.Hint);
        var locationPath = error.Location?.Path is null ? null : redaction.Apply(error.Location.Path);
        clippedFields = new ClippedErrorFields(
            message.Length > errorCellCharacters,
            hint is not null && hint.Length > errorCellCharacters,
            locationPath is not null && locationPath.Length > maximumCellCharacters);
        var redacted = error with
        {
            Message = AgentOutputLimits.PrefixErrorText(message, errorCellCharacters),
            Hint = hint is null
                ? null
                : AgentOutputLimits.PrefixErrorText(hint, errorCellCharacters),
            Location = error.Location is null
                ? null
                : error.Location with
                {
                    Path = locationPath is null
                        ? null
                        : AgentOutputLimits.PrefixErrorText(locationPath, maximumCellCharacters),
                },
        };
        return redacted;
    }

    private static McpAgentError? ToMcpError(SqlHarnessError? error) =>
        error is null
            ? null
            : new McpAgentError(error.Code, error.Phase, error.Message, error.Hint, error.Location)
            {
                MatrixStop = error.MatrixStop,
            };

    private static object? PlaceCoverage(object? projected, SqlHarnessOutcome outcome, ref McpAgentError? error)
    {
        var coverage = outcome.Coverage
            ?? (outcome.Report as SqlHarnessCompareMatrixReport)?.Coverage
            ?? (projected as AgentMatrixOutput)?.Coverage
            ?? (projected as CompareMatrixBenchmarkSummary)?.Coverage
            ?? (projected as ArtifactMatrixCellsSection)?.Coverage
            ?? error?.MatrixStop;
        projected = projected switch
        {
            AgentMatrixOutput matrix => matrix.Coverage is null && coverage is not null
                ? matrix with { Coverage = coverage }
                : matrix,
            CompareMatrixBenchmarkSummary summary => summary.Coverage is null && coverage is not null
                ? summary with { Coverage = coverage }
                : summary,
            ArtifactMatrixCellsSection section => section.Coverage is null && coverage is not null
                ? section with { Coverage = coverage }
                : section,
            _ => projected,
        };
        var resultHasCoverage = projected is AgentMatrixOutput { Coverage: not null }
            or CompareMatrixBenchmarkSummary { Coverage: not null }
            or ArtifactMatrixCellsSection { Coverage: not null };
        if (resultHasCoverage)
        {
            if (error is { MatrixStop: not null })
                error = error with { MatrixStop = null };
            return projected;
        }

        if (projected is null && coverage is not null)
            error = (error ?? McpAgentError.Internal()) with { MatrixStop = coverage };
        return projected;
    }

    private static bool TryEnvelopeBytes(
        string status,
        object? result,
        object? error,
        McpTruncationInfo? truncation,
        string? operationRef,
        out byte[] bytes)
    {
        var envelope = new McpAgentEnvelope(SchemaVersion, status, result, error, truncation, operationRef);
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