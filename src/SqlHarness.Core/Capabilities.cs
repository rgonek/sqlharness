using System.Reflection;
using System.Text.Json.Serialization;

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SqlHarnessProfileCapability? Profile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ParameterTypes = null);

public sealed record SqlHarnessCommandCapability(string Name, string Description);

public sealed record SqlHarnessEngineSummary(string Name, string Summary);

public sealed record SqlHarnessProfileCapability(string Name, string Engine);

public static class SqlHarnessCapabilitiesProvider
{
    // Bump when the document shape changes. 2: engines became a short { name, summary } list;
    // parameter types, session-temp rules and engine-only limits moved behind a profile.
    public const int ContractVersion = 2;

    // Parameter types both engines accept; an engine detail adds only what is its own.
    private static readonly string[] SharedParameterTypes =
    [
        "nvarchar", "nvarchar(max)", "nvarchar(n)", "varchar", "varchar(max)", "varchar(n)", "char", "char(n)", "nchar", "nchar(n)", "int", "bigint", "smallint", "tinyint", "bit", "decimal", "decimal(p,s)", "numeric", "numeric(p,s)", "float", "real", "date", "time", "datetime", "datetime2", "datetimeoffset", "uniqueidentifier", "varbinary", "varbinary(max)", "varbinary(n)", "binary(n)",
    ];

    // One entry per engine: adding an engine adds one entry here (and one SqlEngine value).
    // An ordered array: the order of the engines list is part of the contract.
    private static readonly EngineDetail[] Details =
    [
        new(
            SqlEngine.SqlServer,
            "sqlserver",
            "SQL Server and Azure SQL: all commands, including Query Store (qstop) and missing indexes (indexes). IF/ELSE/BEGIN…END (nested) allowed; scan every branch regardless of predicate; WHILE/BREAK/CONTINUE/GOTO/labels/RETURN/TRY…CATCH/WAITFOR/THROW/RAISERROR denied.",
            [.. SharedParameterTypes, "money", "smallmoney", "smalldatetime", "hierarchyid", "geography", "geometry"],
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["qstopTop"] = new { min = OperationLimits.TopMin, max = OperationLimits.TopMax },
                ["qstopWindowMinutes"] = new { min = OperationLimits.QueryStoreWindowMinutesMin, max = OperationLimits.QueryStoreWindowMinutesMax },
                ["indexesTop"] = new { min = OperationLimits.TopMin, max = OperationLimits.TopMax },
            },
            [
                "DECLARE scalar variables with analyzed initializers",
                "Plain SET @v = expr to a scalar local declared earlier in the same batch: allowed in query, measured SQL and --setup. The RHS gets the external, stateful and cross-database checks. Compound, cursor, member and session/transaction option SET stay denied",
                "DECLARE @t TABLE (...) then INSERT/UPDATE/DELETE/MERGE/SELECT against it, declared earlier in the same batch. A table variable from --setup is not visible to measured SQL (separate batch): carry data in #temp. OUTPUT INTO @t only when the primary target is also proven local (persistent primary target: MutationNotAllowed). Aliased DML targets and user-defined table types stay denied",
                "TRUNCATE TABLE #temp (unambiguous local temp only)",
                "ALTER TABLE #temp ADD/DROP COLUMN and local CHECK/DEFAULT/NULL/UNIQUE constraints"]),
        new(
            SqlEngine.Postgres,
            "postgres",
            "PostgreSQL: per-command READ ONLY; no qstop or indexes; a session that does not report standard_conforming_strings=on is refused with exit 2.",
            [.. SharedParameterTypes],
            new Dictionary<string, object>(StringComparer.Ordinal),
            [
                "EXPLAIN over a safe SELECT (plan-only, read-only)",
                "EXPLAIN ANALYZE with full inner-statement effect analysis",
                "SELECT INTO TEMP TABLE with unambiguous single-part name",
                "TRUNCATE [ONLY] of proven current-session temps only, named unqualified or as pg_temp.name; not ON COMMIT DROP. Persistent, mixed, CASCADE, RESTART IDENTITY and other schema-qualified targets stay denied",
                "Temp DML runs in a read-only transaction, where PostgreSQL refuses a persistent target itself, so an unqualified INSERT/UPDATE/DELETE/MERGE target needs no proof; the server reports the miss as exit 2. Session-temp proof for setup DDL (CREATE TEMP recording, DROP TABLE, CREATE INDEX, TRUNCATE) and for the approved mutation path is name-based. An unqualified name assumes the default search_path (pg_temp first) and is not checked against the server; when search_path may differ, write to pg_temp.name, which does not depend on it. A name must be quoted or all-ASCII-unquoted and at most 63 UTF-8 bytes where declared and where used, else never proven (quote or shorten). DROP TABLE of a name unknown offline revokes every proof. See README.md"]),
    ];

    /// <summary>Engine-neutral document: commands, modes, shared limits and a one-line summary per engine.</summary>
    public static SqlHarnessCapabilities Get() =>
        Build(Details.Select(d => new SqlHarnessEngineSummary(d.Name, d.Summary)).ToList(), null, null, null, null);

    /// <summary>Document scoped to one engine; the profile name is echoed back. Offline, no connection.</summary>
    public static SqlHarnessCapabilities Get(SqlEngine engine, string profileName)
    {
        var detail = Details.Single(d => d.Engine == engine);
        return Build(
            [new SqlHarnessEngineSummary(detail.Name, detail.Summary)],
            new SqlHarnessProfileCapability(profileName, detail.Name),
            detail.ParameterTypes,
            detail.EngineLimits,
            detail.SessionTempStatements);
    }

    /// <summary>Scoped document for a profile's declared engine string (null or empty means sqlserver).</summary>
    public static SqlHarnessCapabilities Get(string profileName, string? profileEngine) =>
        Get(SqlEngineNames.Parse(profileEngine), profileName);

    private sealed record EngineDetail(
        SqlEngine Engine,
        string Name,
        string Summary,
        IReadOnlyList<string> ParameterTypes,
        IReadOnlyDictionary<string, object> EngineLimits,
        IReadOnlyList<string> SessionTempStatements);

    private static SqlHarnessCapabilities Build(
        IReadOnlyList<SqlHarnessEngineSummary> engines,
        SqlHarnessProfileCapability? profile,
        IReadOnlyList<string>? parameterTypes,
        IReadOnlyDictionary<string, object>? engineLimits,
        IReadOnlyList<string>? sessionTempStatements)
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
            ["targetIdentity"] = new { server = "informational; authentication depends on transport; PostgreSQL hostname check needs sslMode: verify-full", database = "must match the resolved database (ordinal); mismatch is exit 4" },
            ["agentOutputBytes"] = new { min = 4096, max = 1048576, defaultValue = 65536 },
            ["agentCellChars"] = new { min = 0, max = 4096, defaultValue = 512 },
            ["agentEnvelopeSchemaVersion"] = AgentEnvelopeSchema.Version,
            ["activityJournalStorage"] = new
            {
                errorMessages = "credential-redacted error messages are stored by default",
                sqlTextAndPlans = "journal.storeSensitive",
            },
            ["artifactRead"] = new
            {
                sections = ArtifactReader.SupportedSections,
                manifestVersion = ArtifactReader.CurrentManifestVersion,
                maxReportBytes = ArtifactReader.MaxReportBytes,
                pagedSections = "matrix-cells,operators,plan,statements",
                planSelectors = "variant,statement,parameterSet,repetition,includeText",
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
                new("capabilities", "Describe commands, engines, limits, modes. Pass a profile for the parameter types and limits of its engine."),
                new("doctor", "Check installation and profiles."),
                new("validate", "Classify SQL offline for a closed profile. Usages: query, setup, benchmark."),
                new("query", "Run a bounded query; Core/MCP dryRun checks exact arguments offline without connecting or writing, with no CLI switch. Every output replaces the profile password and Azure CLI access token fetched for the operation; other text, including SQL quoted in server errors, stays readable. PostgreSQL: per-command READ ONLY, known administrative effects denied; TEMP setup is read-write under static checks; user functions can hide effects (setup SELECTs too), so use a least-privilege role."),
                new("measure", "Measure a query; Core/MCP dryRun checks exact arguments offline without connecting or writing, with no CLI switch."),
                new("compare", "Compare baseline/candidate: result equivalence and latency regression verdict; Core/MCP dryRun checks exact arguments offline without connecting or writing, with no CLI switch."),
                new("schema", "Inspect schema."),
                new("ping", "Check a connection."),
                new("counts", "Inspect row counts."),
                new("space", "Inspect storage."),
                new("watch", "Poll a bounded query passing the static visible-effects check."),
                new("snapshot", "Capture or diff a named result."),
                new("qstop", "Rank SQL Server Query Store consumers."),
                new("indexes", "Inspect SQL Server missing indexes."),
                new("plan", "Saved plan reads include measured runs with missing plans."),
                new("artifact", "Read safe sections of a saved artifact offline."),
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
            profile,
            parameterTypes);
    }

    private static Dictionary<string, IReadOnlyList<string>> OutputModesByCommand()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var command in new[] { "capabilities", "doctor", "validate" })
            map[command] = ["json"];
        map["artifact"] = ["json", "agent"];
        foreach (var command in new[] { "query", "schema", "ping", "counts", "space", "snapshot", "qstop", "indexes", "plan" })
            map[command] = ["text", "json", "agent"];
        foreach (var command in new[] { "measure", "compare" })
            map[command] = ["text", "json", "json-summary", "agent"];
        map["watch"] = ["text", "json", "agent", "ndjson"];
        return map;
    }
}