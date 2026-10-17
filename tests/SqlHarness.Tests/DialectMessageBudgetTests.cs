using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Tests;

public sealed class DialectMessageBudgetTests
{
    [Fact]
    public async Task SqlServer_repeated_runs_keep_statistics_and_release_session_memory()
    {
        var session = new DrainingSession(command =>
            command.Sql.StartsWith("SET", StringComparison.Ordinal) ? Empty() : SingleRow());
        session.OnExecute = command =>
        {
            if (!command.Sql.StartsWith("SET", StringComparison.Ordinal))
                EmitStatisticsMessage(session, reads: 5, cpu: 10, elapsed: 12);
        };
        var dialect = new SqlServerDialect();

        for (var repetition = 1; repetition <= 3; repetition++)
        {
            using var raw = new CanonicalResultAccumulator();
            var run = await dialect.ExecuteBenchmarkRunAsync(
                session, "SELECT Value", [], 30, repetition, "measure", raw,
                captureComparison: true, comparisonMaximumRows: 1000, CancellationToken.None);

            Assert.Equal(5, run.Artifact.LogicalReads);
            Assert.Equal(10, run.Artifact.CpuTimeMilliseconds);
            Assert.Equal(12, run.Artifact.ElapsedTimeMilliseconds);
            Assert.Null(run.Artifact.Metrics);
            Assert.Equal(3, run.Artifact.MessageCount);
            Assert.NotEmpty(run.Artifact.ResultHash);
            Assert.Empty(session.Messages);
        }
    }

    [Fact]
    public async Task Table_io_survives_message_and_character_caps()
    {
        var session = new DrainingSession(command =>
            command.Sql.StartsWith("SET", StringComparison.Ordinal) ? Empty() : SingleRow());
        session.OnExecute = command =>
        {
            if (command.Sql.StartsWith("SET", StringComparison.Ordinal))
                return;
            for (var index = 0; index < 1500; index++)
                session.Emit(3615,
                    "Table 'T'. Scan count 1, logical reads 2, physical reads 0, lob logical reads 1.\n" + new string('x', 200));
        };
        using var raw = new CanonicalResultAccumulator();

        var run = await new SqlServerDialect().ExecuteBenchmarkRunAsync(
            session, "SELECT Value", [], 30, 1, "measure", raw,
            captureComparison: true, comparisonMaximumRows: 1000, CancellationToken.None);

        Assert.True(run.Artifact.MessageCount < 1500);
        Assert.True(run.Artifact.TableIoComplete);
        var table = Assert.Single(run.Artifact.TableIo);
        Assert.Equal("T", table.Table);
        Assert.Equal(1500, table.ScanCount);
        Assert.Equal(3000, table.LogicalReads);
        Assert.Equal(1500, table.LobLogicalReads);
        Assert.NotNull(run.Artifact.Metrics);
        Assert.Contains("omitted", Assert.Single(run.Artifact.Metrics!.Warnings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task User_messages_imitating_statistics_are_ignored()
    {
        var session = new DrainingSession(command =>
            command.Sql.StartsWith("SET", StringComparison.Ordinal) ? Empty() : SingleRow());
        session.OnExecute = command =>
        {
            if (!command.Sql.StartsWith("SET", StringComparison.Ordinal))
                session.Emit(0,
                    "Table 'X'. Scan count 1, logical reads 999, physical reads 0, lob logical reads 0.\n" +
                    "SQL Server Execution Times: CPU time = 90 ms, elapsed time = 91 ms.");
        };
        using var raw = new CanonicalResultAccumulator();

        var run = await new SqlServerDialect().ExecuteBenchmarkRunAsync(
            session, "SELECT Value", [], 30, 1, "measure", raw,
            captureComparison: true, comparisonMaximumRows: 1000, CancellationToken.None);

        Assert.Equal(0, run.Artifact.LogicalReads);
        Assert.Equal(0, run.Artifact.CpuTimeMilliseconds);
        Assert.Equal(0, run.Artifact.ElapsedTimeMilliseconds);
        Assert.Empty(run.Artifact.TableIo);
        Assert.True(run.Artifact.TableIoComplete);
    }

    [Fact]
    public async Task SqlServer_trailing_statistics_parse_under_avalanche_and_keep_io_available()
    {
        var session = new DrainingSession(command =>
            command.Sql.StartsWith("SET", StringComparison.Ordinal) ? Empty() : SingleRow());
        session.OnExecute = command =>
        {
            if (command.Sql.StartsWith("SET", StringComparison.Ordinal))
                return;
            // A PRINT avalanche arrives before STATISTICS IO: the sliding window
            // keeps the trailing STATISTICS lines. The read accumulator also
            // preserves complete IO if those trailing lines are later evicted.
            for (var index = 0; index < 2500; index++)
                session.Emit(0, $"diagnostic {index}");
            EmitStatisticsMessage(session, reads: 5, cpu: 10, elapsed: 12);
        };
        var dialect = new SqlServerDialect();
        using var raw = new CanonicalResultAccumulator();

        var run = await dialect.ExecuteBenchmarkRunAsync(
            session, "SELECT Value", [], 30, 1, "measure", raw,
            captureComparison: true, comparisonMaximumRows: 1000, CancellationToken.None);

        // 2500 prints plus three STATISTICS messages minus 1000 retained arrivals.
        Assert.Equal(5, run.Artifact.LogicalReads);
        Assert.NotNull(run.Artifact.Metrics);
        var metrics = run.Artifact.Metrics!;
        Assert.Equal(BenchmarkMetricReport.Measured, metrics.LogicalReadsAvailability);
        Assert.Equal(BenchmarkMetricReport.Unavailable, metrics.CpuTimeAvailability);
        Assert.Equal(BenchmarkMetricReport.Unavailable, metrics.ElapsedTimeAvailability);
        var warning = Assert.Single(metrics.Warnings);
        Assert.Contains("1503", warning, StringComparison.Ordinal);
        Assert.Contains("omitted", warning, StringComparison.OrdinalIgnoreCase);
        // Equivalence inputs stay complete even though presentation metrics are not.
        Assert.NotEmpty(run.Artifact.ResultHash);
        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task SqlServer_evicted_statistics_keep_accumulated_reads()
    {
        var session = new DrainingSession(command =>
            command.Sql.StartsWith("SET", StringComparison.Ordinal) ? Empty() : SingleRow());
        session.OnExecute = command =>
        {
            if (command.Sql.StartsWith("SET", StringComparison.Ordinal))
                return;
            // STATISTICS IO arrives early and is evicted by the later avalanche;
            // the accumulator preserves the read total independently of text.
            EmitStatisticsMessage(session, reads: 5, cpu: 10, elapsed: 12);
            for (var index = 0; index < 2500; index++)
                session.Emit(0, $"diagnostic {index}");
        };
        var dialect = new SqlServerDialect();
        using var raw = new CanonicalResultAccumulator();

        var run = await dialect.ExecuteBenchmarkRunAsync(
            session, "SELECT Value", [], 30, 1, "measure", raw,
            captureComparison: true, comparisonMaximumRows: 1000, CancellationToken.None);

        Assert.Equal(5, run.Artifact.LogicalReads);
        Assert.NotNull(run.Artifact.Metrics);
        var metrics = run.Artifact.Metrics!;
        Assert.Equal(BenchmarkMetricReport.Measured, metrics.LogicalReadsAvailability);
        Assert.Equal(BenchmarkMetricReport.Unavailable, metrics.CpuTimeAvailability);
        Assert.Equal(BenchmarkMetricReport.Unavailable, metrics.ElapsedTimeAvailability);
        var warning = Assert.Single(metrics.Warnings);
        Assert.Contains("1503", warning, StringComparison.Ordinal);
        Assert.Contains("omitted", warning, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(run.Artifact.ResultHash);
        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task SqlServer_unrecognized_statistics_time_marks_run_metrics_unavailable()
    {
        var session = new DrainingSession(command =>
            command.Sql.StartsWith("SET", StringComparison.Ordinal) ? Empty() : SingleRow());
        session.OnExecute = command =>
        {
            if (command.Sql.StartsWith("SET", StringComparison.Ordinal))
                return;
            // Localized STATISTICS text contains no English TIME block. A real
            // server still sent one; the zeros below are not measured zeros.
            session.Emit(3615, "Tabelle 'X'. Scananzahl 1, logische Lesevorgänge 5");
            session.Emit(3613, "SQL Server-Ausführungszeiten:\n   CPU-Zeit = 12 ms, verstrichene Zeit = 20 ms.");
        };
        var dialect = new SqlServerDialect();
        using var raw = new CanonicalResultAccumulator();

        var run = await dialect.ExecuteBenchmarkRunAsync(
            session, "SELECT Value", [], 30, 1, "measure", raw,
            captureComparison: true, comparisonMaximumRows: 1000, CancellationToken.None);

        Assert.Equal(0, run.Artifact.LogicalReads);
        Assert.Equal(0, run.Artifact.CpuTimeMilliseconds);
        Assert.Equal(0, run.Artifact.ElapsedTimeMilliseconds);
        Assert.NotNull(run.Artifact.Metrics);
        var metrics = run.Artifact.Metrics!;
        Assert.Equal(BenchmarkMetricReport.Unavailable, metrics.CpuTimeAvailability);
        Assert.Equal(BenchmarkMetricReport.Unavailable, metrics.LogicalReadsAvailability);
        Assert.Equal(BenchmarkMetricReport.Unavailable, metrics.ElapsedTimeAvailability);
        var warning = Assert.Single(metrics.Warnings);
        Assert.Contains("was not recognized", warning, StringComparison.Ordinal);
        Assert.NotEmpty(run.Artifact.ResultHash);
        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task Postgres_truncated_notices_warn_without_changing_explain_metrics()
    {
        const string planJson = """[{"Plan":{"Node Type":"Seq Scan","Relation Name":"foo","Schema":"public","Shared Hit Blocks":2},"Planning Time":1.25,"Execution Time":9.5}]""";
        var session = new DrainingSession(command =>
            command.Sql.StartsWith("EXPLAIN", StringComparison.Ordinal)
                ? ExplainReader(planJson)
                : SingleRow());
        session.OnExecute = _ =>
        {
            for (var index = 0; index < 1500; index++)
                session.Emit(0, $"NOTICE: row {index}");
        };
        using var raw = new CanonicalResultAccumulator();

        var run = await PostgresBenchmark.ExecuteBenchmarkRunAsync(
            session, "SELECT * FROM public.foo", [], 30, 2, "measure", raw,
            captureComparison: false, comparisonMaximumRows: 1000, CancellationToken.None);

        // Metrics come from EXPLAIN, so values are unaffected and stay measured.
        Assert.Equal(11, run.Artifact.ElapsedTimeMilliseconds);
        Assert.Equal(2, run.Artifact.LogicalReads);
        Assert.NotNull(run.Artifact.Metrics);
        var metrics = run.Artifact.Metrics!;
        Assert.Equal(BenchmarkMetricReport.Measured, metrics.LogicalReadsAvailability);
        var warning = Assert.Single(
            metrics.Warnings,
            text => text.Contains("omitted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("1000", warning, StringComparison.Ordinal);
        Assert.Empty(session.Messages);
    }

    private static void EmitStatisticsMessage(DrainingSession session, long reads, int cpu, int elapsed)
    {
        session.Emit(3615, $"Table 'Clients'. Scan count 1, logical reads {reads}, physical reads 0, lob logical reads 0.");
        session.Emit(3613, "SQL Server parse and compile time: CPU time = 0 ms, elapsed time = 0 ms.");
        session.Emit(3612, $"SQL Server Execution Times: CPU time = {cpu} ms, elapsed time = {elapsed} ms.");
    }

    private static ISqlReader Empty() =>
        new ScriptReader([], [], []);

    private static ISqlReader SingleRow() =>
        new ScriptReader(["Value"], [typeof(int)], [[1]]);

    private static ISqlReader ExplainReader(string planJson) =>
        new ScriptReader(["QUERY PLAN"], [typeof(string)], [[planJson]]);

    private sealed class DrainingSession(Func<SqlExecutionCommand, ISqlReader> readers) : ISqlSession
    {
        private readonly SessionMessageBuffer _buffer = new();

        public Action<SqlExecutionCommand>? OnExecute { get; set; }

        public void Emit(int number, string message) => _buffer.Add(number, message);

        public IReadOnlyList<SessionMessage> Messages => _buffer.Snapshot();
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("server", "db", "server", "db", "profile");

        // Production-like per-command consumption: the buffer is drained on every
        // consume, so repetitions reuse bounded memory.
        public ConsumedSessionMessages ConsumeMessages(int startIndex) => _buffer.Consume(startIndex);

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            OnExecute?.Invoke(command);
            return Task.FromResult(readers(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptReader(string[] names, Type[] types, object?[][] rows) : ISqlReader
    {
        private int _position = -1;

        public int FieldCount => names.Length;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => names[ordinal];
        public Type GetFieldType(int ordinal) => types[ordinal];
        public bool GetAllowNull(int ordinal) => true;
        public object GetValue(int ordinal) => rows[_position][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            _position++;
            return Task.FromResult(_position < rows.Length);
        }

        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
