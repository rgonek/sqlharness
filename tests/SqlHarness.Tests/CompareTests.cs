using System.Data;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public class SqlHarnessCompareTests
{
    private const string Token = "compare-access-token-never-emit";
    private const string PlanA = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Table=\"[Clients]\" /></IndexScan></RelOp></BatchSequence></ShowPlanXML>";
    private const string PlanB = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"2\" PhysicalOp=\"Hash Match\"><Warnings><SpillToTempDb /></Warnings><Hash /></RelOp></BatchSequence></ShowPlanXML>";

    [Fact]
    public async Task Compare_alternates_variants_after_one_excluded_warmup_pair_on_one_session()
    {
        var session = FakeCompareSession.Create();
        var artifacts = new CapturingArtifactWriter();

        var outcome = await Module(session, artifacts: artifacts).ExecuteAsync(Compare(repeat: 5));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(
            ["setup", "warmup-A", "warmup-B", "A", "B", "B", "A", "A", "B", "B", "A", "A", "B"],
            session.Labels);
        Assert.Equal(1, session.FactoryOpenCount);
        Assert.All(session.Commands, command => Assert.DoesNotContain("DBCC", command.Sql, StringComparison.OrdinalIgnoreCase));
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(10, report.MeasuredRunCount);
        Assert.True(report.ResultsEquivalent);
        Assert.Equal(ResultComparisonMode.Ordered, report.Equivalence.Mode);
        Assert.True(report.Equivalence.Equivalent);
        Assert.Equal("session-local", report.Classification.Setup);
        Assert.Equal("read-only", report.Classification.Baseline);
        Assert.Equal("read-only", report.Classification.Candidate);
        Assert.Equal("cell-artifacts", report.ArtifactDirectory);
        var written = Assert.IsType<SqlHarnessCompareReport>(artifacts.Report);
        Assert.Null(written.ArtifactDirectory);
        Assert.True(written.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Ordered, written.Equivalence.Mode);
        Assert.Equal(report.Classification, written.Classification);
        Assert.Equal(report.Parameters, written.Parameters);
        Assert.Equal("testdb-a", artifacts.Target);
        Assert.Equal(
            ["baseline", "candidate", "candidate", "baseline", "baseline", "candidate", "candidate", "baseline", "baseline", "candidate"],
            artifacts.Runs.Select(run => run.Variant));
    }

    [Fact]
    public async Task CompareCellRunner_repeat_two_uses_one_session_and_returns_equivalence_and_artifacts()
    {
        var session = FakeCompareSession.Create();
        var artifacts = new CapturingArtifactWriter();
        var runner = new CompareCellRunner(session, artifacts);

        var cell = await runner.RunAsync(
            new CompareCellRequest(
                new ResolvedTarget("test-server", "testdb-a", new AuthSpec(AuthStrategy.Integrated), "profile"),
                "SELECT Id INTO #ids FROM dbo.Clients",
                "SELECT Value FROM dbo.Clients",
                "SELECT Value FROM dbo.Clients -- candidate",
                [],
                30,
                2,
                ResultComparisonMode.Ordered),
            CancellationToken.None);

        Assert.Equal(
            ["setup", "warmup-A", "warmup-B", "A", "B", "B", "A"],
            session.Labels);
        Assert.Equal(1, session.FactoryOpenCount);
        Assert.True(cell.Report.ResultsEquivalent);
        Assert.True(cell.Report.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Ordered, cell.Report.Equivalence.Mode);
        Assert.Equal("cell-artifacts", cell.Report.ArtifactDirectory);
        Assert.Equal("testdb-a", artifacts.Target);
        Assert.Equal(4, cell.Runs.Count);
        Assert.Equal(
            ["baseline", "candidate", "candidate", "baseline"],
            cell.Runs.Select(run => run.Variant));
    }

    [Fact]
    public async Task Compare_reports_measured_distributions_tables_equivalence_operators_and_warnings()
    {
        var session = FakeCompareSession.Create();

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 3));

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.True(report.ResultsEquivalent);
        Assert.Equal(ResultComparisonMode.Ordered, report.Equivalence.Mode);
        Assert.True(report.Equivalence.Equivalent);
        Assert.Equal(0, report.Equivalence.DifferingPositions);
        Assert.Equal(0, report.Equivalence.BaselineOnlyCount);
        Assert.Equal(0, report.Equivalence.CandidateOnlyCount);
        Assert.Equal(new CompareDistribution(10, 20, 30), report.Baseline.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(12, 22, 32), report.Baseline.ElapsedTimeMilliseconds);
        Assert.Equal(new CompareDistribution(5, 10, 15), report.Baseline.LogicalReads);
        Assert.Equal(30, report.Baseline.TotalLogicalReadsByTable["Clients"]);
        Assert.Equal(new CompareDistribution(5, 10, 15), report.Baseline.LogicalReadsByTable["Clients"]);
        Assert.Contains(report.Baseline.Operators, op => op is { NodeId: 1, PhysicalOp: "Index Seek", Object: "Clients" });
        Assert.Empty(report.Baseline.Warnings);
        Assert.Contains(report.Candidate.Operators, op => op is { NodeId: 2, PhysicalOp: "Hash Match", HasSpill: true });
        Assert.Contains("SpillToTempDb", report.Candidate.Warnings);
    }

    [Fact]
    public async Task Compare_reports_per_table_logical_read_distributions_classifications_and_parameter_metadata()
    {
        var session = FakeCompareSession.Create(
            ioTable: "dbo.Orders",
            tableReadsForMeasured: measured => measured switch
            {
                1 => 1,
                2 => 5,
                3 => 9,
                _ => measured * 5,
            });
        var operation = Compare(repeat: 3) with
        {
            SetupSql = "SELECT Id INTO #ids FROM dbo.Clients WHERE Created <= @AsOfDate",
            BaselineSql = "SELECT Value FROM dbo.Clients WHERE AsOf <= @AsOfDate",
            CandidateSql = "SELECT Value FROM dbo.Clients WHERE AsOf <= @AsOfDate -- candidate",
            Parameters = ["AsOfDate:datetime2=2026-07-29T12:00:00"],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(
            new CompareDistribution(1, 5, 9),
            report.Baseline.LogicalReadsByTable["dbo.Orders"]);
        Assert.Equal(
            new CompareDistribution(1, 5, 9),
            report.Candidate.LogicalReadsByTable["dbo.Orders"]);
        Assert.Equal(15, report.Baseline.TotalLogicalReadsByTable["dbo.Orders"]);
        Assert.Equal("session-local", report.Classification.Setup);
        Assert.Equal("read-only", report.Classification.Baseline);
        Assert.Equal("read-only", report.Classification.Candidate);
        var parameter = Assert.Single(report.Parameters);
        Assert.Equal("@AsOfDate", parameter.Name);
        Assert.Equal("datetime2", parameter.Type);
        Assert.Null(parameter.Size);
        Assert.Null(parameter.Precision);
        Assert.Null(parameter.Scale);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("2026-07-29", json, StringComparison.Ordinal);
        Assert.DoesNotContain("T12:00:00", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_treats_missing_table_reads_as_zero_in_distributions()
    {
        var session = FakeCompareSession.Create(
            ioTable: "dbo.Orders",
            tableReadsForMeasured: measured => measured switch
            {
                1 => 1,
                2 => 5,
                3 => 9,
                _ => measured * 5,
            },
            secondaryIoTable: "dbo.Lines",
            secondaryTableReadsForMeasured: measured => measured == 2 ? 4L : 0L);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 3));

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(
            new CompareDistribution(1, 5, 9),
            report.Baseline.LogicalReadsByTable["dbo.Orders"]);
        Assert.Equal(
            new CompareDistribution(0, 0, 4),
            report.Baseline.LogicalReadsByTable["dbo.Lines"]);
    }

    [Fact]
    public async Task Compare_reports_non_equivalent_results_when_candidate_values_differ()
    {
        var session = FakeCompareSession.Create(candidateValue: 43);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1));

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.False(report.ResultsEquivalent);
        Assert.Equal(ResultComparisonMode.Ordered, report.Equivalence.Mode);
        Assert.False(report.Equivalence.Equivalent);
    }

    [Fact]
    public async Task Compare_order_only_change_is_false_ordered_true_multiset_and_null_off()
    {
        var ordered = await Module(FakeCompareSession.Create(reorderCandidate: true))
            .ExecuteAsync(Compare(repeat: 1));
        var multiset = await Module(FakeCompareSession.Create(reorderCandidate: true))
            .ExecuteAsync(Compare(repeat: 1) with { CompareResults = ResultComparisonMode.Multiset });
        var off = await Module(FakeCompareSession.Create(reorderCandidate: true))
            .ExecuteAsync(Compare(repeat: 1) with { CompareResults = ResultComparisonMode.Off });

        var orderedReport = Assert.IsType<SqlHarnessCompareReport>(ordered.Report);
        Assert.False(orderedReport.ResultsEquivalent);
        Assert.False(orderedReport.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Ordered, orderedReport.Equivalence.Mode);
        Assert.True(orderedReport.Equivalence.DifferingPositions > 0);

        var multisetReport = Assert.IsType<SqlHarnessCompareReport>(multiset.Report);
        Assert.True(multisetReport.ResultsEquivalent);
        Assert.True(multisetReport.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Multiset, multisetReport.Equivalence.Mode);

        var offReport = Assert.IsType<SqlHarnessCompareReport>(off.Report);
        Assert.Null(offReport.ResultsEquivalent);
        Assert.Null(offReport.Equivalence.Equivalent);
        Assert.Equal(ResultComparisonMode.Off, offReport.Equivalence.Mode);
    }

    [Fact]
    public async Task Compare_validates_all_user_SQL_before_authentication_or_session_open()
    {
        var session = FakeCompareSession.Create();
        var azure = new FakeAzureCli();
        var operation = Compare(repeat: 1) with { CandidateSql = "DELETE dbo.Clients" };

        var outcome = await Module(session, azure).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
    }

    [Fact]
    public async Task Compare_invalid_repeat_uses_compare_wording()
    {
        var outcome = await Module(FakeCompareSession.Create()).ExecuteAsync(Compare(repeat: 0));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Contains("Compare repetitions", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Compare" + "ormance", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_loads_profiles_exactly_once()
    {
        var loads = 0;
        var outcome = await Module(FakeCompareSession.Create(), loadProfiles: () =>
        {
            loads++;
            return Profiles();
        }).ExecuteAsync(Compare(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Compare_rejects_a_parameter_referenced_by_none_of_setup_baseline_or_candidate()
    {
        var session = FakeCompareSession.Create();
        var azure = new FakeAzureCli();
        var operation = Compare(repeat: 1) with { Parameters = ["ClinetId:int=42"] };

        var outcome = await Module(session, azure).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.FactoryOpenCount);
    }

    [Fact]
    public async Task Compare_parameter_reference_matching_uses_the_union_of_all_batches_case_insensitively()
    {
        var session = FakeCompareSession.Create();
        var operation = Compare(repeat: 1) with
        {
            SetupSql = "SELECT @clientid AS Id INTO #ids FROM dbo.Clients",
            Parameters = ["CLIENTID:int=42"],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var setup = Assert.Single(session.Commands, command => command.Sql == operation.SetupSql);
        Assert.Equal(operation.SetupSql, setup.Sql);
        Assert.Equal("@CLIENTID", Assert.Single(setup.Parameters).Name);
    }

    [Fact]
    public async Task Compare_raw_footprint_includes_setup_warmups_messages_and_every_plan_but_hashes_are_result_only()
    {
        var minimalGain = new FakeGainStore();
        var richGain = new FakeGainStore();
        var minimal = await Module(FakeCompareSession.Create(), gain: minimalGain).ExecuteAsync(Compare(repeat: 1));
        var rich = await Module(
            FakeCompareSession.Create(includeSetupResult: true, includeSecondPlan: true, includeExtraMessage: true),
            gain: richGain).ExecuteAsync(Compare(repeat: 1));

        await Assert.IsType<SqlHarnessEmissionReceipt>(minimal.EmissionReceipt).CompleteAsync(new OutputFootprint(1, 1));
        await Assert.IsType<SqlHarnessEmissionReceipt>(rich.EmissionReceipt).CompleteAsync(new OutputFootprint(1, 1));

        Assert.True(Assert.Single(richGain.Records).RawBytes > Assert.Single(minimalGain.Records).RawBytes);
        Assert.True(Assert.IsType<SqlHarnessCompareReport>(rich.Report).ResultsEquivalent);
    }

    [Fact]
    public async Task Compare_retains_and_parses_every_showplan_resultset_in_a_run()
    {
        var session = FakeCompareSession.Create(includeSecondPlan: true);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1));

        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Contains(report.Baseline.Operators, op => op.NodeId == 1);
        Assert.Contains(report.Baseline.Operators, op => op.NodeId == 2);
    }

    [Fact]
    public async Task Compare_defers_gain_until_receipt_completion()
    {
        var session = FakeCompareSession.Create();
        var gain = new FakeGainStore();
        var outcome = await Module(session, gain: gain).ExecuteAsync(Compare(repeat: 1));

        Assert.Empty(gain.Records);
        var completion = await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(80, 4));

        Assert.Equal(SqlHarnessExitCode.Success, completion);
        var record = Assert.Single(gain.Records);
        Assert.Equal("compare", record.Command);
        Assert.Equal(80, record.EmittedBytes);
    }

    [Fact]
    public async Task Partial_statistics_enable_failure_still_attempts_OFF_cleanup()
    {
        var session = FakeCompareSession.Create(failStatisticsEnable: true);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Single(session.StatisticsCleanupTokens);
    }

    [Fact]
    public async Task Measured_failure_receipt_preserves_exact_setup_and_warmup_raw_footprint()
    {
        var gain = new FakeGainStore();
        var session = FakeCompareSession.Create(includeSetupResult: true, failOnBenchmarkNumber: 3);

        var outcome = await Module(session, gain: gain).ExecuteAsync(Compare(repeat: 1));
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(1, 1));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        using var expected = new CanonicalResultAccumulator();
        AddResult(expected, "SetupValue", "System.String", "setup-result-value");
        AddResult(expected, "Value", "System.Int32", 42);
        expected.AddMessage("planXml", PlanA);
        expected.AddMessage("sql", StatisticsMessage(5, 10, 12));
        AddResult(expected, "Value", "System.Int32", 42);
        expected.AddMessage("planXml", PlanB);
        expected.AddMessage("sql", StatisticsMessage(5, 10, 12));
        var completed = expected.Complete().Footprint;
        var record = Assert.Single(gain.Records);
        Assert.Equal(completed.Bytes - 2, record.RawBytes);
        Assert.Equal(completed.Lines, record.RawLines);
        Assert.True(record.RawBytes > 0);
    }

    private static void AddResult(
        CanonicalResultAccumulator accumulator,
        string name,
        string dataType,
        object value)
    {
        accumulator.BeginResultSet([new CanonicalColumn(0, name, dataType, false)]);
        accumulator.AddRow([value]);
        accumulator.EndResultSet();
    }

    private static string StatisticsMessage(long reads, int cpu, int elapsed, string table = "Clients") =>
        $"Table '{table}'. Scan count 1, logical reads {reads}, physical reads 0, lob logical reads 0.\nSQL Server Execution Times: CPU time = {cpu} ms, elapsed time = {elapsed} ms.";

    private static string TableIoLine(string table, long reads) =>
        $"Table '{table}'. Scan count 1, logical reads {reads}, physical reads 0, lob logical reads 0.";

    [Fact]
    public async Task Caller_cancellation_uses_bounded_non_cancelled_OFF_cleanup_token()
    {
        using var callerCancellation = new CancellationTokenSource();
        var session = FakeCompareSession.Create(cancelOnBenchmark: callerCancellation);

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 1), callerCancellation.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        var cleanupToken = Assert.Single(session.StatisticsCleanupTokens);
        Assert.True(cleanupToken.CanBeCanceled);
        Assert.False(cleanupToken.IsCancellationRequested);
        Assert.NotEqual(callerCancellation.Token, cleanupToken);
    }

    [Fact]
    public async Task Compare_ordered_applies_comparison_row_limit()
    {
        var session = FakeCompareSession.Create(resultRowCount: 3);

        var outcome = await Module(session, comparisonMaximumRows: 2)
            .ExecuteAsync(Compare(repeat: 1));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(
            "Result comparison exceeds the 1000000-row limit.",
            outcome.SafeError);
    }

    [Fact]
    public async Task Compare_off_does_not_apply_comparison_row_limit()
    {
        var session = FakeCompareSession.Create(resultRowCount: 3);

        var outcome = await Module(session, comparisonMaximumRows: 2)
            .ExecuteAsync(Compare(repeat: 1) with { CompareResults = ResultComparisonMode.Off });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.Equal(ResultComparisonMode.Off, report.Equivalence.Mode);
        Assert.Null(report.Equivalence.Equivalent);
        Assert.DoesNotContain(
            "Result comparison exceeds",
            outcome.SafeError ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static SqlHarnessModule Module(
        FakeCompareSession session,
        FakeAzureCli? azure = null,
        FakeGainStore? gain = null,
        Func<IReadOnlyDictionary<string, TargetProfile>>? loadProfiles = null,
        int? comparisonMaximumRows = null,
        ICompareArtifactWriter? artifacts = null) =>
        new(session, gain ?? new FakeGainStore(), artifacts ?? new NullArtifactWriter(), loadProfiles ?? Profiles)
        {
            ComparisonMaximumRows = comparisonMaximumRows ?? CanonicalComparisonAccumulator.MaximumComparedRows,
        };

    private static SqlHarnessCompareOperation Compare(int repeat) =>
        new(Target(), "SELECT Id INTO #ids FROM dbo.Clients", "SELECT Value FROM dbo.Clients", "SELECT Value FROM dbo.Clients -- candidate", [], 30, repeat);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

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

    private sealed class NullArtifactWriter : ICompareArtifactWriter
    {
        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target) => "ignored";
    }

    private sealed class CapturingArtifactWriter : ICompareArtifactWriter
    {
        public object? Report { get; private set; }
        public IReadOnlyList<CompareRunArtifact> Runs { get; private set; } = [];
        public string? Target { get; private set; }

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
        {
            Report = report;
            Runs = runs.ToArray();
            Target = target;
            return "cell-artifacts";
        }
    }

    private sealed class FakeCompareSession : ISqlSessionFactory, ISqlSession
    {
        private readonly int _candidateValue;
        private readonly bool _failStatisticsEnable;
        private readonly CancellationTokenSource? _cancelOnBenchmark;
        private readonly bool _includeSetupResult;
        private readonly bool _includeSecondPlan;
        private readonly bool _includeExtraMessage;
        private readonly int? _failOnBenchmarkNumber;
        private readonly bool _reorderCandidate;
        private readonly string _ioTable;
        private readonly Func<int, long>? _tableReadsForMeasured;
        private readonly string? _secondaryIoTable;
        private readonly Func<int, long>? _secondaryTableReadsForMeasured;
        private readonly int _resultRowCount;
        private int _baseline;
        private int _candidate;
        private readonly List<string> _messages = [];

        public List<string> Labels { get; } = [];
        public List<SqlExecutionCommand> Commands { get; } = [];
        public int FactoryOpenCount { get; private set; }
        public List<CancellationToken> StatisticsCleanupTokens { get; } = [];
        public IReadOnlyList<string> Messages => _messages;
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        private FakeCompareSession(
            int candidateValue,
            bool failStatisticsEnable,
            CancellationTokenSource? cancelOnBenchmark,
            bool includeSetupResult,
            bool includeSecondPlan,
            bool includeExtraMessage,
            int? failOnBenchmarkNumber,
            bool reorderCandidate,
            string ioTable,
            Func<int, long>? tableReadsForMeasured,
            string? secondaryIoTable,
            Func<int, long>? secondaryTableReadsForMeasured,
            int resultRowCount)
        {
            _candidateValue = candidateValue;
            _failStatisticsEnable = failStatisticsEnable;
            _cancelOnBenchmark = cancelOnBenchmark;
            _includeSetupResult = includeSetupResult;
            _includeSecondPlan = includeSecondPlan;
            _includeExtraMessage = includeExtraMessage;
            _failOnBenchmarkNumber = failOnBenchmarkNumber;
            _reorderCandidate = reorderCandidate;
            _ioTable = ioTable;
            _tableReadsForMeasured = tableReadsForMeasured;
            _secondaryIoTable = secondaryIoTable;
            _secondaryTableReadsForMeasured = secondaryTableReadsForMeasured;
            _resultRowCount = resultRowCount;
        }

        public static FakeCompareSession Create(
            int candidateValue = 42,
            bool failStatisticsEnable = false,
            CancellationTokenSource? cancelOnBenchmark = null,
            bool includeSetupResult = false,
            bool includeSecondPlan = false,
            bool includeExtraMessage = false,
            int? failOnBenchmarkNumber = null,
            bool reorderCandidate = false,
            string ioTable = "Clients",
            Func<int, long>? tableReadsForMeasured = null,
            string? secondaryIoTable = null,
            Func<int, long>? secondaryTableReadsForMeasured = null,
            int resultRowCount = 1) =>
            new(candidateValue, failStatisticsEnable, cancelOnBenchmark, includeSetupResult, includeSecondPlan, includeExtraMessage, failOnBenchmarkNumber, reorderCandidate, ioTable, tableReadsForMeasured, secondaryIoTable, secondaryTableReadsForMeasured, resultRowCount);

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            FactoryOpenCount++;
            return Task.FromResult<ISqlSession>(this);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.Sql.Contains("STATISTICS IO ON", StringComparison.Ordinal))
            {
                if (_failStatisticsEnable)
                    return Task.FromException<ISqlReader>(new InvalidOperationException("statistics partially enabled"));
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }
            if (command.Sql.Contains("STATISTICS IO OFF", StringComparison.Ordinal))
            {
                StatisticsCleanupTokens.Add(ct);
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }
            if (command.Sql.Contains("INTO #ids", StringComparison.Ordinal))
            {
                Labels.Add("setup");
                return Task.FromResult<ISqlReader>(_includeSetupResult
                    ? FakeReader.Single(["SetupValue"], ["setup-result-value"])
                    : FakeReader.Empty());
            }

            if (_cancelOnBenchmark is not null)
            {
                _cancelOnBenchmark.Cancel();
                return Task.FromException<ISqlReader>(new OperationCanceledException(ct));
            }

            var baseline = !command.Sql.Contains("candidate", StringComparison.Ordinal);
            var count = baseline ? ++_baseline : ++_candidate;
            var benchmarkNumber = _baseline + _candidate;
            if (_failOnBenchmarkNumber == benchmarkNumber)
                return Task.FromException<ISqlReader>(new TimeoutException("measured run failed"));
            var measured = Math.Max(count - 1, 1);
            Labels.Add(count == 1 ? $"warmup-{(baseline ? "A" : "B")}" : baseline ? "A" : "B");
            var cpu = measured * 10;
            var elapsed = cpu + 2;
            var reads = _tableReadsForMeasured?.Invoke(measured) ?? measured * 5L;
            var message = StatisticsMessage(reads, cpu, elapsed, _ioTable);
            if (_secondaryIoTable is not null)
            {
                var secondaryReads = _secondaryTableReadsForMeasured?.Invoke(measured) ?? 0L;
                if (secondaryReads > 0)
                    message = TableIoLine(_secondaryIoTable, secondaryReads) + "\n" + message;
            }
            _messages.Add(message);
            if (_includeExtraMessage)
                _messages.Add("ordinary diagnostic message");
            var plans = _includeSecondPlan ? new[] { PlanA, PlanB } : new[] { baseline ? PlanA : PlanB };
            if (_reorderCandidate)
            {
                object?[][] rows = baseline
                    ? [[1], [2]]
                    : [[2], [1]];
                return Task.FromResult<ISqlReader>(FakeReader.WithPlans(["Value"], rows, plans));
            }

            var baseValue = baseline ? 42 : _candidateValue;
            object?[][] valueRows = Enumerable.Range(0, _resultRowCount)
                .Select(index => new object?[] { baseValue + index })
                .ToArray();
            return Task.FromResult<ISqlReader>(FakeReader.WithPlans(
                ["Value"],
                valueRows,
                plans));
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
        public static FakeReader WithPlans(string[] names, object?[][] rows, IReadOnlyList<string> plans) => new(
            [new(names, rows), .. plans.Select(plan => new Result(["Microsoft SQL Server 2005 XML Showplan"], [[plan]]))]);
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