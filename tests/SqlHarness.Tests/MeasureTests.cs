using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public class SqlHarnessMeasureTests
{
    private const string Token = "measure-access-token-never-emit";
    private const string Plan = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Table=\"[Clients]\" /></IndexScan></RelOp></BatchSequence></ShowPlanXML>";

    [Fact]
    public async Task Measure_runs_setup_warmup_and_measured_queries_in_order_on_one_session()
    {
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure(repeat: 3));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(["setup", "warmup", "query-1", "query-2", "query-3"], session.Labels);
        Assert.Equal(1, session.FactoryOpenCount);
        Assert.All(session.Commands, command => Assert.DoesNotContain("DBCC", command.Sql, StringComparison.OrdinalIgnoreCase));
        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.Equal(3, report.Repetitions);
        Assert.Equal(3, report.MeasuredRunCount);
    }

    [Fact]
    public async Task Measure_without_setup_starts_with_warmup()
    {
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure(1) with { SetupSql = null });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(["warmup", "query-1"], session.Labels);
    }

    [Fact]
    public async Task Measure_reports_measured_only_distributions_and_result_stability()
    {
        var session = FakeMeasureSession.Create(changeLastResult: true);

        var outcome = await Module(session).ExecuteAsync(Measure(3));

        var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.False(report.ResultsStable);
        Assert.Equal(new CompareDistribution(10, 20, 30), report.Query.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(12, 22, 32), report.Query.ElapsedTimeMilliseconds);
        Assert.Equal(new CompareDistribution(5, 10, 15), report.Query.LogicalReads);
        Assert.Equal(30, report.Query.TotalLogicalReadsByTable["Clients"]);
        Assert.Contains(report.Query.Operators, op => op is { NodeId: 1, PhysicalOp: "Index Seek" });
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(301, 1)]
    [InlineData(30, 0)]
    [InlineData(30, 101)]
    public async Task Measure_validates_bounds_before_authentication(int timeout, int repeat)
    {
        var session = FakeMeasureSession.Create();
        var azure = new FakeAzureCli();

        var outcome = await Module(session, azure).ExecuteAsync(Measure(repeat) with { TimeoutSeconds = timeout });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
    }

    [Fact]
    public async Task Measure_loads_profiles_exactly_once()
    {
        var loads = 0;
        var outcome = await Module(FakeMeasureSession.Create(), loadProfiles: () =>
        {
            loads++;
            return Profiles();
        }).ExecuteAsync(Measure(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, loads);
    }

    [Theory]
    [InlineData("DELETE dbo.Clients", "SELECT Value FROM dbo.Clients")]
    [InlineData("SELECT Id INTO #ids FROM dbo.Clients", "UPDATE dbo.Clients SET Value = 1")]
    public async Task Measure_classifies_all_SQL_before_authentication(string? setup, string query)
    {
        var session = FakeMeasureSession.Create();
        var azure = new FakeAzureCli();

        var outcome = await Module(session, azure).ExecuteAsync(Measure(1) with { SetupSql = setup, QuerySql = query });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
    }

    [Fact]
    public async Task Measure_parameter_matching_uses_case_insensitive_union_of_setup_and_query()
    {
        var session = FakeMeasureSession.Create();
        var operation = Measure(1) with
        {
            SetupSql = "SELECT @clientid AS Id INTO #ids FROM dbo.Clients",
            Parameters = ["CLIENTID:int=42"],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.All(session.Commands.Where(command => command.Sql == operation.SetupSql || command.Sql == operation.QuerySql),
            command => Assert.Equal("@CLIENTID", Assert.Single(command.Parameters).Name));
    }

    [Fact]
    public async Task Measure_rejects_unused_parameter_before_authentication()
    {
        var azure = new FakeAzureCli();
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session, azure).ExecuteAsync(Measure(1) with { Parameters = ["ClinetId:int=42"] });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
    }

    [Fact]
    public async Task Measure_writes_only_measured_runs_to_artifacts()
    {
        var writer = new CapturingArtifactWriter();

        var outcome = await Module(FakeMeasureSession.Create(), writer: writer).ExecuteAsync(Measure(2));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal("measure-artifacts", Assert.IsType<SqlHarnessMeasureReport>(outcome.Report).ArtifactDirectory);
        Assert.Equal([1, 2], writer.Runs.Select(run => run.Repetition));
        Assert.All(writer.Runs, run => Assert.Equal("measure", run.Variant));
    }

    [Fact]
    public async Task Measure_maps_artifact_failure_to_local_storage()
    {
        var outcome = await Module(FakeMeasureSession.Create(), writer: new ThrowingArtifactWriter())
            .ExecuteAsync(Measure(1));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Contains("disk unavailable", outcome.SafeError);
    }

    [Fact]
    public async Task Measure_defers_measure_gain_receipt_and_full_raw_includes_setup_warmup_and_measured_runs()
    {
        var leanGain = new FakeGainStore();
        var richGain = new FakeGainStore();
        var lean = await Module(FakeMeasureSession.Create(), gain: leanGain).ExecuteAsync(Measure(1));
        var rich = await Module(FakeMeasureSession.Create(includeSetupResult: true, includeExtraMessage: true), gain: richGain)
            .ExecuteAsync(Measure(1));

        Assert.Empty(richGain.Records);
        await Assert.IsType<SqlHarnessEmissionReceipt>(lean.EmissionReceipt).CompleteAsync(new OutputFootprint(1, 1));
        await Assert.IsType<SqlHarnessEmissionReceipt>(rich.EmissionReceipt).CompleteAsync(new OutputFootprint(80, 4));

        var record = Assert.Single(richGain.Records);
        Assert.Equal("measure", record.Command);
        Assert.Equal(80, record.EmittedBytes);
        Assert.True(record.RawBytes > Assert.Single(leanGain.Records).RawBytes);
    }

    [Fact]
    public async Task Measure_partial_failure_receipt_preserves_prior_raw_snapshot()
    {
        var gain = new FakeGainStore();
        var outcome = await Module(FakeMeasureSession.Create(includeSetupResult: true, failOnQueryNumber: 2), gain: gain)
            .ExecuteAsync(Measure(2));

        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new OutputFootprint(1, 1));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.True(Assert.Single(gain.Records).RawBytes > 0);
    }

    [Fact]
    public async Task Measure_partial_query_failure_retains_message_emitted_before_failure_exactly_once()
    {
        var gain = new FakeGainStore();
        var session = FakeMeasureSession.Create(failOnQueryNumber: 2, emitMessageBeforeQueryFailure: true);
        var outcome = await Module(session, gain: gain).ExecuteAsync(Measure(1) with { SetupSql = null });

        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new OutputFootprint(1, 1));

        using var expected = new CanonicalResultAccumulator();
        AddResult(expected, "Value", "System.Int32", 42);
        expected.AddMessage("planXml", Plan);
        expected.AddMessage("sql", StatisticsMessage(5, 10, 12));
        expected.AddMessage("sql", "diagnostic before query failure");
        var footprint = expected.SnapshotFootprint();
        var record = Assert.Single(gain.Records);
        Assert.Equal(footprint.Bytes, record.RawBytes);
        Assert.Equal(footprint.Lines, record.RawLines);
    }

    [Fact]
    public async Task Measure_setup_failure_retains_message_emitted_before_failure_exactly_once()
    {
        var gain = new FakeGainStore();
        var session = FakeMeasureSession.Create(failSetup: true);
        var outcome = await Module(session, gain: gain).ExecuteAsync(Measure(1));

        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new OutputFootprint(1, 1));

        using var expected = new CanonicalResultAccumulator();
        expected.AddMessage("sql", "diagnostic before setup failure");
        var footprint = expected.SnapshotFootprint();
        var record = Assert.Single(gain.Records);
        Assert.Equal(footprint.Bytes, record.RawBytes);
        Assert.Equal(footprint.Lines, record.RawLines);
    }

    [Fact]
    public async Task Measure_cancellation_uses_bounded_caller_independent_cleanup()
    {
        using var callerCancellation = new CancellationTokenSource();
        var session = FakeMeasureSession.Create(cancelOnQuery: callerCancellation);

        var outcome = await Module(session).ExecuteAsync(Measure(1), callerCancellation.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        var cleanupToken = Assert.Single(session.StatisticsCleanupTokens);
        Assert.True(cleanupToken.CanBeCanceled);
        Assert.False(cleanupToken.IsCancellationRequested);
        Assert.NotEqual(callerCancellation.Token, cleanupToken);
    }

    private static SqlHarnessMeasureOperation Measure(int repeat) =>
        new(Target(), "SELECT Id INTO #ids FROM dbo.Clients", "SELECT Value FROM dbo.Clients", [], 30, repeat);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private static void AddResult(CanonicalResultAccumulator accumulator, string name, string dataType, object value)
    {
        accumulator.BeginResultSet([new CanonicalColumn(0, name, dataType, false)]);
        accumulator.AddRow([value]);
        accumulator.EndResultSet();
    }

    private static string StatisticsMessage(int reads, int cpu, int elapsed) =>
        $"Table 'Clients'. Scan count 1, logical reads {reads}, physical reads 0, lob logical reads 0.\nSQL Server Execution Times: CPU time = {cpu} ms, elapsed time = {elapsed} ms.";

    private static SqlHarnessModule Module(
        FakeMeasureSession session,
        FakeAzureCli? azure = null,
        FakeGainStore? gain = null,
        ICompareArtifactWriter? writer = null,
        Func<IReadOnlyDictionary<string, TargetProfile>>? loadProfiles = null) =>
        new(session, gain ?? new FakeGainStore(), writer ?? new CapturingArtifactWriter(), loadProfiles ?? Profiles);

    private sealed class FakeAzureCli : IAzureCli
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<JsonElement> RunJsonAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args.ToArray());
            using var document = JsonDocument.Parse($"{{\"accessToken\":\"{Token}\"}}");
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class FakeGainStore : IGainStore
    {
        public List<GainRecord> Records { get; } = [];
        public void Append(GainRecord record) => Records.Add(record);
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class CapturingArtifactWriter : ICompareArtifactWriter
    {
        public IReadOnlyList<CompareRunArtifact> Runs { get; private set; } = [];
        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
        {
            Runs = runs.ToArray();
            return "measure-artifacts";
        }
    }

    private sealed class ThrowingArtifactWriter : ICompareArtifactWriter
    {
        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target) =>
            throw new IOException("disk unavailable");
    }

    private sealed class FakeMeasureSession : ISqlSessionFactory, ISqlSession
    {
        private readonly bool _changeLastResult;
        private readonly bool _includeSetupResult;
        private readonly bool _includeExtraMessage;
        private readonly int? _failOnQueryNumber;
        private readonly CancellationTokenSource? _cancelOnQuery;
        private readonly bool _emitMessageBeforeQueryFailure;
        private readonly bool _failSetup;
        private readonly List<string> _messages = [];
        private int _queryCount;

        public List<string> Labels { get; } = [];
        public List<SqlExecutionCommand> Commands { get; } = [];
        public int FactoryOpenCount { get; private set; }
        public List<CancellationToken> StatisticsCleanupTokens { get; } = [];
        public IReadOnlyList<string> Messages => _messages;
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        private FakeMeasureSession(bool changeLastResult, bool includeSetupResult, bool includeExtraMessage, int? failOnQueryNumber, CancellationTokenSource? cancelOnQuery, bool emitMessageBeforeQueryFailure, bool failSetup)
        {
            _changeLastResult = changeLastResult;
            _includeSetupResult = includeSetupResult;
            _includeExtraMessage = includeExtraMessage;
            _failOnQueryNumber = failOnQueryNumber;
            _cancelOnQuery = cancelOnQuery;
            _emitMessageBeforeQueryFailure = emitMessageBeforeQueryFailure;
            _failSetup = failSetup;
        }

        public static FakeMeasureSession Create(bool changeLastResult = false, bool includeSetupResult = false, bool includeExtraMessage = false, int? failOnQueryNumber = null, CancellationTokenSource? cancelOnQuery = null, bool emitMessageBeforeQueryFailure = false, bool failSetup = false) =>
            new(changeLastResult, includeSetupResult, includeExtraMessage, failOnQueryNumber, cancelOnQuery, emitMessageBeforeQueryFailure, failSetup);

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            FactoryOpenCount++;
            return Task.FromResult<ISqlSession>(this);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.Sql.Contains("STATISTICS IO ON", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            if (command.Sql.Contains("STATISTICS IO OFF", StringComparison.Ordinal))
            {
                StatisticsCleanupTokens.Add(ct);
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }
            if (command.Sql.Contains("INTO #ids", StringComparison.Ordinal))
            {
                Labels.Add("setup");
                if (_failSetup)
                {
                    _messages.Add("diagnostic before setup failure");
                    return Task.FromException<ISqlReader>(new TimeoutException("setup failed"));
                }
                return Task.FromResult<ISqlReader>(_includeSetupResult ? FakeReader.Single(["SetupValue"], ["setup-result-value"]) : FakeReader.Empty());
            }

            if (_cancelOnQuery is not null)
            {
                _cancelOnQuery.Cancel();
                return Task.FromException<ISqlReader>(new OperationCanceledException(ct));
            }

            var queryNumber = ++_queryCount;
            if (_failOnQueryNumber == queryNumber)
            {
                if (_emitMessageBeforeQueryFailure)
                    _messages.Add("diagnostic before query failure");
                return Task.FromException<ISqlReader>(new TimeoutException("measured run failed"));
            }
            Labels.Add(queryNumber == 1 ? "warmup" : $"query-{queryNumber - 1}");
            var measured = Math.Max(queryNumber - 1, 1);
            _messages.Add(StatisticsMessage(measured * 5, measured * 10, measured * 10 + 2));
            if (_includeExtraMessage)
                _messages.Add("ordinary diagnostic message");
            var value = _changeLastResult && queryNumber == 4 ? 43 : 42;
            return Task.FromResult<ISqlReader>(FakeReader.WithPlan(["Value"], [value], Plan));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader : ISqlReader
    {
        private readonly IReadOnlyList<Result> _results;
        private int _result;
        private int _row = -1;
        private Result Current => _results[_result];
        private FakeReader(IReadOnlyList<Result> results) => _results = results;
        public int FieldCount => _results.Count == 0 ? 0 : Current.Names.Length;
        public int RecordsAffected => -1;
        public static FakeReader Empty() => new([]);
        public static FakeReader Single(string[] names, object?[] row) => new([new(names, [row])]);
        public static FakeReader WithPlan(string[] names, object?[] row, string plan) => new([new(names, [row]), new(["Microsoft SQL Server 2005 XML Showplan"], [[plan]])]);
        public string GetName(int ordinal) => Current.Names[ordinal];
        public Type GetFieldType(int ordinal) => Current.Rows[0][ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) => false;
        public object GetValue(int ordinal) => Current.Rows[_row][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(_results.Count > 0 && ++_row < Current.Rows.Length);
        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (_results.Count == 0 || ++_result >= _results.Count)
                return Task.FromResult(false);
            _row = -1;
            return Task.FromResult(true);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed record Result(string[] Names, object?[][] Rows);
    }
}