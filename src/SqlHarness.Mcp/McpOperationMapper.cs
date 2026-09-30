using System.Text;
using System.Text.RegularExpressions;

using SqlHarness.Core;

using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp;

/// <summary>Safe mapping failure: constant text, never parameter values or SQL.</summary>
public sealed class McpMappingException(string message) : Exception(message);

/// <summary>Capabilities document for the capabilities tool: versions, scope engine, tools, limits, and the static-analysis boundary.</summary>
public sealed record McpCapabilitiesDocument(
    string Server,
    string ServerVersion,
    string ProtocolVersion,
    string Engine,
    IReadOnlyList<string> Tools,
    IReadOnlyDictionary<string, long> Limits,
    bool QueryStoreAvailable,
    bool IndexesAvailable,
    IReadOnlyDictionary<string, string>? Diagnostics,
    SqlHarnessSafetyAnalysis SafetyAnalysis);

/// <summary>
/// Shared projection sanitizer for plan and artifact results. Distilled plans
/// lose statement text and literal predicates (both engines distill to the
/// same Core shape); artifact sections from the manifest reader are already
/// limited to summary/metrics/operators and carry no raw plans, SQL, or
/// snapshot cells, so they pass through unchanged.
/// </summary>
public static class McpResultSanitizer
{
    public static object? Sanitize(object? report) =>
        report is DistilledPlan plan ? StripPlan(plan) : report;

    internal static DistilledPlan StripPlan(DistilledPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan with
        {
            Statements = plan.Statements
                .Select(statement => statement with
                {
                    StatementText = null,
                    Root = StripNode(statement.Root),
                })
                .ToArray(),
        };
    }

    private static PlanNode StripNode(PlanNode node) =>
        node with
        {
            Predicate = null,
            Children = node.Children.Select(StripNode).ToArray(),
        };
}

/// <summary>
/// Maps MCP tool arguments onto existing Core operations. The mapper never
/// composes shell commands or SQL text: SQL travels verbatim from the caller
/// into Core operations, parameters travel as Core declaration strings, and
/// every safety check stays inside Core (SqlValidation, dialect classifiers,
/// the parameter binder, and module bounds). There is no second SQL
/// validator here; argument faults below are contract checks with constant
/// messages, and Core re-validates every bound at execution.
/// </summary>
public static partial class McpOperationMapper
{
    private static readonly HashSet<string> InspectKinds = new(StringComparer.OrdinalIgnoreCase)
        { "ping", "schema", "counts", "space", "qstop", "indexes" };
    private static readonly HashSet<string> ValidateUsages = new(StringComparer.OrdinalIgnoreCase)
        { "query", "setup", "benchmark" };
    private static readonly HashSet<string> CompareResultModes = new(StringComparer.OrdinalIgnoreCase)
        { "ordered", "multiset", "set", "off" };
    private static readonly HashSet<string> SnapshotActions = new(StringComparer.OrdinalIgnoreCase)
        { "capture", "diff" };
    private static readonly HashSet<string> ArtifactSections = new(StringComparer.OrdinalIgnoreCase)
        { "summary", "metrics", "operators" };

    private static readonly TimeSpan DefaultWatchInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultWatchMaxDuration = TimeSpan.FromMinutes(15);
    private const int DefaultWatchUntilUnchanged = 3;
    private const int DefaultQueryTimeout = 30;
    private const int DefaultQueryMaxRows = 50;
    private const int DefaultRepeat = 5;

    public static McpCapabilitiesDocument BuildCapabilities(McpScope scope, bool includeDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var engine = scope.ResolvedTarget.Engine switch
        {
            SqlEngine.SqlServer => "sqlserver",
            SqlEngine.Postgres => "postgres",
            _ => throw new McpMappingException("The scoped engine is not supported."),
        };
        var pg = string.Equals(engine, "postgres", StringComparison.Ordinal);
        return new McpCapabilitiesDocument(
            McpHost.ServerName,
            McpHost.ServerVersion,
            McpHost.PinnedProtocolVersion,
            engine,
            McpToolCatalog.ToolNames,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["maxSqlBytes"] = McpLimits.MaxSqlBytes,
                ["maxPlanBytes"] = McpLimits.MaxPlanBytes,
                ["maxParameterSetBytes"] = McpLimits.MaxParameterSetBytes,
                ["maxInlineBytes"] = McpLimits.MaxInlineBytes,
                ["maxTools"] = McpLimits.MaxTools,
                ["toolsListBudgetBytes"] = McpLimits.ToolsListBudgetBytes,
                ["maxTimeoutSeconds"] = 300,
                ["maxRepeat"] = 100,
                ["maxTop"] = 500,
                ["maxRows"] = 500,
                ["callToolResultBudgetBytes"] = scope.MaxResultBytes,
                ["maxOperationSeconds"] = scope.MaxOperationSeconds,
            },
            QueryStoreAvailable: !pg,
            IndexesAvailable: !pg,
            includeDiagnostics ? LocalDiagnostics(scope) : null,
            new SqlHarnessSafetyAnalysis(
                SqlSafetyAnalysis.AnalysisKind,
                SqlSafetyAnalysis.ContractVersion,
                SqlSafetyAnalysis.HiddenEffectsVerified,
                SqlSafetyAnalysis.ObjectAndPermissionStatus));
    }

    private static IReadOnlyDictionary<string, string> LocalDiagnostics(McpScope scope)
    {
        // Local counts and existence flags only: no profile list, no paths, no secrets.
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["inputRootCount"] = scope.InputRoots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["tools"] = McpToolCatalog.ToolNames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var (key, directory) in new (string, string)[]
        {
            ("compareDirExists", SqlHarnessPaths.CompareDir),
            ("snapshotsDirExists", SqlHarnessPaths.SnapshotsDir),
        })
        {
            try
            {
                diagnostics[key] = Directory.Exists(directory) ? "true" : "false";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                diagnostics[key] = "unknown";
            }
        }

        return diagnostics;
    }

    public static SqlHarnessOperation MapInspect(
        McpScope scope,
        string? kind,
        string? @object,
        string? filter,
        IReadOnlyList<string>? tables,
        string? like,
        int? top,
        bool exact,
        string? window,
        int? timeout)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (kind is null || !InspectKinds.Contains(kind))
            throw new McpMappingException("Unknown inspect kind. Supported kinds: ping, schema, counts, space, qstop, indexes.");
        var normalized = kind.ToLowerInvariant();
        var tableList = tables ?? [];
        if (normalized == "qstop" || normalized == "indexes")
            ThrowIfPostgres(scope, normalized);
        return normalized switch
        {
            "ping" => MapPingInspect(scope, @object, filter, tableList, like, top, exact, window, timeout),
            "schema" => MapSchemaInspect(scope, @object, filter, tableList, like, top, exact, window, timeout),
            "counts" => MapCountsInspect(scope, @object, filter, tableList, like, top, exact, window, timeout),
            "space" => MapSpaceInspect(scope, @object, filter, tableList, like, top, exact, window, timeout),
            "qstop" => MapQueryStoreInspect(scope, @object, filter, tableList, like, top, exact, window, timeout),
            _ => MapIndexesInspect(scope, @object, filter, tableList, like, top, exact, window, timeout),
        };
    }

    private static void RejectMisplaced(
        string kind, string? @object, string? filter, IReadOnlyList<string> tables,
        string? like, int? top, bool exact, string? window,
        bool allowObject, bool allowFilter, bool allowTables, bool allowLike,
        bool allowTop, bool allowExact, bool allowWindow)
    {
        if (!allowObject && !string.IsNullOrWhiteSpace(@object))
            throw new McpMappingException($"The object argument does not apply to inspect kind '{kind}'.");
        if (!allowFilter && !string.IsNullOrWhiteSpace(filter))
            throw new McpMappingException($"The filter argument does not apply to inspect kind '{kind}'.");
        if (!allowTables && tables.Count > 0)
            throw new McpMappingException($"The tables argument does not apply to inspect kind '{kind}'.");
        if (!allowLike && !string.IsNullOrWhiteSpace(like))
            throw new McpMappingException($"The like argument does not apply to inspect kind '{kind}'.");
        if (!allowTop && top is not null)
            throw new McpMappingException($"The top argument does not apply to inspect kind '{kind}'.");
        if (!allowExact && exact)
            throw new McpMappingException($"The exact argument does not apply to inspect kind '{kind}'.");
        if (!allowWindow && !string.IsNullOrWhiteSpace(window))
            throw new McpMappingException($"The window argument does not apply to inspect kind '{kind}'.");
    }

    private static SqlHarnessOperation MapPingInspect(
        McpScope scope, string? @object, string? filter, IReadOnlyList<string> tables,
        string? like, int? top, bool exact, string? window, int? timeout)
    {
        RejectMisplaced("ping", @object, filter, tables, like, top, exact, window,
            allowObject: false, allowFilter: false, allowTables: false, allowLike: false,
            allowTop: false, allowExact: false, allowWindow: false);
        return new SqlHarnessPingOperation(scope.TargetRequest, timeout ?? 5);
    }

    private static SqlHarnessOperation MapSchemaInspect(
        McpScope scope, string? @object, string? filter, IReadOnlyList<string> tables,
        string? like, int? top, bool exact, string? window, int? timeout)
    {
        RejectMisplaced("schema", @object, filter, tables, like, top, exact, window,
            allowObject: true, allowFilter: true, allowTables: false, allowLike: false,
            allowTop: true, allowExact: false, allowWindow: false);
        if (!string.IsNullOrWhiteSpace(@object) && !string.IsNullOrWhiteSpace(filter))
            throw new McpMappingException("The object and filter arguments cannot be combined.");
        return new SqlHarnessSchemaOperation(
            scope.TargetRequest, filter, RequireTimeout(timeout), top is null ? 50 : RequireTop(top.Value), @object);
    }

    private static SqlHarnessOperation MapCountsInspect(
        McpScope scope, string? @object, string? filter, IReadOnlyList<string> tables,
        string? like, int? top, bool exact, string? window, int? timeout)
    {
        RejectMisplaced("counts", @object, filter, tables, like, top, exact, window,
            allowObject: false, allowFilter: false, allowTables: true, allowLike: true,
            allowTop: true, allowExact: true, allowWindow: false);
        if (tables.Count > 0 && !string.IsNullOrWhiteSpace(like))
            throw new McpMappingException("The tables and like arguments cannot be combined.");
        return new SqlHarnessCountsOperation(
            scope.TargetRequest, tables, like, top is null ? 50 : RequireTop(top.Value), exact, RequireTimeout(timeout));
    }

    private static SqlHarnessOperation MapSpaceInspect(
        McpScope scope, string? @object, string? filter, IReadOnlyList<string> tables,
        string? like, int? top, bool exact, string? window, int? timeout)
    {
        RejectMisplaced("space", @object, filter, tables, like, top, exact, window,
            allowObject: true, allowFilter: false, allowTables: false, allowLike: false,
            allowTop: true, allowExact: false, allowWindow: false);
        RequireSpaceObject(@object);
        return new SqlHarnessSpaceOperation(
            scope.TargetRequest, top is null ? 25 : RequireTop(top.Value), @object, RequireTimeout(timeout));
    }

    private static SqlHarnessOperation MapQueryStoreInspect(
        McpScope scope, string? @object, string? filter, IReadOnlyList<string> tables,
        string? like, int? top, bool exact, string? window, int? timeout)
    {
        RejectMisplaced("qstop", @object, filter, tables, like, top, exact, window,
            allowObject: false, allowFilter: false, allowTables: false, allowLike: false,
            allowTop: true, allowExact: false, allowWindow: true);
        return new SqlHarnessQueryStoreTopOperation(
            scope.TargetRequest, top is null ? 20 : RequireTop(top.Value),
            ParseQueryStoreWindow(string.IsNullOrWhiteSpace(window) ? "24h" : window),
            RequireTimeout(timeout));
    }

    private static SqlHarnessOperation MapIndexesInspect(
        McpScope scope, string? @object, string? filter, IReadOnlyList<string> tables,
        string? like, int? top, bool exact, string? window, int? timeout)
    {
        RejectMisplaced("indexes", @object, filter, tables, like, top, exact, window,
            allowObject: true, allowFilter: false, allowTables: false, allowLike: false,
            allowTop: true, allowExact: false, allowWindow: false);
        if (!IndexObjectSyntax.TryParse(@object, out _, out _, out var objectError))
            throw new McpMappingException(objectError);
        return new SqlHarnessIndexesOperation(
            scope.TargetRequest, top is null ? 20 : RequireTop(top.Value), @object, RequireTimeout(timeout));
    }

    private static void ThrowIfPostgres(McpScope scope, string kind)
    {
        if (scope.ResolvedTarget.Engine == SqlEngine.Postgres)
            throw new McpMappingException($"The '{kind}' inspect kind is available only on SQL Server.");
    }

    public static async Task<SqlValidationReport> MapValidateAsync(
        McpScope scope,
        string? sql,
        string? file,
        string? usage,
        IReadOnlyList<McpParameterArgument>? parameters,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (usage is null || !ValidateUsages.Contains(usage))
            throw new McpMappingException("Unknown validate usage. Supported usages: query, setup, benchmark.");
        // One Core offline classifier serves every usage: the already-validated
        // usage string selects the caller intent (query/setup/benchmark) in the
        // shared model, so no second validator exists here. The validate tool
        // has no setup-SQL input, so query and benchmark pass no setup context:
        // a setup-dependent batch validates under engine query rules here,
        // the same as execution without setup.
        var sqlText = await McpInputReader.ReadSqlAsync(sql, file, scope, ct);
        var declarations = FormatParameters(parameters);
        var options = new ValidationOptions(usage!.ToLowerInvariant() switch
        {
            "setup" => ValidationUsage.Setup,
            "benchmark" => ValidationUsage.Benchmark,
            _ => ValidationUsage.Query,
        });
        return SqlValidation.Validate(scope.TargetRequest, sqlText, declarations, scope.Profiles, options);
    }

    public static Task<SqlHarnessQueryOperation> MapQueryAsync(
        McpScope scope,
        string? sql,
        string? file,
        IReadOnlyList<McpParameterArgument>? parameters,
        int timeout,
        int maxRows,
        CancellationToken ct) =>
        MapQueryAsync(scope, new McpSqlSourceArgument { Sql = sql, File = file }, parameters, timeout, maxRows, ct);

    internal static async Task<SqlHarnessQueryOperation> MapQueryAsync(
        McpScope scope,
        McpSqlSourceArgument source,
        IReadOnlyList<McpParameterArgument>? parameters,
        int timeout,
        int maxRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(source);
        var sqlText = await McpInputReader.ReadSqlAsync(source.Sql, source.File, scope, ct);
        // Persistent mutation is never offered over MCP: Core always sees false
        // with no confirmation database, and its classifier enforces read-only
        // plus session-local temp rules.
        return new SqlHarnessQueryOperation(
            scope.TargetRequest, sqlText, FormatParameters(parameters),
            RequireTimeout(timeout), RequireMaxRows(maxRows), AllowMutation: false, ConfirmDatabase: null);
    }

    public static async Task<SqlHarnessMeasureOperation> MapMeasureAsync(
        McpScope scope,
        McpSqlSourceArgument query,
        McpSqlSourceArgument? setup,
        IReadOnlyList<McpParameterArgument>? parameters,
        IReadOnlyList<string>? paramSetFiles,
        int repeat,
        int? timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        var querySql = await McpInputReader.ReadSqlAsync(query.Sql, query.File, scope, ct);
        var setupSql = await ReadOptionalSourceAsync(setup, scope, "setup", ct);
        var sets = await ReadParameterSetsOrNullAsync(paramSetFiles, scope, ct);
        return new SqlHarnessMeasureOperation(
            scope.TargetRequest, setupSql, querySql, FormatParameters(parameters),
            RequireTimeout(timeout), RequireRepeat(repeat), sets);
    }

    public static async Task<SqlHarnessOperation> MapCompareAsync(
        McpScope scope,
        McpSqlSourceArgument baseline,
        McpSqlSourceArgument candidate,
        McpSqlSourceArgument? setup,
        IReadOnlyList<McpParameterArgument>? parameters,
        int repeat,
        int? timeout,
        string? compareResults,
        McpMatrixArgument? matrix,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (compareResults is null || !CompareResultModes.Contains(compareResults))
            throw new McpMappingException("Unknown compare mode. Supported modes: ordered, multiset, set, off.");
        var mode = compareResults.ToLowerInvariant() switch
        {
            "ordered" => ResultComparisonMode.Ordered,
            "multiset" => ResultComparisonMode.Multiset,
            "set" => ResultComparisonMode.Set,
            _ => ResultComparisonMode.Off,
        };
        var baselineSql = await McpInputReader.ReadSqlAsync(baseline.Sql, baseline.File, scope, ct);
        var candidateSql = await McpInputReader.ReadSqlAsync(candidate.Sql, candidate.File, scope, ct);
        var setupSql = await ReadOptionalSourceAsync(setup, scope, "setup", ct);
        var declarations = FormatParameters(parameters);
        var validatedTimeout = RequireTimeout(timeout);
        var validatedRepeat = RequireRepeat(repeat);
        if (matrix is null)
            return new SqlHarnessCompareOperation(
                scope.TargetRequest, setupSql, baselineSql, candidateSql,
                declarations, validatedTimeout, validatedRepeat, mode);
        return new SqlHarnessCompareMatrixOperation(
            scope.TargetRequest, setupSql, baselineSql, candidateSql,
            declarations, validatedTimeout, validatedRepeat, FormatMatrix(matrix), mode);
    }

    public static async Task<SqlHarnessWatchOperation> MapWatchAsync(
        McpScope scope,
        string? sql,
        string? file,
        IReadOnlyList<McpParameterArgument>? parameters,
        int timeout,
        int maxRows,
        string? until,
        int? untilUnchanged,
        string? interval,
        string? maxDuration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var hasUntil = !string.IsNullOrWhiteSpace(until);
        if (hasUntil && untilUnchanged is not null)
            throw new McpMappingException("Specify exactly one of until or untilUnchanged.");
        if (untilUnchanged is < 1)
            throw new McpMappingException("The untilUnchanged argument must be a positive integer.");
        var sqlText = await McpInputReader.ReadSqlAsync(sql, file, scope, ct);
        return new SqlHarnessWatchOperation(
            scope.TargetRequest, sqlText, FormatParameters(parameters),
            RequireTimeout(timeout), RequireMaxRows(maxRows),
            ParseWatchDuration(interval, DefaultWatchInterval, "interval"),
            ParseWatchDuration(maxDuration, DefaultWatchMaxDuration, "maxDuration"),
            hasUntil ? until!.Trim() : null,
            hasUntil ? null : untilUnchanged ?? DefaultWatchUntilUnchanged);
    }

    public static async Task<SqlHarnessSnapshotOperation> MapSnapshotAsync(
        McpScope scope,
        string? action,
        string? name,
        string? sql,
        string? file,
        IReadOnlyList<McpParameterArgument>? parameters,
        int timeout,
        int maxRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (action is null || !SnapshotActions.Contains(action))
            throw new McpMappingException("Unknown snapshot action. Supported actions: capture, diff.");
        if (string.IsNullOrWhiteSpace(name) || !SnapshotNamePattern().IsMatch(name))
            throw new McpMappingException("The snapshot name must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$.");
        var sqlText = await McpInputReader.ReadSqlAsync(sql, file, scope, ct);
        // Capture never overwrites: force stays false and the Core store
        // rejects an existing name. There is no force argument at all.
        // The frozen scope owner travels on the operation so Core stamps
        // scoped captures and refuses foreign baselines before data.
        return new SqlHarnessSnapshotOperation(
            scope.TargetRequest, sqlText, FormatParameters(parameters),
            RequireTimeout(timeout), RequireMaxRows(maxRows), name,
            Diff: string.Equals(action, "diff", StringComparison.OrdinalIgnoreCase), Force: false,
            Owner: scope.Owner);
    }

    public static async Task<SqlHarnessPlanOperation> MapPlanAsync(
        McpScope scope,
        string? content,
        string? file,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var text = await McpInputReader.ReadPlanAsync(content, file, scope, ct);
        var bytes = Encoding.UTF8.GetByteCount(text);
        var lines = text.Length == 0 ? 0 : text.Count(character => character == '\n') + (text[^1] == '\n' ? 0 : 1);
        return new SqlHarnessPlanOperation(text, new OutputFootprint(bytes, lines));
    }

    public static object ReadArtifactSection(McpScope scope, string? id, string? section)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (section is null || !ArtifactSections.Contains(section))
            throw new McpMappingException("Unknown artifact section. Supported sections: summary, metrics, operators.");
        // The shared manifest reader pins ids to single directory names under
        // the artifact root and serves only the three safe sections: no raw
        // plans, queries.jsonl, SQL, or snapshot cells can leave through it.
        return ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, id ?? string.Empty, section, scope.Owner);
    }

    /// <summary>
    /// Formats MCP parameters as Core declaration strings without any further
    /// interpretation: null becomes name[:type]:null, anything else becomes
    /// name[:type]=value (so '=' inside a value survives: only the first '='
    /// separates the declaration). Duplicates, unknown types, and bad values
    /// stay Core's job at execution; nothing is echoed here.
    /// </summary>
    public static IReadOnlyList<string> FormatParameters(IReadOnlyList<McpParameterArgument>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return [];
        var declarations = new List<string>(parameters.Count);
        foreach (var parameter in parameters)
        {
            if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name))
                throw new McpMappingException("Each parameter requires a non-empty name.");
            declarations.Add(parameter.Value is null
                ? parameter.Type is null ? $"{parameter.Name}:null" : $"{parameter.Name}:{parameter.Type}:null"
                : parameter.Type is null ? $"{parameter.Name}={parameter.Value}" : $"{parameter.Name}:{parameter.Type}={parameter.Value}");
        }

        return declarations;
    }

    internal static string FormatMatrix(McpMatrixArgument matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (string.IsNullOrWhiteSpace(matrix.Name) || string.IsNullOrWhiteSpace(matrix.Type))
            throw new McpMappingException("The matrix requires a non-empty name and type.");
        if (matrix.Values is null || matrix.Values.Count < 2)
            throw new McpMappingException("The matrix requires at least two values.");
        foreach (var value in matrix.Values)
        {
            if (string.IsNullOrEmpty(value))
                throw new McpMappingException("The matrix contains an empty value.");
            if (value.Contains(',', StringComparison.Ordinal))
                throw new McpMappingException("The matrix value is not representable: it contains a comma.");
        }

        return $"{matrix.Name}:{matrix.Type}={string.Join(",", matrix.Values)}";
    }

    private static async Task<string?> ReadOptionalSourceAsync(
        McpSqlSourceArgument? source, McpScope scope, string label, CancellationToken ct)
    {
        if (source is null)
            return null;
        if (string.IsNullOrWhiteSpace(source.Sql) && string.IsNullOrWhiteSpace(source.File))
            return null;
        var hasInline = !string.IsNullOrWhiteSpace(source.Sql);
        var hasFile = !string.IsNullOrWhiteSpace(source.File);
        if (hasInline && hasFile)
            throw new McpMappingException($"Provide exactly one {label} source: inline sql or file.");
        if (hasInline)
        {
            McpInputReader.ThrowIfInlineTooLarge(source.Sql!);
            return source.Sql!;
        }

        return await McpInputReader.ReadTextFileAsync(source.File!, scope.InputRoots, McpLimits.MaxSqlBytes, ct, kind: "SQL input");
    }

    private static async Task<IReadOnlyList<SqlHarnessParameterSetInput>?> ReadParameterSetsOrNullAsync(
        IReadOnlyList<string>? paths, McpScope scope, CancellationToken ct)
    {
        if (paths is null || paths.Count == 0)
            return null;
        if (paths.Count == 1)
            throw new McpMappingException("Exactly one parameter-set file is invalid. Omit paramSetFiles for a single set.");
        return await McpInputReader.ReadParameterSetsAsync(paths, scope, ct);
    }

    private static int RequireTimeout(int? timeout) =>
        timeout switch
        {
            null => DefaultQueryTimeout,
            < 1 or > 300 => throw new McpMappingException("SQL timeout must be between 1 and 300 seconds."),
            var value => value.Value,
        };

    private static int RequireRepeat(int repeat) =>
        repeat is < 1 or > 100
            ? throw new McpMappingException("Measurement repetitions must be between 1 and 100.")
            : repeat;

    private static int RequireMaxRows(int maxRows) =>
        maxRows is < 0 or > 500
            ? throw new McpMappingException("Maximum displayed rows must be between 0 and 500.")
            : maxRows;

    private static int RequireTop(int top) =>
        top is < 1 or > 500
            ? throw new McpMappingException("The top limit must be between 1 and 500.")
            : top;

    private static void RequireSpaceObject(string? @object)
    {
        if (@object is null)
            return;
        if (string.IsNullOrWhiteSpace(@object))
            throw new McpMappingException("The inspect object must be a single object name or schema.name.");
        var firstDot = @object.IndexOf('.');
        if (firstDot >= 0)
        {
            var lastDot = @object.LastIndexOf('.');
            if (firstDot != lastDot || firstDot == 0 || firstDot == @object.Length - 1)
                throw new McpMappingException("The inspect object must be a single object name or schema.name.");
        }
    }

    internal static int ParseQueryStoreWindow(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new McpMappingException("The inspect window must be a positive integer with an m, h, or d suffix, totaling 1..44640 minutes.");
        var trimmed = text.Trim();
        var unit = trimmed[^1];
        if (unit is not ('m' or 'h' or 'd') && unit is not ('M' or 'H' or 'D'))
            throw new McpMappingException("The inspect window must be a positive integer with an m, h, or d suffix, totaling 1..44640 minutes.");
        if (!long.TryParse(trimmed[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var magnitude) || magnitude <= 0)
            throw new McpMappingException("The inspect window must be a positive integer with an m, h, or d suffix, totaling 1..44640 minutes.");
        long minutes;
        try
        {
            minutes = checked(magnitude * (char.ToLowerInvariant(unit) switch { 'm' => 1L, 'h' => 60L, _ => 1440L }));
        }
        catch (OverflowException)
        {
            throw new McpMappingException("The inspect window must be a positive integer with an m, h, or d suffix, totaling 1..44640 minutes.");
        }

        if (minutes is < 1 or > 44640)
            throw new McpMappingException("The inspect window must be a positive integer with an m, h, or d suffix, totaling 1..44640 minutes.");
        return (int)minutes;
    }

    internal static TimeSpan ParseWatchDuration(string? text, TimeSpan @default, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
            return @default;
        var trimmed = text.Trim();
        char? unit = null;
        var numberPart = trimmed;
        if (trimmed.Length > 0 && char.IsAsciiLetter(trimmed[^1]))
        {
            unit = char.ToLowerInvariant(trimmed[^1]);
            numberPart = trimmed[..^1];
        }

        if (unit is not (null or 's' or 'm' or 'h') || numberPart.Length == 0 ||
            !long.TryParse(numberPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new McpMappingException($"The watch {label} must be a positive integral value with optional s, m, or h suffix.");
        TimeSpan duration;
        try
        {
            duration = unit switch
            {
                null or 's' => TimeSpan.FromSeconds(value),
                'm' => TimeSpan.FromMinutes(value),
                _ => TimeSpan.FromHours(value),
            };
        }
        catch (OverflowException)
        {
            throw new McpMappingException($"The watch {label} must not exceed 24 hours.");
        }

        if (duration > TimeSpan.FromHours(24))
            throw new McpMappingException($"The watch {label} must not exceed 24 hours.");
        return duration;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotNamePattern();
}
