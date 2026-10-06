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
        [Description("Include local counts and existence flags. Never secrets, paths, or profile lists.")] bool includeDiagnostics = false,
        CancellationToken ct = default) => TargetFreeAsync("sqlharness_capabilities", ct, token =>
    {
        ThrowUnknown(ctx, ["includeDiagnostics"]);
        return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success,
            McpOperationMapper.BuildCapabilities(process, includeDiagnostics), null));
    });

    public Task<CallToolResult> InspectAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Catalog inspection to run.")][AllowedValues("ping", "schema", "counts", "space", "qstop", "indexes")] string kind,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Single object name or schema.name, where applicable.")] string? @object = null,
        [Description("Schema filter pattern; schema only, exclusive with object.")] string? filter = null,
        [Description("Table names; counts only, exclusive with like.")] string[]? tables = null,
        [Description("Table name pattern; counts only, exclusive with tables.")] string? like = null,
        [Description("Row cap 1..500; not for ping.")] int? top = null,
        [Description("Exact COUNT_BIG(*) instead of estimates; counts only.")] bool exact = false,
        [Description("Lookback like 24h; qstop only.")] string? window = null,
        [Description("SQL timeout 1..300 seconds.")] int? timeout = null,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_inspect", scope, handler => handler.InspectAsync(ctx, kind, @object, filter, tables, like, top, exact, window, timeout, ct));

    public Task<CallToolResult> ValidateAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Caller intent: query, setup, or benchmark.")][AllowedValues("query", "setup", "benchmark")] string usage,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Inline SQL, at most 1 MiB.")] string? sql = null,
        [Description("SQL file under an operator input root.")] string? file = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_validate", scope, handler => handler.ValidateAsync(ctx, usage, sql, file, parameters, ct));

    public Task<CallToolResult> QueryAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Inline SQL, at most 1 MiB.")] string? sql = null,
        [Description("SQL file under an operator input root.")] string? file = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        [Description("SQL timeout 1..300 seconds.")] int timeout = 30,
        [Description("Presentation row cap 0..500.")] int maxRows = 50,
        [Description("Response cap 4096..1048576 bytes; only lowers the process maximum.")] int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds; only lowers the process maximum.")] int? maxOperationSeconds = null,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_query", scope, handler => handler.QueryAsync(ctx, sql, file, parameters, timeout, maxRows, maxResultBytes, maxOperationSeconds, ct));

    public Task<CallToolResult> MeasureAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Query source: exactly one of inline sql or file.")] McpSqlSourceArgument query,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Optional setup source.")] McpSqlSourceArgument? setup = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        [Description("Parameter-set files under an input root.")] string[]? paramSetFiles = null,
        [Description("Repetitions 1..100.")] int repeat = 5,
        [Description("SQL timeout 1..300 seconds.")] int? timeout = null,
        [Description("Response cap 4096..1048576 bytes.")] int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds.")] int? maxOperationSeconds = null,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_measure", scope, handler => handler.MeasureAsync(ctx, query, setup, parameters, paramSetFiles, repeat, timeout, maxResultBytes, maxOperationSeconds, ct));

    public Task<CallToolResult> CompareAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Baseline source.")] McpSqlSourceArgument baseline,
        [Description("Candidate source.")] McpSqlSourceArgument candidate,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Optional setup source.")] McpSqlSourceArgument? setup = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        [Description("Repetitions 1..100.")] int repeat = 5,
        [Description("SQL timeout 1..300 seconds.")] int? timeout = null,
        [Description("Result comparison mode.")][AllowedValues("ordered", "multiset", "set", "off")] string compareResults = "ordered",
        [Description("Optional single matrix dimension.")] McpMatrixArgument? matrix = null,
        [Description("Response cap 4096..1048576 bytes.")] int? maxResultBytes = null,
        [Description("Time budget 1..86400 seconds.")] int? maxOperationSeconds = null,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_compare", scope, handler => handler.CompareAsync(ctx, baseline, candidate, setup, parameters, repeat, timeout, compareResults, matrix, maxResultBytes, maxOperationSeconds, ct));

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
        [Description("Response cap.")] int? maxResultBytes = null,
        [Description("Time budget.")] int? maxOperationSeconds = null,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_watch", scope, handler => handler.WatchAsync(ctx, sql, file, parameters, timeout, maxRows, until, untilUnchanged, interval, maxDuration, maxResultBytes, maxOperationSeconds, ct));

    public Task<CallToolResult> SnapshotAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("capture stores; diff compares without printing cell values.")][AllowedValues("capture", "diff")] string action,
        [Description("Snapshot label.")] string name,
        [Description("Request target scope.")] McpRequestScopeArgument scope,
        [Description("Inline SQL.")] string? sql = null,
        [Description("SQL file under an operator input root.")] string? file = null,
        [Description("Parameters as {name, type, value}.")] McpParameterArgument[]? parameters = null,
        [Description("SQL timeout.")] int timeout = 30,
        [Description("Presentation row cap.")] int maxRows = 50,
        [Description("Response cap.")] int? maxResultBytes = null,
        [Description("Time budget.")] int? maxOperationSeconds = null,
        CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_snapshot", scope, handler => handler.SnapshotAsync(ctx, action, name, sql, file, parameters, timeout, maxRows, maxResultBytes, maxOperationSeconds, ct));

    public Task<CallToolResult> PlanAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Inline plan document.")] string? content = null,
        [Description("Plan file under an operator input root.")] string? file = null,
        CancellationToken ct = default) => TargetFreeAsync("sqlharness_plan", ct, async token =>
        {
            ThrowUnknown(ctx, ["content", "file"]);
            var operation = await McpOperationMapper.MapPlanAsync(process.InputRoots, content, file, token);
            var outcome = await process.CreateModule().ExecuteAsync(operation, token);
            return outcome.ExitCode == SqlHarnessExitCode.Success
                ? outcome with { Report = McpResultSanitizer.Sanitize(outcome.Report) }
                : outcome;
        });

    public Task<CallToolResult> ArtifactAsync(RequestContext<CallToolRequestParams> ctx,
        [Description("Artifact directory name from a saved report.")] string id,
        [Description("Safe section to read.")][AllowedValues("summary", "metrics", "operators")] string section,
        [Description("Request target scope.")] McpRequestScopeArgument scope, CancellationToken ct = default) =>
        Invoke(ctx, "sqlharness_artifact", scope, handler => handler.ArtifactAsync(ctx, id, section, ct));

    public Task<CallToolResult> GainAsync(RequestContext<CallToolRequestParams> ctx, CancellationToken ct = default) =>
        TargetFreeAsync("sqlharness_gain", ct, token =>
        {
            ThrowUnknown(ctx, []);
            return process.CreateModule().ExecuteAsync(new SqlHarnessGainOperation(), token);
        });

    private Task<CallToolResult> Invoke(RequestContext<CallToolRequestParams> ctx, string command, McpRequestScopeArgument suppliedScope,
        Func<McpToolHandlers, Task<CallToolResult>> invoke)
    {
        McpScope scope;
        try
        {
            scope = process.ResolveScope(ParseScope(ctx, suppliedScope));
        }
        catch (Exception exception) when (exception is McpStartupException or McpMappingException)
        {
            return Task.FromResult(Failure("The MCP request scope is invalid.", command, process.MaxResultBytes));
        }
        var handlers = new McpToolHandlers(scope, _moduleFactory(scope), process.Gate, _clock, hostShutdown, acceptRequestScope: true);
        return invoke(handlers);
    }

    private Task<CallToolResult> TargetFreeAsync(string command, CancellationToken ct,
        Func<CancellationToken, Task<SqlHarnessOutcome>> run) => RunAsync(command, ct, run);

    private async Task<CallToolResult> RunAsync(string command, CancellationToken ct,
        Func<CancellationToken, Task<SqlHarnessOutcome>> run)
    {
        var budget = new McpResultBudget(process.MaxResultBytes);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, hostShutdown);
            linked.CancelAfter(TimeSpan.FromSeconds(process.MaxOperationSeconds));
            return McpResultAdapter.Adapt(await run(linked.Token), command, budget);
        }
        catch (OperationCanceledException) { return McpExecutionGate.CancelledResult(command, budget); }
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