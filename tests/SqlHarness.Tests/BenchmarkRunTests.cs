using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;

namespace SqlHarness.Tests;

public sealed class BenchmarkRunTests
{
    private const string EmptyPlanHash = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExecuteAsync_returns_the_dialect_result_and_copies_parameter_set()
    {
        var plans = new[]
        {
            new ExecutionPlan([new PlanOperator(1, "Index Seek", "Clients", false, false, false)]),
            new ExecutionPlan([new PlanOperator(2, "Clustered Index Scan", "Orders", true, false, false)]),
        };
        var comparison = new CanonicalComparisonResult("schema", ["row-a", "row-b"]);
        var artifact = Artifact("<ShowPlanXML>one</ShowPlanXML>", "<ShowPlanXML>two</ShowPlanXML>");
        var dialect = new RecordingDialect
        {
            Result = new CollectedCompareRun(artifact, plans, comparison),
        };
        var session = new IdleSession();
        var parameters = new[] { new SqlHarnessParameter("@id", SqlDbType.Int, 7, null) };
        using var raw = new CanonicalResultAccumulator();
        using var cts = new CancellationTokenSource();

        var run = await Run(
            dialect,
            session,
            raw,
            sql: "SELECT Value FROM dbo.Clients WHERE Id = @id",
            parameters: parameters,
            timeoutSeconds: 33,
            repetition: 4,
            variant: "candidate",
            parameterSet: "small",
            captureComparison: true,
            comparisonMaximumRows: 17,
            cancellationToken: cts.Token);

        Assert.Equal(1, dialect.CallCount);
        var observed = dialect.Observed;
        Assert.NotNull(observed);
        Assert.Same(session, observed.Session);
        Assert.Equal("SELECT Value FROM dbo.Clients WHERE Id = @id", observed.Sql);
        Assert.Same(parameters, observed.Parameters);
        Assert.Equal(33, observed.TimeoutSeconds);
        Assert.Equal(4, observed.Repetition);
        Assert.Equal("candidate", observed.Variant);
        Assert.Same(raw, observed.Raw);
        Assert.True(observed.CaptureComparison);
        Assert.Equal(17, observed.ComparisonMaximumRows);
        Assert.Equal(cts.Token, observed.CancellationToken);
        Assert.Equal(artifact with { ParameterSet = "small" }, run.Artifact);
        Assert.Equal("small", run.Artifact.ParameterSet);
        Assert.Same(plans, run.Plans);
        Assert.Same(comparison, run.Comparison);
        Assert.Equal(0, session.ReaderCalls);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(run.Artifact, WebJson));
        Assert.Equal("small", json.RootElement.GetProperty("parameterSet").GetString());
    }

    [Fact]
    public async Task Plan_hashes_are_uppercase_sha256_of_exact_plan_documents()
    {
        var same = "<ShowPlanXML>same</ShowPlanXML>";
        var distinct = "<ShowPlanXML>other α</ShowPlanXML>";
        var untrimmed = same + " ";
        var documents = new[] { "", same, same, distinct, untrimmed };
        var dialect = Dialect(Artifact(documents));
        using var raw = new CanonicalResultAccumulator();

        var run = await Run(dialect, new IdleSession(), raw, parameterSet: "wide");

        var expected = documents.Select(HashPlan).ToArray();
        Assert.Equal(expected, run.PlanHashes);
        Assert.Equal(EmptyPlanHash, run.PlanHashes[0]);
        Assert.Equal(run.PlanHashes[1], run.PlanHashes[2]);
        Assert.NotEqual(run.PlanHashes[1], run.PlanHashes[3]);
        Assert.NotEqual(run.PlanHashes[1], run.PlanHashes[4]);
        Assert.All(run.PlanHashes, hash =>
        {
            Assert.Equal(64, hash.Length);
            Assert.Matches("^[0-9A-F]{64}$", hash);
            Assert.DoesNotContain("0x", hash, StringComparison.OrdinalIgnoreCase);
        });

        using var againRaw = new CanonicalResultAccumulator();
        var again = await Run(Dialect(Artifact(same)), new IdleSession(), againRaw);
        Assert.Equal(run.PlanHashes[1], Assert.Single(again.PlanHashes));
    }

    [Fact]
    public async Task Null_parameter_set_does_not_change_serialized_run_json()
    {
        var plan = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "distiller-sample.sqlplan"));
        var artifact = Artifact(plan);
        var dialect = Dialect(artifact);
        using var raw = new CanonicalResultAccumulator();

        var run = await Run(dialect, new IdleSession(), raw, parameterSet: null, captureComparison: false);

        Assert.Null(run.Artifact.ParameterSet);
        Assert.Equal(artifact, run.Artifact);
        Assert.False(dialect.Observed!.CaptureComparison);
        Assert.Equal(JsonSerializer.Serialize(artifact, WebJson), JsonSerializer.Serialize(run.Artifact, WebJson));
        Assert.Equal(JsonSerializer.Serialize(artifact), JsonSerializer.Serialize(run.Artifact));
        Assert.DoesNotContain("parameterSet", JsonSerializer.Serialize(run.Artifact, WebJson), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ParameterSet", JsonSerializer.Serialize(run.Artifact), StringComparison.Ordinal);

        using var temp = new TempDirectory();
        var writer = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var report = new SqlHarnessMeasureReport(
            new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile"),
            1, 1, true, Variant("measure"), null);
        var baselineDirectory = writer.Write(report, [artifact], "wind");
        var labeledDirectory = writer.Write(report, [run.Artifact], "wind");
        var baselineRuns = File.ReadAllText(Path.Combine(baselineDirectory, "runs.jsonl"));
        var labeledRuns = File.ReadAllText(Path.Combine(labeledDirectory, "runs.jsonl"));
        Assert.Equal(baselineRuns, labeledRuns);
        Assert.DoesNotContain("parameterSet", labeledRuns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ParameterSet", labeledRuns, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_propagates_from_the_dialect()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await AssertPropagates(new OperationCanceledException(cts.Token), cts.Token);
    }

    [Fact]
    public async Task Primary_exception_propagates_from_the_dialect()
    {
        await AssertPropagates(new TimeoutException("measured run failed"), CancellationToken.None);
    }

    private static async Task AssertPropagates(Exception failure, CancellationToken cancellationToken)
    {
        var dialect = new RecordingDialect
        {
            Result = new CollectedCompareRun(Artifact("<unused/>"), [], BenchmarkCollector.EmptyComparison),
            Failure = failure,
        };
        var session = new IdleSession();
        using var raw = new CanonicalResultAccumulator();

        var exception = await Assert.ThrowsAsync(failure.GetType(), () =>
            Run(dialect, session, raw, cancellationToken: cancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(1, dialect.CallCount);
        Assert.Equal(0, session.ReaderCalls);
    }

    private static Task<CollectedBenchmarkRun> Run(
        RecordingDialect dialect,
        ISqlSession session,
        CanonicalResultAccumulator raw,
        string sql = "SELECT Value FROM dbo.Clients",
        IReadOnlyList<SqlHarnessParameter>? parameters = null,
        int timeoutSeconds = 30,
        int repetition = 2,
        string variant = "measure",
        string? parameterSet = null,
        bool captureComparison = false,
        int comparisonMaximumRows = 17,
        CancellationToken cancellationToken = default) =>
        BenchmarkRunner.ExecuteAsync(
            dialect,
            session,
            sql,
            parameters ?? [],
            timeoutSeconds,
            repetition,
            variant,
            parameterSet,
            raw,
            captureComparison,
            comparisonMaximumRows,
            cancellationToken);

    private static RecordingDialect Dialect(CompareRunArtifact artifact) =>
        new()
        {
            Result = new CollectedCompareRun(artifact, [], BenchmarkCollector.EmptyComparison),
        };

    private static CompareRunArtifact Artifact(params string[] plans) =>
        new("measure", 2, 10, 12, 5, new Dictionary<string, long> { ["Clients"] = 5 }, "RESULT-HASH", plans, 3);

    private static string HashPlan(string document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document)));

    private static CompareVariantReport Variant(string name) => new(
        name,
        new CompareDistribution(1, 2, 3),
        new CompareDistribution(2, 3, 4),
        new CompareDistribution(3, 4, 5),
        new Dictionary<string, long> { ["Clients"] = 4 },
        [new CompareOperatorReport(1, "Index Seek", "Clients", false, false, false)],
        []);

    private sealed class RecordingDialect : ISqlDialect
    {
        internal required CollectedCompareRun Result { get; init; }
        internal Exception? Failure { get; init; }
        internal int CallCount { get; private set; }
        internal BenchmarkCall? Observed { get; private set; }

        public Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
            ISqlSession session,
            string sql,
            IReadOnlyList<SqlHarnessParameter> parameters,
            int timeoutSeconds,
            int repetition,
            string variant,
            CanonicalResultAccumulator raw,
            bool captureComparison,
            int comparisonMaximumRows,
            CancellationToken ct)
        {
            CallCount++;
            Observed = new BenchmarkCall(
                session,
                sql,
                parameters,
                timeoutSeconds,
                repetition,
                variant,
                raw,
                captureComparison,
                comparisonMaximumRows,
                ct);
            if (Failure is not null)
                return Task.FromException<CollectedCompareRun>(Failure);
            return Task.FromResult(Result);
        }

        public SqlEngine Engine => throw Unused();
        public string IdentitySql => throw Unused();
        public string PingSql => throw Unused();
        public string CountsCatalogSql => throw Unused();
        public string SchemaSql => throw Unused();
        public string SpaceSql => throw Unused();
        public SqlSafetyDecision Classify(string sql, SqlUsage usage, string? database, bool allowMutation, string? confirmDatabase, IReadOnlySet<string> sessionTempTables) => throw Unused();
        public IReadOnlySet<string> CollectSessionTempTables(string sql) => throw Unused();
        public IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs) => throw Unused();
        public void ValidateMeasuredBatch(string sql) => throw Unused();
        public DistilledPlan DistillPlan(string document) => throw Unused();
        public string BuildCountsExactSql(IReadOnlyList<ResolvedCountObject> objects) => throw Unused();

        private static NotSupportedException Unused() =>
            new("BenchmarkRunner must delegate execution, not call this member.");
    }

    private sealed record BenchmarkCall(
        ISqlSession Session,
        string Sql,
        IReadOnlyList<SqlHarnessParameter> Parameters,
        int TimeoutSeconds,
        int Repetition,
        string Variant,
        CanonicalResultAccumulator Raw,
        bool CaptureComparison,
        int ComparisonMaximumRows,
        CancellationToken CancellationToken);

    private sealed class IdleSession : ISqlSession
    {
        public int ReaderCalls { get; private set; }
        public IReadOnlyList<string> Messages => throw new NotSupportedException("BenchmarkRunner must not read session messages.");
        public SqlHarnessTargetIdentityReport Identity
        {
            get => throw new NotSupportedException("BenchmarkRunner must not read session identity.");
            set => throw new NotSupportedException("BenchmarkRunner must not set session identity.");
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            ReaderCalls++;
            throw new NotSupportedException("BenchmarkRunner must not execute SQL.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-benchmark-run-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}