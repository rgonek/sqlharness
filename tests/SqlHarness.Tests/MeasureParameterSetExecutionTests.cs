using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class MeasureParameterSetExecutionTests
{
    private const string SetupSql = "SELECT Id INTO #ids FROM dbo.Clients WHERE Id = @id";
    private const string QuerySql = "SELECT Value FROM dbo.Clients WHERE Id = @id";
    private const string Plan = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Table=\"[Clients]\" /></IndexScan></RelOp></BatchSequence></ShowPlanXML>";
    private const string MeasuredOrderRule =
        "In one-based round r, measured execution starts at index r modulo setCount and wraps in user-supplied order.";
    private const string PlanCacheWarning =
        "Parameter-set measurements use the observed server plan-cache state; SQLHarness did not clear or isolate the plan cache.";

    [Fact]
    public async Task Three_sets_warm_in_input_order_and_rotate_each_round()
    {
        var session = RecordingSession.Create();
        var runner = Runner(session);
        var execution = await runner.ExecuteAsync(Operation(repeat: 3), Prepared("A", "B", "C"));

        Assert.Equal(["A", "B", "C"], session.WarmupSetNames);
        Assert.Equal(
            ["B", "C", "A", "C", "A", "B", "A", "B", "C"],
            session.MeasuredSetNames);
        Assert.Equal(1, session.SetupCount);
        Assert.Equal(1, session.ConnectionCount);

        Assert.Equal(1, execution.SetupExecutionCount);
        Assert.Equal(["A", "B", "C"], execution.WarmupOrder);
        Assert.Equal(
            ["B", "C", "A", "C", "A", "B", "A", "B", "C"],
            execution.Runs.Select(run => run.Artifact.ParameterSet));
        Assert.Equal([1, 1, 1, 2, 2, 2, 3, 3, 3], execution.Runs.Select(run => run.Artifact.Repetition));
        Assert.Equal([0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3], runner.Dialect.Calls.Select(call => call.Repetition));
        Assert.All(runner.Dialect.Calls, call =>
        {
            Assert.False(call.CaptureComparison);
            Assert.Equal("measure", call.Variant);
            Assert.Equal(QuerySql, call.Sql);
            Assert.Equal(30, call.TimeoutSeconds);
            Assert.Same(session, call.Session);
        });
        Assert.Equal(11, ParameterId(Assert.Single(session.Commands, command => command.Sql == SetupSql)));
        AssertNoCacheControl(session.Commands);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task No_setup_still_warms_in_input_order_and_rotates_one_round(string? setupSql)
    {
        var session = RecordingSession.Create();
        var execution = await Runner(session).ExecuteAsync(Operation(repeat: 1, setupSql: setupSql), Prepared("A", "B"));

        Assert.Equal(0, session.SetupCount);
        Assert.Equal(0, execution.SetupExecutionCount);
        Assert.DoesNotContain(session.Commands, command => command.Sql == SetupSql);
        Assert.Equal(["A", "B"], session.WarmupSetNames);
        Assert.Equal(["B", "A"], session.MeasuredSetNames);
        Assert.Equal(1, session.ConnectionCount);
    }

    [Fact]
    public async Task Each_round_executes_every_set_once()
    {
        var session = RecordingSession.Create();
        var execution = await Runner(session).ExecuteAsync(Operation(repeat: 4), Prepared("A", "B"));

        Assert.Equal(["A", "B"], session.WarmupSetNames);
        Assert.Equal(["B", "A", "A", "B", "B", "A", "A", "B"], session.MeasuredSetNames);
        Assert.Equal(8, execution.Runs.Count);
        for (var index = 0; index < execution.Runs.Count; index += 2)
        {
            var round = session.MeasuredSetNames.Skip(index).Take(2).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Equal(["A", "B"], round);
            Assert.Equal(index / 2 + 1, execution.Runs[index].Artifact.Repetition);
            Assert.Equal(execution.Runs[index].Artifact.Repetition, execution.Runs[index + 1].Artifact.Repetition);
        }

        Assert.All(["A", "B"], name => Assert.Equal(4, session.MeasuredSetNames.Count(candidate => candidate == name)));
    }

    [Fact]
    public async Task Repeat_one_executes_a_single_rotated_round()
    {
        var session = RecordingSession.Create();
        var execution = await Runner(session).ExecuteAsync(Operation(repeat: 1), Prepared("A", "B", "C"));

        Assert.Equal(["A", "B", "C"], session.WarmupSetNames);
        Assert.Equal(["B", "C", "A"], session.MeasuredSetNames);
        Assert.Equal([1, 1, 1], execution.Runs.Select(run => run.Artifact.Repetition));
        Assert.All(execution.Runs, run => Assert.Single(run.PlanHashes));
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

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            Runner(session).ExecuteAsync(Operation(repeat: 3), Prepared("A", "B", "C")));

        Assert.Same(failure, exception);
        Assert.Equal(0, session.SetupCount);
        Assert.Empty(session.WarmupSetNames);
        Assert.Empty(session.MeasuredSetNames);
        Assert.Contains(session.Commands, command => command.Sql == SetupSql);
        Assert.DoesNotContain(session.Commands, command => command.Sql == QuerySql);
    }

    [Fact]
    public async Task Warmup_failure_stops_before_later_sets()
    {
        var failure = new TimeoutException("warmup failed");
        var session = RecordingSession.Create(failOnQueryNumber: 2, failure: failure);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            Runner(session).ExecuteAsync(Operation(repeat: 3), Prepared("A", "B", "C")));

        Assert.Same(failure, exception);
        Assert.Equal(["A"], session.WarmupSetNames);
        Assert.Empty(session.MeasuredSetNames);
        Assert.Equal(1, QueryCount(session, 11));
        Assert.Equal(1, QueryCount(session, 22));
        Assert.Equal(0, QueryCount(session, 33));
    }

    [Fact]
    public async Task Measured_failure_stops_without_a_successful_result()
    {
        var failure = new TimeoutException("measured run failed");
        var session = RecordingSession.Create(failOnQueryNumber: 3, failure: failure);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            Runner(session).ExecuteAsync(Operation(repeat: 2), Prepared("A", "B")));

        Assert.Same(failure, exception);
        Assert.Equal(["A", "B"], session.WarmupSetNames);
        Assert.Empty(session.MeasuredSetNames);
        Assert.Equal(1, QueryCount(session, 11));
        Assert.Equal(2, QueryCount(session, 22));
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
        Assert.Equal(caller.Token, Assert.Single(session.Tokens));
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
    public async Task Empty_parameter_sets_stay_on_the_single_set_path()
    {
        var session = RecordingSession.Create();
        var operation = Operation(repeat: 1) with
        {
            ParameterSets = [],
            Parameters = ["id:int=42"],
        };

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
        Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(1, session.SetupCount);
        Assert.All(session.Commands.Where(command => command.Sql is SetupSql or QuerySql), command =>
            Assert.Equal(42, Assert.IsType<int>(Assert.Single(command.Parameters).Value)));
    }

    [Fact]
    public async Task Module_rotates_on_one_session_and_returns_set_report()
    {
        const string tenant = "acme-secret-884422";
        var session = RecordingSession.Create(
            setCount: 2,
            resultRowCount: 3,
            setupSql: "SELECT Id INTO #ids FROM dbo.Clients WHERE Id = @id AND Tenant = @tenant",
            querySql: "SELECT Value FROM dbo.Clients WHERE Id = @id AND Tenant = @tenant");
        var writer = new CapturingWriter();
        var gain = new FakeGainStore();
        var operation = new SqlHarnessMeasureOperation(
            Target(),
            session.SetupSql,
            session.QuerySql,
            [$"tenant:nvarchar={tenant}"],
            17,
            2,
            Inputs("A", "B"));

        var outcome = await Module(session, writer: writer, comparisonMaximumRows: 2, gain: gain).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Null(outcome.SafeError);
        Assert.Equal(1, writer.Writes);
        Assert.Equal("testdb-a", writer.Target);
        Assert.Equal(["B", "A", "A", "B"], writer.Runs.Select(run => run.ParameterSet));
        Assert.Equal([1, 1, 2, 2], writer.Runs.Select(run => run.Repetition));
        Assert.DoesNotContain(writer.Runs, run => run.Repetition == 0);
        Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(1, session.SetupCount);
        Assert.Equal(["A", "B"], session.WarmupSetNames);
        Assert.Equal(["B", "A", "A", "B"], session.MeasuredSetNames);
        var setup = Assert.Single(session.Commands, command => command.Sql == session.SetupSql);
        Assert.Equal(["@tenant", "@id"], setup.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(tenant, setup.Parameters[0].Value);
        Assert.Equal(11, setup.Parameters[1].Value);
        Assert.All(session.Commands, command => Assert.Equal(17, command.TimeoutSeconds));
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
        Assert.Equal(1, report.SetupExecutionCount);
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
        Assert.Contains(setA.ValueHash, json, StringComparison.Ordinal);
        Assert.Contains(setB.ValueHash, json, StringComparison.Ordinal);

        Assert.Empty(gain.Records);
        Assert.Equal(
            SqlHarnessExitCode.Success,
            await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new OutputFootprint(4, 1)));
        var gainRecord = Assert.Single(gain.Records);
        Assert.Equal("measure", gainRecord.Command);
        Assert.True(gainRecord.Success);
        Assert.True(gainRecord.RawBytes > 0);
        Assert.DoesNotContain(tenant, gainRecord.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_maps_artifact_failure_to_local_storage_without_a_report()
    {
        var session = RecordingSession.Create(setCount: 2);

        var outcome = await Module(session, writer: new ThrowingWriter()).ExecuteAsync(MeasureSets(1, Inputs("A", "B")));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("disk unavailable", outcome.SafeError, StringComparison.Ordinal);
        Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(["A", "B"], session.WarmupSetNames);
        Assert.Equal(["B", "A"], session.MeasuredSetNames);
    }

    [Fact]
    public async Task Module_setup_failure_redacts_parameter_values_and_does_not_succeed()
    {
        const string fixedSecret = "fixed-secret-884422";
        const string setSecret = "set-secret-991991";
        var session = RecordingSession.Create(
            setupSql: "SELECT Id INTO #ids FROM dbo.Clients WHERE Id = @id AND Tenant = @tenant AND Label = @label",
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
        Assert.DoesNotContain(fixedSecret, outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain(setSecret, outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain($"tenant:nvarchar={fixedSecret}", outcome.SafeError, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcome.SafeError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_failure_redacts_longer_prefix_and_varbinary_base64()
    {
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var base64 = Convert.ToBase64String(bytes);
        const string setupSql = "SELECT Id INTO #ids FROM dbo.Clients WHERE Id = @n AND Payload = @blob";
        const string querySql = "SELECT Value FROM dbo.Clients WHERE Id = @n AND Payload = @blob";
        var session = RecordingSession.Create(
            setupSql: setupSql,
            querySql: querySql,
            failSetup: true,
            failure: new TimeoutException($"failed 1 and 100 and {base64}"));
        var operation = new SqlHarnessMeasureOperation(
            Target(),
            setupSql,
            querySql,
            [],
            30,
            1,
            [
                new("A", ["n:int=1", $"blob:varbinary={base64}"]),
                new("B", ["n:int=100", $"blob:varbinary={base64}"]),
            ]);

        var outcome = await Module(session).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.DoesNotContain("100", outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("[REDACTED]00", outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain(base64, outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Byte[]", outcome.SafeError, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcome.SafeError, StringComparison.Ordinal);
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
        Assert.Null(outcome.Report);
        Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(["A", "B", "C"], session.WarmupSetNames);
        Assert.Empty(session.MeasuredSetNames);
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
        int? comparisonMaximumRows = null,
        FakeGainStore? gain = null) =>
        new(session, gain ?? new FakeGainStore(), writer ?? new CapturingWriter(), profiles ?? Profiles)
        {
            ComparisonMaximumRows = comparisonMaximumRows ?? CanonicalComparisonAccumulator.MaximumComparedRows,
        };

    private sealed class SessionRunner(RecordingSession session, int comparisonMaximumRows)
    {
        public ObservingDialect Dialect { get; } = new(SqlDialects.For(SqlEngine.SqlServer));

        public async Task<MeasureParameterSetExecution> ExecuteAsync(
            SqlHarnessMeasureOperation operation,
            IReadOnlyList<PreparedMeasureParameterSet> sets,
            CancellationToken cancellationToken = default)
        {
            session.UseSuppliedSession(sets.Count);
            using var raw = new CanonicalResultAccumulator();
            return await new MeasureParameterSetRunner(Dialect, comparisonMaximumRows)
                .ExecuteAsync(session, operation, sets, raw, cancellationToken);
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
        public string PingSql => inner.PingSql;
        public string CountsCatalogSql => inner.CountsCatalogSql;
        public string SchemaSql => inner.SchemaSql;
        public string SpaceSql => inner.SpaceSql;

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

        public string BuildCountsExactSql(IReadOnlyList<ResolvedCountObject> objects) =>
            inner.BuildCountsExactSql(objects);

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

    private sealed class FakeGainStore : IGainStore
    {
        public List<GainRecord> Records { get; } = [];

        public void Append(GainRecord record) => Records.Add(record);

        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class CapturingWriter : ICompareArtifactWriter
    {
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

    private sealed class ThrowingWriter : ICompareArtifactWriter
    {
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
        private readonly List<string> _messages = [];
        private int _setCount;
        private int _queryCount;

        private RecordingSession(
            int setCount,
            string setupSql,
            string querySql,
            bool failSetup,
            int? failOnQueryNumber,
            Exception? failure,
            int resultRowCount)
        {
            _setCount = setCount;
            _setupSql = setupSql;
            _querySql = querySql;
            _failSetup = failSetup;
            _failOnQueryNumber = failOnQueryNumber;
            _failure = failure;
            _resultRowCount = resultRowCount;
        }

        public List<string> WarmupSetNames { get; } = [];
        public List<string> MeasuredSetNames { get; } = [];
        public List<SqlExecutionCommand> Commands { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public int SetupCount { get; private set; }
        public int ConnectionCount { get; private set; }
        public string SetupSql => _setupSql;
        public string QuerySql => _querySql;
        public IReadOnlyList<string> Messages => _messages;
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        public static RecordingSession Create(
            int setCount = 0,
            string? setupSql = null,
            string? querySql = null,
            bool failSetup = false,
            int? failOnQueryNumber = null,
            Exception? failure = null,
            int resultRowCount = 1) =>
            new(
                setCount,
                setupSql ?? MeasureParameterSetExecutionTests.SetupSql,
                querySql ?? MeasureParameterSetExecutionTests.QuerySql,
                failSetup,
                failOnQueryNumber,
                failure,
                resultRowCount);

        public void UseSuppliedSession(int setCount)
        {
            if (setCount < 1)
                throw new ArgumentOutOfRangeException(nameof(setCount));
            if (ConnectionCount != 0)
                throw new InvalidOperationException("The physical session was already opened.");
            _setCount = setCount;
            ConnectionCount = 1;
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

            if (command.Sql.Contains("STATISTICS", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(FakeReader.Empty());

            if (string.Equals(command.Sql, _setupSql, StringComparison.Ordinal))
            {
                if (_failSetup)
                    return Task.FromException<ISqlReader>(_failure ?? new TimeoutException("setup failed"));
                SetupCount++;
                return Task.FromResult<ISqlReader>(FakeReader.Empty());
            }

            if (!string.Equals(command.Sql, _querySql, StringComparison.Ordinal))
                return Task.FromException<ISqlReader>(new InvalidOperationException("Unexpected SQL batch."));

            var number = _queryCount + 1;
            if (_failOnQueryNumber == number)
                return Task.FromException<ISqlReader>(_failure ?? new TimeoutException("measured run failed"));

            _queryCount = number;
            if (TryGetSetName(command, out var setName))
            {
                if (number <= _setCount)
                    WarmupSetNames.Add(setName);
                else
                    MeasuredSetNames.Add(setName);
            }

            _messages.Add(StatisticsMessage(5, 10, 12));
            object?[][] rows = Enumerable.Range(0, _resultRowCount)
                .Select(index => new object?[] { 42 + index })
                .ToArray();
            return Task.FromResult<ISqlReader>(FakeReader.WithPlan(["Value"], rows, Plan));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
        public static FakeReader WithPlan(string[] names, object?[][] rows, string plan) =>
            new([new(names, rows), new(["Microsoft SQL Server 2005 XML Showplan"], [[plan]])]);
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