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
    public void Older_database_is_backed_up_before_current_schema_is_created()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE old_rows (id INTEGER PRIMARY KEY); INSERT INTO old_rows VALUES (1); PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        Open(temp, storeSensitive: false);

        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(temp.DatabasePath)!, "activity.db.v1-*.bak"));
        Assert.Single(JournalDb.Rows(backup, "SELECT id FROM old_rows"));
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM sessions"));
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
        Assert.Equal("2", metric["case_id"]);
        Assert.Equal("Case 3 of 3", metric["case_label"]);
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
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT * FROM plan_occurrences"));
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT sha256 FROM plan_documents"));
    }

    [Fact]
    public void Omitted_statement_count_is_persisted_on_its_variant_case_metric()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: false);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        var record = Record();
        var variant = record.Variants.Single() with { OmittedStatements = 2 };

        journal.RecordBenchmark(handle, record with { Variants = [variant] });

        Assert.Equal(2L, JournalDb.Rows(temp.DatabasePath,
            "SELECT omitted_statements FROM operation_metrics").Single()["omitted_statements"]);
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
        record = record with
        {
            Statements = record.Statements.Select(statement => statement with { UdfElapsedMilliseconds = 14.75 }).ToArray(),
        };

        journal.RecordBenchmark(handle, record);

        var statements = JournalDb.Rows(temp.DatabasePath,
            "SELECT statement_ordinal, statement_hash, statement_key, occurrence, statement_text, executions, plan_hash, status, cpu_ms, elapsed_ms, dop, operators_json, udf_elapsed_ms, hidden_work_json, case_id, case_label FROM operation_statements ORDER BY statement_ordinal");
        Assert.Equal(2, statements.Count);
        Assert.Equal(14.75d, Convert.ToDouble(statements[0]["udf_elapsed_ms"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.All(statements, row =>
        {
            Assert.Equal("0", row["case_id"]);
            Assert.Equal("Case 1 of 1", row["case_label"]);
        });
        Assert.Equal([10L, 20L], statements.Select(row => (long)row["cpu_ms"]!).ToArray());
        Assert.Equal([15L, 25L], statements.Select(row => (long)row["elapsed_ms"]!).ToArray());
        Assert.All(statements, row => Assert.Equal(64, ((string)row["statement_hash"]!).Length));
        Assert.All(statements, row =>
        {
            Assert.Equal(64, ((string)row["statement_key"]!).Length);
            Assert.Equal("unavailable", row["status"]);
            Assert.StartsWith("statement ", (string)row["statement_text"]!);
            Assert.Equal("[]", row["hidden_work_json"]);
        });
        Assert.Contains("Index Seek", (string)statements[0]["operators_json"]!);
        Assert.DoesNotContain("UPDATE dbo.SecretTable", (string)statements[0]["operators_json"]!, StringComparison.Ordinal);
        Assert.Equal(2, JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_statement_objects").Count);
        Assert.Equal(0L, JournalDb.Rows(temp.DatabasePath, "SELECT omitted_statements FROM operation_metrics").Single()["omitted_statements"]);
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

        var plan = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM plan_documents").Single();
        Assert.Equal("showplan-xml", plan["format"]);
        using var gzip = new GZipStream(new MemoryStream((byte[])plan["gz"]!), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var text = reader.ReadToEnd();
        Assert.Equal(PlanMetricsExtractorTests.ActualPlan, text);
        Assert.Equal((long)System.Text.Encoding.UTF8.GetByteCount(text), plan["raw_size"]);
        Assert.Equal(2, JournalDb.Rows(temp.DatabasePath, "SELECT metric_id FROM plan_occurrences").Count);
    }

    [Fact]
    public void Repetitions_keep_their_own_documents()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        const string first = "<ShowPlanXML><StmtSimple><QueryPlan QueryPlanHash=\"0xabc\"><RelOp><RunTimeInformation><RunTimeCountersPerThread ActualRows=\"3\"/></RunTimeInformation></RelOp></QueryPlan></StmtSimple></ShowPlanXML>";
        const string second = "<ShowPlanXML><StmtSimple><QueryPlan QueryPlanHash=\"0xabc\"><RelOp><RunTimeInformation><RunTimeCountersPerThread ActualRows=\"7\"/></RunTimeInformation></RelOp></QueryPlan></StmtSimple></ShowPlanXML>";
        var record = JournalBenchmarkBuilder.Build(
        [
            new CompareRunArtifact("measure", 1, 10, 12, 5, new Dictionary<string, long>(), "h", [first], 1),
            new CompareRunArtifact("measure", 2, 10, 12, 5, new Dictionary<string, long>(), "h", [second], 1),
        ]);

        journal.RecordBenchmark(handle, record);

        var rows = JournalDb.Rows(temp.DatabasePath, """
            SELECT o.repetition, d.gz FROM plan_occurrences o
            JOIN plan_documents d ON d.sha256 = o.document_sha ORDER BY o.repetition;
            """);
        Assert.Equal(2, rows.Count);
        Assert.Equal([1L, 2L], rows.Select(row => (long)row["repetition"]!).ToArray());
        Assert.Equal([first, second], rows.Select(row =>
        {
            using var gzip = new GZipStream(new MemoryStream((byte[])row["gz"]!), CompressionMode.Decompress);
            using var text = new StreamReader(gzip);
            return text.ReadToEnd();
        }).ToArray());
    }

    [Fact]
    public void Multi_statement_documents_persist_one_occurrence_per_document_statement_index()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        const string document = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT 1" StatementId="5" StatementCompId="2"><QueryPlan><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan></StmtSimple>
            <StmtSimple StatementText="SELECT 2" StatementId="6" StatementCompId="3"><QueryPlan><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan></StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;
        const string executedSql = "DECLARE @x int; SELECT 1; SELECT 2;";
        var record = JournalBenchmarkBuilder.Build(
        [new CompareRunArtifact("measure", 1, 10, 12, 5, new Dictionary<string, long>(), "h", [document], 1)
            { ExecutedSql = executedSql }]);

        journal.RecordBenchmark(handle, record);

        var occurrences = JournalDb.Rows(temp.DatabasePath, """
            SELECT repetition, document_ordinal, statement_index, document_sha,
                   span_start, span_length, match_kind, statement_id, statement_comp_id
            FROM plan_occurrences ORDER BY statement_index;
            """);
        Assert.Equal(2, occurrences.Count);
        Assert.Equal([0L, 1L], occurrences.Select(row => (long)row["statement_index"]!).ToArray());
        Assert.All(occurrences, row =>
        {
            Assert.Equal(1L, row["repetition"]);
            Assert.Equal(0L, row["document_ordinal"]);
            Assert.Equal(record.PlanDocuments.Single().Sha256, row["document_sha"]);
            Assert.Equal("exact", row["match_kind"]);
            Assert.NotNull(row["span_start"]);
            Assert.NotNull(row["span_length"]);
            Assert.NotNull(row["statement_id"]);
            Assert.NotNull(row["statement_comp_id"]);
        });
        Assert.Equal([5L, 6L], occurrences.Select(row => (long)row["statement_id"]!).ToArray());
        Assert.Equal([2L, 3L], occurrences.Select(row => (long)row["statement_comp_id"]!).ToArray());
        Assert.Equal([16L, 26L], occurrences.Select(row => (long)row["span_start"]!).ToArray());
        Assert.Equal([9L, 9L], occurrences.Select(row => (long)row["span_length"]!).ToArray());
        Assert.Equal(PlanIdentity.StatementSha256("SELECT 1;"), record.Statements[0].StatementHash);
        Assert.Equal(PlanIdentity.StatementSha256("SELECT 2;"), record.Statements[1].StatementHash);
    }

    [Fact]
    public void Ordered_plan_documents_share_statement_pairing_across_the_execution()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        const string first = "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements><StmtSimple StatementText=\"SELECT 1\" StatementId=\"1\" StatementCompId=\"1\"><QueryPlan /></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>";
        const string second = "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements><StmtSimple StatementText=\"SELECT 1\" StatementId=\"2\" StatementCompId=\"2\"><QueryPlan /></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>";
        const string executedSql = "SELECT 1; SELECT 1;";
        var record = JournalBenchmarkBuilder.Build(
            [new CompareRunArtifact("measure", 1, 10, 12, 5, new Dictionary<string, long>(), "h", [first, second], 1)
                { ExecutedSql = executedSql }]);

        journal.RecordBenchmark(handle, record);

        var occurrences = JournalDb.Rows(temp.DatabasePath,
            "SELECT document_ordinal, span_start, span_length, match_kind FROM plan_occurrences ORDER BY document_ordinal");
        Assert.Equal(2, occurrences.Count);
        Assert.Equal([0L, 1L], occurrences.Select(row => (long)row["document_ordinal"]!).ToArray());
        Assert.Equal([0L, 10L], occurrences.Select(row => (long)row["span_start"]!).ToArray());
        Assert.Equal([9L, 9L], occurrences.Select(row => (long)row["span_length"]!).ToArray());
        Assert.All(occurrences, row => Assert.Equal("exact", row["match_kind"]));
    }

    [Fact]
    public void Plan_budget_records_omitted_occurrences_and_allows_later_small_documents()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = true }, TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0), maximumPlanDocumentBytesPerOperation: 4);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        var record = JournalBenchmarkBuilder.Build(
        [
            new CompareRunArtifact("baseline", 1, 10, 12, 5, new Dictionary<string, long>(), "h",
                ["💡x", "tiny"], 1),
        ]);

        journal.RecordBenchmark(handle, record);

        var occurrences = JournalDb.Rows(temp.DatabasePath,
            "SELECT document_ordinal, document_sha, omitted FROM plan_occurrences ORDER BY document_ordinal");
        Assert.Equal(2, occurrences.Count);
        Assert.Equal((0L, null, 1L), ((long)occurrences[0]["document_ordinal"]!, occurrences[0]["document_sha"], (long)occurrences[0]["omitted"]!));
        Assert.Equal((1L, record.PlanDocuments.Single(document => document.Document == "tiny").Sha256, 0L),
            ((long)occurrences[1]["document_ordinal"]!, occurrences[1]["document_sha"], (long)occurrences[1]["omitted"]!));
        var span = JournalDb.Rows(temp.DatabasePath,
            "SELECT span_start, span_length, match_kind, statement_id, statement_comp_id FROM plan_occurrences ORDER BY document_ordinal")[0];
        Assert.Null(span["span_start"]);
        Assert.Null(span["span_length"]);
        Assert.Equal("unmatched", span["match_kind"]);
        Assert.Null(span["statement_id"]);
        Assert.Null(span["statement_comp_id"]);
        Assert.Equal("tiny", JournalDb.Rows(temp.DatabasePath, "SELECT sha256 FROM plan_documents")
            .Single()["sha256"] is string sha ? record.PlanDocuments.Single(document => document.Sha256 == sha).Document : null);
    }

    [Fact]
    public void Repeated_document_sha_costs_the_budget_once_with_exact_fit()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = true }, TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0), maximumPlanDocumentBytesPerOperation: 4);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        var record = JournalBenchmarkBuilder.Build(
        [
            new CompareRunArtifact("measure", 1, 10, 12, 5, new Dictionary<string, long>(), "h", ["same"], 1),
            new CompareRunArtifact("measure", 2, 10, 12, 5, new Dictionary<string, long>(), "h", ["same"], 1),
        ]);

        journal.RecordBenchmark(handle, record);

        var occurrences = JournalDb.Rows(temp.DatabasePath, "SELECT repetition, document_sha, omitted FROM plan_occurrences ORDER BY repetition");
        Assert.Equal(2, occurrences.Count);
        Assert.Equal([1L, 2L], occurrences.Select(row => (long)row["repetition"]!).ToArray());
        Assert.All(occurrences, row =>
        {
            Assert.Equal(record.PlanDocuments.Single().Sha256, row["document_sha"]);
            Assert.Equal(0L, row["omitted"]);
        });
        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT sha256 FROM plan_documents"));
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