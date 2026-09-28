using System.Data;
using System.Globalization;
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
    public async Task Plan_hashes_identify_the_operator_tree_not_runtime_values()
    {
        var seek = Showplan(planHash: null, physicalOp: "Index Seek", runtimeValue: "1", actualRows: "3");
        var seekNoisy = Showplan(planHash: null, physicalOp: "Index Seek", runtimeValue: "secret-value", actualRows: "400");
        var scan = Showplan(planHash: null, physicalOp: "Table Scan", runtimeValue: "1", actualRows: "3");
        var pretty = seek.Replace("><", ">\n<", StringComparison.Ordinal);
        var hashed = Showplan(planHash: "0xAA", physicalOp: "Index Seek", runtimeValue: "1", actualRows: "3");
        var hashedNoisy = Showplan(planHash: "0xAA", physicalOp: "Table Scan", runtimeValue: "secret-value", actualRows: "400");
        var otherHash = Showplan(planHash: "0xBB", physicalOp: "Index Seek", runtimeValue: "1", actualRows: "3");
        var bothHashes = TwoPlanHashes("0xAA", "0xBB");
        var repeatedHash = TwoPlanHashes("0xAA", "0xAA");
        var postgres = PostgresPlan("Seq Scan", "secret-value");
        var postgresNoisy = PostgresPlan("Seq Scan", "other-secret", actualRows: 50, buffers: 9, planningTime: 80);
        var postgresReordered = PostgresPlan("Seq Scan", "secret-value", reorder: true);
        var postgresTree = PostgresPlan("Index Scan", "secret-value");
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "distiller-sample.sqlplan"));
        var fixtureRows = fixture.Replace("ActualRows=\"3\"", "ActualRows=\"30\"", StringComparison.Ordinal)
            .Replace("ActualRows=\"4\"", "ActualRows=\"40\"", StringComparison.Ordinal);
        var fixtureTree = fixture.Replace("PhysicalOp=\"Index Seek\"", "PhysicalOp=\"Clustered Index Scan\"", StringComparison.Ordinal);
        var explain = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "seq-scan.explain.json"));
        var explainNoisy = explain.Replace("\"Actual Rows\":100", "\"Actual Rows\":1", StringComparison.Ordinal)
            .Replace("\"Actual Rows\":10", "\"Actual Rows\":999", StringComparison.Ordinal)
            .Replace("\"Shared Hit Blocks\":2", "\"Shared Hit Blocks\":50", StringComparison.Ordinal)
            .Replace("\"Planning Time\":10.4", "\"Planning Time\":1", StringComparison.Ordinal)
            .Replace("\"Execution Time\":4.6", "\"Execution Time\":80", StringComparison.Ordinal);
        var explainTree = explain.Replace("\"Node Type\":\"Seq Scan\"", "\"Node Type\":\"Index Scan\"", StringComparison.Ordinal);
        var documents = new[]
        {
            "",
            seek,
            seekNoisy,
            pretty + " \n",
            scan,
            hashed,
            hashedNoisy,
            otherHash,
            bothHashes,
            repeatedHash,
            postgres,
            postgresNoisy,
            postgresReordered,
            postgresTree,
            fixture,
            fixtureRows,
            fixtureTree,
            explain,
            explainNoisy,
            explainTree,
            "not-a-plan α",
        };
        using var raw = new CanonicalResultAccumulator();

        var run = await Run(Dialect(Artifact(documents)), new IdleSession(), raw, parameterSet: "wide");

        Assert.Equal(documents.Select(PlanIdentity.Hash), run.PlanHashes);
        Assert.Equal(EmptyPlanHash, run.PlanHashes[0]);
        Assert.Equal(run.PlanHashes[1], run.PlanHashes[2]);
        Assert.Equal(run.PlanHashes[1], run.PlanHashes[3]);
        Assert.NotEqual(run.PlanHashes[1], run.PlanHashes[4]);
        Assert.Equal(Sha256("0xAA"), run.PlanHashes[5]);
        Assert.Equal(run.PlanHashes[5], run.PlanHashes[6]);
        Assert.NotEqual(run.PlanHashes[5], run.PlanHashes[7]);
        Assert.Equal(Sha256("0xAA\n0xBB"), run.PlanHashes[8]);
        Assert.NotEqual(run.PlanHashes[8], run.PlanHashes[9]);
        Assert.Equal(run.PlanHashes[10], run.PlanHashes[11]);
        Assert.Equal(run.PlanHashes[10], run.PlanHashes[12]);
        Assert.NotEqual(run.PlanHashes[10], run.PlanHashes[13]);
        Assert.Equal(run.PlanHashes[14], run.PlanHashes[15]);
        Assert.NotEqual(run.PlanHashes[14], run.PlanHashes[16]);
        Assert.Equal(run.PlanHashes[17], run.PlanHashes[18]);
        Assert.NotEqual(run.PlanHashes[17], run.PlanHashes[19]);
        Assert.Equal(Sha256("not-a-plan α"), run.PlanHashes[20]);
        Assert.All(run.PlanHashes, hash =>
        {
            Assert.Equal(64, hash.Length);
            Assert.Matches("^[0-9A-F]{64}$", hash);
            Assert.DoesNotContain("0x", hash, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-value", hash, StringComparison.Ordinal);
            Assert.DoesNotContain("other-secret", hash, StringComparison.Ordinal);
        });
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

    [Fact]
    public async Task ExecuteRawAsync_preserves_the_primary_failure_when_cleanup_fails()
    {
        var primary = new TimeoutException("setup failed");
        var session = new FlakyMessagesSession(primary);
        using var raw = new CanonicalResultAccumulator();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            BenchmarkRunner.ExecuteRawAsync(
                session,
                new SqlExecutionCommand("SELECT 1", [], 30),
                raw,
                CancellationToken.None));

        Assert.Same(primary, exception);
    }

    [Fact]
    public async Task ExecuteRawAsync_propagates_cleanup_failure_without_a_primary_failure()
    {
        var cleanup = new InvalidOperationException("cleanup messages failed");
        var session = new MessagesFailingSession(cleanup);
        using var raw = new CanonicalResultAccumulator();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunner.ExecuteRawAsync(
                session,
                new SqlExecutionCommand("SELECT 1", [], 30),
                raw,
                CancellationToken.None));

        Assert.Same(cleanup, exception);
    }

    private sealed class FlakyMessagesSession(Exception primary) : ISqlSession
    {
        private bool _failed;
        public IReadOnlyList<string> Messages => _failed
            ? throw new InvalidOperationException("cleanup messages failed")
            : Array.Empty<string>();
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("s", "d", "s", "d", "profile");

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            _failed = true;
            return Task.FromException<ISqlReader>(primary);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MessagesFailingSession(Exception cleanup) : ISqlSession
    {
        private int _reads;
        public IReadOnlyList<string> Messages => ++_reads == 1
            ? Array.Empty<string>()
            : throw cleanup;
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("s", "d", "s", "d", "profile");

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct) =>
            Task.FromResult<ISqlReader>(new EmptyReader());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyReader : ISqlReader
    {
        public int FieldCount => 0;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => throw new NotSupportedException();
        public Type GetFieldType(int ordinal) => throw new NotSupportedException();
        public bool GetAllowNull(int ordinal) => throw new NotSupportedException();
        public object GetValue(int ordinal) => throw new NotSupportedException();
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Showplan(string? planHash, string physicalOp, string runtimeValue, string actualRows)
    {
        var hashAttribute = planHash is null ? string.Empty : $" QueryPlanHash=\"{planHash}\"";
        return $"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT @id"><QueryPlan{hashAttribute}><ParameterList><ColumnReference Column="@id" ParameterCompiledValue="(1)" ParameterRuntimeValue="({runtimeValue})" /></ParameterList><QueryTimeStats CpuTime="{actualRows}" ElapsedTime="{runtimeValue.Length}" /><RelOp NodeId="0" PhysicalOp="{physicalOp}" EstimateRows="1" ActualRows="{actualRows}" ActualElapsedms="{actualRows}" ActualCPUms="4"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="{actualRows}" ActualElapsedms="9" ActualCPUms="4" /></RunTimeInformation><IndexScan><Object Table="[dbo].[Orders]" /><ColumnReference Column="@id" ParameterRuntimeValue="({runtimeValue})" /></IndexScan></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
            """;
    }

    private static string TwoPlanHashes(string first, string second) =>
        $"""<ShowPlanXML><QueryPlan QueryPlanHash="{first}" /><QueryPlan QueryPlanHash="{second}"><RelOp PhysicalOp="Nested Loops" /></QueryPlan></ShowPlanXML>""";

    private static string PostgresPlan(
        string nodeType,
        string secret,
        int actualRows = 1,
        int buffers = 2,
        double planningTime = 1.25,
        bool reorder = false)
    {
        var fields = reorder
            ? $"\"Shared Hit Blocks\":{buffers},\"Plan Rows\":10,\"Actual Rows\":{actualRows},\"Relation Name\":\"foo\",\"Node Type\":\"{nodeType}\",\"Actual Total Time\":3.5,\"Filter\":\"(id = 7)\""
            : $"\"Node Type\":\"{nodeType}\",\"Relation Name\":\"foo\",\"Plan Rows\":10,\"Actual Rows\":{actualRows},\"Actual Total Time\":3.5,\"Shared Hit Blocks\":{buffers},\"Filter\":\"(id = 7)\",\"Actual Label\":\"{secret}\"";
        return "[{\"Planning Time\":" + planningTime.ToString(CultureInfo.InvariantCulture)
            + ",\"Execution Time\":9.5,\"Plan\":{" + fields + "}}]";
    }

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
        public string PingSql => throw Unused();
        public string CountsCatalogSql => throw Unused();
        public string SchemaSql => throw Unused();
        public string SpaceSql => throw Unused();
        public SqlSafetyDecision Classify(string sql, SqlUsage usage, string? database, bool allowMutation, string? confirmDatabase, IReadOnlySet<string> sessionTempTables) => throw Unused();
        public IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs) => throw Unused();
        public void ValidateParameterReferences(IReadOnlyList<SqlHarnessParameter> parameters, params string?[] batches) => throw Unused();
        public void ValidateMeasuredBatch(string sql) => throw Unused();
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