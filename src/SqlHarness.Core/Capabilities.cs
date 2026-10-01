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
    SqlHarnessSafetyAnalysis SafetyAnalysis);

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
                new("capabilities", "Describe commands, engines, limits, and output modes."),
                new("doctor", "Check local SQLHarness installation and profile-file availability."),
                new("validate", "Classify SQL offline using a closed target profile; never connects. Usages: query, setup, benchmark."),
                new("query", "Run a bounded SQL query."),
                new("measure", "Measure a query."),
                new("compare", "Compare baseline and candidate queries."),
                new("schema", "Inspect database schema."),
                new("ping", "Check a database connection."),
                new("counts", "Inspect table row counts."),
                new("space", "Inspect database storage."),
                new("watch", "Poll a bounded query passing the static visible-effects text check."),
                new("snapshot", "Capture or compare a named query result."),
                new("qstop", "Rank SQL Server Query Store consumers."),
                new("indexes", "Inspect SQL Server missing-index evidence."),
                new("plan", "Distill a saved execution plan offline."),
                new("gain", "Report saved-output estimates."),
                new("artifact", "Read safe sections of a saved benchmark artifact offline."),
            ],
            [
                new("sqlserver", true, true, [
                    "nvarchar", "nvarchar(max)", "varchar", "varchar(max)", "char", "nchar", "int", "bigint", "smallint", "tinyint", "bit", "decimal", "decimal(p,s)", "numeric", "numeric(p,s)", "float", "real", "money", "smallmoney", "date", "time", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "uniqueidentifier", "varbinary", "varbinary(max)", "hierarchyid", "geography", "geometry"]),
                new("postgres", false, false, [
                    "nvarchar", "nvarchar(max)", "varchar", "varchar(max)", "char", "nchar", "int", "bigint", "smallint", "tinyint", "bit", "decimal", "decimal(p,s)", "numeric", "numeric(p,s)", "float", "real", "date", "time", "datetime", "datetime2", "datetimeoffset", "uniqueidentifier", "varbinary", "varbinary(max)"]),
            ],
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["queryTimeoutSeconds"] = new { min = OperationLimits.QueryTimeoutSecondsMin, max = OperationLimits.QueryTimeoutSecondsMax },
                ["queryMaxRows"] = new { min = OperationLimits.MaxRowsMin, max = OperationLimits.MaxRowsMax },
                ["comparisonRowCapPerRun"] = new { min = 0, max = CanonicalComparisonAccumulator.MaximumComparedRows },
                ["comparisonUniqueFingerprintBudgetPerCell"] = new { min = 0, max = ComparisonBudget.DefaultMaxUniqueFingerprints },
                ["repeat"] = new { min = OperationLimits.RepeatMin, max = OperationLimits.RepeatMax },
                ["qstopTop"] = new { min = OperationLimits.TopMin, max = OperationLimits.TopMax },
                ["qstopWindowMinutes"] = new { min = OperationLimits.QueryStoreWindowMinutesMin, max = OperationLimits.QueryStoreWindowMinutesMax },
                ["indexesTop"] = new { min = OperationLimits.TopMin, max = OperationLimits.TopMax },
                ["agentOutputBytes"] = new { min = 4096, max = 1048576, defaultValue = 16384 },
                ["agentCellChars"] = new { min = 0, max = 4096, defaultValue = 512 },
                ["sessionTempStatements"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["sqlserver"] = [
                        "DECLARE scalar variables with analyzed initializers",
                        "Plain SET @v = <expr> to a scalar local declared earlier in the same batch; query SQL only, denied in --setup (RHS analyzed for external/stateful/cross-database sources; compound (+=), cursor, and session/transaction option SET stay denied)",
                        "DECLARE @t TABLE (...) then INSERT/UPDATE/DELETE/MERGE/SELECT against that table variable, declared earlier in the same batch; OUTPUT INTO @t only when the statement's primary target is also proven local (persistent primary target: MutationNotAllowed); aliased DML targets and user-defined table types stay denied",
                        "TRUNCATE TABLE #temp (unambiguous local temp only)",
                        "ALTER TABLE #temp ADD/DROP COLUMN and local CHECK/DEFAULT/NULL/UNIQUE constraints"],
                    ["postgres"] = [
                        "EXPLAIN over a safe SELECT (plan-only, read-only)",
                        "EXPLAIN ANALYZE with full inner-statement effect analysis",
                        "SELECT INTO TEMP TABLE with unambiguous single-part name",
                        "TRUNCATE [ONLY] of proven current-session temps only (single-part name; not ON COMMIT DROP; persistent, mixed, CASCADE, RESTART IDENTITY, and schema-qualified targets stay denied)",
                        "Session-temp proof for temp DML, DROP TABLE, CREATE INDEX, and TRUNCATE targets: name-based, assumes the default search_path (pg_temp first) for unqualified names; the name must be quoted or all-ASCII-unquoted and at most 63 UTF-8 bytes where declared and where used, else never proven (quote or shorten); DROP TABLE of a name unknown offline revokes every proof; see AGENTS.md"],
                },
                ["artifactRead"] = new { sections = ArtifactReader.SupportedSections, manifestVersion = ArtifactReader.CurrentManifestVersion, maxReportBytes = ArtifactReader.MaxReportBytes },
                ["watchNdjson"] = new { events = new[] { "started", "changed", "completed", "failed" }, schemaVersion = WatchNdjsonWriter.SchemaVersion, sequence = "strictly increasing from started; exactly one terminal record", history = "no retention: every change is emitted immediately" },
            },
            ["text", "json", "json-summary", "agent"],
            new SqlHarnessSafetyAnalysis(
                SqlSafetyAnalysis.AnalysisKind,
                SqlSafetyAnalysis.ContractVersion,
                SqlSafetyAnalysis.HiddenEffectsVerified,
                SqlSafetyAnalysis.ObjectAndPermissionStatus));
    }
}
