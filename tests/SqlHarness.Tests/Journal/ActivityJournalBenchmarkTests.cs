using System.IO.Compression;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class ActivityJournalBenchmarkTests
{
    private static IActivityJournal Open(JournalTempDirectory temp, bool storeSensitive) =>
        ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));

    private static BenchmarkJournalRecord Record(string document = PlanMetricsExtractorTests.ActualPlan) =>
        JournalBenchmarkBuilder.Build(
        [
            new CompareRunArtifact("baseline", 1, 10, 12, 5, new Dictionary<string, long> { ["Orders"] = 5 }, "h", [document], 1)
            {
                TableIo = [new TableIoCounters("Orders", 1, 5, 2, 0, 1, 0, 0, 0)],
                MatrixCell = 2,
            },
        ]);

    [Fact]
    public void Version_1_database_migrates_to_the_current_version_and_keeps_rows()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = JournalSchema.Version1 + """
                INSERT INTO sessions (session_key, agent_kind, transport, source, host_pid, first_seen, last_seen)
                VALUES ('cli:old', 'claude', 'cli', 'process-tree', 1, 't', 't');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        Open(temp, storeSensitive: false);

        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM sessions WHERE session_key = 'cli:old'"));
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operation_metrics"));
        Assert.Contains("raw_bytes", JournalDb.Rows(temp.DatabasePath, "SELECT name FROM pragma_table_info('operations')").Select(r => (string)r["name"]!));
    }

    [Fact]
    public void Benchmark_record_writes_metrics_table_io_and_plan_links()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: false);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());

        journal.RecordBenchmark(handle, Record());

        var metric = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_metrics").Single();
        Assert.Equal("baseline", metric["variant"]);
        Assert.Equal(2L, metric["matrix_cell"]);
        Assert.Equal(1L, metric["runs"]);
        Assert.Equal(10L, metric["cpu_ms_median"]);
        Assert.Equal(4096L, metric["grant_granted_kb"]);
        Assert.Equal(256L, metric["grant_max_used_kb"]);
        Assert.Equal(4L, metric["dop"]);
        Assert.Equal(1L, metric["spill_count"]);
        Assert.Equal(1L, metric["has_implicit_conversion"]);
        Assert.Contains("PAGEIOLATCH_SH", (string)metric["waits_json"]!);
        var io = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_table_io").Single();
        Assert.Equal(("Orders", 5L, 2L, 1L), ((string)io["table_name"]!, (long)io["logical_reads"]!, (long)io["physical_reads"]!, (long)io["cold_runs"]!));
        var link = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_plans").Single();
        Assert.Equal(1L, link["repetition"]);
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT hash FROM plans"));
    }

    [Fact]
    public void Benchmark_record_persists_per_statement_metrics_without_statement_text()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: false);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        var plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
            <StmtSimple StatementText="UPDATE dbo.SecretTable SET A = 1"><QueryPlan DegreeOfParallelism="2"><QueryTimeStats CpuTime="10" ElapsedTime="15"/><RelOp NodeId="0" PhysicalOp="Index Seek" EstimateRows="2"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="4" ActualExecutions="1"/></RunTimeInformation><IndexScan><Object Table="[dbo].[SecretTable]" Index="[IX_A]"/></IndexScan></RelOp></QueryPlan></StmtSimple>
            <StmtSimple StatementText="UPDATE dbo.SecretTable SET B = 2"><QueryPlan DegreeOfParallelism="1"><QueryTimeStats CpuTime="20" ElapsedTime="25"/><RelOp NodeId="0" PhysicalOp="Table Scan" EstimateRows="3"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="6" ActualExecutions="2"/></RunTimeInformation><TableScan><Object Table="[dbo].[SecretTable]"/></TableScan></RelOp></QueryPlan></StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;
        var record = JournalBenchmarkBuilder.Build(
            [new CompareRunArtifact("baseline", 1, 30, 40, 0, new Dictionary<string, long>(), "h", [plan], 0)]);

        journal.RecordBenchmark(handle, record);

        var statements = JournalDb.Rows(temp.DatabasePath,
            "SELECT statement_ordinal, statement_hash, cpu_ms, elapsed_ms, dop, operators_json FROM operation_statements ORDER BY statement_ordinal");
        Assert.Equal(2, statements.Count);
        Assert.Equal([10L, 20L], statements.Select(row => (long)row["cpu_ms"]!).ToArray());
        Assert.Equal([15L, 25L], statements.Select(row => (long)row["elapsed_ms"]!).ToArray());
        Assert.All(statements, row => Assert.Equal(64, ((string)row["statement_hash"]!).Length));
        Assert.Contains("Index Seek", (string)statements[0]["operators_json"]!);
        Assert.DoesNotContain("UPDATE dbo.SecretTable", (string)statements[0]["operators_json"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Plans_are_stored_compressed_and_deduplicated_only_when_sensitive()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);
        var first = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        var second = journal.Begin(JournalTestData.Session(), JournalTestData.Start());

        journal.RecordBenchmark(first, Record());
        journal.RecordBenchmark(second, Record());

        var plan = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM plans").Single();
        Assert.Equal("showplan-xml", plan["format"]);
        using var gzip = new GZipStream(new MemoryStream((byte[])plan["gz"]!), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var text = reader.ReadToEnd();
        Assert.Equal(PlanMetricsExtractorTests.ActualPlan, text);
        Assert.Equal((long)System.Text.Encoding.UTF8.GetByteCount(text), plan["raw_size"]);
        Assert.Equal(2, JournalDb.Rows(temp.DatabasePath, "SELECT metric_id FROM operation_plans").Count);
    }

    [Fact]
    public void Null_handle_and_empty_record_are_no_ops()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);

        journal.RecordBenchmark(null, Record());
        journal.RecordBenchmark(journal.Begin(JournalTestData.Session(), JournalTestData.Start()), new BenchmarkJournalRecord([], []));

        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operation_metrics"));
    }

    [Fact]
    public void Complete_stores_artifact_directory_and_summary()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: false);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());

        journal.Complete(handle, JournalTestData.End() with { ArtifactDirectory = "/a/b", SummaryJson = """{"kind":"measure"}""" });

        var row = JournalDb.Rows(temp.DatabasePath, "SELECT artifact_dir, summary_json FROM operations").Single();
        Assert.Equal("/a/b", row["artifact_dir"]);
        Assert.Equal("""{"kind":"measure"}""", row["summary_json"]);
    }
}