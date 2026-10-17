using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using SqlHarness.Core;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;

using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp;

/// <summary>Safe mapping failure: constant text, never parameter values or SQL.</summary>
public sealed class McpMappingException(string message) : Exception(message);

/// <summary>Enforced process ceilings reported by capabilities.</summary>
public sealed record McpProcessLimits(int MaxResultBytes, int MaxOperationSeconds);

/// <summary>
/// Runtime configuration for the capabilities tool. <see cref="ConfigurationRef"/>
/// is random per process and identifies this frozen configuration; it is not
/// authorization. <see cref="AllowedProfiles"/> exists only in request scope
/// (fixed scope omits it). <see cref="InputRoots"/> holds the normalized roots
/// of this page in configured order; <see cref="InputRootCount"/> is the total;
/// <see cref="InputRootsComplete"/> is true only when this single response
/// carries the whole inventory; <see cref="InputRootsNextCursor"/> is the
/// next page offset (null at the end); <see cref="InputRootsOmitted"/> counts
/// roots on this page's range that cannot fit any page within the budget.
/// </summary>
public sealed record McpCapabilitiesDocument(
    string ConfigurationRef,
    string ScopeMode,
    IReadOnlyList<string> Engines,
    McpProcessLimits ProcessLimits,
    SqlHarnessCompareContextCapability CompareContext,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? AllowedProfiles = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Diagnostics = null,
    IReadOnlyList<string>? InputRoots = null,
    int InputRootCount = 0,
    bool InputRootsComplete = true,
    int? InputRootsNextCursor = null,
    int InputRootsOmitted = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SqlHarnessMutationCapability? Mutations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SqlHarnessProfileCapability? ProfileMutations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AllowedProfileMutationCapability>? AllowedProfileMutations = null);

public sealed record AllowedProfileMutationCapability(string Profile, string Mode, bool WriterConfigured);

/// <summary>
/// Plan text policy for the offline plan tool. The caller supplies the plan
/// document, so with <c>includeText</c> true bounded statement text and
/// predicates are returned unchanged; with false both are omitted. Names,
/// metrics and objects are never changed.
/// Length bounds stay with <see cref="AgentOutputProjection"/>. Non-plan
/// reports, including manifest-read artifact sections, pass through unchanged.
/// </summary>
public static partial class McpPlanTextPolicy
{
    public static object? Apply(object? report, bool includeText) =>
        report is DistilledPlan plan ? ApplyPlan(plan, includeText) : report;

    internal static DistilledPlan ApplyPlan(DistilledPlan plan, bool includeText)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan with
        {
            Statements = plan.Statements
                .Select(statement => statement with
                {
                    StatementText = Text(statement.StatementText, includeText),
                    Root = ApplyNode(statement.Root, includeText),
                })
                .ToArray(),
        };
    }

    private static PlanNode ApplyNode(PlanNode node, bool includeText) =>
        node with
        {
            Predicate = Text(node.Predicate, includeText),
            Children = node.Children.Select(child => ApplyNode(child, includeText)).ToArray(),
        };

    private static string? Text(string? value, bool includeText)
    {
        if (!includeText || value is null)
            return null;

        return value;
    }
}

/// <summary>
/// Maps MCP tool arguments onto existing Core operations. The mapper never
/// composes shell commands or SQL text: SQL travels verbatim from the caller
/// into Core operations, parameters and matrix values travel as the typed
/// Core parameter model (never joined into or split from text), and
/// every safety check stays inside Core (the dialect classifiers,
/// the parameter binder, and module bounds). There is no second SQL
/// validator here; argument faults below are contract checks with constant
/// messages, and Core re-validates every bound at execution.
/// </summary>
public static partial class McpOperationMapper
{
    private static readonly HashSet<string> CompareResultModes = new(AgentProjectionNames.ComparisonModes, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SnapshotActions = new(StringComparer.OrdinalIgnoreCase)
        { "capture", "diff" };
    private static readonly HashSet<string> ArtifactSections = new(StringComparer.OrdinalIgnoreCase)
        { "summary", "metrics", "operators", "statements", "matrix-cells", "plan" };

    private static readonly TimeSpan DefaultWatchInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultWatchMaxDuration = TimeSpan.FromMinutes(15);
    private const int DefaultWatchUntilUnchanged = 3;
    private const int DefaultQueryTimeout = 30;
    private const int DefaultQueryMaxRows = 50;
    private const int DefaultRepeat = 5;

    public static McpCapabilitiesDocument BuildCapabilities(McpScope scope, bool includeDiagnostics, int inputRootCursor = 0, CompareConfig? compareConfig = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ThrowIfInvalidInputRootCursor(scope.InputRoots, inputRootCursor);
        var engine = scope.ResolvedTarget.Engine switch
        {
            SqlEngine.SqlServer => "sqlserver",
            SqlEngine.Postgres => "postgres",
            _ => throw new McpMappingException("The scoped engine is not supported."),
        };
        var document = new McpCapabilitiesDocument(
            scope.ConfigurationRef,
            "fixed",
            [engine],
            new McpProcessLimits(scope.MaxResultBytes, scope.MaxOperationSeconds),
            SqlHarnessCapabilitiesProvider.CompareContextPolicy(compareConfig),
            Diagnostics: includeDiagnostics ? LocalDiagnostics(scope.InputRoots) : null,
            Mutations: SqlHarnessCapabilitiesProvider.MutationCapability(null, false),
            ProfileMutations: ProfileMutation(scope.TargetRequest.Profile, scope.Profiles, null));
        return WithInputRootPage(document, scope.InputRoots, inputRootCursor, scope.MaxResultBytes);
    }

    public static McpCapabilitiesDocument BuildCapabilities(McpProcessContext context, bool includeDiagnostics, int inputRootCursor = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.RequestScope)
        {
            var fixedDocument = BuildCapabilities(context.FixedScope!, includeDiagnostics, inputRootCursor, context.CompareConfig);
            return fixedDocument with
            {
                Mutations = SqlHarnessCapabilitiesProvider.MutationCapability(context.Config.Mutations, context.IsDashboardReachable()),
                ProfileMutations = ProfileMutation(context.FixedScope!.TargetRequest.Profile, context.Profiles, context.Config.Mutations),
            };
        }
        ThrowIfInvalidInputRootCursor(context.InputRoots, inputRootCursor);

        var document = new McpCapabilitiesDocument(
            context.ConfigurationRef,
            "request",
            ["sqlserver"],
            new McpProcessLimits(context.MaxResultBytes, context.MaxOperationSeconds),
            SqlHarnessCapabilitiesProvider.CompareContextPolicy(context.CompareConfig),
            context.AllowedProfiles,
            includeDiagnostics ? LocalDiagnostics(context.InputRoots) : null,
            Mutations: SqlHarnessCapabilitiesProvider.MutationCapability(context.Config.Mutations, context.IsDashboardReachable()),
            AllowedProfileMutations: context.AllowedProfiles.Select(name =>
            {
                var policy = SqlHarnessCapabilitiesProvider.ProfileMutationCapability(context.Profiles[name], context.Config.Mutations);
                return new AllowedProfileMutationCapability(name, policy.Mode, policy.WriterConfigured);
            }).ToArray());
        return WithInputRootPage(document, context.InputRoots, inputRootCursor, context.MaxResultBytes);
    }

    private static SqlHarnessProfileCapability? ProfileMutation(string? profileName,
        IReadOnlyDictionary<string, TargetProfile> profiles, MutationsConfig? config)
    {
        if (string.IsNullOrWhiteSpace(profileName) || !profiles.TryGetValue(profileName, out var profile))
            return null;
        var policy = SqlHarnessCapabilitiesProvider.ProfileMutationCapability(profile, config);
        var engine = string.Equals(profile.Engine, "postgres", StringComparison.OrdinalIgnoreCase) ? "postgres" : "sqlserver";
        return new SqlHarnessProfileCapability(profileName, engine, policy.Mode, policy.WriterConfigured);
    }

    private static void ThrowIfInvalidInputRootCursor(IReadOnlyList<string> inputRoots, int cursor)
    {
        if (cursor < 0 || cursor > inputRoots.Count)
            throw new McpMappingException("The input root cursor is invalid.");
    }

    /// <summary>
    /// Fills one page of the enforced input roots, verbatim and in configured
    /// order, starting at <paramref name="cursor"/>. Page size is derived from
    /// the real wire cost: every candidate page is measured as the complete
    /// serialized capabilities CallToolResult (diagnostics included) against
    /// the result budget, so the page always travels whole. A leading root
    /// that cannot fit even alone is counted in InputRootsOmitted and skipped,
    /// so the cursor always advances and paging terminates.
    /// </summary>
    private static McpCapabilitiesDocument WithInputRootPage(
        McpCapabilitiesDocument document, IReadOnlyList<string> roots, int cursor, int maximumBytes)
    {
        var command = McpToolCatalog.ToolNames[0];
        var start = cursor;
        var omitted = 0;
        while (start < roots.Count && !Fits(1))
        {
            omitted++;
            start++;
        }

        if (start == roots.Count)
            return Page(0);

        // Largest count whose page fits; one root is already known to fit.
        var low = 1;
        var high = roots.Count - start;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (Fits(middle))
                low = middle;
            else
                high = middle - 1;
        }

        return Page(low);

        bool Fits(int count) => McpResultAdapter.FitsRawSuccess(Page(count), command, maximumBytes);

        McpCapabilitiesDocument Page(int count)
        {
            var end = start + count;
            int? next = end < roots.Count ? end : null;
            return document with
            {
                InputRoots = roots.Skip(start).Take(count).ToArray(),
                InputRootCount = roots.Count,
                InputRootsComplete = cursor == 0 && next is null && omitted == 0,
                InputRootsNextCursor = next,
                InputRootsOmitted = omitted,
            };
        }
    }

    private static IReadOnlyDictionary<string, string> LocalDiagnostics(IReadOnlyList<string> inputRoots)
    {
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["inputRootCount"] = inputRoots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var (key, directory) in new (string, string)[]
        {
            ("compareDirExists", SqlHarnessPaths.CompareDir),
            ("snapshotsDirExists", SqlHarnessPaths.SnapshotsDir),
        })
            diagnostics[key] = Directory.Exists(directory) ? "true" : "false";
        return diagnostics;
    }

    public static Task<SqlHarnessQueryOperation> MapQueryAsync(
        McpScope scope,
        string? sql,
        string? file,
        IReadOnlyList<McpParameterArgument>? parameters,
        int timeout,
        int maxRows,
        CancellationToken ct,
        bool dryRun = false) =>
        MapQueryAsync(scope, new McpSqlSourceArgument { Sql = sql, File = file }, parameters, timeout, maxRows, ct, dryRun);

    internal static async Task<SqlHarnessQueryOperation> MapQueryAsync(
        McpScope scope,
        McpSqlSourceArgument source,
        IReadOnlyList<McpParameterArgument>? parameters,
        int timeout,
        int maxRows,
        CancellationToken ct,
        bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(source);
        var sqlText = await McpInputReader.ReadSqlAsync(source.Sql, source.File, scope, ct);
        // Persistent mutation is never offered over MCP: Core always sees false
        // with no confirmation database, and its classifier enforces read-only
        // plus session-local temp rules.
        return new SqlHarnessQueryOperation(
            scope.TargetRequest, sqlText, [],
            RequireTimeout(timeout), RequireMaxRows(maxRows), AllowMutation: false, ConfirmDatabase: null)
        {
            TypedParameters = MapParameters(parameters),
            DryRun = dryRun,
        };
    }

    public static async Task<SqlHarnessMutationOperation> MapMutationAsync(
        McpScope scope,
        string? sql,
        string? file,
        IReadOnlyList<McpParameterArgument>? parameters,
        int timeout,
        int maxRows,
        string? expectDatabase,
        string sessionKey,
        CancellationToken ct,
        bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        var sqlText = await McpInputReader.ReadSqlAsync(sql, file, scope, ct);
        return new SqlHarnessMutationOperation(
            scope.TargetRequest,
            sqlText,
            MapParameters(parameters),
            RequireTimeout(timeout),
            RequireMaxRows(maxRows),
            expectDatabase)
        {
            DryRun = dryRun,
            SessionKey = sessionKey,
        };
    }

    public static async Task<SqlHarnessMeasureOperation> MapMeasureAsync(
        McpScope scope,
        McpSqlSourceArgument query,
        McpSqlSourceArgument? setup,
        IReadOnlyList<McpParameterArgument>? parameters,
        IReadOnlyList<string>? parameterSetFiles,
        int repeat,
        int? timeout,
        CancellationToken ct,
        bool dryRun = false,
        IReadOnlyList<McpParameterSetArgument>? parameterSets = null,
        McpMatrixArgument? matrix = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ValidateParameterSetChoices(parameterSets, parameterSetFiles, matrix);
        var querySql = await McpInputReader.ReadSqlAsync(query.Sql, query.File, scope, ct);
        var setupSql = await ReadOptionalSourceAsync(setup, scope, "setup", ct);
        var sets = await MapParameterSetsAsync(parameterSets, parameterSetFiles, scope, ct);
        return new SqlHarnessMeasureOperation(
            scope.TargetRequest, setupSql, querySql, [],
            RequireTimeout(timeout), RequireRepeat(repeat), sets)
        {
            TypedParameters = MapParameters(parameters),
            TypedMatrix = matrix is null ? null : MapMatrix(matrix),
            DryRun = dryRun,
        };
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
        CancellationToken ct,
        bool compareNullability = false,
        bool dryRun = false,
        IReadOnlyList<McpParameterSetArgument>? parameterSets = null,
        IReadOnlyList<string>? parameterSetFiles = null,
        string? subject = null,
        string? optimizationScope = null,
        CompareConfig? compareConfig = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateParameterSetChoices(parameterSets, parameterSetFiles, matrix);
        CompareContext? context;
        try
        {
            context = CompareContext.Parse(subject, optimizationScope, (compareConfig ?? SqlHarnessConfig.Default.Compare).RequireContext);
        }
        catch (ArgumentException exception)
        {
            throw new McpMappingException(exception.Message);
        }
        if (compareResults is null || !CompareResultModes.Contains(compareResults))
            throw new McpMappingException("Unknown compare mode. Supported modes: ordered, multiset, set, off.");
        var mode = compareResults.ToLowerInvariant() switch
        {
            "ordered" => ResultComparisonMode.Ordered,
            "multiset" => ResultComparisonMode.Multiset,
            "set" => ResultComparisonMode.Set,
            _ => ResultComparisonMode.Off,
        };
        if (compareNullability && mode == ResultComparisonMode.Off)
            throw new McpMappingException("compareNullability requires compareResults ordered, multiset, or set.");
        var baselineSql = await McpInputReader.ReadSqlAsync(baseline.Sql, baseline.File, scope, ct);
        var candidateSql = await McpInputReader.ReadSqlAsync(candidate.Sql, candidate.File, scope, ct);
        var setupSql = await ReadOptionalSourceAsync(setup, scope, "setup", ct);
        var typedParameters = MapParameters(parameters);
        var parameterSetInputs = await MapParameterSetsAsync(parameterSets, parameterSetFiles, scope, ct);
        var validatedTimeout = RequireTimeout(timeout);
        var validatedRepeat = RequireRepeat(repeat);
        if (matrix is null && parameterSetInputs is null)
            return new SqlHarnessCompareOperation(
                scope.TargetRequest, setupSql, baselineSql, candidateSql,
                [], validatedTimeout, validatedRepeat, mode, compareNullability)
            {
                TypedParameters = typedParameters,
                DryRun = dryRun,
                Context = context,
            };
        return new SqlHarnessCompareMatrixOperation(
            scope.TargetRequest, setupSql, baselineSql, candidateSql,
            [], validatedTimeout, validatedRepeat, string.Empty, mode, compareNullability)
        {
            TypedParameters = typedParameters,
            TypedMatrix = matrix is null ? null : MapMatrix(matrix),
            ParameterSets = parameterSetInputs,
            DryRun = dryRun,
            Context = context,
        };
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
            scope.TargetRequest, sqlText, [],
            RequireTimeout(timeout), RequireMaxRows(maxRows),
            ParseWatchDuration(interval, DefaultWatchInterval, "interval"),
            ParseWatchDuration(maxDuration, DefaultWatchMaxDuration, "maxDuration"),
            hasUntil ? until!.Trim() : null,
            hasUntil ? null : untilUnchanged ?? DefaultWatchUntilUnchanged)
        {
            TypedParameters = MapParameters(parameters),
        };
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
            scope.TargetRequest, sqlText, [],
            RequireTimeout(timeout), RequireMaxRows(maxRows), name,
            Diff: string.Equals(action, "diff", StringComparison.OrdinalIgnoreCase), Force: false,
            Owner: scope.Owner)
        {
            TypedParameters = MapParameters(parameters),
        };
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

    public static async Task<SqlHarnessPlanOperation> MapPlanAsync(
        IReadOnlyList<string> inputRoots,
        string? content,
        string? file,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputRoots);
        var text = await McpInputReader.ReadPlanAsync(content, file, inputRoots, ct);
        var bytes = Encoding.UTF8.GetByteCount(text);
        var lines = text.Length == 0 ? 0 : text.Count(character => character == '\n') + (text[^1] == '\n' ? 0 : 1);
        return new SqlHarnessPlanOperation(text, new OutputFootprint(bytes, lines));
    }

    public static object ReadArtifactSection(McpScope scope, string? id, string? section, int? cursor = null,
        string? variant = null, int statement = 0, string? parameterSet = null, int? repetition = null, bool includeText = false)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (section is null || !ArtifactSections.Contains(section))
            throw new McpMappingException("Unknown artifact section. Supported sections: summary, metrics, operators, statements, matrix-cells, plan.");
        if (section != ArtifactReader.PlanSection && (variant is not null || statement != 0 || parameterSet is not null || repetition is not null || includeText))
            throw new McpMappingException("Plan selectors are only valid for the plan section.");
        return ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, id ?? string.Empty, section, scope.Owner, cursor,
            BuildArtifactPlanSelector(section, variant, statement, parameterSet, repetition), includeText);
    }

    public static SqlHarnessArtifactReadOperation MapArtifactRead(McpScope scope, string? id, string? section, int? cursor = null,
        string? variant = null, int statement = 0, string? parameterSet = null, int? repetition = null, bool includeText = false)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (section is null || !ArtifactSections.Contains(section))
            throw new McpMappingException("Unknown artifact section. Supported sections: summary, metrics, operators, statements, matrix-cells, plan.");
        // The shared manifest reader pins ids to single directory names under
        // the artifact root and serves only scope-owned safe sections: no raw
        // plans, queries.jsonl, SQL, or snapshot cells can leave through it.
        if (section != ArtifactReader.PlanSection && (variant is not null || statement != 0 || parameterSet is not null || repetition is not null || includeText))
            throw new McpMappingException("Plan selectors are only valid for the plan section.");
        return new SqlHarnessArtifactReadOperation(SqlHarnessPaths.CompareDir, id ?? string.Empty, section,
            scope.Owner, cursor, BuildArtifactPlanSelector(section, variant, statement, parameterSet, repetition), includeText);
    }

    private static ArtifactPlanSelector? BuildArtifactPlanSelector(string? section, string? variant, int statement,
        string? parameterSet, int? repetition) =>
        section == ArtifactReader.PlanSection && (variant is not null || statement != 0 || parameterSet is not null || repetition is not null)
            ? new ArtifactPlanSelector(variant ?? string.Empty, statement, parameterSet, repetition) : null;

    /// <summary>
    /// Hands MCP parameters to Core as the typed model, one input per
    /// argument and in caller order, without building or parsing text: the
    /// value stays whole, and a null value is SQL NULL (typed when a type is
    /// given). Only the argument shape is checked here. Names, duplicates,
    /// unknown types, and bad values stay the Core binder's job; nothing is
    /// echoed here.
    /// </summary>
    public static IReadOnlyList<SqlHarnessParameterInput> MapParameters(IReadOnlyList<McpParameterArgument>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return [];
        var inputs = new List<SqlHarnessParameterInput>(parameters.Count);
        foreach (var parameter in parameters)
        {
            if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name))
                throw new McpMappingException("Each parameter requires a non-empty name.");
            inputs.Add(new SqlHarnessParameterInput(parameter.Name, parameter.Type, parameter.Value));
        }

        return inputs;
    }

    /// <summary>
    /// Hands the matrix to Core as the typed model. Shape checks only: a
    /// comma, an empty string, and a null (typed NULL) are all legal values
    /// here, and the Core binder decides whether each fits the matrix type.
    /// </summary>
    internal static SqlHarnessParameterMatrixInput MapMatrix(McpMatrixArgument matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (string.IsNullOrWhiteSpace(matrix.Name) || string.IsNullOrWhiteSpace(matrix.Type))
            throw new McpMappingException("The matrix requires a non-empty name and type.");
        if (matrix.Values is null || matrix.Values.Count < 2)
            throw new McpMappingException("The matrix requires at least two values.");
        return new SqlHarnessParameterMatrixInput(matrix.Name, matrix.Type, matrix.Values);
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
        if (paths is null)
            return null;
        return await McpInputReader.ReadParameterSetsAsync(paths, scope, ct);
    }

    private static void ValidateParameterSetChoices(
        IReadOnlyList<McpParameterSetArgument>? parameterSets,
        IReadOnlyList<string>? parameterSetFiles,
        McpMatrixArgument? matrix)
    {
        var supplied = (parameterSets is not null ? 1 : 0)
            + (parameterSetFiles is not null ? 1 : 0)
            + (matrix is not null ? 1 : 0);
        if (supplied > 1)
            throw new McpMappingException("Use one of parameterSets, parameterSetFiles or matrix.");

        var setCount = parameterSets?.Count ?? parameterSetFiles?.Count;
        if (setCount is not null and (< 2 or > 32))
            throw new McpMappingException("Parameter sets must contain between 2 and 32 sets.");
    }

    private static async Task<IReadOnlyList<SqlHarnessParameterSetInput>?> MapParameterSetsAsync(
        IReadOnlyList<McpParameterSetArgument>? parameterSets,
        IReadOnlyList<string>? parameterSetFiles,
        McpScope scope,
        CancellationToken ct)
    {
        if (parameterSets is not null)
        {
            var mapped = new SqlHarnessParameterSetInput[parameterSets.Count];
            for (var index = 0; index < parameterSets.Count; index++)
            {
                var set = parameterSets[index];
                if (set is null)
                    throw new McpMappingException("Each parameter set requires a name and parameters.");
                mapped[index] = new SqlHarnessParameterSetInput(set.Name, [])
                {
                    TypedParameters = MapParameters(set.Parameters),
                };
            }
            return mapped;
        }

        return await ReadParameterSetsOrNullAsync(parameterSetFiles, scope, ct);
    }

    private static int RequireTimeout(int? timeout)
    {
        // Range lives in Core; the default and the MCP message stay here.
        if (timeout is null)
            return DefaultQueryTimeout;
        if (!OperationLimits.IsQueryTimeoutSeconds(timeout.Value))
            throw new McpMappingException("SQL timeout must be between 1 and 300 seconds.");
        return timeout.Value;
    }

    private static int RequireRepeat(int repeat) =>
        // Range lives in Core; the MCP message stays here.
        OperationLimits.IsRepeat(repeat)
            ? repeat
            : throw new McpMappingException("Measurement repetitions must be between 1 and 100.");

    private static int RequireMaxRows(int maxRows) =>
        // Range lives in Core; the MCP message stays here.
        OperationLimits.IsMaxRows(maxRows)
            ? maxRows
            : throw new McpMappingException("Maximum displayed rows must be between 0 and 500.");

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
        // The checks above guarantee a positive value with a supported unit; the
        // pure conversion and 24-hour cap live in Core, the message stays here.
        if (!OperationLimits.TryCreateWatchDuration(value, unit, out var duration))
            throw new McpMappingException($"The watch {label} must not exceed 24 hours.");
        return duration;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotNamePattern();
}