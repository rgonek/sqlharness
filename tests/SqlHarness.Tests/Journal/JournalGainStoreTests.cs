using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalGainStoreTests
{
    private static void Run(IActivityJournal journal, string operation, string status, long raw, long emitted, long durationMs = 10, bool emit = true)
    {
        var handle = journal.Begin(JournalTestData.Session(), new OperationStart(operation, null, null, false, null, null, null, null));
        journal.Complete(handle, new OperationEnd(status, status == "succeeded" ? 0 : 5, null, durationMs, null, null, null, null, null));
        if (emit)
            journal.RecordEmission(handle, new OutputFootprint(raw, 3), new OutputFootprint(emitted, 1));
    }

    private static (JournalTempDirectory Temp, IActivityJournal Journal) Open()
    {
        var temp = new JournalTempDirectory();
        return (temp, ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System));
    }

    [Fact]
    public void Gain_buckets_match_the_previous_semantics()
    {
        var (temp, journal) = Open();
        using (temp)
        {
            Run(journal, "query", "succeeded", raw: 400, emitted: 40, durationMs: 7);
            Run(journal, "query", "failed", raw: 100, emitted: 20, durationMs: 3);
            Run(journal, "compare", "succeeded", raw: 800, emitted: 80);
            Run(journal, "qstop", "succeeded", raw: 40, emitted: 4);
            Run(journal, "plan", "succeeded", raw: 40, emitted: 8);
            Run(journal, "schema", "rejected", raw: 0, emitted: 4);

            var report = new JournalGainStore(temp.DatabasePath, () => true).Aggregate();

            Assert.True(report.JournalEnabled);
            Assert.Equal((6L, 2L), (report.Total.Executions, report.Total.Failures));
            Assert.Equal((2L, 1L, 10L), (report.Query.Executions, report.Query.Failures, report.Query.DurationMilliseconds));
            Assert.Equal((500L, 6L, 60L, 2L), (report.Query.RawBytes, report.Query.RawLines, report.Query.EmittedBytes, report.Query.EmittedLines));
            Assert.Equal(OutputFootprint.EstimateTokens(400) + OutputFootprint.EstimateTokens(100), report.Query.RawEstimatedTokens);
            Assert.Equal(1, report.Compare.Executions);
            Assert.Equal(1, report.QueryStoreTop.Executions);
            Assert.Equal(0, report.Measure.Executions);
            // Saved tokens are the per-execution non-negative gross, as gain.jsonl recorded them.
            Assert.Equal(SumSaved([400, 100, 800, 40, 40, 0], [40, 20, 80, 4, 8, 4]), report.Total.SavedEstimatedTokens);
        }
    }

    private static long SumSaved(long[] raw, long[] emitted) =>
        raw.Zip(emitted, (r, e) => Math.Max(OutputFootprint.EstimateTokens(r) - OutputFootprint.EstimateTokens(e), 0)).Sum();

    [Fact]
    public void Operations_without_an_emitted_footprint_are_not_counted()
    {
        var (temp, journal) = Open();
        using (temp)
        {
            Run(journal, "query", "succeeded", raw: 400, emitted: 40);
            Run(journal, "query", "succeeded", raw: 400, emitted: 40, emit: false);
            Run(journal, "gain", "succeeded", raw: 4, emitted: 4);
            Run(journal, "validate", "succeeded", raw: 4, emitted: 4);

            var report = new JournalGainStore(temp.DatabasePath, () => true).Aggregate();

            Assert.Equal(1, report.Total.Executions);
        }
    }

    [Fact]
    public void Disabled_journal_reports_zeros_and_says_so()
    {
        var (temp, journal) = Open();
        using (temp)
        {
            Run(journal, "query", "succeeded", raw: 400, emitted: 40);

            var report = new JournalGainStore(temp.DatabasePath, () => false).Aggregate();

            Assert.False(report.JournalEnabled);
            Assert.Equal(0, report.Total.Executions);
        }
    }

    [Fact]
    public void Missing_database_reports_zeros()
    {
        using var temp = new JournalTempDirectory();

        var report = new JournalGainStore(temp.DatabasePath, () => true).Aggregate();

        Assert.True(report.JournalEnabled);
        Assert.Equal(0, report.Total.Executions);
    }

    [Fact]
    public void Emission_records_bytes_and_lines()
    {
        var (temp, journal) = Open();
        using (temp)
        {
            Run(journal, "query", "succeeded", raw: 400, emitted: 40);

            var row = JournalDb.Rows(temp.DatabasePath, "SELECT raw_bytes, raw_lines, emitted_bytes, emitted_lines FROM operations").Single();

            Assert.Equal((400L, 3L, 40L, 1L), ((long)row["raw_bytes"]!, (long)row["raw_lines"]!, (long)row["emitted_bytes"]!, (long)row["emitted_lines"]!));
        }
    }
}