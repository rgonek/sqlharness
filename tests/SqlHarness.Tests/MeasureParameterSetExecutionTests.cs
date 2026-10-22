using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;
using SqlHarness.Tests.Journal;

namespace SqlHarness.Tests;

public sealed class MeasureParameterSetExecutionTests
{
    private const string SetupSql = "CREATE TABLE #ids (Id int); INSERT #ids (Id) SELECT Id FROM dbo.Clients WHERE Id = @id";
    private const string QuerySql = "SELECT Value FROM dbo.Clients WHERE Id = @id";
    private const string Plan = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Table=\"[Clients]\" /></IndexScan></RelOp></BatchSequence></ShowPlanXML>";
    private const string MeasuredOrderRule =
        "Measured runs execute parameter sets sequentially in user-supplied order.";
    private const string PlanCacheWarning =
        "Parameter-set measurements use separate sessions; SQLHarness did not clear or isolate the server plan cache.";

    [Fact]
    public async Task Three_sets_warm_and_measure_in_input_order_on_one_session_per_set()
    {
        var session = RecordingSession.Create();
        var runner = Runner(session);
        var execution = await runner.ExecuteAsync(Operation(repeat: 3), Prepared("A", "B", "C"));

        Assert.Equal(["A", "B", "C"], session.WarmupSetNames);
        Assert.Equal(
            ["A", "A", "A", "B", "B", "B", "C", "C", "C"],
            session.MeasuredSetNames);
        Assert.Equal(3, session.SetupCount);
        Assert.Equal(3, session.ConnectionCount);

        Assert.Equal(3, execution.SetupExecutionCount);
        Assert.Equal(["A", "B", "C"], execution.WarmupOrder);
        Assert.Equal(
            ["A", "A", "A", "B", "B", "B", "C", "C", "C"],
            execution.Runs.Select(run => run.Artifact.ParameterSet));
        Assert.Equal([1, 2, 3, 1, 2, 3, 1, 2, 3], execution.Runs.Select(run => run.Artifact.Repetition));
        Assert.Equal([0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3], runner.Dialect.Calls.Select(call => call.Repetition));
        Assert.All(runner.Dialect.Calls, call =>
        {
            Assert.False(call.CaptureComparison);
            Assert.Equal("measure", call.Variant);
            Assert.Equal(QuerySql, call.Sql);
            Assert.Equal(30, call.TimeoutSeconds);
            Assert.Same(session, call.Session);
        });
        Assert.Equal(
            [11, 22, 33],
            session.Commands.Where(command => command.Sql.Contains("INSERT #ids", StringComparison.Ordinal))
                .Select(ParameterId));
        AssertNoCacheControl(session.Commands);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task No_setup_still_warms_and_measures_in_input_order(string? setupSql)
    {
        var session = RecordingSession.Create();
        var execution = await Runner(session).ExecuteAsync(Operation(repeat: 1, setupSql: setupSql), Prepared("A", "B"));

        Assert.Equal(0, session.SetupCount);
        Assert.Equal(0, execution.SetupExecutionCount);
        Assert.DoesNotContain(session.Commands, command => command.Sql == SetupSql);
        Assert.Equal(["A", "B"], session.WarmupSetNames);
        Assert.Equal(["A", "B"], session.MeasuredSetNames);
        Assert.Equal(2, session.ConnectionCount);
    }

    [Fact]
    public async Task Each_set_executes_all_repetitions_before_the_next_set()
    {
        var session = RecordingSession.Create();
        var execution = await Runner(session).ExecuteAsync(Operation(repeat: 4), Prepared("A", "B"));

        Assert.Equal(["A", "B"], session.WarmupSetNames);
        Assert.Equal(["A", "A", "A", "A", "B", "B", "B", "B"], session.MeasuredSetNames);
        Assert.Equal(8, execution.Runs.Count);
        Assert.Equal([1, 2, 3, 4, 1, 2, 3, 4], execution.Runs.Select(run => run.Artifact.Repetition));

        Assert.All(["A", "B"], name => Assert.Equal(4, session.MeasuredSetNames.Count(candidate => candidate == name)));
    }

    [Fact]
    public async Task Repeat_one_measures_in_input_order()
    {
        var session = RecordingSession.Create();
        var execution = await Runner(session).ExecuteAsync(Operation(repeat: 1), Prepared("A", "B", "C"));

        Assert.Equal(["A", "B", "C"], session.WarmupSetNames);
        Assert.Equal(["A", "B", "C"], session.MeasuredSetNames);
        Assert.Equal([1, 1, 1], execution.Runs.Select(run => run.Artifact.Repetition));
        Assert.All(execution.Runs, run => Assert.Single(run.PlanHashes));
    }

    [Fact]
    public async Task Materialized_conditions_are_recorded_once_after_connect_for_each_case()
    {
        var session = RecordingSession.Create();
        var recordedCases = new List<int>();
        var execution = await Runner(session).ExecuteAsync(
            Operation(repeat: 1),
            Prepared("A", "B"),
            recordMaterializedConditions: (index, _) => recordedCases.Add(index));

        Assert.Equal([0, 1], recordedCases);
        Assert.Equal(2, execution.CompletedCases);
    }

    [Fact]
    public async Task Named_set_conditions_record_resolved_type_and_rows_from_content_before_measurement()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = false }, TextWriter.Null, TimeProvider.System);
        var session = RecordingSession.Create(querySql: "SELECT * FROM @Ids");
        var operation = Operation(repeat: 1, setupSql: null) with
        {
            QuerySql = "SELECT * FROM @Ids",
        };
        var sets = new[] { "A", "B" }.Select(name => new PreparedMeasureParameterSet(name, [], [], null)
        {
            Inputs = [new SqlHarnessParameterInput("Ids", "dbo.IdList", null)
            {
                Kind = "tvp",
                SourceSql = name == "A" ? "SELECT Id FROM dbo.SourceA" : "SELECT Id FROM dbo.SourceB",
            }],
        }).ToArray();
        var cases = sets.Select(set => new ConditionsCaseInput(set.Name, set.Name, [])).ToArray();
        var target = TargetResolver.Resolve(Target(), Profiles());
        var recorded = new Dictionary<int, IReadOnlyList<MaterializedParameterInfo>>();
        JournalHandle? handle = null;
        operation = operation with { RecordConditions = model => journal.RecordConditions(handle!, model) };
        handle = journal.Begin(JournalTestData.Session(), OperationJournalDescriber.DescribeStart(operation));
        Assert.NotNull(handle);
        operation.RecordConditions?.Invoke(ConditionsModelBuilder.Build(operation, target,
            SqlDialects.For(SqlEngine.SqlServer), [], cases, storeSensitive: true));
        var runner = Runner(session);

        await runner.ExecuteAsync(operation, sets, recordMaterializedConditions: (index, details) =>
        {
            Assert.Equal(index * 2, session.Commands.Count(command => command.Sql == operation.QuerySql));
            var model = ConditionsModelBuilder.Build(operation, target, SqlDialects.For(SqlEngine.SqlServer), [], cases, true);
            var enriched = ConditionsModelBuilder.WithMaterializedParameters(model, index, details, true);
            recorded[index] = details;
            foreach (var item in recorded.OrderBy(item => item.Key))
                enriched = ConditionsModelBuilder.WithMaterializedParameters(enriched, item.Key, item.Value, true);
            operation.RecordConditions?.Invoke(enriched);
        });

        var jsonText = (string)JournalDb.Rows(temp.DatabasePath, "SELECT conditions_json FROM operations").Single()["conditions_json"]!;
        using var json = JsonDocument.Parse(jsonText);
        var first = json.RootElement.GetProperty("cases")[0].GetProperty("varying")[0];
        var second = json.RootElement.GetProperty("cases")[1].GetProperty("varying")[0];
        Assert.Equal(2, recorded.Count);
        Assert.Equal("dbo.IdList READONLY", first.GetProperty("effectiveType").GetString());
        Assert.Equal("dbo.IdList READONLY", second.GetProperty("effectiveType").GetString());
        Assert.Equal(1, first.GetProperty("count").GetInt32());
        Assert.Equal("Id", first.GetProperty("columns")[0].GetString());
        Assert.NotEqual(first.GetProperty("contentHash").GetString(), second.GetProperty("contentHash").GetString());
        Assert.False(first.TryGetProperty("rows", out _));
        Assert.False(first.TryGetProperty("sourceText", out _));
        Assert.False(JournalDb.Contains(JournalDb.AllBytes(temp.DatabasePath), "SourceA"));
        Assert.False(JournalDb.Contains(JournalDb.AllBytes(temp.DatabasePath), "SourceB"));
        Assert.Equal(4, session.Commands.Count(command => command.Sql == operation.QuerySql));
    }

    [Fact]
    public async Task Runs_do_not_capture_comparison_rows()
    {
        var session = RecordingSession.Create(resultRowCount: 3);
        var runner = Runner(session, comparisonMaximumRows: 2);

        var execution = await runner.ExecuteAsync(Operation(repeat: 1), Prepared("A", "B"));

        Assert.Equal(2, execution.Runs.Count);
        Assert.NotEmpty(runner.Dialect.Calls);
        Assert.All(runner.Dialect.Calls, call =>
        {
            Assert.False(call.CaptureComparison);
            Assert.Equal(2, call.ComparisonMaximumRows);
        });
    }

    [Fact]
    public async Task Setup_failure_stops_before_warmup()
    {
        var failure = new TimeoutException("setup failed");
        var session = RecordingSession.Create(failSetup: true, failure: failure);

        var exception = await Assert.ThrowsAsync<MeasureCaseFailedException>(() =>
            Runner(session).ExecuteAsync(Operation(repeat: 3), Prepared("A", "B", "C")));

        Assert.Same(failure, exception.InnerException);
        Assert.Equal(0, exception.Execution.FailedCase?.Index);
        Assert.Equal(0, session.SetupCount);
        Assert.Empty(session.WarmupSetNames);
        Assert.Empty(session.MeasuredSetNames);
        Assert.Contains(session.Commands, command => command.Sql.Contains("#ids", StringComparison.Ordinal));
        Assert.DoesNotContain(session.Commands, command => command.Sql == QuerySql);
    }

    [Fact]
    public async Task Warmup_failure_stops_before_later_sets()
    {
        var failure = new TimeoutException("warmup failed");
        var session = RecordingSession.Create(failOnQueryNumber: 1, failure: failure);

        var exception = await Assert.ThrowsAsync<MeasureCaseFailedException>(() =>
            Runner(session).ExecuteAsync(Operation(repeat: 3), Prepared("A", "B", "C")));

        Assert.Same(failure, exception.InnerException);
        Assert.Equal(0, exception.Execution.FailedCase?.Index);
        Assert.Empty(session.WarmupSetNames);
        Assert.Empty(session.MeasuredSetNames);
        Assert.Equal(1, QueryCount(session, 11));
        Assert.Equal(0, QueryCount(session, 22));
        Assert.Equal(0, QueryCount(session, 33));
    }

    [Fact]
    public async Task Measured_failure_stops_without_a_successful_result()
    {
        var failure = new TimeoutException("measured run failed");
        var session = RecordingSession.Create(failOnQueryNumber: 3, failure: failure);

        var exception = await Assert.ThrowsAsync<MeasureCaseFailedException>(() =>
            Runner(session).ExecuteAsync(Operation(repeat: 2), Prepared("A", "B")));

        Assert.Same(failure, exception.InnerException);
        Assert.Equal(0, exception.Execution.CompletedCases);
        Assert.Equal(["A"], session.WarmupSetNames);
        Assert.Equal(["A"], session.MeasuredSetNames);
        Assert.Empty(exception.CompletedRuns);
        Assert.Equal(3, QueryCount(session, 11));
        Assert.Equal(0, QueryCount(session, 22));
    }

    [Fact]
    public async Task Cancellation_propagates_without_being_wrapped()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var session = RecordingSession.Create();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Runner(session).ExecuteAsync(Operation(repeat: 2), Prepared("A", "B"), caller.Token));

        Assert.Null(exception.InnerException);
        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(0, session.SetupCount);
        Assert.Empty(session.WarmupSetNames);
        Assert.Empty(session.MeasuredSetNames);
        Assert.Empty(session.Tokens);
    }

    [Fact]
    public async Task Invalid_parameter_set_shape_does_not_connect_and_returns_exit_2()
    {
        var session = RecordingSession.Create();
        var operation = MeasureSets(
            1,
            [
                new("small", ["id:int=717171"]),
                new("large", ["id:bigint=717171"]),
            ]);

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(0, session.ConnectionCount);
        Assert.Empty(session.Commands);
        Assert.Null(outcome.Report);
        Assert.Contains("different type", outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("717171", outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("id:bigint=717171", outcome.SafeError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Parameter_sets_reject_repeat_outside_1_to_100_before_connect(int repeat)
    {
        var session = RecordingSession.Create();

        var outcome = await Module(session).ExecuteAsync(MeasureSets(repeat, Inputs("A", "B")));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(0, session.ConnectionCount);
        Assert.Contains("Measurement repetitions must be between 1 and 100.", outcome.SafeError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicitly_empty_parameter_sets_are_rejected_before_connecting()
    {
        var session = RecordingSession.Create();
        var operation = Operation(repeat: 1) with
        {
            ParameterSets = [],
            Parameters = ["id:int=42"],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(0, session.ConnectionCount);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Singleton_named_set_runs_once_per_repeat_and_keeps_report_and_artifact_identity()
    {
        const string subject = "orders-report";
        const string scope = "whole";
        var session = RecordingSession.Create(setCount: 1);
        var writer = new CapturingWriter();
        var journal = new StartCaptureJournal();
        var singleton = new SqlHarnessParameterSetInput("only", [])
        {
            TypedParameters = [new SqlHarnessParameterInput("id", "int", "11")],
        };
        var operation = MeasureSets(3, [singleton]) with { Context = new CompareContext(subject, scope) };

        var module = new JournalingModule(Module(session, writer: writer), () => journal,
            () => JournalTestData.Session());
        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessMeasureSetReport>(outcome.Report);
        var set = Assert.Single(report.Sets);
        Assert.Equal("only", set.Name);
        Assert.Matches("^[0-9A-F]{64}$", set.ValueHash);
        Assert.Equal([new MeasureParameterMetadata("@id", "int")], set.Parameters);
        Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(1, session.SetupCount);
        Assert.Equal(["only", "only", "only"], session.MeasuredSetNames);
        Assert.Equal(["only", "only", "only"], writer.Runs.Select(run => run.ParameterSet));
        Assert.Same(report, writer.Report);
        Assert.Equal("testdb-a", writer.Target);
        Assert.Contains(set.ValueHash!, System.Text.Json.JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.All(session.Commands.Where(command => command.Sql == QuerySql), command =>
            Assert.Equal(11, Assert.IsType<int>(Assert.Single(command.Parameters).Value)));
        Assert.Equal(new CompareContext(subject, scope), journal.Start!.Context);
    }

    [Fact]
    public async Task Compare_singleton_parameter_set_executes_both_variants_and_keeps_case_identity()
    {
        const string subject = "orders-report";
        const string scope = "whole";
        const string candidateSql = QuerySql + " -- candidate";
        using var temp = new JournalTempDirectory();
        var artifactRoot = Path.Combine(temp.Path, "artifacts");
        Directory.CreateDirectory(artifactRoot);
        var activityJournal = ActivityJournal.Open(temp.DatabasePath,
            new JournalConfig { StoreSensitive = false }, TextWriter.Null, TimeProvider.System);
        var session = RecordingSession.Create(setCount: 1);
        var writer = new CompareArtifactWriter(artifactRoot, () => DateTimeOffset.UnixEpoch);
        var operation = new SqlHarnessCompareMatrixOperation(
            Target(), SetupSql, QuerySql, candidateSql, [], 30, 1, "")
        {
            ParameterSets =
            [
                new SqlHarnessParameterSetInput("only", [])
                {
                    TypedParameters = [new SqlHarnessParameterInput("id", "int", "11")],
                },
            ],
            Context = new CompareContext(subject, scope),
        };
        var module = new JournalingModule(new SqlHarnessModule(session, writer, Profiles), () => activityJournal,
            () => JournalTestData.Session());

        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        var cell = Assert.Single(report.Cells);
        Assert.Equal("only", cell.SetName);
        Assert.Equal(0, cell.Index);
        Assert.Equal([new BenchmarkParameterReport("@id", "int", null, null, null)], cell.SetParameters);
        Assert.True(cell.Compare.ResultsEquivalent);
        Assert.NotNull(cell.Compare.ArtifactDirectory);
        var artifactDirectory = Assert.IsType<string>(cell.Compare.ArtifactDirectory);
        Assert.True(File.Exists(Path.Combine(artifactDirectory, "report.json")));
        var runsJson = File.ReadAllLines(Path.Combine(artifactDirectory, "runs.jsonl"));
        Assert.Equal(2, runsJson.Length);
        Assert.All(runsJson, line => Assert.Contains("\"parameterSet\":\"only\"", line, StringComparison.Ordinal));
        using var matrixIndex = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifactDirectory, "matrix-cells.json")));
        var artifactCase = Assert.Single(matrixIndex.RootElement.GetProperty("cases").EnumerateArray().ToArray());
        Assert.Equal(0, artifactCase.GetProperty("index").GetInt32());
        Assert.Equal("only", artifactCase.GetProperty("setName").GetString());
        Assert.True(artifactCase.GetProperty("artifactId").GetString()!.Length > 0);
        var runs = Assert.IsAssignableFrom<IReadOnlyList<CompareRunArtifact>>(outcome.BenchmarkRuns);
        Assert.Equal(["baseline", "candidate"], runs.Select(run => run.Variant));
        Assert.Equal([0], runs.Select(run => run.MatrixCell));
        Assert.Equal(["only", "only"], runs.Select(run => run.ParameterSet));
        var baselineCommands = session.Commands.Where(command => command.Sql == QuerySql).ToArray();
        var candidateCommands = session.Commands.Where(command => command.Sql == candidateSql).ToArray();
        Assert.NotEmpty(baselineCommands);
        Assert.NotEmpty(candidateCommands);
        Assert.All(baselineCommands.Concat(candidateCommands), command =>
            Assert.Equal(11, Assert.IsType<int>(Assert.Single(command.Parameters).Value)));
        Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(1, session.SetupCount);
        var journalRow = Assert.Single(JournalDb.Rows(temp.DatabasePath,
            "SELECT compare_subject, compare_scope, conditions_json FROM operations"));
        Assert.Equal(subject, journalRow["compare_subject"]);
        Assert.Equal(scope, journalRow["compare_scope"]);
        using var conditions = JsonDocument.Parse((string)journalRow["conditions_json"]!);
        Assert.Matches("^[0-9A-F]{64}$", conditions.RootElement.GetProperty("fingerprint").GetString());
        var reproducedCase = Assert.Single(conditions.RootElement.GetProperty("cases").EnumerateArray().ToArray());
        Assert.Equal("0", reproducedCase.GetProperty("id").GetString());
        Assert.Equal("only", reproducedCase.GetProperty("setName").GetString());
        Assert.Matches("^[0-9A-F]{64}$", reproducedCase.GetProperty("measurementFingerprint").GetString());
    }

    [Fact]
    public async Task Module_uses_one_session_and_setup_per_set_and_returns_set_report()
    {
        const string tenant = "acme-secret-884422";
        var session = RecordingSession.Create(
            setCount: 2,
            resultRowCount: 3,
            setupSql: "CREATE TABLE #ids (Id int); INSERT #ids (Id) SELECT Id FROM dbo.Clients WHERE Id = @id AND Tenant = @tenant",
            querySql: "SELECT Value FROM dbo.Clients WHERE Id = @id AND Tenant = @tenant");
        var writer = new CapturingWriter();
        var operation = new SqlHarnessMeasureOperation(
            Target(),
            session.SetupSql,
            session.QuerySql,
            [$"tenant:nvarchar={tenant}"],
            17,
            2,
            Inputs("A", "B"));

        var outcome = await Module(session, writer: writer, comparisonMaximumRows: 2).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Null(outcome.SafeError);
        Assert.Equal(1, writer.Writes);
        Assert.Equal("testdb-a", writer.Target);
        Assert.Equal(["A", "A", "B", "B"], writer.Runs.Select(run => run.ParameterSet));
        Assert.Equal([1, 2, 1, 2], writer.Runs.Select(run => run.Repetition));
        Assert.DoesNotContain(writer.Runs, run => run.Repetition == 0);
        Assert.Equal(2, session.ConnectionCount);
        Assert.Equal(2, session.SetupCount);
        Assert.Equal(["A", "B"], session.WarmupSetNames);
        Assert.Equal(["A", "A", "B", "B"], session.MeasuredSetNames);
        var setups = session.Commands.Where(command => command.Sql.Contains("INSERT #ids", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, setups.Length);
        Assert.All(setups, setup => Assert.Equal(["@tenant", "@id"], setup.Parameters.Select(parameter => parameter.Name)));
        Assert.Equal([11, 22], setups.Select(setup => Assert.IsType<int>(setup.Parameters[1].Value)));
        Assert.All(setups, setup => Assert.Equal(tenant, setup.Parameters[0].Value));
        Assert.All(session.Commands.Where(command =>
            !command.Sql.StartsWith("SELECT @@OPTIONS", StringComparison.Ordinal)
            && !command.Sql.StartsWith("SET NOCOUNT", StringComparison.Ordinal)),
            command => Assert.Equal(17, command.TimeoutSeconds));
        AssertNoCacheControl(session.Commands);

        var report = Assert.IsType<SqlHarnessMeasureSetReport>(outcome.Report);
        var passed = Assert.IsType<SqlHarnessMeasureSetReport>(writer.Report);
        Assert.Null(passed.ArtifactDirectory);
        Assert.Equal(passed with { ArtifactDirectory = "measure-artifacts" }, report);
        Assert.Equal("measure-artifacts", report.ArtifactDirectory);
        var captured = JsonSerializer.Serialize(passed) + JsonSerializer.Serialize(writer.Runs) + writer.Target;
        Assert.DoesNotContain(tenant, captured, StringComparison.Ordinal);
        Assert.DoesNotContain($"tenant:nvarchar={tenant}", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("id:int=11", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("id:int=22", captured, StringComparison.Ordinal);
        Assert.DoesNotContain(session.QuerySql, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(session.SetupSql, captured, StringComparison.Ordinal);
        Assert.Equal(session.Identity, report.Target);
        Assert.Equal(2, report.Repeat);
        Assert.Equal(4, report.MeasuredRunCount);
        Assert.Equal(2, report.SetupExecutionCount);
        Assert.Equal(["A", "B"], report.WarmupOrder);
        Assert.Equal(MeasuredOrderRule, report.MeasuredOrderRule);
        Assert.Equal(PlanCacheWarning, report.PlanCacheWarning);
        Assert.Equal(["A", "B"], report.Sets.Select(set => set.Name));
        Assert.Equal("A", report.CrossSetSummary.MinimumMedianElapsedSet);
        Assert.Equal("A", report.CrossSetSummary.MaximumMedianElapsedSet);
        Assert.Equal(12, report.CrossSetSummary.MinimumMedianElapsedMilliseconds);
        Assert.Equal(12, report.CrossSetSummary.MaximumMedianElapsedMilliseconds);
        Assert.Equal("A", report.CrossSetSummary.MinimumMedianCpuSet);
        Assert.Equal("A", report.CrossSetSummary.MaximumMedianCpuSet);
        Assert.Equal(10, report.CrossSetSummary.MinimumMedianCpuMilliseconds);
        Assert.Equal(10, report.CrossSetSummary.MaximumMedianCpuMilliseconds);
        Assert.Equal("A", report.CrossSetSummary.MinimumMedianReadsSet);
        Assert.Equal("A", report.CrossSetSummary.MaximumMedianReadsSet);
        Assert.Equal(5, report.CrossSetSummary.MinimumMedianLogicalReads);
        Assert.Equal(5, report.CrossSetSummary.MaximumMedianLogicalReads);

        var setA = report.Sets[0];
        var setB = report.Sets[1];
        Assert.Equal(2, setA.Repetitions);
        Assert.Equal(2, setB.Repetitions);
        Assert.True(setA.ResultsStable);
        Assert.True(setB.ResultsStable);
        Assert.NotNull(setA.ResultHash);
        Assert.Equal(setA.ResultHash, setB.ResultHash);
        Assert.Equal(setA.PlanHashes, setB.PlanHashes);
        Assert.Single(setA.PlanHashes);
        Assert.Equal(
            [new MeasureParameterMetadata("@id", "int"), new MeasureParameterMetadata("@tenant", "nvarchar")],
            setA.Parameters);
        Assert.Equal(setA.Parameters, setB.Parameters);
        Assert.Equal(TypedParameterHasher.Hash(SqlParameterParser.Parse(["id:int=11", $"tenant:nvarchar={tenant}"])), setA.ValueHash);
        Assert.Equal(TypedParameterHasher.Hash(SqlParameterParser.Parse(["id:int=22", $"tenant:nvarchar={tenant}"])), setB.ValueHash);
        Assert.NotEqual(setA.ValueHash, setB.ValueHash);
        Assert.Equal(new CompareDistribution(10, 10, 10), setA.Metrics.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(12, 12, 12), setA.Metrics.ElapsedTimeMilliseconds);
        Assert.Equal(new CompareDistribution(5, 5, 5), setA.Metrics.LogicalReads);
        Assert.Equal(10, setA.Metrics.TotalLogicalReadsByTable["Clients"]);
        Assert.Equal(new CompareDistribution(5, 5, 5), setA.Metrics.LogicalReadsByTable["Clients"]);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(tenant, json, StringComparison.Ordinal);
        Assert.DoesNotContain($"tenant:nvarchar={tenant}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("id:int=11", json, StringComparison.Ordinal);
        Assert.DoesNotContain("id:int=22", json, StringComparison.Ordinal);
        Assert.DoesNotContain(session.QuerySql, json, StringComparison.Ordinal);
        Assert.DoesNotContain(session.SetupSql, json, StringComparison.Ordinal);
        Assert.Contains(setA.ValueHash!, json, StringComparison.Ordinal);
        Assert.Contains(setB.ValueHash!, json, StringComparison.Ordinal);

        Assert.Equal(
            SqlHarnessExitCode.Success,
            await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new OutputFootprint(4, 1)));
        var gainRecord = outcome.EmissionReceipt!.RawFootprint!;
        Assert.True(gainRecord.Bytes > 0);
        Assert.DoesNotContain(tenant, gainRecord.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_parameter_set_captures_post_setup_state_and_restores_after_warmup_and_repetitions()
    {
        var session = RecordingSession.Create(setCount: 2);
        var runner = Runner(session);
        var operation = Operation(repeat: 2);
        var result = await runner.ExecuteAsync(operation, Prepared("A", "B"));

        Assert.Equal(4, result.Runs.Count);
        Assert.Equal(
            [11, 11, 11, 22, 22, 22],
            runner.Dialect.Calls.Select(call => Assert.IsType<int>(call.Parameters.Single().Value)));
        Assert.Equal([0, 1, 2, 0, 1, 2], runner.Dialect.Calls.Select(call => call.Repetition));

        static string CommandEvent(SqlExecutionCommand command)
        {
            if (command.Sql.StartsWith("SELECT @@OPTIONS", StringComparison.Ordinal)) return "capture";
            if (command.Sql.StartsWith("SET NOCOUNT", StringComparison.Ordinal)) return "restore";
            if (command.Sql.StartsWith("SET STATISTICS IO ON", StringComparison.Ordinal)) return "begin-run";
            if (command.Sql.StartsWith("SET STATISTICS XML OFF", StringComparison.Ordinal)) return "end-run";
            if (command.Sql.StartsWith("CREATE TABLE #ids", StringComparison.Ordinal)) return "create-temp";
            if (command.Sql.StartsWith("INSERT #ids", StringComparison.Ordinal))
                return $"setup-{Assert.IsType<int>(command.Parameters.Single().Value)}";
            if (command.Sql == QuerySql)
                return $"query-{Assert.IsType<int>(command.Parameters.Single().Value)}";
            return $"unexpected:{command.Sql}";
        }

        static IEnumerable<string> CaseEvents(int id)
        {
            yield return "create-temp";
            yield return $"setup-{id}";
            yield return "capture";
            for (var repetition = 0; repetition <= 2; repetition++)
            {
                yield return "begin-run";
                yield return $"query-{id}";
                yield return "end-run";
                yield return "restore";
            }
        }

        Assert.Equal(
            CaseEvents(11).Concat(CaseEvents(22)),
            session.Commands.Select(CommandEvent));
    }

    [Fact]
    public async Task Module_second_case_failure_retains_first_case_runs_and_reports_matrix_stop()
    {
        var session = RecordingSession.Create(setCount: 2, failOnQueryNumber: 4);

        var outcome = await Module(session).ExecuteAsync(MeasureSets(2, Inputs("A", "B")));

        Assert.NotEqual(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessMeasureSetReport>(outcome.Report);
        Assert.Equal(["A"], report.Sets.Select(set => set.Name));
        Assert.Equal(["A", "A"], Assert.IsAssignableFrom<IReadOnlyList<CompareRunArtifact>>(outcome.BenchmarkRuns!)
            .Select(run => run.ParameterSet));
        Assert.Equal(new MatrixExecutionCoverage(2, 1, 0, new MatrixFailedCase(1, SetName: "B", Phase: "sql")), outcome.Coverage);
        Assert.Equal(outcome.Coverage, outcome.Error?.MatrixStop);
        Assert.Equal(2, session.ConnectionCount);
        Assert.Equal(2, session.SetupCount);
        Assert.Equal(["A"], session.WarmupSetNames);
        Assert.Equal(["A", "A"], session.MeasuredSetNames);
    }

    [Fact]
    public async Task Singleton_parameter_set_failure_has_one_failed_case_and_no_phantom_unexecuted_case()
    {
        var session = RecordingSession.Create(setCount: 1, failOnQueryNumber: 2);

        var outcome = await Module(session).ExecuteAsync(MeasureSets(1, Inputs("A")));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(outcome.BenchmarkRuns!);
        Assert.Equal(
            new MatrixExecutionCoverage(1, 0, 0, new MatrixFailedCase(0, SetName: "A", Phase: "sql")),
            outcome.Coverage);
        Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(["A"], session.WarmupSetNames);
        Assert.Empty(session.MeasuredSetNames);
    }

    [Fact]
    public async Task Intermediate_disposal_failure_does_not_count_case_complete_or_corrupt_matrix_coverage()
    {
        var disposeFailure = new IOException("session cleanup failed");
        var session = RecordingSession.Create(setCount: 2, failDisposeOnConnectionNumber: 1, disposeFailure: disposeFailure);

        var outcome = await Module(session).ExecuteAsync(MeasureSets(1, Inputs("A", "B")));

        var coverage = new MatrixExecutionCoverage(2, 0, 1, new MatrixFailedCase(0, SetName: "A", Phase: "sql"));
        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Equal(coverage, outcome.Coverage);
        Assert.Equal(coverage, outcome.Error?.MatrixStop);
        Assert.Empty(outcome.BenchmarkRuns!);
        Assert.Null(outcome.Report);
        Assert.Contains(disposeFailure.Message, outcome.SafeError, StringComparison.Ordinal);
        Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Cleanup_failure_does_not_replace_sql_failure_or_discard_completed_case_runs()
    {
        var sqlFailure = new TimeoutException("measured run failed");
        var session = RecordingSession.Create(
            setCount: 2,
            failOnQueryNumber: 5,
            failure: sqlFailure,
            failDisposeOnConnectionNumber: 2,
            disposeFailure: new IOException("session cleanup failed"));

        var exception = await Assert.ThrowsAsync<MeasureCaseFailedException>(() =>
            Runner(session).ExecuteAsync(Operation(repeat: 2), Prepared("A", "B")));

        Assert.Same(sqlFailure, exception.InnerException);
        Assert.Equal(new MeasureCaseFailure(1, "B", "sql"), exception.Execution.FailedCase);
        Assert.Equal(1, exception.Execution.CompletedCases);
        Assert.Equal(["A", "A"], exception.CompletedRuns.Select(run => run.Artifact.ParameterSet));
        Assert.Equal(["A"], exception.Execution.WarmupOrder);
        Assert.Equal(["A"], exception.PartialReport?.Sets.Select(set => set.Name));
        Assert.Equal(2, session.ConnectionCount);
        Assert.Equal(2, session.DisposeCount);
    }

    [Fact]
    public async Task Module_maps_artifact_failure_to_local_storage_without_a_report()
    {
        var session = RecordingSession.Create(setCount: 2);

        var outcome = await Module(session, writer: new ThrowingWriter()).ExecuteAsync(MeasureSets(1, Inputs("A", "B")));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("disk unavailable", outcome.SafeError, StringComparison.Ordinal);
        Assert.Equal(2, session.ConnectionCount);
        Assert.Equal(["A", "B"], session.WarmupSetNames);
        Assert.Equal(["A", "B"], session.MeasuredSetNames);
    }

    [Fact]
    public async Task Module_setup_failure_keeps_parameter_values_and_does_not_succeed()
    {
        const string fixedSecret = "fixed-secret-884422";
        const string setSecret = "set-secret-991991";
        var session = RecordingSession.Create(
            setupSql: "CREATE TABLE #ids (Id int); INSERT #ids (Id) SELECT Id FROM dbo.Clients WHERE Id = @id AND Tenant = @tenant AND Label = @label",
            querySql: "SELECT Value FROM dbo.Clients WHERE Id = @id AND Tenant = @tenant AND Label = @label",
            failSetup: true,
            failure: new TimeoutException(
                $"setup failed {fixedSecret} {setSecret} tenant:nvarchar={fixedSecret} label:nvarchar={setSecret}"));
        var operation = new SqlHarnessMeasureOperation(
            Target(),
            session.SetupSql,
            session.QuerySql,
            [$"tenant:nvarchar={fixedSecret}"],
            30,
            1,
            [
                new("A", ["id:int=11", $"label:nvarchar={setSecret}"]),
                new("B", ["id:int=22", $"label:nvarchar={setSecret}"]),
            ]);

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(1, session.ConnectionCount);
        Assert.Empty(session.WarmupSetNames);
        Assert.Empty(session.MeasuredSetNames);
        Assert.Contains(fixedSecret, outcome.SafeError, StringComparison.Ordinal);
        Assert.Contains(setSecret, outcome.SafeError, StringComparison.Ordinal);
        Assert.Contains($"tenant:nvarchar={fixedSecret}", outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("[REDACTED]", outcome.SafeError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_failure_keeps_numeric_prefix_values_and_varbinary_base64()
    {
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var base64 = Convert.ToBase64String(bytes);
        const string setupSql = "CREATE TABLE #ids (Id int); INSERT #ids (Id) SELECT Id FROM dbo.Clients WHERE Id = @n AND Payload = @blob";
        const string querySql = "SELECT Value FROM dbo.Clients WHERE Id = @n AND Payload = @blob";
        var session = RecordingSession.Create(
            setupSql: setupSql,
            querySql: querySql,
            failSetup: true,
            failure: new TimeoutException($"failed 1000 and 10000 and {base64}"));
        var operation = new SqlHarnessMeasureOperation(
            Target(),
            setupSql,
            querySql,
            [],
            30,
            1,
            [
                new("A", ["n:int=1000", $"blob:varbinary={base64}"]),
                new("B", ["n:int=10000", $"blob:varbinary={base64}"]),
            ]);

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains($"failed 1000 and 10000 and {base64}", outcome.SafeError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_measured_failure_does_not_return_success()
    {
        var session = RecordingSession.Create(
            setCount: 3,
            failOnQueryNumber: 4,
            failure: new TimeoutException("measured run failed"));

        var outcome = await Module(session).ExecuteAsync(MeasureSets(2, Inputs("A", "B", "C")));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.IsType<SqlHarnessMeasureSetReport>(outcome.Report);
        Assert.Equal(2, session.ConnectionCount);
        Assert.Equal(["A"], session.WarmupSetNames);
        Assert.Equal(["A", "A"], session.MeasuredSetNames);
        Assert.Contains("measured run failed", outcome.SafeError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Postgres_parameter_rejection_does_not_connect()
    {
        var session = RecordingSession.Create();
        var operation = new SqlHarnessMeasureOperation(
            new SqlTargetRequest("local-pg", new Dictionary<string, string>()),
            null,
            "SELECT @amount AS amount",
            [],
            30,
            1,
            [
                new("small", ["amount:money=12.50"]),
                new("large", ["amount:money=99.25"]),
            ]);

        var outcome = await Module(session, profiles: PostgresProfiles).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(0, session.ConnectionCount);
        Assert.Empty(session.Commands);
        Assert.Contains("SQL parameter type 'Money' is not supported on Postgres.", outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("12.50", outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("99.25", outcome.SafeError, StringComparison.Ordinal);
    }

    private static int QueryCount(RecordingSession session, int id) =>
        session.Commands.Count(command => command.Sql == QuerySql && ParameterId(command) == id);

    private static int ParameterId(SqlExecutionCommand command) =>
        Assert.IsType<int>(Assert.Single(command.Parameters, parameter => parameter.Name == "@id").Value);

    private static void AssertNoCacheControl(IEnumerable<SqlExecutionCommand> commands)
    {
        foreach (var command in commands)
        {
            Assert.DoesNotContain("FREEPROCCACHE", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sp_recompile", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OPTION(RECOMPILE)", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OPTION (RECOMPILE)", command.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DBCC", command.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static SessionRunner Runner(RecordingSession session, int? comparisonMaximumRows = null) =>
        new(session, comparisonMaximumRows ?? CanonicalComparisonAccumulator.MaximumComparedRows);

    private static SqlHarnessMeasureOperation Operation(int repeat, string? setupSql = SetupSql, int timeoutSeconds = 30) =>
        new(Target(), setupSql, QuerySql, [], timeoutSeconds, repeat);

    private static SqlHarnessMeasureOperation MeasureSets(
        int repeat,
        IReadOnlyList<SqlHarnessParameterSetInput> sets,
        string? setupSql = SetupSql,
        string querySql = QuerySql) =>
        new(Target(), setupSql, querySql, [], 30, repeat, sets);

    private static IReadOnlyList<PreparedMeasureParameterSet> Prepared(params string[] names) =>
        names.Select(name =>
        {
            var parameters = SqlParameterParser.Parse([$"id:int={IdFor(name)}"]);
            return new PreparedMeasureParameterSet(
                name,
                parameters,
                [new MeasureParameterMetadata("@id", "int")],
                TypedParameterHasher.Hash(parameters));
        }).ToArray();

    private static IReadOnlyList<SqlHarnessParameterSetInput> Inputs(params string[] names) =>
        names.Select(name => new SqlHarnessParameterSetInput(name, [$"id:int={IdFor(name)}"])).ToArray();

    private static int IdFor(string name)
    {
        if (name.Length != 1 || name[0] is < 'A' or > 'Z')
            throw new ArgumentOutOfRangeException(nameof(name));
        return (name[0] - 'A' + 1) * 11;
    }

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private static IReadOnlyDictionary<string, TargetProfile> PostgresProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["local-pg"] = new(
                "localhost,5432",
                "appdb",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "sqlharness",
                PasswordEnvVar: "SQLHARNESS_PG_PASSWORD",
                TrustServerCertificate: true,
                Engine: "postgres"),
        };

    private static SqlHarnessModule Module(
        RecordingSession session,
        Func<IReadOnlyDictionary<string, TargetProfile>>? profiles = null,
        ICompareArtifactWriter? writer = null,
        int? comparisonMaximumRows = null) =>
        new(session, writer ?? new CapturingWriter(), profiles ?? Profiles)
        {
            ComparisonMaximumRows = comparisonMaximumRows ?? CanonicalComparisonAccumulator.MaximumComparedRows,
        };

    private sealed class SessionRunner(RecordingSession session, int comparisonMaximumRows)
    {
        public ObservingDialect Dialect { get; } = new(SqlDialects.For(SqlEngine.SqlServer));

        public async Task<MeasureParameterSetExecution> ExecuteAsync(
            SqlHarnessMeasureOperation operation,
            IReadOnlyList<PreparedMeasureParameterSet> sets,
            CancellationToken cancellationToken = default,
            Action<int, IReadOnlyList<MaterializedParameterInfo>>? recordMaterializedConditions = null)
        {
            using var raw = new CanonicalResultAccumulator();
            session.UseSuppliedSession(sets.Count);
            return await new MeasureParameterSetRunner(Dialect, session, comparisonMaximumRows)
                .ExecuteAsync(
                    TargetResolver.Resolve(Target(), Profiles()),
                    operation,
                    sets,
                    raw,
                    cancellationToken,
                    recordMaterializedConditions: recordMaterializedConditions);
        }
    }

    private sealed record BenchmarkObservation(
        ISqlSession Session,
        string Sql,
        IReadOnlyList<SqlHarnessParameter> Parameters,
        int TimeoutSeconds,
        int Repetition,
        string Variant,
        bool CaptureComparison,
        int ComparisonMaximumRows,
        CancellationToken CancellationToken);

    private sealed class ObservingDialect(ISqlDialect inner) : ISqlDialect
    {
        public List<BenchmarkObservation> Calls { get; } = [];

        public SqlEngine Engine => inner.Engine;
        public SqlSafetyDecision Classify(
            string sql,
            SqlUsage usage,
            string? database,
            bool allowMutation,
            string? confirmDatabase,
            IReadOnlySet<string> sessionTempTables) =>
            inner.Classify(sql, usage, database, allowMutation, confirmDatabase, sessionTempTables);

        public IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs) =>
            inner.ParseParameters(inputs);

        public IReadOnlyList<SqlHarnessParameter> BindParameters(IEnumerable<SqlHarnessParameterInput> inputs) =>
            inner.BindParameters(inputs);

        public void ValidateParameterReferences(IReadOnlyList<SqlHarnessParameter> parameters, params string?[] batches) =>
            inner.ValidateParameterReferences(parameters, batches);

        public void ValidateMeasuredBatch(string sql) => inner.ValidateMeasuredBatch(sql);

        public Task<SessionState?> CaptureSessionStateAsync(ISqlSession session, CancellationToken ct) =>
            inner.CaptureSessionStateAsync(session, ct);

        public Task RestoreSessionStateAsync(ISqlSession session, SessionState? state, CancellationToken ct) =>
            inner.RestoreSessionStateAsync(session, state, ct);

        public Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
            ISqlSession session,
            string sql,
            IReadOnlyList<SqlHarnessParameter> parameters,
            int timeoutSeconds,
            int repetition,
            string variant,
            CanonicalResultAccumulator? raw,
            bool captureComparison,
            int comparisonMaximumRows,
            CancellationToken ct)
        {
            Calls.Add(new BenchmarkObservation(
                session,
                sql,
                parameters,
                timeoutSeconds,
                repetition,
                variant,
                captureComparison,
                comparisonMaximumRows,
                ct));
            return inner.ExecuteBenchmarkRunAsync(
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
        }
    }

    private sealed class CapturingWriter : ICompareArtifactWriter
    {
        public void CheckStorage() { /* This test writer stores artifacts in memory. */ }

        public int Writes { get; private set; }
        public object? Report { get; private set; }
        public IReadOnlyList<CompareRunArtifact> Runs { get; private set; } = [];
        public string? Target { get; private set; }

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
        {
            Writes++;
            Report = report;
            Runs = runs.ToArray();
            Target = target;
            return "measure-artifacts";
        }
    }

    private sealed class StartCaptureJournal : IActivityJournal
    {
        public OperationStart? Start { get; private set; }
        public JournalHandle? Begin(SessionIdentity session, OperationStart start)
        {
            Start = start;
            return new(1);
        }

        public bool Complete(JournalHandle? handle, OperationEnd end) => true;
        public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) { }
        public void RecordConditions(JournalHandle handle, ConditionsModel model) { }
    }

    private sealed class ThrowingWriter : ICompareArtifactWriter
    {
        public void CheckStorage() { /* This test writer stores artifacts in memory. */ }

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target) =>
            throw new IOException("disk unavailable");
    }

    private sealed class RecordingSession : ISqlSessionFactory, ISqlSession
    {
        private readonly string _setupSql;
        private readonly string _querySql;
        private readonly bool _failSetup;
        private readonly int? _failOnQueryNumber;
        private readonly Exception? _failure;
        private readonly int _resultRowCount;
        private readonly int? _failDisposeOnConnectionNumber;
        private readonly Exception? _disposeFailure;
        private readonly List<SessionMessage> _messages = [];
        private int _setCount;
        private int _queryCount;

        private RecordingSession(
            int setCount,
            string setupSql,
            string querySql,
            bool failSetup,
            int? failOnQueryNumber,
            Exception? failure,
            int resultRowCount,
            int? failDisposeOnConnectionNumber,
            Exception? disposeFailure)
        {
            _setCount = setCount;
            _setupSql = setupSql;
            _querySql = querySql;
            _failSetup = failSetup;
            _failOnQueryNumber = failOnQueryNumber;
            _failure = failure;
            _resultRowCount = resultRowCount;
            _failDisposeOnConnectionNumber = failDisposeOnConnectionNumber;
            _disposeFailure = disposeFailure;
        }

        public List<string> WarmupSetNames { get; } = [];
        public List<string> MeasuredSetNames { get; } = [];
        public List<SqlExecutionCommand> Commands { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public int SetupCount { get; private set; }
        public int ConnectionCount { get; private set; }
        public int DisposeCount { get; private set; }
        public string SetupSql => _setupSql;
        public string QuerySql => _querySql;
        public IReadOnlyList<SessionMessage> Messages => _messages;
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        public static RecordingSession Create(
            int setCount = 0,
            string? setupSql = null,
            string? querySql = null,
            bool failSetup = false,
            int? failOnQueryNumber = null,
            Exception? failure = null,
            int resultRowCount = 1,
            int? failDisposeOnConnectionNumber = null,
            Exception? disposeFailure = null) =>
            new(
                setCount,
                setupSql ?? MeasureParameterSetExecutionTests.SetupSql,
                querySql ?? MeasureParameterSetExecutionTests.QuerySql,
                failSetup,
                failOnQueryNumber,
                failure,
                resultRowCount,
                failDisposeOnConnectionNumber,
                disposeFailure);

        public void UseSuppliedSession(int setCount)
        {
            if (setCount < 1)
                throw new ArgumentOutOfRangeException(nameof(setCount));
            _setCount = setCount;
        }

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ConnectionCount++;
            return Task.FromResult<ISqlSession>(this);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            Tokens.Add(ct);
            if (ct.IsCancellationRequested)
            {
                var canceled = new OperationCanceledException(ct);
                return Task.FromException<ISqlReader>(canceled);
            }

            if (command.Sql.Contains("sys.table_types", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Catalog());

            if (command.Sql == "SELECT Id FROM dbo.SourceA")
                return Task.FromResult<ISqlReader>(FakeReader.Source(11));

            if (command.Sql == "SELECT Id FROM dbo.SourceB")
                return Task.FromResult<ISqlReader>(FakeReader.Source(22));

            if (command.Sql.Contains("STATISTICS", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Empty());

            if (command.Sql.StartsWith("SELECT @@OPTIONS", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.SessionState());

            if (command.Sql.StartsWith("SET NOCOUNT", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Empty());

            if (command.Sql.Contains("CREATE TABLE #ids", StringComparison.Ordinal))
            {
                if (_failSetup)
                    return Task.FromException<ISqlReader>(_failure ?? new TimeoutException("setup failed"));
                SetupCount++;
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }

            if (command.Sql.Contains("INSERT #ids", StringComparison.Ordinal))
            {
                if (_failSetup)
                    return Task.FromException<ISqlReader>(_failure ?? new TimeoutException("setup failed"));
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }

            if (!string.Equals(command.Sql, _querySql, StringComparison.Ordinal)
                && !string.Equals(command.Sql, _querySql + " -- candidate", StringComparison.Ordinal))
                return Task.FromException<ISqlReader>(new InvalidOperationException("Unexpected SQL batch."));

            var number = _queryCount + 1;
            if (_failOnQueryNumber == number)
                return Task.FromException<ISqlReader>(_failure ?? new TimeoutException("measured run failed"));

            _queryCount = number;
            if (TryGetSetName(command, out var setName))
            {
                if (!WarmupSetNames.Contains(setName, StringComparer.Ordinal))
                    WarmupSetNames.Add(setName);
                else
                    MeasuredSetNames.Add(setName);
            }

            _messages.Add(new SessionMessage(3615,
                "Table 'Clients'. Scan count 1, logical reads 5, physical reads 0, lob logical reads 0."));
            _messages.Add(new SessionMessage(3613,
                "SQL Server Execution Times: CPU time = 10 ms, elapsed time = 12 ms."));
            object?[][] rows = Enumerable.Range(0, _resultRowCount)
                .Select(index => new object?[] { 42 + index })
                .ToArray();
            return Task.FromResult<ISqlReader>(FakeReader.WithPlan(["Value"], rows, Plan));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (_failDisposeOnConnectionNumber == ConnectionCount)
                return ValueTask.FromException(_disposeFailure ?? new IOException("session cleanup failed"));
            return ValueTask.CompletedTask;
        }

        private static bool TryGetSetName(SqlExecutionCommand command, out string name)
        {
            name = string.Empty;
            var parameter = command.Parameters.FirstOrDefault(candidate =>
                candidate.Name.Equals("@id", StringComparison.OrdinalIgnoreCase));
            if (parameter?.Value is not int id || id % 11 != 0)
                return false;

            var index = id / 11;
            if (index is < 1 or > 26)
                return false;

            name = ((char)('A' + index - 1)).ToString();
            return true;
        }

        private static string StatisticsMessage(long reads, int cpu, int elapsed) =>
            $"Table 'Clients'. Scan count 1, logical reads {reads}, physical reads 0, lob logical reads 0.\nSQL Server Execution Times: CPU time = {cpu} ms, elapsed time = {elapsed} ms.";
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
        public static FakeReader SessionState() => new(
            [new(["Options", "Isolation", "LockTimeout", "DateFirst", "DateFormat", "DeadlockPriority"], [[29560, 2, -1, 7, "mdy", 0]])]);
        public static FakeReader Catalog() => new(
            [new(["QualifiedName", "ColumnName", "TypeName", "Size", "Precision", "Scale", "Nullable"],
                [["[dbo].[IdList]", "Id", "int", 4, 10, 0, false]])]);
        public static FakeReader Source(int id) => new([new(["Id"], [[id]])]);
        public static FakeReader WithPlan(string[] names, object?[][] rows, string plan) =>
            new([new(names, rows), new(["Microsoft SQL Server 2005 XML Showplan"], [[plan]])]);
        public string GetName(int ordinal) => Current.Names[ordinal];
        public Type GetFieldType(int ordinal) => Current.Rows[0][ordinal]?.GetType() ?? typeof(object);
        public string GetSqlTypeName(int ordinal) => Current.Names[ordinal] == "Id" ? "int" : GetFieldType(ordinal).Name;
        public SqlReaderColumnMetadata GetColumnMetadata(int ordinal) => Current.Names[ordinal] == "Id"
            ? new("int", 4, 10, 0)
            : new(GetSqlTypeName(ordinal), null, null, null);
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
