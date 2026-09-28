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
/// Argument faults surface as failed results with constant, value-free text;
/// unexpected failures surface as a generic failed result. Every result goes
/// through <see cref="McpResultAdapter"/>, so success, controlled outcomes,
/// and failures all share the small budgeted agent envelope as both
/// structured content and JSON text.
/// </summary>
public sealed class McpToolHandlers(McpScope scope, ISqlHarnessModule module)
{
    public Task<CallToolResult> CapabilitiesAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Include local counts and existence flags. Never secrets, paths, or profile lists.")]
        bool includeDiagnostics = false,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_capabilities", _ =>
        {
            ThrowIfUnknown(ctx, ["includeDiagnostics"]);
            return Task.FromResult(Ok(McpOperationMapper.BuildCapabilities(scope, includeDiagnostics), "sqlharness_capabilities"));
        }, ct);

    public Task<CallToolResult> InspectAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Catalog inspection to run.")]
        [AllowedValues("ping", "schema", "counts", "space", "qstop", "indexes")]
        string kind,
        [Description("Single object name or schema.name, where applicable.")]
        string? @object = null,
        [Description("Schema filter pattern; schema only, exclusive with object.")]
        string? filter = null,
        [Description("Table names; counts only, exclusive with like.")]
        string[]? tables = null,
        [Description("Table name pattern; counts only, exclusive with tables.")]
        string? like = null,
        [Description("Row cap 1..500; not for ping. Defaults per kind (counts 50, space 25, schema 50, qstop/indexes 20).")]
        int? top = null,
        [Description("Exact COUNT_BIG(*) instead of estimates; counts only.")]
        bool exact = false,
        [Description("Lookback like 24h (m/h/d suffix, 1..44640 minutes); qstop only.")]
        string? window = null,
        [Description("SQL timeout 1..300 seconds. Defaults per kind (ping 5, others 30).")]
        int? timeout = null,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_inspect", async token =>
        {
            ThrowIfUnknown(ctx, ["kind", "object", "filter", "tables", "like", "top", "exact", "window", "timeout"]);
            return OutcomeResult(await module.ExecuteAsync(
                McpOperationMapper.MapInspect(scope, kind, @object, filter, tables, like, top, exact, window, timeout),
                token), "sqlharness_inspect");
        }, ct);

    public Task<CallToolResult> ValidateAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Caller intent. Every usage runs the same Core offline classifier.")]
        [AllowedValues("query", "setup", "benchmark")]
        string usage,
        [Description("Inline SQL, at most 1 MiB. Exactly one of sql or file.")]
        string? sql = null,
        [Description("SQL file under an operator input root. Exactly one of sql or file.")]
        string? file = null,
        [Description("Parameters as {name, type, value}; value is a culture-invariant string or JSON null.")]
        McpParameterArgument[]? parameters = null,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_validate", async token =>
        {
            ThrowIfUnknown(ctx, ["usage", "sql", "file", "parameters"]);
            return Ok(await McpOperationMapper.MapValidateAsync(scope, sql, file, usage, parameters, token), "sqlharness_validate");
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
        CancellationToken ct = default) =>
        RunAsync("sqlharness_query", async token =>
        {
            ThrowIfUnknown(ctx, ["sql", "file", "parameters", "timeout", "maxRows"]);
            return OutcomeResult(await module.ExecuteAsync(
                await McpOperationMapper.MapQueryAsync(scope, sql, file, parameters, timeout, maxRows, token),
                token), "sqlharness_query");
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
        CancellationToken ct = default) =>
        RunAsync("sqlharness_measure", async token =>
        {
            ThrowIfUnknown(ctx, ["query", "setup", "parameters", "paramSetFiles", "repeat", "timeout"]);
            return OutcomeResult(await module.ExecuteAsync(
                await McpOperationMapper.MapMeasureAsync(scope, query, setup, parameters, paramSetFiles, repeat, timeout, token),
                token), "sqlharness_measure");
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
        [Description("Optional single matrix dimension {name, type, values}: at least two values, no commas.")]
        McpMatrixArgument? matrix = null,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_compare", async token =>
        {
            ThrowIfUnknown(ctx, ["baseline", "candidate", "setup", "parameters", "repeat", "timeout", "compareResults", "matrix"]);
            return OutcomeResult(await module.ExecuteAsync(
                await McpOperationMapper.MapCompareAsync(scope, baseline, candidate, setup, parameters, repeat, timeout, compareResults, matrix, token),
                token), "sqlharness_compare");
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
        CancellationToken ct = default) =>
        RunAsync("sqlharness_watch", async token =>
        {
            ThrowIfUnknown(ctx, ["sql", "file", "parameters", "timeout", "maxRows", "until", "untilUnchanged", "interval", "maxDuration"]);
            return OutcomeResult(await module.ExecuteAsync(
                await McpOperationMapper.MapWatchAsync(scope, sql, file, parameters, timeout, maxRows, until, untilUnchanged, interval, maxDuration, token),
                token), "sqlharness_watch");
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
        CancellationToken ct = default) =>
        RunAsync("sqlharness_snapshot", async token =>
        {
            ThrowIfUnknown(ctx, ["action", "name", "sql", "file", "parameters", "timeout", "maxRows"]);
            return OutcomeResult(await module.ExecuteAsync(
                await McpOperationMapper.MapSnapshotAsync(scope, action, name, sql, file, parameters, timeout, maxRows, token),
                token), "sqlharness_snapshot");
        }, ct);

    public Task<CallToolResult> PlanAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Inline plan document (Showplan XML or EXPLAIN JSON), at most 1 MiB. Exactly one of content or file.")]
        string? content = null,
        [Description("Plan file under an operator input root. Exactly one of content or file.")]
        string? file = null,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_plan", async token =>
        {
            ThrowIfUnknown(ctx, ["content", "file"]);
            var outcome = await module.ExecuteAsync(await McpOperationMapper.MapPlanAsync(scope, content, file, token), token);
            return outcome.ExitCode == SqlHarnessExitCode.Success
                ? Ok(McpResultSanitizer.Sanitize(outcome.Report), "sqlharness_plan")
                : OutcomeResult(outcome, "sqlharness_plan");
        }, ct);

    public Task<CallToolResult> ArtifactAsync(
        RequestContext<CallToolRequestParams> ctx,
        [Description("Artifact directory name from a saved report.")]
        string id,
        [Description("Safe section to read.")]
        [AllowedValues("summary", "metrics", "operators")]
        string section,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_artifact", _ =>
        {
            ThrowIfUnknown(ctx, ["id", "section"]);
            return Task.FromResult(Ok(McpOperationMapper.ReadArtifactSection(scope, id, section), "sqlharness_artifact"));
        }, ct);

    public Task<CallToolResult> GainAsync(
        RequestContext<CallToolRequestParams> ctx,
        CancellationToken ct = default) =>
        RunAsync("sqlharness_gain", async token =>
        {
            ThrowIfUnknown(ctx, []);
            return OutcomeResult(await module.ExecuteAsync(new SqlHarnessGainOperation(), token), "sqlharness_gain");
        }, ct);

    /// <summary>
    /// Rejects unknown argument keys before any mapping or execution. Keys
    /// compare case-insensitively (matching binder behavior); the rejection
    /// lists supported names only and never echoes values.
    /// </summary>
    private static void ThrowIfUnknown(RequestContext<CallToolRequestParams> ctx, string[] known)
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

            if (!recognized)
                throw new McpMappingException("Unknown argument. Supported arguments: " + string.Join(", ", known) + ".");
        }
    }

    private async Task<CallToolResult> RunAsync(string command, Func<CancellationToken, Task<CallToolResult>> run, CancellationToken ct)
    {
        try
        {
            return await run(ct);
        }
        catch (Exception exception) when (exception is McpMappingException
            or McpInputException
            or ParameterSetFileException)
        {
            // Contract rejections carry constant, value-free messages.
            return Fail(SqlHarnessExitCode.Safety, exception.Message, command);
        }
        catch (ArtifactReadException exception)
        {
            return Fail(SqlHarnessExitCode.LocalStorage, exception.Message, command);
        }
        catch (Exception)
        {
            return Fail(SqlHarnessExitCode.SqlExecution, null, command);
        }
    }

    private static CallToolResult OutcomeResult(SqlHarnessOutcome outcome, string command)
    {
        // Controlled outcomes (watch max duration, snapshot diff) are valid
        // results, not transport failures; the adapter maps only real
        // failures to IsError and keeps the Core exit code in the envelope.
        ArgumentNullException.ThrowIfNull(outcome);
        return McpResultAdapter.Adapt(outcome, command);
    }

    private static CallToolResult Ok(object? report, string command) =>
        McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), command);

    private static CallToolResult Fail(SqlHarnessExitCode exitCode, string? message, string command) =>
        McpResultAdapter.Adapt(
            new SqlHarnessOutcome(
                exitCode,
                null,
                string.IsNullOrWhiteSpace(message) ? "The tool failed without a safe error." : message),
            command);
}

/// <summary>
/// Explicit tool catalog: exactly the 11 MCP v1 tools, registered one by one
/// by name. No assembly scanning (WithToolsFromAssembly) is used anywhere on
/// this path, so adding a public method can never silently widen the surface.
/// </summary>
public static class McpToolCatalog
{
    public static readonly IReadOnlyList<string> ToolNames =
    [
        "sqlharness_capabilities",
        "sqlharness_inspect",
        "sqlharness_validate",
        "sqlharness_query",
        "sqlharness_measure",
        "sqlharness_compare",
        "sqlharness_watch",
        "sqlharness_snapshot",
        "sqlharness_plan",
        "sqlharness_artifact",
        "sqlharness_gain",
    ];

    public static IReadOnlyList<McpServerTool> CreateTools(McpScope scope, ISqlHarnessModule module)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(module);
        var handlers = new McpToolHandlers(scope, module);
        var type = typeof(McpToolHandlers);
        McpServerTool Tool(string name, string method, string description) =>
            McpServerTool.Create(
                type.GetMethod(method) ?? throw new InvalidOperationException($"Unknown MCP tool method '{method}'."),
                handlers,
                new McpServerToolCreateOptions { Name = name, Description = description });
        var tools = new List<McpServerTool>(ToolNames.Count)
        {
            Tool(ToolNames[0], nameof(McpToolHandlers.CapabilitiesAsync), "Describe server versions, scope engine, tools, and limits. Optional local diagnostics; no secrets or profile lists."),
            Tool(ToolNames[1], nameof(McpToolHandlers.InspectAsync), "Run one read-only catalog inspection: ping, schema, counts, space, qstop, or indexes. qstop/indexes are SQL Server only. No SQL input."),
            Tool(ToolNames[2], nameof(McpToolHandlers.ValidateAsync), "Classify SQL offline with Core safety; never connects. Exactly one of inline sql (at most 1 MiB) or a file under an input root."),
            Tool(ToolNames[3], nameof(McpToolHandlers.QueryAsync), "Run a bounded read-only query. Persistent mutation is always off. Exactly one of inline sql (at most 1 MiB) or a file under an input root."),
            Tool(ToolNames[4], nameof(McpToolHandlers.MeasureAsync), "Measure one query across repeats, optionally with .sqljson parameter-set files. Setup runs once per session."),
            Tool(ToolNames[5], nameof(McpToolHandlers.CompareAsync), "Compare baseline vs candidate with the CLI sessions and equivalence rules. Optional single matrix dimension."),
            Tool(ToolNames[6], nameof(McpToolHandlers.WatchAsync), "Poll a bounded read-only query until until/untilUnchanged, within interval/maxDuration bounds."),
            Tool(ToolNames[7], nameof(McpToolHandlers.SnapshotAsync), "Capture a named result (never overwrites) or diff live results against it. No force flag."),
            Tool(ToolNames[8], nameof(McpToolHandlers.PlanAsync), "Distill a plan document offline. Sanitized projection only: no statement text or literal predicates."),
            Tool(ToolNames[9], nameof(McpToolHandlers.ArtifactAsync), "Read one safe section (summary, metrics, operators) of a saved benchmark artifact."),
            Tool(ToolNames[10], nameof(McpToolHandlers.GainAsync), "Report the local output-savings aggregate."),
        };
        if (tools.Count != McpLimits.MaxTools)
            throw new InvalidOperationException($"The MCP catalog must serve exactly {McpLimits.MaxTools} tools.");
        return tools;
    }

    public static void Wire(ModelContextProtocol.Server.McpServerOptions options, McpScope scope, ISqlHarnessModule module)
    {
        ArgumentNullException.ThrowIfNull(options);
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in CreateTools(scope, module))
            collection.Add(tool);
        options.ToolCollection = collection;
    }
}
