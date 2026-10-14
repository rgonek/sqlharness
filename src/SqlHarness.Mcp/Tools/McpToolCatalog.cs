using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;

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
    McpScope scope,
    ISqlHarnessModule module,
    IMcpClock? clock = null,
    CancellationToken hostShutdown = default,
    bool acceptRequestScope = false)
{
    private readonly IMcpClock _clock = clock ?? SystemMcpClock.Instance;
    private readonly CancellationToken _hostShutdown = hostShutdown;
    private readonly bool _acceptRequestScope = acceptRequestScope;

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
                McpOperationMapper.BuildCapabilities(scope, includeDiagnostics, inputRootCursor),
                null));
        }, ct);

    public Task<CallToolResult> QueryAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Inline SQL, at most 1 MiB. Exactly one of sql or file.")]
        string? sql = null,
        [Description("SQL file under an operator input root. Exactly one of sql or file.")]
        string? file = null,
        [Description("Parameters as {name, type, value}; value is a culture-invariant string or JSON null.")]
        McpParameterArgument[]? parameters = null,
        [Description("SQL timeout 1..300 seconds.")]
        int timeout = 30,
        [Description("Presentation row cap 0..500.")]
        int maxRows = 50,
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")]
        int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds; only lowers the process maximum.")]
        int? maxOperationSeconds = null,
        [Description("Check every argument offline and stop before connecting. Nothing runs, nothing is written.")]
        bool dryRun = false,
        CancellationToken ct = default) =>
        RunDbAsync("sqlharness_query", ctx, maxResultBytes, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["sql", "file", "parameters", "timeout", "maxRows", "maxResultBytes", "maxOperationSeconds", "dryRun"]);
            return await module.ExecuteAsync(
                await McpOperationMapper.MapQueryAsync(scope, sql, file, parameters, timeout, maxRows, token, dryRun),
                token);
        }, ct);

    public Task<CallToolResult> MeasureAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Measured query source: exactly one of sql or file.")]
        McpSqlSourceArgument query,
        [Description("Optional setup source: exactly one of sql or file, or omit.")]
        McpSqlSourceArgument? setup = null,
        [Description("Parameters as {name, type, value}; value is a culture-invariant string or JSON null.")]
        McpParameterArgument[]? parameters = null,
        [Description("At least two .sqljson parameter-set files under input roots; omit for a single set.")]
        string[]? paramSetFiles = null,
        [Description("Repetitions 1..100.")]
        int repeat = 5,
        [Description("SQL timeout 1..300 seconds.")]
        int? timeout = null,
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")]
        int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds; only lowers the process maximum.")]
        int? maxOperationSeconds = null,
        [Description("Check every argument offline and stop before connecting. Nothing runs, nothing is written.")]
        bool dryRun = false,
        CancellationToken ct = default) =>
        RunDbAsync("sqlharness_measure", ctx, maxResultBytes, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["query", "setup", "parameters", "paramSetFiles", "repeat", "timeout", "maxResultBytes", "maxOperationSeconds", "dryRun"]);
            return await module.ExecuteAsync(
                await McpOperationMapper.MapMeasureAsync(scope, query, setup, parameters, paramSetFiles, repeat, timeout, token, dryRun),
                token);
        }, ct);

    public Task<CallToolResult> CompareAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Baseline source: exactly one of sql or file.")]
        McpSqlSourceArgument baseline,
        [Description("Candidate source: exactly one of sql or file.")]
        McpSqlSourceArgument candidate,
        [Description("Optional setup source: exactly one of sql or file, or omit.")]
        McpSqlSourceArgument? setup = null,
        [Description("Parameters as {name, type, value}; value is a culture-invariant string or JSON null.")]
        McpParameterArgument[]? parameters = null,
        [Description("Repetitions 1..100.")]
        int repeat = 5,
        [Description("SQL timeout 1..300 seconds.")]
        int? timeout = null,
        [Description("Result comparison mode.")]
        [AllowedValues("ordered", "multiset", "set", "off")]
        string compareResults = "ordered",
        [Description("Optional single matrix dimension {name, type, values}: at least two distinct values, each a culture-invariant string or JSON null.")]
        McpMatrixArgument? matrix = null,
        [Description("Also require matching column nullability (allowNull) for result equivalence. Default compares observed data only and reports nullability separately. Not allowed with compareResults off.")]
        bool compareNullability = false,
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")]
        int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds; only lowers the process maximum.")]
        int? maxOperationSeconds = null,
        [Description("Check every argument offline and stop before connecting. Nothing runs, nothing is written.")]
        bool dryRun = false,
        CancellationToken ct = default) =>
        RunDbAsync("sqlharness_compare", ctx, maxResultBytes, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["baseline", "candidate", "setup", "parameters", "repeat", "timeout", "compareResults", "matrix", "compareNullability", "maxResultBytes", "maxOperationSeconds", "dryRun"]);
            return await module.ExecuteAsync(
                await McpOperationMapper.MapCompareAsync(scope, baseline, candidate, setup, parameters, repeat, timeout, compareResults, matrix, token, compareNullability, dryRun),
                token);
        }, ct);

    public Task<CallToolResult> WatchAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Inline SQL, at most 1 MiB. Exactly one of sql or file.")]
        string? sql = null,
        [Description("SQL file under an operator input root. Exactly one of sql or file.")]
        string? file = null,
        [Description("Parameters as {name, type, value}; value is a culture-invariant string or JSON null.")]
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
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")]
        int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds; only lowers the process maximum.")]
        int? maxOperationSeconds = null,
        CancellationToken ct = default) =>
        RunDbAsync("sqlharness_watch", ctx, maxResultBytes, maxOperationSeconds, async (token, remaining) =>
        {
            ThrowIfUnknown(ctx, ["sql", "file", "parameters", "timeout", "maxRows", "until", "untilUnchanged", "interval", "maxDuration", "maxResultBytes", "maxOperationSeconds"]);
            var operation = await McpOperationMapper.MapWatchAsync(scope, sql, file, parameters, timeout, maxRows, until, untilUnchanged, interval, maxDuration, token);
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
        [Description("Parameters as {name, type, value}; value is a culture-invariant string or JSON null.")]
        McpParameterArgument[]? parameters = null,
        [Description("SQL timeout 1..300 seconds.")]
        int timeout = 30,
        [Description("Presentation row cap 0..500.")]
        int maxRows = 50,
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")]
        int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds; only lowers the process maximum.")]
        int? maxOperationSeconds = null,
        CancellationToken ct = default) =>
        RunDbAsync("sqlharness_snapshot", ctx, maxResultBytes, maxOperationSeconds, async (token, _) =>
        {
            ThrowIfUnknown(ctx, ["action", "name", "sql", "file", "parameters", "timeout", "maxRows", "maxResultBytes", "maxOperationSeconds"]);
            return await module.ExecuteAsync(
                await McpOperationMapper.MapSnapshotAsync(scope, action, name, sql, file, parameters, timeout, maxRows, token),
                token);
        }, ct);

    public Task<CallToolResult> PlanAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Inline plan document (Showplan XML or EXPLAIN JSON), at most 1 MiB. Exactly one of content or file.")]
        string? content = null,
        [Description("Plan file under an operator input root. Exactly one of content or file.")]
        string? file = null,
        [Description("includeText (default true) returns bounded statement text and predicates; false returns metadata only.")]
        bool includeText = true,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_plan", ctx, async token =>
        {
            ThrowIfUnknown(ctx, ["content", "file", "includeText"]);
            var outcome = await module.ExecuteAsync(await McpOperationMapper.MapPlanAsync(scope, content, file, token), token);
            return outcome.ExitCode == SqlHarnessExitCode.Success
                ? outcome with { Report = McpPlanTextPolicy.Apply(outcome.Report, includeText) }
                : outcome;
        }, ct);

    public Task<CallToolResult> ArtifactAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Artifact directory name from a saved report.")]
        string id,
        [Description("Safe section to read.")]
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
        [Description("Measured repetition to select for section=plan.")]
        int? repetition = null,
        [Description("Return bounded statement text and predicates in section=plan.")]
        bool includeText = false,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_artifact", ctx, async token =>
        {
            ThrowIfUnknown(ctx, ["id", "section", "cursor", "variant", "statement", "parameterSet", "repetition", "includeText"]);
            var operation = McpOperationMapper.MapArtifactRead(scope, id, section, cursor, variant, statement, parameterSet, repetition, includeText);
            return await module.ExecuteAsync(operation, token);
        }, ct);

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

            if (!recognized && _acceptRequestScope && string.Equals(key, "scope", StringComparison.OrdinalIgnoreCase))
                recognized = true;

            if (!recognized)
                throw new McpMappingException("Unknown argument. Supported arguments: " + string.Join(", ", known) + ".");
        }
    }

    private async Task<CallToolResult> RunAsync(string command, RequestContext<CallToolRequestParams>? context, Func<CancellationToken, Task<SqlHarnessOutcome>> run, CancellationToken ct)
    {
        var budget = new McpResultBudget(scope.MaxResultBytes);
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
            return Fail(SqlHarnessExitCode.SqlExecution, null, command, budget);
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
        int? maxOperationSeconds,
        Func<CancellationToken, TimeSpan, Task<SqlHarnessOutcome>> run,
        CancellationToken ct)
    {
        McpResultBudget budget;
        TimeSpan callBudget;
        try
        {
            budget = McpResultBudget.Resolve(scope.MaxResultBytes, maxResultBytes);
            callBudget = TimeSpan.FromSeconds(
                McpLimits.ResolveOperationSeconds(scope.MaxOperationSeconds, maxOperationSeconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Fail(SqlHarnessExitCode.Safety, "The requested budget is invalid.", command);
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
}

/// <summary>
/// Explicit tool catalog: exactly the 8 MCP tools, registered one by one
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
        "Runtime configuration of this server: scopeMode, engines, allowedProfiles (request scope), inputRoots (paged), processLimits, configurationRef. Call when this connection's configuration is unknown; reuse the result while configurationRef is unchanged.";
    public const string IncludeDiagnosticsDescription = "Add inputRootCount and local directory existence flags.";
    public const string InputRootCursorDescription = "Offset into inputRoots; follow inputRootsNextCursor until it is null.";

    public static readonly IReadOnlyList<string> ToolNames =
    [
        "sqlharness_capabilities",
        "sqlharness_query",
        "sqlharness_measure",
        "sqlharness_compare",
        "sqlharness_watch",
        "sqlharness_snapshot",
        "sqlharness_plan",
        "sqlharness_artifact",
    ];

    public static IReadOnlyList<McpServerTool> CreateTools(
        McpProcessContext process,
        IMcpClock? clock = null,
        CancellationToken hostShutdown = default,
        Func<McpScope, ISqlHarnessModule>? moduleFactory = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.RequestScope)
            return CreateTools(process.FixedScope!, moduleFactory?.Invoke(process.FixedScope!) ?? process.Decorate(process.FixedScope!.CreateModule()), clock, hostShutdown);
        var handlers = new McpRequestToolHandlers(process, clock, hostShutdown, moduleFactory);
        var type = typeof(McpRequestToolHandlers);
        McpServerTool Tool(string name, string method, string description, bool? readOnly, bool? destructive,
            bool? idempotent, bool? openWorld) => McpServerTool.Create(
            type.GetMethod(method) ?? throw new InvalidOperationException($"Unknown MCP tool method '{method}'."), handlers,
            new McpServerToolCreateOptions
            {
                Name = name,
                Description = description,
                ReadOnly = readOnly,
                Destructive = destructive,
                Idempotent = idempotent,
                OpenWorld = openWorld,
            });
        var tools = new List<McpServerTool>(ToolNames.Count)
        {
            Tool(ToolNames[0], nameof(McpRequestToolHandlers.CapabilitiesAsync), CapabilitiesDescription, true, false, true, false),
            Tool(ToolNames[1], nameof(McpRequestToolHandlers.QueryAsync), "Run a bounded query against the supplied request scope.", true, false, null, true),
            Tool(ToolNames[2], nameof(McpRequestToolHandlers.MeasureAsync), "Measure one query against the supplied request scope.", null, false, null, true),
            Tool(ToolNames[3], nameof(McpRequestToolHandlers.CompareAsync), "Compare queries against the supplied request scope.", null, false, null, true),
            Tool(ToolNames[4], nameof(McpRequestToolHandlers.WatchAsync), "Poll a bounded query against the supplied request scope.", true, false, null, true),
            Tool(ToolNames[5], nameof(McpRequestToolHandlers.SnapshotAsync), "Capture or diff a snapshot against the supplied request scope.", null, false, null, true),
            Tool(ToolNames[6], nameof(McpRequestToolHandlers.PlanAsync), "Distill a plan document offline. includeText (default true) returns bounded statement text and predicates; false returns metadata only.", true, false, true, false),
            Tool(ToolNames[7], nameof(McpRequestToolHandlers.ArtifactAsync), "Read a safe section of an artifact against the supplied request scope.", true, false, true, false),
        };
        if (tools.Count != McpLimits.MaxTools)
            throw new InvalidOperationException($"The MCP catalog must serve exactly {McpLimits.MaxTools} tools.");
        return tools;
    }

    public static void Wire(
        ModelContextProtocol.Server.McpServerOptions options,
        McpProcessContext process,
        IMcpClock? clock = null,
        CancellationToken hostShutdown = default,
        Func<McpScope, ISqlHarnessModule>? moduleFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in CreateTools(process, clock, hostShutdown, moduleFactory)) collection.Add(tool);
        options.ToolCollection = collection;
    }

    public static IReadOnlyList<McpServerTool> CreateTools(
        McpScope scope,
        ISqlHarnessModule module,
        IMcpClock? clock = null,
        CancellationToken hostShutdown = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(module);
        var handlers = new McpToolHandlers(scope, module, clock, hostShutdown);
        var type = typeof(McpToolHandlers);
        McpServerTool Tool(
            string name,
            string method,
            string description,
            bool? readOnly,
            bool? destructive,
            bool? idempotent,
            bool? openWorld) =>
            McpServerTool.Create(
                type.GetMethod(method) ?? throw new InvalidOperationException($"Unknown MCP tool method '{method}'."),
                handlers,
                new McpServerToolCreateOptions
                {
                    Name = name,
                    Description = description,
                    ReadOnly = readOnly,
                    Destructive = destructive,
                    Idempotent = idempotent,
                    OpenWorld = openWorld,
                });
        var tools = new List<McpServerTool>(ToolNames.Count)
        {
            Tool(ToolNames[0], nameof(McpToolHandlers.CapabilitiesAsync), CapabilitiesDescription, true, false, true, false),
            Tool(ToolNames[1], nameof(McpToolHandlers.QueryAsync), "Run a bounded query passing the static visible-effects text check; no persistent-mutation flags are offered on this path. Hidden effects beyond the text are limited only by the DB account role prepared outside SQLHarness. Exactly one of inline sql (at most 1 MiB) or a file under an input root.", true, false, null, true),
            Tool(ToolNames[2], nameof(McpToolHandlers.MeasureAsync), "Measure one query across repeats, optionally with .sqljson parameter-set files. Setup runs once per session.", null, false, null, true),
            Tool(ToolNames[3], nameof(McpToolHandlers.CompareAsync), "Compare baseline vs candidate with the CLI sessions and equivalence rules. Optional single matrix dimension.", null, false, null, true),
            Tool(ToolNames[4], nameof(McpToolHandlers.WatchAsync), "Poll a bounded query passing the static visible-effects text check until until/untilUnchanged, within interval/maxDuration bounds.", true, false, null, true),
            Tool(ToolNames[5], nameof(McpToolHandlers.SnapshotAsync), "Capture a named result (never overwrites) or diff live results against it. No force flag.", null, false, null, true),
            Tool(ToolNames[6], nameof(McpToolHandlers.PlanAsync), "Distill a plan document offline. includeText (default true) returns bounded statement text and predicates; false returns metadata only.", true, false, true, false),
            Tool(ToolNames[7], nameof(McpToolHandlers.ArtifactAsync), "Read a safe section (summary, metrics, operators, statements, matrix-cells, or plan) of a saved benchmark artifact.", true, false, true, false),
        };
        if (tools.Count != McpLimits.MaxTools)
            throw new InvalidOperationException($"The MCP catalog must serve exactly {McpLimits.MaxTools} tools.");
        return tools;
    }

    public static void Wire(
        ModelContextProtocol.Server.McpServerOptions options,
        McpScope scope,
        ISqlHarnessModule module,
        IMcpClock? clock = null,
        CancellationToken hostShutdown = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in CreateTools(scope, module, clock, hostShutdown))
            collection.Add(tool);
        options.ToolCollection = collection;
    }
}