using SqlHarness.Core;
using SqlHarness.Tests.Journal;

namespace SqlHarness.Tests.Dashboard;

internal sealed class JournalSeed
{
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly IActivityJournal _journal;

    public JournalSeed(string databasePath, bool storeSensitive = false) =>
        _journal = ActivityJournal.Open(databasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, _time);

    public static readonly DateTimeOffset HostStarted = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    public static SessionIdentity Session(string key, string agent = "claude", int hostPid = 5151) => new(
        key, agent, "process-tree", JournalTransport.Cli, null, null, null, 4242, HostStarted.AddMinutes(-5), hostPid, HostStarted, "/work");

    public JournalHandle Operation(
        SessionIdentity session, string operation = "query", string status = "succeeded", int exitCode = 0,
        string sql = "SELECT 1", long durationMs = 10, BenchmarkJournalRecord? benchmark = null, bool complete = true)
    {
        var handle = _journal.Begin(session, new OperationStart(operation, "local", new Dictionary<string, string> { ["tenant"] = "acme" },
            false, OperationJournalDescriber.SqlHash(sql), null, sql, null))!;
        _time.Now = _time.Now.AddSeconds(1);
        if (complete)
        {
            _journal.Complete(handle, new OperationEnd(status, exitCode, status == "rejected" ? "safety" : null, durationMs,
                "sqlserver", "srv", "db", 1, 3, 100, null, null));
            _journal.RecordEmission(handle, new OutputFootprint(400, 1), new OutputFootprint(40, 1));
        }

        if (benchmark is not null)
            _journal.RecordBenchmark(handle, benchmark);
        _time.Now = _time.Now.AddSeconds(1);
        return handle;
    }

    public static BenchmarkJournalRecord Benchmark(long readsMedian = 5, long physical = 0, long granted = 4096, long used = 256) =>
        new(
        [
            new JournalVariantMetrics("measure", null, null, 3, new CompareDistribution(10, 12, 14), new CompareDistribution(8, 9, 10),
                new CompareDistribution(readsMedian, readsMedian, readsMedian), 4096, granted, used, 4, 12, 10, 1, true, true, 1,
                [new JournalWait("PAGEIOLATCH_SH", 40, 7)], null,
                [new JournalTableIo("Orders", readsMedian, 1, physical, 0, 0, 0, 0, 0, physical > 0 ? 1 : 0)],
                [new JournalPlanLink(1, 0, new string('A', 64))]),
        ],
        [new JournalPlanDocument(new string('A', 64), "showplan-xml", Journal.PlanMetricsExtractorTests.ActualPlan)]);
}