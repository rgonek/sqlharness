using System.Reflection;

namespace SqlHarness.Core;

public sealed record SqlHarnessCapabilities(
    string Version,
    string BuildId,
    int ContractVersion,
    IReadOnlyList<SqlHarnessCommandCapability> Commands,
    IReadOnlyList<SqlHarnessEngineCapability> Engines,
    IReadOnlyDictionary<string, object> Limits,
    IReadOnlyList<string> OutputModes,
    SqlHarnessSafetyAnalysis SafetyAnalysis,
    IReadOnlyDictionary<string, string> OutputModesByCommand);

public sealed record SqlHarnessCommandCapability(string Name, string Description);

public sealed record SqlHarnessEngineCapability(
    string Name,
    bool SupportsQstop,
    bool SupportsIndexes,
    IReadOnlyList<string> ParameterTypes);

public static class SqlHarnessCapabilitiesProvider
{
    public static SqlHarnessCapabilities Get()
    {
        var assembly = typeof(SqlHarnessCapabilitiesProvider).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var buildId = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
        return new SqlHarnessCapabilities(
            version,
            buildId,
            ContractVersion: 1,
            [
                new("capabilities", "Describe commands, engines, limits, modes."),
                new("doctor", "Check installation and profile files."),
                new("validate", "Classify SQL offline for a closed profile; never connects. Usages: query, setup, benchmark."),
                new("query", "Run a bounded query. PostgreSQL runs READ ONLY per command and denies known administrative effects; user functions can hide effects (setup SELECTs too), so use a least-privilege role."),
                new("measure", "Measure a query."),
                new("compare", "Compare baseline and candidate: result equivalence and latency regression verdict."),
                new("schema", "Inspect schema."),
                new("ping", "Check a connection."),
                new("counts", "Inspect row counts."),
                new("space", "Inspect storage."),
                new("watch", "Poll a bounded query passing the static visible-effects check."),
                new("snapshot", "Capture or diff a named result."),
                new("qstop", "Rank SQL Server Query Store consumers."),
                new("indexes", "Inspect SQL Server missing indexes."),
                new("plan", "Distill a saved plan offline; bounded statements and predicates."),
                new("artifact", "Read safe sections of a saved artifact offline."),
                new("mcp", "Serve the Core tools to one MCP client over local stdio (mcp serve)."),
                new("dashboard", "Operator tool: serve the local activity dashboard on 127.0.0.1; agents do not call it or its API."),
            ],
            [
                new("sqlserver", true, true, [
                    "nvarchar", "nvarchar(max)", "nvarchar(n)", "varchar", "varchar(max)", "varchar(n)", "char", "char(n)", "nchar", "nchar(n)", "int", "bigint", "smallint", "tinyint", "bit", "decimal", "decimal(p,s)", "numeric", "numeric(p,s)", "float", "real", "money", "smallmoney", "date", "time", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "uniqueidentifier", "varbinary", "varbinary(max)", "varbinary(n)", "binary(n)", "hierarchyid", "geography", "geometry"]),
                new("postgres", false, false, [
                    "nvarchar", "nvarchar(max)", "nvarchar(n)", "varchar", "varchar(max)", "varchar(n)", "char", "char(n)", "nchar", "nchar(n)", "int", "bigint", "smallint", "tinyint", "bit", "decimal", "decimal(p,s)", "numeric", "numeric(p,s)", "float", "real", "date", "time", "datetime", "datetime2", "datetimeoffset", "uniqueidentifier", "varbinary", "varbinary(max)", "varbinary(n)", "binary(n)"]),
            ],
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["queryTimeoutSeconds"] = new { min = OperationLimits.QueryTimeoutSecondsMin, max = OperationLimits.QueryTimeoutSecondsMax },
                ["queryMaxRows"] = new { min = OperationLimits.MaxRowsMin, max = OperationLimits.MaxRowsMax },
                ["comparisonRowCapPerRun"] = new { min = 0, max = CanonicalComparisonAccumulator.MaximumComparedRows },
                ["comparisonUniqueFingerprintBudgetPerCell"] = new { min = 0, max = ComparisonBudget.DefaultMaxUniqueFingerprints },
                ["repeat"] = new { min = OperationLimits.RepeatMin, max = OperationLimits.RepeatMax },
                ["targetIdentity"] = new { server = "informational; authentication depends on transport mode; PostgreSQL hostname check needs sslMode: verify-full", database = "must match the resolved database (ordinal); mismatch is exit 4" },
                ["qstopTop"] = new { min = OperationLimits.TopMin, max = OperationLimits.TopMax },
                ["qstopWindowMinutes"] = new { min = OperationLimits.QueryStoreWindowMinutesMin, max = OperationLimits.QueryStoreWindowMinutesMax },
                ["indexesTop"] = new { min = OperationLimits.TopMin, max = OperationLimits.TopMax },
                ["agentOutputBytes"] = new { min = 4096, max = 1048576, defaultValue = 16384 },
                ["agentCellChars"] = new { min = 0, max = 4096, defaultValue = 512 },
                ["sessionTempStatements"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["sqlserver"] = [
                        "DECLARE scalar variables with analyzed initializers",
                        "Plain SET @v = expr to a scalar local declared earlier in the same batch: allowed in query, measured SQL and --setup. The RHS gets the external, stateful and cross-database checks. Compound, cursor, member and session/transaction option SET stay denied",
                        "DECLARE @t TABLE (...) then INSERT/UPDATE/DELETE/MERGE/SELECT against it, declared earlier in the same batch. A table variable from --setup is not visible to measured SQL (separate batch): carry data in #temp. OUTPUT INTO @t only when the primary target is also proven local (persistent primary target: MutationNotAllowed). Aliased DML targets and user-defined table types stay denied",
                        "TRUNCATE TABLE #temp (unambiguous local temp only)",
                        "ALTER TABLE #temp ADD/DROP COLUMN and local CHECK/DEFAULT/NULL/UNIQUE constraints"],
                    ["postgres"] = [
                        "EXPLAIN over a safe SELECT (plan-only, read-only)",
                        "EXPLAIN ANALYZE with full inner-statement effect analysis",
                        "SELECT INTO TEMP TABLE with unambiguous single-part name",
                        "TRUNCATE [ONLY] of proven current-session temps only, named unqualified or as pg_temp.name; not ON COMMIT DROP. Persistent, mixed, CASCADE, RESTART IDENTITY and other schema-qualified targets stay denied",
                        "Session-temp proof for temp DML, DROP TABLE, CREATE INDEX and TRUNCATE targets is name-based. An unqualified name assumes the default search_path (pg_temp first) and is not checked against the server; when search_path may differ, write to pg_temp.name, which does not depend on it. A name must be quoted or all-ASCII-unquoted and at most 63 UTF-8 bytes where declared and where used, else never proven (quote or shorten). DROP TABLE of a name unknown offline revokes every proof. See README.md"],
                },
                ["artifactRead"] = new { sections = ArtifactReader.SupportedSections, manifestVersion = ArtifactReader.CurrentManifestVersion, maxReportBytes = ArtifactReader.MaxReportBytes },
                ["watchNdjson"] = new { events = new[] { "started", "changed", "completed", "failed" }, schemaVersion = WatchNdjsonWriter.SchemaVersion, sequence = "strictly increasing; one terminal record", history = "none; every change emitted" },
            },
            ["text", "json", "json-summary", "agent"],
            new SqlHarnessSafetyAnalysis(
                SqlSafetyAnalysis.AnalysisKind,
                SqlSafetyAnalysis.ContractVersion,
                SqlSafetyAnalysis.HiddenEffectsVerified,
                SqlSafetyAnalysis.ObjectAndPermissionStatus),
            // Per-command modes. "outputModes" above stays the global union; no command accepts
            // two of --json, --json-summary and --output together; keys without text need one of the listed flags.
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["json"] = "capabilities,doctor,validate",
                ["json|agent"] = "artifact",
                ["text|json|agent"] = "query,schema,ping,counts,space,snapshot,qstop,indexes,plan",
                ["text|json|json-summary|agent"] = "measure,compare",
                ["text|json|agent|ndjson"] = "watch",
            });
    }
}