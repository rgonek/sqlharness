using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.AI;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Mutations;

namespace SqlHarness.Mcp.Tools;

/// <summary>
/// Scope-bound tool handlers. Every method maps its arguments through
/// <see cref="McpOperationMapper"/> onto existing Core operations and executes
/// them on the frozen-scope module, so Core safety checks stay authoritative.
/// <see cref="RequestContext{T}"/> and <see cref="CancellationToken"/> are
/// SDK-injected (never part of the input schema); the context additionally
/// carries the raw argument keys so unknown properties are rejected here even
/// when the SDK binder only enforces them for scalar-only signatures.
/// Argument faults surface as failed results with safe text (input-path rejections echo only the caller's bounded, credential-filtered path and configured roots);
/// unexpected failures surface as a generic failed result. Every result goes
/// through <see cref="McpResultAdapter"/>, so success, controlled outcomes,
/// and failures all share the small budgeted agent envelope as both
/// structured content and JSON text.
///
/// Database tools (query, measure, compare, watch, snapshot) run
/// concurrently: each call gets its own per-call time budget (the process
/// maximum only lowered), which reaches Core as a linked deadline, and its own
/// cancellation, so cancelling one call never cancels another. Cancellation
/// propagates to Core on every path; a cancelled call reports a stable
/// cancelled result, never the natural watch deadline exit 7. Long calls send
/// throttled stage progress only when the client supplied a progress token,
/// without SQL, parameters, or rows. The host shutdown token is linked into
/// every execution, so closing the process (Ctrl+C/SIGTERM via the host token,
/// stdin EOF via the host's explicit EOF binding -- the SDK alone does not
/// propagate EOF) cancels every in-flight call, as does a protocol
/// cancellation for its own call: no hanging watch and sessions disposed by
/// Core.
/// </summary>
public sealed class McpToolHandlers(
    McpScope mcpScope,
    ISqlHarnessModule module,
    IMcpClock? clock = null,
    CancellationToken hostShutdown = default,
    bool acceptRequestScope = false,
    CompareConfig? compareConfig = null,
    IMutationBrokerClient? mutationBroker = null,
    string? mutationSessionKey = null,
    bool operationAnnotationsEnabled = false)
{
    private readonly McpScope scope = mcpScope;
    private readonly IMcpClock _clock = clock ?? SystemMcpClock.Instance;
    private readonly CancellationToken _hostShutdown = hostShutdown;
    private readonly bool _acceptRequestScope = acceptRequestScope;
    private readonly CompareConfig _compareConfig = compareConfig ?? SqlHarnessConfig.Default.Compare;
    private readonly IMutationBrokerClient _mutationBroker = mutationBroker ?? UnavailableMutationBrokerClient.Instance;
    private readonly string _mutationSessionKey = mutationSessionKey ?? "mcp:" + Guid.NewGuid().ToString("N");
    private readonly bool _operationAnnotationsEnabled = operationAnnotationsEnabled;

    public Task<CallToolResult> CapabilitiesAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description(McpToolCatalog.IncludeDiagnosticsDescription)]
        bool includeDiagnostics = false,
        [Description(McpToolCatalog.InputRootCursorDescription)]
        int inputRootCursor = 0,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_capabilities", ctx, _ =>
        {
            ThrowIfUnknown(ctx, ["includeDiagnostics", "inputRootCursor"]);
            return Task.FromResult(new SqlHarnessOutcome(
                SqlHarnessExitCode.Success,
                McpOperationMapper.BuildCapabilities(scope, includeDiagnostics, inputRootCursor, _compareConfig) with
                {
                    OperationAnnotations = _operationAnnotationsEnabled ? true : null,
                },
                null));
        }, ct);

    public Task<CallToolResult> QueryAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Inline SQL, at most 1 MiB. Exactly one of sql or file.")]
        string? sql = null,
        [Description("SQL file under an operator input root. Exactly one of sql or file.")]
        string? file = null,
        [Description(McpToolCatalog.ParametersDescription)]
        McpParameterArgument[]? parameters = null,
        [Description("SQL timeout 1..300 seconds.")]
        int timeout = 30,
        [Description("Presentation row cap 0..500.")]
        int maxRows = 50,
        [Description("Result text: 4096..1048576 bytes; default 65536; capped by process limit.")]
        int? maxResultBytes = null,
        [Description("Cell clip 0..4096 characters; default 2048.")]
        int? maxCellChars = null,
        [Description("Seconds 1..86400; may only lower the process limit.")]
        int? maxOperationSeconds = null,
        [Description("Offline validation only; no execution or writes.")]
        bool dryRun = false,
        CancellationToken ct = default,
        [Description(McpToolCatalog.OperationLabelDescription)] string? label = null,
        [Description(McpToolCatalog.OperationKeepDescription)] bool? keep = null) =>
        RunDbAsync("sqlharness_query", ctx, maxResultBytes, maxCellChars, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["sql", "file", "parameters", "timeout", "maxRows", "maxResultBytes", "maxCellChars", "maxOperationSeconds", "dryRun", .. AnnotationArgumentNames()]);
            var operation = await McpOperationMapper.MapQueryAsync(scope, sql, file, parameters, timeout, maxRows, token, dryRun);
            return await module.ExecuteAsync(McpOperationAnnotations.Apply(operation, label, keep, _operationAnnotationsEnabled), token);
        }, ct);

    public Task<CallToolResult> MutationAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Inline SQL, at most 1 MiB. Exactly one of sql or file.")]
        string? sql = null,
        [Description("SQL file under an operator input root. Exactly one of sql or file.")]
        string? file = null,
        [Description(McpToolCatalog.ParametersDescription)]
        McpParameterArgument[]? parameters = null,
        [Description("SQL timeout 1..300 seconds.")]
        int timeout = 30,
        [Description("Presentation row cap 0..500.")]
        int maxRows = 50,
        [Description("Database name this change is intended for.")]
        string? expectDatabase = null,
        [Description("Result text: 4096..1048576 bytes; default 65536; capped by process limit.")]
        int? maxResultBytes = null,
        [Description("Cell clip 0..4096 characters; default 2048.")]
        int? maxCellChars = null,
        [Description("Seconds 1..86400; may only lower the process limit.")]
        int? maxOperationSeconds = null,
        [Description("Offline validation only; no execution or approval request.")]
        bool dryRun = false,
        CancellationToken ct = default) =>
        RunDbAsync("sqlharness_mutation", ctx, maxResultBytes, maxCellChars, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["sql", "file", "parameters", "timeout", "maxRows", "expectDatabase", "maxResultBytes", "maxCellChars", "maxOperationSeconds", "dryRun"]);
            var operation = await McpOperationMapper.MapMutationAsync(
                scope, sql, file, parameters, timeout, maxRows, expectDatabase, _mutationSessionKey, token, dryRun);
            var outcome = await module.ExecuteAsync(operation, token);
            if (dryRun || outcome.Report is not SqlHarnessMutationPrepared prepared)
                return outcome;

            var progress = new McpProgressReporter(_clock);
            using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var progressTask = ReportApprovalProgressAsync(ctx, progress, progressCancellation.Token);
            MutationBrokerResult brokerResult;
            try
            {
                brokerResult = await _mutationBroker.SubmitAsync(prepared.Request, progress: null, token);
            }
            finally
            {
                await progressCancellation.CancelAsync();
                await progressTask.ConfigureAwait(false);
            }

            var status = IsMutationStatus(brokerResult.Status) ? brokerResult.Status : MutationStatus.Failed;
            brokerResult = brokerResult with { Status = status };
            var error = status == MutationStatus.Success
                ? null
                : new SqlHarnessError(status, "mutation", brokerResult.Message ?? status);
            return new SqlHarnessOutcome(MutationExitCode(status), brokerResult, brokerResult.Message, Error: error)
            {
                OperationRef = outcome.OperationRef,
                ParentOperationRef = outcome.ParentOperationRef,
            };
        }, ct);

    public Task<CallToolResult> MeasureAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Exactly one of sql/file.")]
        McpSqlSourceArgument query,
        McpSqlSourceArgument? setup = null,
        [Description(McpToolCatalog.ParametersDescription)]
        McpParameterArgument[]? parameters = null,
        [Description(McpToolCatalog.ParameterSetFilesDescription)]
        string[]? parameterSetFiles = null,
        [Description(McpToolCatalog.ParameterSetsDescription)]
        McpParameterSetArgument[]? parameterSets = null,
        [Description(McpToolCatalog.MatrixDescription)]
        McpMatrixArgument? matrix = null,
        [Description("Repetitions 1..100.")]
        int repeat = 5,
        [Description("SQL timeout 1..300 seconds.")]
        int? timeout = null,
        [Description("Result text: 4096..1048576 bytes; default 65536; capped by process limit.")]
        int? maxResultBytes = null,
        [Description("Cell clip 0..4096 characters; default 2048.")]
        int? maxCellChars = null,
        [Description("Seconds 1..86400; may only lower the process limit.")]
        int? maxOperationSeconds = null,
        [Description("Offline validation only; no execution or writes.")]
        bool dryRun = false,
        CancellationToken ct = default,
        [Description("Optional optimization subject; provide together with scope.")]
        string? subject = null,
        [Description("Optional optimization scope; provide together with subject.")]
        string? scope = null,
        [Description(McpToolCatalog.OperationLabelDescription)] string? label = null,
        [Description(McpToolCatalog.OperationKeepDescription)] bool? keep = null) =>
        RunDbAsync("sqlharness_measure", ctx, maxResultBytes, maxCellChars, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["query", "setup", "parameters", "parameterSets", "parameterSetFiles", "matrix", "repeat", "timeout", "maxResultBytes", "maxCellChars", "maxOperationSeconds", "dryRun", "subject", "scope", .. AnnotationArgumentNames()]);
            var operation = await McpOperationMapper.MapMeasureAsync(this.scope, query, setup, parameters, parameterSetFiles, repeat, timeout, token, dryRun, parameterSets, matrix, subject, scope);
            return await module.ExecuteAsync(McpOperationAnnotations.Apply(operation, label, keep, _operationAnnotationsEnabled), token);
        }, ct);

    public Task<CallToolResult> CompareAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Exactly one of sql/file.")]
        McpSqlSourceArgument baseline,
        [Description("Exactly one of sql/file.")]
        McpSqlSourceArgument candidate,
        [Description("Short stable identifier for the SQL being optimized.")]
        string subject,
        [Description("SQL fragment under test, or exactly 'whole' for the full query.")]
        string scope,
        McpSqlSourceArgument? setup = null,
        [Description(McpToolCatalog.ParametersDescription)]
        McpParameterArgument[]? parameters = null,
        [Description("Repetitions 1..100.")]
        int repeat = 5,
        [Description("SQL timeout 1..300 seconds.")]
        int? timeout = null,
        [Description("Result comparison mode.")]
        [AllowedValues("ordered", "multiset", "set", "off")]
        string compareResults = "ordered",
        [Description(McpToolCatalog.MatrixDescription)]
        McpMatrixArgument? matrix = null,
        [Description(McpToolCatalog.ParameterSetsDescription)]
        McpParameterSetArgument[]? parameterSets = null,
        [Description(McpToolCatalog.ParameterSetFilesDescription)]
        string[]? parameterSetFiles = null,
        [Description("Require matching allowNull; otherwise reported separately. Invalid with compareResults off.")]
        bool compareNullability = false,
        [Description("Result text: 4096..1048576 bytes; default 65536; capped by process limit.")]
        int? maxResultBytes = null,
        [Description("Cell clip 0..4096 characters; default 2048.")]
        int? maxCellChars = null,
        [Description("Seconds 1..86400; may only lower the process limit.")]
        int? maxOperationSeconds = null,
        [Description("Offline validation only; no execution or writes.")]
        bool dryRun = false,
        CancellationToken ct = default,
        [Description(McpToolCatalog.OperationLabelDescription)] string? label = null,
        [Description(McpToolCatalog.OperationKeepDescription)] bool? keep = null) =>
        RunDbAsync("sqlharness_compare", ctx, maxResultBytes, maxCellChars, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["baseline", "candidate", "subject", "scope", "setup", "parameters", "parameterSets", "parameterSetFiles", "matrix", "repeat", "timeout", "compareResults", "compareNullability", "maxResultBytes", "maxCellChars", "maxOperationSeconds", "dryRun", .. AnnotationArgumentNames()]);
            var operation = await McpOperationMapper.MapCompareAsync(this.scope, baseline, candidate, setup, parameters, repeat, timeout, compareResults, matrix, token, compareNullability, dryRun, parameterSets, parameterSetFiles, subject, scope, _compareConfig);
            return await module.ExecuteAsync(McpOperationAnnotations.Apply(operation, label, keep, _operationAnnotationsEnabled), token);
        }, ct);

    public Task<CallToolResult> CompareWithoutContextAsync(
        RequestContext<CallToolRequestParams> ctx,
        McpSqlSourceArgument baseline,
        McpSqlSourceArgument candidate,
        McpSqlSourceArgument? setup = null,
        McpParameterArgument[]? parameters = null,
        int repeat = 5,
        int? timeout = null,
        [AllowedValues("ordered", "multiset", "set", "off")] string compareResults = "ordered",
        McpMatrixArgument? matrix = null,
        McpParameterSetArgument[]? parameterSets = null,
        string[]? parameterSetFiles = null,
        bool compareNullability = false,
        int? maxResultBytes = null,
        [Description("Cell clip 0..4096 characters; default 2048.")]
        int? maxCellChars = null,
        int? maxOperationSeconds = null,
        bool dryRun = false,
        CancellationToken ct = default,
        [Description(McpToolCatalog.OperationLabelDescription)] string? label = null,
        [Description(McpToolCatalog.OperationKeepDescription)] bool? keep = null) =>
        RunDbAsync("sqlharness_compare", ctx, maxResultBytes, maxCellChars, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["baseline", "candidate", "setup", "parameters", "parameterSets", "parameterSetFiles", "matrix", "repeat", "timeout", "compareResults", "compareNullability", "maxResultBytes", "maxCellChars", "maxOperationSeconds", "dryRun", .. AnnotationArgumentNames()]);
            var operation = await McpOperationMapper.MapCompareAsync(this.scope, baseline, candidate, setup, parameters, repeat, timeout, compareResults, matrix, token, compareNullability, dryRun, parameterSets, parameterSetFiles, compareConfig: new CompareConfig { RequireContext = false });
            return await module.ExecuteAsync(McpOperationAnnotations.Apply(operation, label, keep, _operationAnnotationsEnabled), token);
        }, ct);

    public Task<CallToolResult> WatchAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Inline SQL, at most 1 MiB. Exactly one of sql or file.")]
        string? sql = null,
        [Description("SQL file under an operator input root. Exactly one of sql or file.")]
        string? file = null,
        [Description(McpToolCatalog.ParametersDescription)]
        McpParameterArgument[]? parameters = null,
        [Description("SQL timeout 1..300 seconds.")]
        int timeout = 30,
        [Description("Presentation row cap 0..500.")]
        int maxRows = 50,
        [Description("Stop predicate on the first row. Exactly one of until or untilUnchanged.")]
        string? until = null,
        [Description("Stop after N unchanged polls. Exactly one of until or untilUnchanged; default 3.")]
        int? untilUnchanged = null,
        [Description("Poll interval like 30s (s/m/h suffix, at most 24h). Default 30s.")]
        string? interval = null,
        [Description("Maximum watch time like 15m (s/m/h suffix, at most 24h). Default 15m.")]
        string? maxDuration = null,
        [Description("Result text: 4096..1048576 bytes; default 65536; capped by process limit.")]
        int? maxResultBytes = null,
        [Description("Cell clip 0..4096 characters; default 2048.")]
        int? maxCellChars = null,
        [Description("Seconds 1..86400; may only lower the process limit.")]
        int? maxOperationSeconds = null,
        CancellationToken ct = default,
        [Description(McpToolCatalog.OperationLabelDescription)] string? label = null,
        [Description(McpToolCatalog.OperationKeepDescription)] bool? keep = null) =>
        RunDbAsync("sqlharness_watch", ctx, maxResultBytes, maxCellChars, maxOperationSeconds, async (token, remaining) =>
        {
            ThrowIfUnknown(ctx, ["sql", "file", "parameters", "timeout", "maxRows", "until", "untilUnchanged", "interval", "maxDuration", "maxResultBytes", "maxCellChars", "maxOperationSeconds", .. AnnotationArgumentNames()]);
            var operation = await McpOperationMapper.MapWatchAsync(scope, sql, file, parameters, timeout, maxRows, until, untilUnchanged, interval, maxDuration, token);
            operation = McpOperationAnnotations.Apply(operation, label, keep, _operationAnnotationsEnabled);
            // The watch deadline is bounded by the remaining request budget, so
            // a natural max-duration stop stays a controlled exit 7 inside it.
            if (operation.MaxDuration > remaining)
                operation = operation with { MaxDuration = remaining };
            return await module.ExecuteAsync(operation, token);
        }, ct);

    public Task<CallToolResult> SnapshotAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("capture stores (never overwrites); diff compares without printing cell values.")]
        [AllowedValues("capture", "diff")]
        string action,
        [Description("Snapshot label matching ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$.")]
        string name,
        [Description("Inline SQL, at most 1 MiB. Exactly one of sql or file.")]
        string? sql = null,
        [Description("SQL file under an operator input root. Exactly one of sql or file.")]
        string? file = null,
        [Description(McpToolCatalog.ParametersDescription)]
        McpParameterArgument[]? parameters = null,
        [Description("SQL timeout 1..300 seconds.")]
        int timeout = 30,
        [Description("Presentation row cap 0..500.")]
        int maxRows = 50,
        [Description("Result text: 4096..1048576 bytes; default 65536; capped by process limit.")]
        int? maxResultBytes = null,
        [Description("Cell clip 0..4096 characters; default 2048.")]
        int? maxCellChars = null,
        [Description("Seconds 1..86400; may only lower the process limit.")]
        int? maxOperationSeconds = null,
        CancellationToken ct = default,
        [Description(McpToolCatalog.OperationLabelDescription)] string? label = null,
        [Description(McpToolCatalog.OperationKeepDescription)] bool? keep = null) =>
        RunDbAsync("sqlharness_snapshot", ctx, maxResultBytes, maxCellChars, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["action", "name", "sql", "file", "parameters", "timeout", "maxRows", "maxResultBytes", "maxCellChars", "maxOperationSeconds", .. AnnotationArgumentNames()]);
            var operation = await McpOperationMapper.MapSnapshotAsync(scope, action, name, sql, file, parameters, timeout, maxRows, token);
            return await module.ExecuteAsync(McpOperationAnnotations.Apply(operation, label, keep, _operationAnnotationsEnabled), token);
        }, ct);

    public Task<CallToolResult> PlanAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Inline plan document (Showplan XML or EXPLAIN JSON), at most 1 MiB. Exactly one of content or file.")]
        string? content = null,
        [Description("Plan file under an operator input root. Exactly one of content or file.")]
        string? file = null,
        [Description("includeText (default true) returns bounded statement text and predicates; false returns metadata only.")]
        bool includeText = true,
        [Description("Cell clip 0..4096 characters; default 2048.")]
        int? maxCellChars = null,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_plan", ctx, async token =>
        {
            ThrowIfUnknown(ctx, ["content", "file", "includeText", "maxCellChars"]);
            var outcome = await module.ExecuteAsync(await McpOperationMapper.MapPlanAsync(scope, content, file, token), token);
            return outcome.ExitCode == SqlHarnessExitCode.Success
                ? outcome with { Report = McpPlanTextPolicy.Apply(outcome.Report, includeText) }
                : outcome;
        }, ct, maxCellChars);

    public Task<CallToolResult> ArtifactAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Artifact directory name from a saved report.")]
        string id,
        [Description(McpToolCatalog.ArtifactSectionDescription)]
        [AllowedValues("summary", "metrics", "operators", "statements", "matrix-cells", "plan")]
        string section,
        [Description("Zero-based continuation cursor for matrix-cells, statements, operators, and plan nodes.")]
        int? cursor = null,
        [Description("Plan variant: baseline, candidate, or query.")]
        string? variant = null,
        [Description("Zero-based statement ordinal for section=plan.")]
        int statement = 0,
        [Description("Measure parameter set for section=plan.")]
        string? parameterSet = null,
        [Description(McpToolCatalog.RepetitionDescription)]
        int? repetition = null,
        [Description("Return bounded statement text and predicates in section=plan.")]
        bool includeText = false,
        [Description("Cell clip 0..4096 characters; default 2048.")]
        int? maxCellChars = null,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_artifact", ctx, async token =>
        {
            ThrowIfUnknown(ctx, ["id", "section", "cursor", "variant", "statement", "parameterSet", "repetition", "includeText", "maxCellChars"]);
            var operation = McpOperationMapper.MapArtifactRead(scope, id, section, cursor, variant, statement, parameterSet, repetition, includeText);
            return await module.ExecuteAsync(operation, token);
        }, ct, maxCellChars);

    public Task<CallToolResult> AnnotateAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("One to twenty journal operation references to annotate.")]
        [MinLength(1), MaxLength(20)] AnnotationItemArgument[] items,
        CancellationToken ct = default)
    {
        ThrowIfUnknown(ctx, ["items"]);
        if (!_operationAnnotationsEnabled)
            return Task.FromResult(Fail(SqlHarnessExitCode.Safety, "Operation annotations are disabled.", "sqlharness_annotate"));

        return RunAsync("sqlharness_annotate", ctx, async token =>
        {
            AnnotateHandler.Validate(items);
            var metadata = McpCallMetadata.Read(ctx?.Params?.Meta);
            return await module.ExecuteAsync(new SqlHarnessAnnotationOperation(async annotationToken =>
            {
                var matchingOn = AnnotateHandler.LoadMatchingFlag(SqlHarnessPaths.ConfigFile);
                var annotated = await AnnotateHandler.ApplyAsync(SqlHarnessPaths.ActivityDatabase,
                    _mutationSessionKey, metadata?.SessionId, items, matchingOn, annotationToken, scope.MaxResultBytes);
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, annotated, null);
            }), token);
        }, ct);
    }

    /// <summary>
    /// Rejects unknown argument keys before any mapping or execution. Keys
    /// compare case-insensitively (matching binder behavior); the rejection
    /// lists supported names only and never echoes values.
    /// </summary>
    private void ThrowIfUnknown(RequestContext<CallToolRequestParams> ctx, string[] known)
    {
        var keys = ctx?.Params?.Arguments?.Keys;
        if (keys is null)
            return;
        foreach (var key in keys)
        {
            var recognized = false;
            foreach (var name in known)
            {
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                {
                    recognized = true;
                    break;
                }
            }

            if (!recognized && _acceptRequestScope && string.Equals(key, "target", StringComparison.OrdinalIgnoreCase))
                recognized = true;

            if (!recognized)
                throw new McpMappingException("Unknown argument. Supported arguments: " + string.Join(", ", known) + ".");
        }
    }

    private string[] AnnotationArgumentNames() =>
        _operationAnnotationsEnabled ? McpToolCatalog.OperationAnnotationArguments : [];

    private async Task<CallToolResult> RunAsync(string command, RequestContext<CallToolRequestParams>? context, Func<CancellationToken, Task<SqlHarnessOutcome>> run, CancellationToken ct, int? maxCellChars = null)
    {
        McpResultBudget budget;
        try
        {
            budget = McpResultBudget.Resolve(scope.MaxResultBytes, null, maxCellChars, McpLimits.MaxCellCharactersLimit);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            var message = exception.ParamName == "callMaximumCellCharacters"
                ? exception.Message
                : "The requested budget is invalid.";
            return Fail(SqlHarnessExitCode.Safety, message, command);
        }
        try
        {
            // Discovery and local tools are bounded by the process time
            // budget through this token.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _hostShutdown);
            using var reason = McpCallRegistry.Track(linked, ct, _hostShutdown, McpCallMetadata.Read(context?.Params?.Meta));
            linked.CancelAfter(TimeSpan.FromSeconds(scope.MaxOperationSeconds));
            var outcome = await run(linked.Token);
            // Same rule as RunDbAsync: a non-success outcome under a cancelled token is a cancellation.
            linked.Token.ThrowIfCancellationRequested();
            return await McpResultAdapter.AdaptAndCompleteAsync(outcome, command, budget);
        }
        catch (OperationCanceledException)
        {
            return McpCallOutcome.CancelledResult(command, budget);
        }
        catch (Exception exception) when (exception is McpMappingException
            or McpInputException
            or ParameterSetFileException)
        {
            // Contract rejections carry safe text; input-path rejections may echo the caller's own
            // credential-filtered, length-bounded path and the configured input roots.
            return Fail(SqlHarnessExitCode.Safety, exception.Message, command, budget);
        }
        catch (ArtifactReadException exception)
        {
            return Fail(exception.ExitCode, exception.Message, command, budget);
        }
        catch (Exception)
        {
            return InternalFailure(command, budget);
        }
    }

    /// <summary>
    /// Executes one database call with the resolved budgets. Concurrent calls
    /// each run here with their own linked deadline and cancellation.
    /// </summary>
    private async Task<CallToolResult> RunDbAsync(
        string command,
        RequestContext<CallToolRequestParams>? context,
        int? maxResultBytes,
        int? maxCellChars,
        int? maxOperationSeconds,
        Func<CancellationToken, TimeSpan, Task<SqlHarnessOutcome>> run,
        CancellationToken ct)
    {
        McpResultBudget budget;
        TimeSpan callBudget;
        try
        {
            budget = McpResultBudget.Resolve(scope.MaxResultBytes, maxResultBytes, maxCellChars, McpLimits.MaxCellCharactersLimit);
            callBudget = TimeSpan.FromSeconds(
                McpLimits.ResolveOperationSeconds(scope.MaxOperationSeconds, maxOperationSeconds));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            var message = exception.ParamName == "callMaximumCellCharacters"
                ? exception.Message
                : "The requested budget is invalid.";
            return Fail(SqlHarnessExitCode.Safety, message, command);
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _hostShutdown);
            using var reason = McpCallRegistry.Track(linked, ct, _hostShutdown, McpCallMetadata.Read(context?.Params?.Meta));
            linked.CancelAfter(callBudget);
            var progress = new McpProgressReporter(_clock);
            await progress.ReportAsync(context, command, McpProgressReporter.Started, linked.Token);
            SqlHarnessOutcome outcome;
            try
            {
                outcome = await run(linked.Token, callBudget);
            }
            catch (Exception exception) when (exception is McpMappingException
                or McpInputException
                or ParameterSetFileException)
            {
                // Contract rejections carry safe text; input-path rejections may echo the caller's own
                // credential-filtered, length-bounded path and the configured input roots.
                return Fail(SqlHarnessExitCode.Safety, exception.Message, command, budget);
            }
            catch (ArtifactReadException exception)
            {
                return Fail(exception.ExitCode, exception.Message, command, budget);
            }

            // Core may translate provider cancellation into a failure outcome
            // after disposing its session. Keep the MCP contract stable by
            // checking the linked request budget before adapting that outcome.
            linked.Token.ThrowIfCancellationRequested();
            await progress.ReportAsync(context, command, McpProgressReporter.Finished, linked.Token);
            return await McpResultAdapter.AdaptAndCompleteAsync(outcome, command, budget);
        }
        catch (OperationCanceledException)
        {
            return McpCallOutcome.CancelledResult(command, budget);
        }
        catch (Exception)
        {
            return Fail(SqlHarnessExitCode.SqlExecution, null, command, budget);
        }
    }

    private CallToolResult Fail(SqlHarnessExitCode exitCode, string? message, string command, McpResultBudget? budget = null) =>
        McpResultAdapter.Adapt(
            new SqlHarnessOutcome(
                exitCode,
                null,
                string.IsNullOrWhiteSpace(message) ? "The tool failed without a safe error." : message),
            command,
            budget ?? new McpResultBudget(scope.MaxResultBytes));

    private CallToolResult InternalFailure(string command, McpResultBudget? budget = null) =>
        McpResultAdapter.Adapt(
            new SqlHarnessOutcome(
                SqlHarnessExitCode.Safety,
                null,
                McpAgentError.InternalMessage,
                Error: McpAgentError.InternalOutcomeError()),
            command,
            budget ?? new McpResultBudget(scope.MaxResultBytes));

    private static async Task ReportApprovalProgressAsync(
        RequestContext<CallToolRequestParams>? context,
        McpProgressReporter progress,
        CancellationToken ct)
    {
        if (context?.Params?.ProgressToken is null)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await progress.ReportAsync(context, "sqlharness_mutation", "awaiting_approval", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static SqlHarnessExitCode MutationExitCode(string status) => status switch
    {
        MutationStatus.Success => SqlHarnessExitCode.Success,
        MutationStatus.DatabaseMismatch => SqlHarnessExitCode.TargetMismatch,
        MutationStatus.Failed or MutationStatus.OutcomeUnknown => SqlHarnessExitCode.SqlExecution,
        _ => SqlHarnessExitCode.Safety,
    };

    private static bool IsMutationStatus(string status) => status is
        MutationStatus.Success or MutationStatus.Disabled or MutationStatus.Unavailable or MutationStatus.Timeout or
        MutationStatus.Denied or MutationStatus.Busy or MutationStatus.NotAMutation or MutationStatus.DatabaseMismatch or
        MutationStatus.PrincipalProtected or MutationStatus.Failed or MutationStatus.OutcomeUnknown;
}

internal sealed class UnavailableMutationBrokerClient : IMutationBrokerClient
{
    internal static readonly UnavailableMutationBrokerClient Instance = new();
    private UnavailableMutationBrokerClient() { }

    public Task<MutationBrokerResult> SubmitAsync(MutationRequest request, IProgress<string>? progress, CancellationToken ct) =>
        Task.FromResult(new MutationBrokerResult(MutationStatus.Unavailable, null, null));
}

/// <summary>
/// Explicit tool catalog: exactly the 9 MCP tools, registered one by one
/// by name. No assembly scanning (WithToolsFromAssembly) is used anywhere on
/// this path, so adding a public method can never silently widen the surface.
/// Annotations are conservative local-effect hints only: benchmark and
/// snapshot capture write data and journal accounting may write to disk, so they
/// never claim readOnly; hints never replace policy enforcement.
/// </summary>
public static class McpToolCatalog
{
    /// <summary>Shared capabilities strings: one definition for the fixed and request catalogs.</summary>
    public const string CapabilitiesDescription =
        "Runtime configuration of this server: scopeMode, active compareContext policy, connectionTimeoutSeconds, engines, allowedProfiles (request scope), inputRoots (paged), processLimits, configurationRef. connectionTimeoutSeconds is the effective SQL connection/authentication timeout; Azure CLI token acquisition has a separate existing budget. Call when this connection's configuration is unknown; reuse the result for this connection and call again after a reconnect or server restart.";
    public const string IncludeDiagnosticsDescription = "Add inputRootCount and local directory existence flags.";
    public const string InputRootCursorDescription = "Offset into inputRoots; follow inputRootsNextCursor until it is null.";
    public const string OperationLabelDescription = "Optional plain-text label for this operation (up to 120 Unicode code points).";
    public const string OperationKeepDescription = "Mark this operation as useful. The marker and operation can expire under normal journal retention.";
    private const string OperationAnnotationToolDescription = " Optional label and keep flag are stored with the local journal entry.";
    /// <summary>Common setup text; each catalog appends its engine's setup guidance.</summary>
    private const string SetupParameterName = "setup";
    public const string SetupDescription = "Once per measured session; setup locals do not carry into measured SQL.";
    public const string ParametersDescription = "Scalar values: culture-invariant text or JSON null. Non-scalar parameter inputs are unsupported by this engine.";
    public const string SqlServerParametersDescription = "Scalar values: culture-invariant text or JSON null. SQL Server also accepts kind tvp with exactly one of rows/rowsFrom and kind list with exactly one of values/valuesFrom in read-only query, measure, compare, watch, and snapshot operations. Source SQL must pass read-only classification; capabilities reports limits. Mutation rejects non-scalar inputs.";
    public const string ParameterSetFilesDescription = "2..32 .sqljson files under input roots; entries may be scalar strings or typed objects; exclusive with parameterSets/matrix; disjoint from fixed parameters.";
    public const string SqlServerParameterSetFilesDescription = "2..32 .sqljson files under input roots; entries may be scalar strings or typed objects. SQL Server TVP/list entries are accepted by read-only measure and compare operations; exclusive with parameterSets/matrix; disjoint from fixed parameters.";
    public const string ParameterSetsDescription = "2..32 named sets, matching declared types; exclusive with parameterSetFiles/matrix; disjoint from fixed parameters.";
    public const string MatrixDescription = "2..32 distinct values for one parameter; exclusive with parameterSets/parameterSetFiles; disjoint from fixed parameters.";
    public const string MutationDescription = "Persistent change (DML/DDL/GRANT/EXEC) on the scope's profile. Table-valued and list parameters are not supported. The operator's dashboard policy decides; the call waits up to the approval timeout. Any status other than success: stop and report; do not retry.";
    public const string MeasureDescription = "Measure one query across repeats. For an optimization measurement, provide both optional subject and textual scope; this is independent of compare.requireContext. Each parameter set runs on its own session with its own setup bound to that set; sets run in order and the first failure stops the run, keeping completed sets. Results report per-set stability, not cross-set equivalence. TVP and list parameter sets have no valueHash; their low-entropy values are excluded from unkeyed hashes.";
    public const string CompareDescription = "Compare baseline/candidate measurements and observed rows. subject and textual scope follow the frozen compare.requireContext policy reported by capabilities; request-scoped target selects the database. Each set has its own session/setup; first failure stops the run and retains completed cells. Read regression verdict/reasons separately from exitCode: pass means no elapsed regression under policy, not improvement. Observed equivalence follows the selected ordering, duplicate and nullability rules; it is not domain equivalence. Dry-run reports validation: passed, execution: not-run, and setup/baseline/candidate classifications.";
    public const string ArtifactSectionDescription = "Safe saved section. operators is capped; planShapeStable compares plan shape, not timing or row counts; null means insufficient evidence.";
    public const string ArtifactDescription = "Read an existing saved artifact offline. Use its returned continuation for matrix-cells, statements, operators, or plan pages; plan nodes report omittedChildCount and omittedSubtreeCount when clipped.";
    public const string WatchDescription = "Poll a bounded query until its stop condition or duration limit. Poll output has no continuation; inspect emitted polls, source omissions, and projection omissions in this response.";
    public const string QueryDescription = "A query duration is one execution's elapsed time, not benchmark evidence. Estimated plan rows are optimizer estimates; actualRows are observed rows and executions is the operator count. Dry-run reports validation: passed and execution: not-run.";
    public const string PlanDescription = "Distill a plan document offline. includeText (default true) returns bounded statement text and predicates; false returns metadata only. Clipped nodes report omittedChildCount and omittedSubtreeCount.";
    public const string RepetitionDescription = "Measured repetition for section=plan; default is the identified last measured run. A run without a saved plan reports missing evidence, never an earlier plan.";
    private const string AnnotationToolName = "sqlharness_annotate";
    private const string AnnotationToolDescription = "Apply an optional plain-text label and keep flag to one to twenty eligible journaled operations by operationRef. Unknown, expired, foreign, and out-of-scope references have the same per-item result. The call is journaled without an operationRef and does not create an annotation target.";

    public static readonly IReadOnlyList<string> ToolNames =
    [
        "sqlharness_capabilities",
        "sqlharness_query",
        "sqlharness_mutation",
        "sqlharness_measure",
        "sqlharness_compare",
        "sqlharness_watch",
        "sqlharness_snapshot",
        "sqlharness_plan",
        "sqlharness_artifact",
    ];

    internal static readonly string[] OperationAnnotationArguments = ["label", "keep"];

    /// <summary>Request scope admits SQL Server profiles only (startup check), so its catalog carries SQL Server guidance.</summary>
    private const SqlEngine RequestScopeEngine = SqlEngine.SqlServer;

    public static IReadOnlyList<McpServerTool> CreateTools(
        McpProcessContext process,
        IMcpClock? clock = null,
        CancellationToken hostShutdown = default,
        Func<McpScope, ISqlHarnessModule>? moduleFactory = null,
        IMutationBrokerClient? mutationBroker = null,
        string? mutationSessionKey = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.RequestScope)
            return CreateTools(process.FixedScope!, moduleFactory?.Invoke(process.FixedScope!) ?? process.Decorate(process.FixedScope!.CreateModule(process.CompareConfig, process.Config.Connection)), clock, hostShutdown, process.CompareConfig, mutationBroker, mutationSessionKey ?? process.MutationSessionKey, process.Config.Journal.OperationAnnotations);
        var factory = new ToolFactory(new McpRequestToolHandlers(process, clock, hostShutdown, moduleFactory, mutationBroker, mutationSessionKey ?? process.MutationSessionKey), RequestScopeEngine, process.Config.Journal.OperationAnnotations);
        var tools = new List<McpServerTool>(ToolNames.Count)
        {
            factory.Tool(ToolNames[0], nameof(McpRequestToolHandlers.CapabilitiesAsync), CapabilitiesDescription, true, false, true, false),
            factory.Tool(ToolNames[1], nameof(McpRequestToolHandlers.QueryAsync), factory.QueryDescription("Run a bounded query against the supplied request scope. " + QueryDescription), true, false, null, true, true),
            factory.Tool(ToolNames[2], nameof(McpRequestToolHandlers.MutationAsync), MutationDescription, null, false, null, true),
            factory.Tool(ToolNames[3], nameof(McpRequestToolHandlers.MeasureAsync), factory.QueryDescription(MeasureDescription), null, false, null, true, true),
            factory.Tool(ToolNames[4], process.CompareConfig.RequireContext ? nameof(McpRequestToolHandlers.CompareAsync) : nameof(McpRequestToolHandlers.CompareWithoutContextAsync), factory.QueryDescription(CompareDescription), null, false, null, true, true),
            factory.Tool(ToolNames[5], nameof(McpRequestToolHandlers.WatchAsync), WatchDescription, true, false, null, true, true),
            factory.Tool(ToolNames[6], nameof(McpRequestToolHandlers.SnapshotAsync), "Capture or diff a snapshot against the supplied request scope.", null, false, null, true, true),
            factory.Tool(ToolNames[7], nameof(McpRequestToolHandlers.PlanAsync), PlanDescription, true, false, true, false),
            factory.Tool(ToolNames[8], nameof(McpRequestToolHandlers.ArtifactAsync), ArtifactDescription + " Against the supplied request scope.", true, false, true, false),
        };
        if (process.Config.Journal.OperationAnnotations)
            tools.Add(factory.Tool(AnnotationToolName, nameof(McpRequestToolHandlers.AnnotateAsync), AnnotationToolDescription, false, false, true, false));
        var expectedCount = process.Config.Journal.OperationAnnotations ? McpLimits.MaxToolsWithOperationAnnotations : McpLimits.MaxToolsWithoutOperationAnnotations;
        if (tools.Count != expectedCount)
            throw new InvalidOperationException($"The MCP catalog must serve exactly {expectedCount} tools for the configured annotation mode.");
        return tools;
    }

    public static void Wire(
        ModelContextProtocol.Server.McpServerOptions options,
        McpProcessContext process,
        IMcpClock? clock = null,
        CancellationToken hostShutdown = default,
        Func<McpScope, ISqlHarnessModule>? moduleFactory = null,
        IMutationBrokerClient? mutationBroker = null,
        string? mutationSessionKey = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in CreateTools(process, clock, hostShutdown, moduleFactory, mutationBroker, mutationSessionKey)) collection.Add(tool);
        options.ToolCollection = collection;
    }

    public static IReadOnlyList<McpServerTool> CreateTools(
        McpScope scope,
        ISqlHarnessModule module,
        IMcpClock? clock = null,
        CancellationToken hostShutdown = default,
        CompareConfig? compareConfig = null,
        IMutationBrokerClient? mutationBroker = null,
        string? mutationSessionKey = null,
        bool operationAnnotationsEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(module);
        var policy = compareConfig ?? SqlHarnessConfig.Default.Compare;
        var handlers = new McpToolHandlers(scope, module, clock, hostShutdown, compareConfig: policy,
            mutationBroker: mutationBroker, mutationSessionKey: mutationSessionKey ?? "mcp:" + Guid.NewGuid().ToString("N"),
            operationAnnotationsEnabled: operationAnnotationsEnabled);
        var factory = new ToolFactory(handlers, scope.ResolvedTarget.Engine, operationAnnotationsEnabled);
        var tools = new List<McpServerTool>(ToolNames.Count)
        {
            factory.Tool(ToolNames[0], nameof(McpToolHandlers.CapabilitiesAsync), CapabilitiesDescription, true, false, true, false),
            factory.Tool(ToolNames[1], nameof(McpToolHandlers.QueryAsync), factory.QueryDescription("Run a bounded query passing the static visible-effects text check; no persistent-mutation flags are offered on this path. Hidden effects beyond the text are limited only by the DB account role prepared outside SQLHarness. Exactly one of inline sql (at most 1 MiB) or a file under an input root. " + QueryDescription), true, false, null, true, true),
            factory.Tool(ToolNames[2], nameof(McpToolHandlers.MutationAsync), MutationDescription, null, false, null, true),
            factory.Tool(ToolNames[3], nameof(McpToolHandlers.MeasureAsync), factory.QueryDescription(MeasureDescription), null, false, null, true, true),
            factory.Tool(ToolNames[4], policy.RequireContext ? nameof(McpToolHandlers.CompareAsync) : nameof(McpToolHandlers.CompareWithoutContextAsync), factory.QueryDescription(CompareDescription), null, false, null, true, true),
            factory.Tool(ToolNames[5], nameof(McpToolHandlers.WatchAsync), "Poll a bounded query passing the static visible-effects text check until or untilUnchanged, within interval/maxDuration bounds. " + WatchDescription, true, false, null, true, true),
            factory.Tool(ToolNames[6], nameof(McpToolHandlers.SnapshotAsync), "Capture a named result (never overwrites) or diff live results against it.", null, false, null, true, true),
            factory.Tool(ToolNames[7], nameof(McpToolHandlers.PlanAsync), PlanDescription, true, false, true, false),
            factory.Tool(ToolNames[8], nameof(McpToolHandlers.ArtifactAsync), ArtifactDescription + " Sections: summary, metrics, operators, statements, matrix-cells, and plan.", true, false, true, false),
        };
        if (operationAnnotationsEnabled)
            tools.Add(factory.Tool(AnnotationToolName, nameof(McpToolHandlers.AnnotateAsync), AnnotationToolDescription, false, false, true, false));
        var expectedCount = operationAnnotationsEnabled ? McpLimits.MaxToolsWithOperationAnnotations : McpLimits.MaxToolsWithoutOperationAnnotations;
        if (tools.Count != expectedCount)
            throw new InvalidOperationException($"The MCP catalog must serve exactly {expectedCount} tools for the configured annotation mode.");
        return tools;
    }

    public static void Wire(
        ModelContextProtocol.Server.McpServerOptions options,
        McpScope scope,
        ISqlHarnessModule module,
        IMcpClock? clock = null,
        CancellationToken hostShutdown = default,
        CompareConfig? compareConfig = null,
        IMutationBrokerClient? mutationBroker = null,
        string? mutationSessionKey = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in CreateTools(scope, module, clock, hostShutdown, compareConfig, mutationBroker, mutationSessionKey))
            collection.Add(tool);
        options.ToolCollection = collection;
    }

    /// <summary>
    /// Builds one catalog's tools with the selected engine's guidance from the Core registry.
    /// The engine's setup text enters through the SDK schema-creation options while each input
    /// schema is inferred, before registration; served schemas are never rewritten. Each catalog
    /// owns its options, so independently built catalogs share no description state.
    /// </summary>
    private sealed class ToolFactory
    {
        private readonly object _handlers;
        private readonly SqlHarnessEngineGuidance _guidance;
        private readonly AIJsonSchemaCreateOptions _schemaOptions;
        private readonly bool _operationAnnotationsEnabled;

        public ToolFactory(object handlers, SqlEngine engine, bool operationAnnotationsEnabled = false)
        {
            _handlers = handlers;
            _operationAnnotationsEnabled = operationAnnotationsEnabled;
            _guidance = SqlHarnessCapabilitiesProvider.GuidanceFor(engine);
            var setup = SetupDescription + " " + _guidance.Setup;
            _schemaOptions = AIJsonSchemaCreateOptions.Default with
            {
                ParameterDescriptionProvider = parameter => parameter.Name switch
                {
                    SetupParameterName => setup,
                    "parameters" => engine == SqlEngine.SqlServer ? SqlServerParametersDescription : ParametersDescription,
                    "parameterSetFiles" => engine == SqlEngine.SqlServer ? SqlServerParameterSetFilesDescription : ParameterSetFilesDescription,
                    _ => null,
                },
                IncludeParameter = parameter =>
                    _operationAnnotationsEnabled || !OperationAnnotationArguments.Contains(parameter.Name, StringComparer.Ordinal),
            };
        }

        public string QueryDescription(string common) => string.IsNullOrEmpty(_guidance.Query) ? common : common + " " + _guidance.Query;

        public McpServerTool Tool(string name, string method, string description, bool? readOnly, bool? destructive, bool? idempotent, bool? openWorld, bool annotationCapable = false) =>
            McpServerTool.Create(
                _handlers.GetType().GetMethod(method) ?? throw new InvalidOperationException($"Unknown MCP tool method '{method}'."),
                _handlers,
                new McpServerToolCreateOptions
                {
                    Name = name,
                    Description = annotationCapable && _operationAnnotationsEnabled ? description + OperationAnnotationToolDescription : description,
                    ReadOnly = readOnly,
                    Destructive = destructive,
                    Idempotent = idempotent,
                    OpenWorld = openWorld,
                    SchemaCreateOptions = _schemaOptions,
                });
    }
}