using System.Reflection;
using System.Text.Json.Serialization;

using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

public sealed record SqlHarnessCapabilities(
    string Version,
    string BuildId,
    int ContractVersion,
    IReadOnlyList<SqlHarnessCommandCapability> Commands,
    IReadOnlyList<SqlHarnessEngineSummary> Engines,
    IReadOnlyDictionary<string, object> Limits,
    IReadOnlyList<string> OutputModes,
    SqlHarnessSafetyAnalysis SafetyAnalysis,
    IReadOnlyDictionary<string, IReadOnlyList<string>> OutputModesByCommand,
    SqlHarnessCompareContextCapability CompareContext,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SqlHarnessProfileCapability? Profile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ParameterTypes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SqlHarnessEngineGuidance? EngineGuidance = null,
    SqlHarnessMutationCapability? Mutations = null);

/// <summary>
/// One engine's operation guidance: facts a caller needs at the call site that the shared command
/// descriptions leave out. Query covers safety and effects (empty when the engine has nothing to add
/// beyond the shared description), Setup the native session-temp and parameterized-setup rules.
/// </summary>
public sealed record SqlHarnessEngineGuidance(string Query, string Setup);

public sealed record SqlHarnessCompareContextCapability(
    bool RequireContext,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LabelPattern = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WholeScope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VersionIdentity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VersionAssignment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HistoryBoundary = null);

public sealed record SqlHarnessCommandCapability(string Name, string Description);

public sealed record SqlHarnessEngineSummary(string Name, string Summary);

public sealed record SqlHarnessProfileCapability(string Name, string Engine, string MutationMode, bool WriterConfigured);

public sealed record SqlHarnessMutationCapability(string DefaultMode, string Verification, int ApprovalTimeoutSeconds, bool DashboardReachable);


public static class SqlHarnessCapabilitiesProvider
{
    // Bump when the document shape changes. 2: engines became a short { name, summary } list;
    // parameter types, session-temp rules and engine-only limits moved behind a profile.
    // 3: report the active compare-context requirement and version/history semantics.
    // 4: add the persistent mutation command and remove query mutation switches.
    // 6: report whether plan documents are available through the sensitive journal gate.
    public const int ContractVersion = 6;

    public static SqlHarnessCompareContextCapability CompareContextPolicy(CompareConfig? config = null)
    {
        if (!(config ?? SqlHarnessConfig.Default.Compare).RequireContext)
            return new SqlHarnessCompareContextCapability(false);

        return new SqlHarnessCompareContextCapability(
            true,
            "^[a-z0-9][a-z0-9._-]{0,63}$",
            "whole",
            "(sessionId, subject, scope)",
            "First-seen SQL hash; baseline precedes candidate on a tie; reused hashes keep their label",
            "Uses full retained journal history before filtering and paging; retention can change labels over time");
    }

    // Parameter types both engines accept; an engine detail adds only what is its own.
    private static readonly string[] SharedParameterTypes =
    [
        "nvarchar", "nvarchar(max)", "nvarchar(n)", "varchar", "varchar(max)", "varchar(n)", "char", "char(n)", "nchar", "nchar(n)", "int", "bigint", "smallint", "tinyint", "bit", "decimal", "decimal(p,s)", "numeric", "numeric(p,s)", "float", "real", "date", "time", "datetime", "datetime2", "datetimeoffset", "uniqueidentifier", "varbinary", "varbinary(max)", "varbinary(n)", "binary(n)",
    ];

    private const string DefaultTargetServer = "informational; authentication depends on transport";

    // One entry per engine: adding an engine adds one entry here (and one SqlEngine value).
    // An ordered array: the order of the engines list is part of the contract.
    private static readonly EngineDetail[] Details =
    [
        new(
            SqlEngine.SqlServer,
            "sqlserver",
            "SQL Server and Azure SQL. Read-only Query (allowMutation=false) and CompareSetup admit the documented session/control-flow allowlist; scan every branch regardless of predicate; persistent-mutation classification is unchanged. EXEC/EXECUTE, WAITFOR, GOTO, labels, value-bearing RETURN, and RAISERROR WITH LOG remain denied.",
            [.. SharedParameterTypes, "money", "smallmoney", "smalldatetime", "hierarchyid", "geography", "geometry"],
            new Dictionary<string, object>(StringComparer.Ordinal),
            new SqlHarnessEngineGuidance(
                "Read-only Query (allowMutation=false) and CompareSetup admit IF/ELSE/BEGIN…END, WHILE with BREAK/CONTINUE, TRY/CATCH, value-less batch RETURN, THROW, RAISERROR without LOG, PRINT, local transactions/savepoints, and LOCAL cursors over admitted SELECTs. Allowed SET options only: NOCOUNT, XACT_ABORT, ARITHABORT, ANSI_NULLS, ANSI_WARNINGS, ANSI_PADDING, QUOTED_IDENTIFIER, CONCAT_NULL_YIELDS_NULL, NUMERIC_ROUNDABORT, DATEFIRST, DATEFORMAT, LOCK_TIMEOUT, DEADLOCK_PRIORITY (LOW, NORMAL, or numeric <= 0), and TRANSACTION ISOLATION LEVEL. Other SET options remain denied, including STATISTICS, SHOWPLAN_*, NOEXEC, PARSEONLY, FMTONLY, ROWCOUNT, IDENTITY_INSERT, CONTEXT_INFO, LANGUAGE, FORCEPLAN, IMPLICIT_TRANSACTIONS, REMOTE_PROC_TRANSACTIONS, and OFFSETS. EXEC/EXECUTE, WAITFOR, GOTO, labels, value-bearing RETURN, RAISERROR WITH LOG, distributed or marked transactions, GLOBAL cursors, cursor variables, SCROLL_LOCKS, and cursor FOR UPDATE remain denied. Every branch and nested statement is classified. Transactions left open at a logical batch boundary are rolled back with a warning; failed cleanup discards the connection and fails the operation. Setup options persist for that case; measured commands restore the state captured after setup.",
                "Create #temps before the first parameter reference, populate afterwards."),
            DefaultTargetServer,
            [
                "DECLARE scalar variables with analyzed initializers",
                "Plain SET @v = expr to a scalar local declared earlier in the same batch: allowed in query, measured SQL and --setup. The RHS gets the external, stateful and cross-database checks. Compound, cursor and member-variable SET stay denied; session and transaction option SET follow the SQL Server query guidance allowlist",
                "DECLARE @t TABLE (...) then INSERT/UPDATE/DELETE/MERGE/SELECT against it, declared earlier in the same batch. A table variable from --setup is not visible to measured SQL (separate batch): carry data in #temp. OUTPUT INTO @t only when the primary target is also proven local (persistent primary target: MutationNotAllowed). Aliased DML targets and user-defined table types stay denied",
                "TRUNCATE TABLE #temp (unambiguous local temp only)",
                "ALTER TABLE #temp ADD/DROP COLUMN and local CHECK/DEFAULT/NULL/UNIQUE constraints"]),
        new(
            SqlEngine.Postgres,
            "postgres",
            "PostgreSQL: per-command READ ONLY; a session that does not report standard_conforming_strings=on is refused with exit 2.",
            [.. SharedParameterTypes],
            new Dictionary<string, object>(StringComparer.Ordinal),
            new SqlHarnessEngineGuidance(
                "Per-command READ ONLY; known administrative effects denied. User functions can still have other effects, so use a least-privilege role.",
                "TEMP setup is read-write under static checks; a setup SELECT can call a user function with persistent effects, so review those functions and use a least-privilege role."),
            "informational; hostname check needs sslMode: verify-full",
            [
                "EXPLAIN over a safe SELECT (plan-only, read-only)",
                "EXPLAIN ANALYZE with full inner-statement effect analysis",
                "SELECT INTO TEMP TABLE with unambiguous single-part name, in --setup only",
                "TRUNCATE [ONLY] of proven current-session temps only, in --setup only; named unqualified or as pg_temp.name; not ON COMMIT DROP. Persistent, mixed, CASCADE, RESTART IDENTITY and other schema-qualified targets stay denied",
                "Temp DML runs in a read-only transaction, where PostgreSQL refuses a persistent target itself, so an unqualified INSERT/UPDATE/DELETE/MERGE target needs no proof; the server reports the miss as exit 2. Session-temp proof for setup DDL (CREATE TEMP recording, DROP TABLE, CREATE INDEX, TRUNCATE) and for the approved mutation path is name-based. That DDL classifies as allowed in every phase but executes only in --setup, the one read-write phase: in query and measured SQL the server refuses it in the read-only transaction (exit 2). An unqualified name assumes the default search_path (pg_temp first) and is not checked against the server; when search_path may differ, write to pg_temp.name, which does not depend on it. A name must be quoted or all-ASCII-unquoted and at most 63 UTF-8 bytes where declared and where used, else never proven (quote or shorten). DROP TABLE of a name unknown offline revokes every proof. See README.md"]),
    ];

    /// <summary>The single guidance owner for an engine; a registered engine without an entry is a programming error, never a fallback.</summary>
    public static SqlHarnessEngineGuidance GuidanceFor(SqlEngine engine) => DetailFor(engine).Guidance;

    private static EngineDetail DetailFor(SqlEngine engine) =>
        Details.FirstOrDefault(d => d.Engine == engine)
        ?? throw new InvalidOperationException($"No capabilities entry is registered for engine {engine}.");

    /// <summary>Engine-neutral document: commands, active compare-context policy, modes, shared limits and engine summaries.</summary>
    public static SqlHarnessCapabilities Get(CompareConfig? compareConfig = null, MutationsConfig? mutationsConfig = null,
        bool dashboardReachable = false, JournalConfig? journalConfig = null) =>
        Build(Details.Select(d => new SqlHarnessEngineSummary(d.Name, d.Summary)).ToList(), null, null, null, null, compareConfig,
            mutationCapability: MutationCapability(mutationsConfig, dashboardReachable), journalConfig: journalConfig);

    /// <summary>Document scoped to one engine; the profile name is echoed back. Offline, no connection.</summary>
    public static SqlHarnessCapabilities Get(SqlEngine engine, string profileName, CompareConfig? compareConfig = null,
        TargetProfile? targetProfile = null, MutationsConfig? mutationsConfig = null, bool dashboardReachable = false,
        JournalConfig? journalConfig = null)
    {
        var detail = DetailFor(engine);
        return Build(
            [new SqlHarnessEngineSummary(detail.Name, detail.Summary)],
            new SqlHarnessProfileCapability(profileName, detail.Name,
                targetProfile?.Mutations?.Mode ?? mutationsConfig?.DefaultMode ?? "never", targetProfile?.Mutations?.Writer is not null),
            detail.ParameterTypes,
            detail.EngineLimits,
            detail.SessionTempStatements,
            compareConfig,
            detail.Guidance,
            detail.TargetServer,
            MutationCapability(mutationsConfig, dashboardReachable),
            journalConfig);
    }

    /// <summary>Scoped document for a profile's declared engine string (null or empty means sqlserver).</summary>
    public static SqlHarnessCapabilities Get(string profileName, string? profileEngine, CompareConfig? compareConfig = null,
        TargetProfile? targetProfile = null, MutationsConfig? mutationsConfig = null, bool dashboardReachable = false,
        JournalConfig? journalConfig = null) =>
        Get(SqlEngineNames.Parse(profileEngine), profileName, compareConfig, targetProfile, mutationsConfig, dashboardReachable, journalConfig);

    public static SqlHarnessMutationCapability MutationCapability(MutationsConfig? config, bool dashboardReachable) =>
        new(config?.DefaultMode ?? "never", config?.Verification ?? "click", config?.ApprovalTimeoutSeconds ?? 120, dashboardReachable);

    public static (string Mode, bool WriterConfigured) ProfileMutationCapability(TargetProfile profile, MutationsConfig? config = null) =>
        (profile.Mutations?.Mode ?? config?.DefaultMode ?? "never", profile.Mutations?.Writer is not null);

    private sealed record EngineDetail(
        SqlEngine Engine,
        string Name,
        string Summary,
        IReadOnlyList<string> ParameterTypes,
        IReadOnlyDictionary<string, object> EngineLimits,
        SqlHarnessEngineGuidance Guidance,
        string TargetServer,
        IReadOnlyList<string> SessionTempStatements);

    private static SqlHarnessCapabilities Build(
        IReadOnlyList<SqlHarnessEngineSummary> engines,
        SqlHarnessProfileCapability? profile,
        IReadOnlyList<string>? parameterTypes,
        IReadOnlyDictionary<string, object>? engineLimits,
        IReadOnlyList<string>? sessionTempStatements,
        CompareConfig? compareConfig = null,
        SqlHarnessEngineGuidance? guidance = null,
        string targetServer = DefaultTargetServer,
        SqlHarnessMutationCapability? mutationCapability = null,
        JournalConfig? journalConfig = null)
    {
        var assembly = typeof(SqlHarnessCapabilitiesProvider).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var buildId = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
        var limits = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["queryTimeoutSeconds"] = new { min = OperationLimits.QueryTimeoutSecondsMin, max = OperationLimits.QueryTimeoutSecondsMax },
            ["queryMaxRows"] = new { min = OperationLimits.MaxRowsMin, max = OperationLimits.MaxRowsMax },
            ["comparisonRowCapPerRun"] = new { min = 0, max = CanonicalComparisonAccumulator.MaximumComparedRows },
            ["comparisonUniqueFingerprintBudgetPerCell"] = new { min = 0, max = ComparisonBudget.DefaultMaxUniqueFingerprints },
            ["repeat"] = new { min = OperationLimits.RepeatMin, max = OperationLimits.RepeatMax },
            ["nonScalarParameters"] = ParameterSetResolver.MaximumNonScalarParameters,
            ["combinedParameterRowsAndValues"] = ParameterSetResolver.MaximumCombinedRowsAndValues,
            ["materializedParameterBytes"] = ParameterSetResolver.MaximumMaterializedBytes,
            ["finalCommandParameters"] = ParameterSetResolver.MaximumCommandParameters,
            ["targetIdentity"] = new { server = targetServer, database = "must match the resolved database (ordinal); mismatch is exit 4" },
            ["agentOutputBytes"] = new { min = 4096, max = 1048576, defaultValue = 65536 },
            ["agentCellChars"] = new { min = 0, max = AgentOutputLimits.MaximumCellCharacters, defaultValue = AgentOutputLimits.DefaultCellCharacters },
            ["agentEnvelopeSchemaVersion"] = AgentEnvelopeSchema.Version,
            ["activityJournalStorage"] = new
            {
                errorMessages = "credential-redacted error messages are stored by default",
                sqlTextAndPlans = "journal.storeSensitive",
                planStorage = new
                {
                    state = journalConfig?.StoreSensitive == true ? "stored" : "plans not stored",
                    enabledBy = "journal.storeSensitive",
                },
            },
            ["artifactRead"] = new
            {
                sections = ArtifactReader.SupportedSections,
                manifestVersion = ArtifactReader.CurrentManifestVersion,
                maxReportBytes = ArtifactReader.MaxReportBytes,
                pagedSections = "matrix-cells,operators,plan,statements",
                planSelectors = "variant,statement,parameterSet,repetition,includeText",
                planSourceWindowNodes = ArtifactReader.MaxPlanPageSourceNodes,
                planPaging = "each plan response is byte-budgeted; continuation advances by nodes returned",
            },
            ["watchNdjson"] = new { events = new[] { "started", "changed", "completed", "failed" }, schemaVersion = WatchNdjsonWriter.SchemaVersion, sequence = "strictly increasing; one terminal record", history = "none; every change emitted" },
        };
        if (engineLimits is not null)
        {
            foreach (var (name, value) in engineLimits)
                limits[name] = value;
        }
        if (sessionTempStatements is not null)
            limits["sessionTempStatements"] = sessionTempStatements;

        return new SqlHarnessCapabilities(
            version,
            buildId,
            ContractVersion,
            [
                new("capabilities", "Describe commands, engines, limits, modes, active compare-context policy, and sensitive plan-storage state. Pass a profile for the parameter types, limits and engine guidance (engineGuidance) of its engine."),
                new("doctor", "Check installation and profiles."),
                new("query", "Run a bounded query passing the static visible-effects check; CLI --dry-run and MCP dryRun check exact arguments offline. Persistent changes use mutation. Every output replaces the profile password and Azure CLI access token fetched for the operation; other text, including SQL quoted in server errors, stays readable."),
                new("mutation", "Submit a persistent change through the dashboard mutation policy. CLI --dry-run and MCP dryRun check exact arguments offline; any non-success status must be reported without retrying."),
                new("measure", "Measure one query across repeats; CLI --param-set or MCP parameterSets, parameterSetFiles or matrix vary inputs. Each set runs on its own session with setup bound to that set; sets run in order and the first failure stops the run, keeping completed sets. Optional offline check: CLI --dry-run or MCP dryRun. Use command help for arguments."),
                new("compare", "Compare baseline/candidate measurements and observed rows; MCP parameterSets, parameterSetFiles or matrix use a session/setup per set. Read regression verdict/reasons separately from exit code. Optional offline check: CLI --dry-run or MCP dryRun. Use command help for arguments."),
                new("watch", "Poll a bounded query passing the static visible-effects check."),
                new("snapshot", "Capture or diff a named result."),
                new("plan", "Distill a saved execution plan offline."),
                new("artifact", "Read saved benchmark evidence offline; plan selection identifies the measured repetition and reports missing evidence. Use command help for selectors and continuation."),
                new("mcp", "Serve the Core tools to one MCP client over local stdio (mcp serve). File inputs require absolute paths under configured roots; UNC paths are rejected and reads are byte-bounded."),
            ],
            engines,
            limits,
            ["text", "json", "json-summary", "agent"],
            new SqlHarnessSafetyAnalysis(
                SqlSafetyAnalysis.AnalysisKind,
                SqlSafetyAnalysis.ContractVersion,
                SqlSafetyAnalysis.HiddenEffectsVerified,
                SqlSafetyAnalysis.ObjectAndPermissionStatus),
            // Per-command modes. "outputModes" above stays the global union; no command accepts
            // two of --json, --json-summary and --output together; a list without text needs one of its flags.
            OutputModesByCommand(),
            CompareContextPolicy(compareConfig),
            profile,
            parameterTypes,
            guidance,
            mutationCapability);
    }

    private static Dictionary<string, IReadOnlyList<string>> OutputModesByCommand()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var command in new[] { "capabilities", "doctor" })
            map[command] = ["json"];
        map["artifact"] = ["json", "agent"];
        foreach (var command in new[] { "query", "snapshot", "plan" })
            map[command] = ["text", "json", "agent"];
        map["mutation"] = ["text", "json", "agent"];
        foreach (var command in new[] { "measure", "compare" })
            map[command] = ["text", "json", "json-summary", "agent"];
        map["watch"] = ["text", "json", "agent", "ndjson"];
        return map;
    }
}