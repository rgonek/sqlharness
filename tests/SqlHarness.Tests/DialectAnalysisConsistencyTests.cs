using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

/// <summary>
/// T1 consolidation lock: the same SQL evaluated as query, compare-setup, and measured
/// batch must differ only by mode contract. Both engines share the table shape; engine
/// rows differ only where the benchmark contract differs (SQL Server measures any batch,
/// PostgreSQL measures one EXPLAIN-able SELECT).
/// </summary>
public class DialectAnalysisConsistencyTests
{
    private static readonly IReadOnlySet<string> NoTemps =
        new HashSet<string>(StringComparer.Ordinal);

    private sealed record ModeRow(
        string Sql,
        bool QueryAllowed,
        SqlSafetyReason QueryReason,
        bool SetupAllowed,
        SqlSafetyReason SetupReason,
        bool MeasuredBatchPasses);

    private static readonly IReadOnlyList<ModeRow> SqlServerRows =
    [
        new("SELECT 1", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("SELECT a, b FROM t WHERE a = @p", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("WITH c AS (SELECT 1 AS x) SELECT x FROM c", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("SELECT 1; SELECT 2", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("SELECT TOP 10 a FROM dbo.t WITH (NOLOCK) OPTION (MAXDOP 1)", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("SELECT ISNULL(a, 0), ROW_NUMBER() OVER (ORDER BY a) FROM dbo.t", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("CREATE TABLE #t (a int); INSERT INTO #t (a) VALUES (1); SELECT a FROM #t", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("SELECT a INTO #t FROM s", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("SELECT a INTO dbo.t FROM s", false, SqlSafetyReason.SelectIntoNotAllowed, false, SqlSafetyReason.NonTemporaryWrite, true),
        new("INSERT INTO t (a) VALUES (1)", false, SqlSafetyReason.MutationNotAllowed, false, SqlSafetyReason.NonTemporaryWrite, true),
        new("SELECT NEXT VALUE FOR dbo.seq", false, SqlSafetyReason.UnsupportedStatement, false, SqlSafetyReason.UnsupportedStatement, true),
        new("SELECT * FROM OPENROWSET('x', 'y', 'z')", false, SqlSafetyReason.UnsupportedStatement, false, SqlSafetyReason.UnsupportedStatement, true),
    ];

    private static readonly IReadOnlyList<ModeRow> PostgresRows =
    [
        new("SELECT 1", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("SELECT a, b FROM t WHERE a = @p", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("WITH c AS (SELECT 1 AS x) SELECT x FROM c", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, true),
        new("SELECT 1; SELECT 2", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, false),
        new("CREATE TEMP TABLE t (a int); INSERT INTO t (a) VALUES (1); SELECT a FROM t", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, false),
        new("SELECT a INTO TEMP TABLE t FROM s", true, SqlSafetyReason.Allowed, true, SqlSafetyReason.Allowed, false),
        new("INSERT INTO t (a) VALUES (1)", false, SqlSafetyReason.MutationNotAllowed, false, SqlSafetyReason.NonTemporaryWrite, false),
        new("SELECT pg_sleep(1)", false, SqlSafetyReason.UnsupportedStatement, false, SqlSafetyReason.UnsupportedStatement, true),
    ];

    [Fact]
    public void Sql_server_modes_differ_only_by_contract()
    {
        foreach (var row in SqlServerRows)
            AssertModes(SqlEngine.SqlServer, row);
    }

    [Fact]
    public void Postgres_modes_differ_only_by_contract()
    {
        foreach (var row in PostgresRows)
            AssertModes(SqlEngine.Postgres, row);
    }

    [Fact]
    public void Both_engines_agree_on_referenced_parameter()
    {
        AssertParameterAgreement(SqlEngine.SqlServer, "SELECT a FROM t WHERE a = @p", referenced: true);
        AssertParameterAgreement(SqlEngine.Postgres, "SELECT a FROM t WHERE a = @p", referenced: true);
    }

    [Fact]
    public void Both_engines_reject_unreferenced_parameter()
    {
        AssertParameterAgreement(SqlEngine.SqlServer, "SELECT a FROM t", referenced: false);
        AssertParameterAgreement(SqlEngine.Postgres, "SELECT a FROM t", referenced: false);
    }

    [Fact]
    public void Core_benchmark_validation_matches_measured_batch_contract_on_sqlserver()
    {
        // SQL Server has no measured-batch shape check, so benchmark usage
        // agrees with the query classification on every row.
        AssertCoreBenchmarkAgreement("sqlserver", [
            ("SELECT 1", [], true, null),
            ("SELECT 1; SELECT 2", [], true, null),
            ("SELECT a FROM t WHERE a = @p", ["p:int=1"], true, null),
            ("DELETE FROM dbo.T;", [], false, "mutation_not_allowed"),
        ]);
    }

    [Fact]
    public void Core_benchmark_validation_matches_measured_batch_contract_on_postgres()
    {
        // PostgreSQL measures exactly one EXPLAIN-able statement, so a
        // multi-statement batch is query-allowed but benchmark-rejected.
        AssertCoreBenchmarkAgreement("postgres", [
            ("SELECT 1", [], true, null),
            ("SELECT a FROM t WHERE a = @p", ["p:int=1"], true, null),
            ("SELECT 1; SELECT 2", [], false, "benchmark_batch_not_supported"),
            ("INSERT INTO public.items (a) VALUES (1);", [], false, "mutation_not_allowed"),
        ]);
    }

    private static void AssertCoreBenchmarkAgreement(
        string engine, (string Sql, string[] Declarations, bool Allowed, string? Reason)[] rows)
    {
        var profiles = new Dictionary<string, TargetProfile>
        {
            ["test"] = new(
                "server-unused", "database-unused", new Dictionary<string, string>(),
                "sql", "user-unused", "MUST_NOT_BE_READ", Engine: engine),
        };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        foreach (var row in rows)
        {
            var report = SqlValidation.Validate(
                request, row.Sql, row.Declarations, profiles,
                new ValidationOptions(ValidationUsage.Benchmark));
            Assert.True(
                report.Allowed == row.Allowed && report.Reason == row.Reason,
                $"{engine} benchmark for '{row.Sql}': expected ({row.Allowed}, {row.Reason}) but was ({report.Allowed}, {report.Reason}).");
            Assert.False(report.Executed);
        }
    }

    private static void AssertParameterAgreement(SqlEngine engine, string sql, bool referenced)
    {
        var dialect = SqlDialects.For(engine);
        var parameters = dialect.ParseParameters(["p:int=1"]);
        if (referenced)
        {
            dialect.ValidateParameterReferences(parameters, sql);
            return;
        }

        var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
            dialect.ValidateParameterReferences(parameters, sql));
        Assert.Contains("@p", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertModes(SqlEngine engine, ModeRow row)
    {
        var dialect = SqlDialects.For(engine);

        var query = dialect.Classify(row.Sql, SqlUsage.Query, "TestDb", false, null, NoTemps);
        Assert.True(
            query.Allowed == row.QueryAllowed && query.Reason == row.QueryReason,
            $"{engine} query for '{row.Sql}': expected ({row.QueryAllowed}, {row.QueryReason}) but was ({query.Allowed}, {query.Reason}).");

        var setup = dialect.Classify(row.Sql, SqlUsage.CompareSetup, "TestDb", false, null, NoTemps);
        Assert.True(
            setup.Allowed == row.SetupAllowed && setup.Reason == row.SetupReason,
            $"{engine} setup for '{row.Sql}': expected ({row.SetupAllowed}, {row.SetupReason}) but was ({setup.Allowed}, {setup.Reason}).");

        if (row.MeasuredBatchPasses)
        {
            dialect.ValidateMeasuredBatch(row.Sql);
        }
        else
        {
            Assert.Throws<SqlHarnessSafetyException>(() => dialect.ValidateMeasuredBatch(row.Sql));
        }
    }
}
