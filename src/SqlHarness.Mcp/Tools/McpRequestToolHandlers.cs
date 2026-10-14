using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;

namespace SqlHarness.Mcp.Tools;

public sealed record McpRequestScopeArgument(string Profile, Dictionary<string, string> Vars);

/// <summary>Request-mode facade. Each target-dependent invocation parses and resolves one immutable scope.</summary>
public sealed class McpRequestToolHandlers(
    McpProcessContext process,
    IMcpClock? clock = null,
    CancellationToken hostShutdown = default,
    Func<McpScope, ISqlHarnessModule>? moduleFactory = null)
{
    private static readonly JsonSerializerOptions ScopeJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IMcpClock _clock = clock ?? SystemMcpClock.Instance;
    private readonly Func<McpScope, ISqlHarnessModule> _moduleFactory = moduleFactory ?? (scope => process.Decorate(scope.CreateModule()));

    public Task<CallToolResult> CapabilitiesAsync(RequestContext<CallToolRequestParams> ctx,
        [Description(McpToolCatalog.IncludeDiagnosticsDescription)] bool includeDiagnostics = false,
        [Description(McpToolCatalog.InputRootCursorDescription)] int inputRootCursor = 0,
        CancellationToken ct = default) => TargetFreeAsync("sqlharness_capabilities", ctx, ct, token =>
    {
        ThrowUnknown(ctx, ["includeDiagnostics", "inputRootCursor"]);
        return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success,
            McpOperationMapper.BuildCapabilities(process, includeDiagnostics, inputRootCursor), null));
    });

    public Task<CallToolResult> QueryAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Inline SQL, at most 1 MiB.")] string? sql = null,
        [Description("SQL file under an operator input root.")] string? file = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        [Description("SQL timeout 1..300 seconds.")] int timeout = 30,
        [Description("Presentation row cap 0..500.")] int maxRows = 50,
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")] int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds; only lowers the process maximum.")] int? maxOperationSeconds = null,
        [Description("Check every argument offline and stop before connecting. Nothing runs, nothing is written.")] bool dryRun = false,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_query", scope, handler => handler.QueryAsync(ctx, sql, file, parameters, timeout, maxRows, maxResultBytes, maxOperationSeconds, dryRun, ct));

    public Task<CallToolResult> MeasureAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Query source: exactly one of inline sql or file.")] McpSqlSourceArgument query,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description(McpToolCatalog.SetupDescription)] McpSqlSourceArgument? setup = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        [Description(McpToolCatalog.ParameterSetFilesDescription)] string[]? parameterSetFiles = null,
        [Description(McpToolCatalog.ParameterSetsDescription)] McpParameterSetArgument[]? parameterSets = null,
        [Description(McpToolCatalog.MatrixDescription)] McpMatrixArgument? matrix = null,
        [Description("Repetitions 1..100.")] int repeat = 5,
        [Description("SQL timeout 1..300 seconds.")] int? timeout = null,
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")] int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds.")] int? maxOperationSeconds = null,
        [Description("Check every argument offline and stop before connecting. Nothing runs, nothing is written.")] bool dryRun = false,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_measure", scope, handler => handler.MeasureAsync(ctx, query, setup, parameters, parameterSetFiles, parameterSets, matrix, repeat, timeout, maxResultBytes, maxOperationSeconds, dryRun, ct));

    public Task<CallToolResult> CompareAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Baseline source.")] McpSqlSourceArgument baseline,
        [Description("Candidate source.")] McpSqlSourceArgument candidate,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description(McpToolCatalog.SetupDescription)] McpSqlSourceArgument? setup = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        [Description("Repetitions 1..100.")] int repeat = 5,
        [Description("SQL timeout 1..300 seconds.")] int? timeout = null,
        [Description("Result comparison mode.")][AllowedValues("ordered", "multiset", "set", "off")] string compareResults = "ordered",
        [Description(McpToolCatalog.MatrixDescription)] McpMatrixArgument? matrix = null,
        [Description(McpToolCatalog.ParameterSetsDescription)] McpParameterSetArgument[]? parameterSets = null,
        [Description(McpToolCatalog.ParameterSetFilesDescription)] string[]? parameterSetFiles = null,
        [Description("Also require matching column nullability (allowNull) for result equivalence. Default compares observed data only and reports nullability separately. Not allowed with compareResults off.")] bool compareNullability = false,
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")] int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds.")] int? maxOperationSeconds = null,
        [Description("Check every argument offline and stop before connecting. Nothing runs, nothing is written.")] bool dryRun = false,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_compare", scope, handler => handler.CompareAsync(ctx, baseline, candidate, setup, parameters, repeat, timeout, compareResults, matrix, parameterSets, parameterSetFiles, compareNullability, maxResultBytes, maxOperationSeconds, dryRun, ct));

    public Task<CallToolResult> WatchAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Inline SQL, at most 1 MiB.")] string? sql = null,
        [Description("SQL file under an operator input root.")] string? file = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        [Description("SQL timeout 1..300 seconds.")] int timeout = 30,
        [Description("Presentation row cap 0..500.")] int maxRows = 50,
        [Description("Stop predicate on the first row.")] string? until = null,
        [Description("Stop after N unchanged polls.")] int? untilUnchanged = null,
        [Description("Poll interval.")] string? interval = null,
        [Description("Maximum watch time.")] string? maxDuration = null,
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")] int? maxResultBytes = null,
        [Description("Time budget.")] int? maxOperationSeconds = null,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_watch", scope, handler => handler.WatchAsync(ctx, sql, file, parameters, timeout, maxRows, until, untilUnchanged, interval, maxDuration, maxResultBytes, maxOperationSeconds, ct));

    public Task<CallToolResult> SnapshotAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("capture stores (never overwrites); diff compares without printing cell values.")][AllowedValues("capture", "diff")] string action,
        [Description("Snapshot label.")] string name,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Inline SQL.")] string? sql = null,
        [Description("SQL file under an operator input root.")] string? file = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        [Description("SQL timeout.")] int timeout = 30,
        [Description("Presentation row cap.")] int maxRows = 50,
        [Description("Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling.")] int? maxResultBytes = null,
        [Description("Time budget.")] int? maxOperationSeconds = null,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_snapshot", scope, handler => handler.SnapshotAsync(ctx, action, name, sql, file, parameters, timeout, maxRows, maxResultBytes, maxOperationSeconds, ct));

    public Task<CallToolResult> PlanAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Inline plan document.")] string? content = null,
        [Description("Plan file under an operator input root.")] string? file = null,
        [Description("includeText (default true) returns bounded statement text and predicates; false returns metadata only.")] bool includeText = true,
        CancellationToken ct = default) => TargetFreeAsync("sqlharness_plan", ctx, ct, async token =>
        {
            ThrowUnknown(ctx, ["content", "file", "includeText"]);
            var operation = await McpOperationMapper.MapPlanAsync(process.InputRoots, content, file, token);
            var outcome = await process.CreateModule().ExecuteAsync(operation, token);
            return outcome.ExitCode == SqlHarnessExitCode.Success
                ? outcome with { Report = McpPlanTextPolicy.Apply(outcome.Report, includeText) }
                : outcome;
        });

    public Task<CallToolResult> ArtifactAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Artifact directory name from a saved report.")] string id,
        [Description(McpToolCatalog.ArtifactSectionDescription)][AllowedValues("summary", "metrics", "operators", "statements", "matrix-cells", "plan")] string section,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Zero-based continuation cursor for matrix-cells, statements, operators, and plan nodes.")] int? cursor = null,
        [Description("Plan variant: baseline, candidate, or query.")] string? variant = null,
        [Description("Zero-based statement ordinal for section=plan.")] int statement = 0,
        [Description("Measure parameter set for section=plan.")] string? parameterSet = null,
        [Description(McpToolCatalog.RepetitionDescription)] int? repetition = null,
        [Description("Return bounded statement text and predicates in section=plan.")] bool includeText = false,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_artifact", scope, handler => handler.ArtifactAsync(ctx, id, section, cursor, variant, statement, parameterSet, repetition, includeText, ct));

    private Task<CallToolResult> Invoke(RequestContext<CallToolRequestParams> ctx, string command, McpRequestScopeArgument suppliedScope,
        Func<McpToolHandlers, Task<CallToolResult>> invoke)
    {
        McpScope scope;
        try
        {
            scope = process.ResolveScope(ParseScope(ctx, suppliedScope));
        }
        catch (McpStartupException exception) when (exception.ExposeSafeMessage)
        {
            return Task.FromResult(Failure(exception.Message, command, process.MaxResultBytes));
        }
        catch (Exception exception) when (exception is McpStartupException or McpMappingException)
        {
            return Task.FromResult(Failure("The MCP request scope is invalid.", command, process.MaxResultBytes));
        }
        var handlers = new McpToolHandlers(scope, _moduleFactory(scope), _clock, hostShutdown, acceptRequestScope: true);
        return invoke(handlers);
    }

    private Task<CallToolResult> TargetFreeAsync(string command, RequestContext<CallToolRequestParams>? context, CancellationToken ct,
        Func<CancellationToken, Task<SqlHarnessOutcome>> run) => RunAsync(command, context, ct, run);

    private async Task<CallToolResult> RunAsync(string command, RequestContext<CallToolRequestParams>? context, CancellationToken ct,
        Func<CancellationToken, Task<SqlHarnessOutcome>> run)
    {
        var budget = new McpResultBudget(process.MaxResultBytes);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, hostShutdown);
            using var reason = McpCallRegistry.Track(linked, ct, hostShutdown, McpCallMetadata.Read(context?.Params?.Meta));
            linked.CancelAfter(TimeSpan.FromSeconds(process.MaxOperationSeconds));
            var outcome = await run(linked.Token);
            // Same rule as RunDbAsync: a non-success outcome under a cancelled token is a cancellation.
            linked.Token.ThrowIfCancellationRequested();
            return await McpResultAdapter.AdaptAndCompleteAsync(outcome, command, budget);
        }
        catch (OperationCanceledException) { return McpCallOutcome.CancelledResult(command, budget); }
        catch (Exception exception) when (exception is McpMappingException or McpInputException or ParameterSetFileException)
        { return Failure(exception.Message, command, process.MaxResultBytes); }
        catch (Exception) { return Failure(null, command, process.MaxResultBytes); }
    }

    private static McpRequestScope ParseScope(RequestContext<CallToolRequestParams> ctx, McpRequestScopeArgument suppliedScope)
    {
        JsonElement json;
        if (ctx?.Params?.Arguments?.TryGetValue("scope", out var raw) == true && raw is JsonElement element)
            json = element;
        else
            json = JsonSerializer.SerializeToElement(suppliedScope, ScopeJsonOptions);
        if (json.ValueKind != JsonValueKind.Object)
            throw new McpMappingException("The MCP request scope is invalid.");
        string? profile = null;
        Dictionary<string, string>? vars = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in json.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new McpMappingException("The MCP request scope is invalid.");
            if (string.Equals(property.Name, "profile", StringComparison.Ordinal))
            {
                if (property.Value.ValueKind != JsonValueKind.String) throw new McpMappingException("The MCP request scope is invalid.");
                profile = property.Value.GetString();
            }
            else if (string.Equals(property.Name, "vars", StringComparison.Ordinal))
            {
                if (property.Value.ValueKind != JsonValueKind.Object) throw new McpMappingException("The MCP request scope is invalid.");
                vars = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var variable in property.Value.EnumerateObject())
                {
                    if (!vars.TryAdd(variable.Name, variable.Value.ValueKind == JsonValueKind.String
                            ? variable.Value.GetString()! : throw new McpMappingException("The MCP request scope is invalid.")))
                        throw new McpMappingException("The MCP request scope is invalid.");
                }
            }
            else throw new McpMappingException("The MCP request scope is invalid.");
        }
        if (profile is null || vars is null) throw new McpMappingException("The MCP request scope is invalid.");
        return new McpRequestScope(profile, vars);
    }

    private static void ThrowUnknown(RequestContext<CallToolRequestParams> ctx, string[] known)
    {
        foreach (var key in ctx?.Params?.Arguments?.Keys ?? [])
            if (!known.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new McpMappingException("Unknown argument. Supported arguments: " + string.Join(", ", known) + ".");
    }

    private static CallToolResult Failure(string? message, string command, int maxBytes) => McpResultAdapter.Adapt(
        new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, string.IsNullOrWhiteSpace(message) ? "The tool failed without a safe error." : message),
        command, new McpResultBudget(maxBytes));
}