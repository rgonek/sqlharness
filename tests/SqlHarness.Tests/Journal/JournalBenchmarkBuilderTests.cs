using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalBenchmarkBuilderTests
{
    private static CompareRunArtifact Run(
        string variant, int repetition, long cpu, long elapsed, long reads,
        string? set = null, int? cell = null, IReadOnlyList<string>? plans = null,
        IReadOnlyList<TableIoCounters>? io = null, BenchmarkRunMetrics? metrics = null) =>
        new(variant, repetition, cpu, elapsed, reads,
            new Dictionary<string, long> { ["Orders"] = reads }, "hash", plans ?? [], 1, set, metrics)
        {
            TableIo = io ?? [new TableIoCounters("Orders", 1, reads, 0, 0, 0, 0, 0, 0)],
            MatrixCell = cell,
        };

    [Fact]
    public void Runs_are_grouped_by_variant_set_and_cell_in_first_appearance_order()
    {
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("baseline", 1, 10, 12, 5, cell: 0), Run("candidate", 1, 8, 9, 3, cell: 0),
            Run("candidate", 2, 9, 10, 4, cell: 0), Run("baseline", 2, 11, 13, 6, cell: 0),
            Run("baseline", 1, 20, 22, 7, cell: 1),
        ]);

        Assert.Equal(
            [("baseline", (int?)0, 2), ("candidate", 0, 2), ("baseline", 1, 1)],
            record.Variants.Select(v => (v.Variant, v.MatrixCell, v.Runs)));
        Assert.Equal(new CompareDistribution(10, 10, 11), record.Variants[0].CpuMilliseconds);
        Assert.Equal(new CompareDistribution(5, 5, 6), record.Variants[0].LogicalReads);
    }

    [Fact]
    public void Table_io_is_median_per_counter_with_cold_run_count()
    {
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 10, io: [new TableIoCounters("Orders", 1, 10, 4, 0, 8, 0, 0, 0)]),
            Run("measure", 2, 1, 1, 12, io: [new TableIoCounters("Orders", 1, 12, 0, 0, 0, 0, 0, 0)]),
            Run("measure", 3, 1, 1, 14, io: [new TableIoCounters("Orders", 3, 14, 0, 0, 0, 0, 0, 0), new TableIoCounters("Worktable", 0, 2, 0, 0, 0, 0, 0, 0)]),
        ]);

        var orders = record.Variants[0].TableIo.Single(t => t.Table == "Orders");
        Assert.Equal(12, orders.LogicalReads);
        Assert.Equal(1, orders.ScanCount);
        Assert.Equal(1, orders.ColdRuns);
        var worktable = record.Variants[0].TableIo.Single(t => t.Table == "Worktable");
        Assert.Equal(0, worktable.LogicalReads);
        Assert.Equal(["Orders", "Worktable"], record.Variants[0].TableIo.Select(t => t.Table));
    }

    [Fact]
    public void Runs_without_detail_fall_back_to_logical_reads_by_table()
    {
        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 0, 5, 9, io: [])]);

        var table = Assert.Single(record.Variants[0].TableIo);
        Assert.Equal(("Orders", 9L), (table.Table, table.LogicalReads));
        Assert.Null(table.ScanCount);
        Assert.Null(table.PhysicalReads);
    }

    [Fact]
    public void Unavailable_metrics_are_null_and_table_io_is_skipped()
    {
        var unavailable = new BenchmarkRunMetrics(
            BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Unavailable, null, false, null, null,
            BenchmarkMetricReport.Unavailable, null, null, null, BenchmarkMetricReport.ResultStatement,
            BenchmarkMetricText.StatementRows, []);

        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 0, 0, 0, metrics: unavailable, io: [])]);

        var variant = record.Variants[0];
        Assert.Null(variant.CpuMilliseconds);
        Assert.Null(variant.ElapsedMilliseconds);
        Assert.Null(variant.LogicalReads);
        Assert.Empty(variant.TableIo);
    }

    [Fact]
    public void Plan_metrics_are_aggregated_and_documents_deduplicated()
    {
        var plan = PlanMetricsExtractorTests.ActualPlan;
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 1, plans: [plan]),
            Run("measure", 2, 1, 1, 1, plans: [plan]),
        ]);

        var variant = record.Variants[0];
        Assert.Equal(4096, variant.GrantGrantedKb);
        Assert.Equal(256, variant.GrantMaxUsedKb);
        Assert.Equal(4, variant.Dop);
        Assert.Equal(1, variant.SpillCount);
        Assert.True(variant.HasImplicitConversion);
        Assert.Equal(new JournalWait("PAGEIOLATCH_SH", 40, 7), variant.Waits[0]);
        Assert.Equal([(1, 0), (2, 0)], variant.PlanLinks.Select(link => (link.Repetition, link.Ordinal)));
        Assert.Single(variant.PlanLinks.Select(link => link.Hash).Distinct());
        var document = Assert.Single(record.PlanDocuments);
        Assert.Equal("showplan-xml", document.Format);
        Assert.Equal(variant.PlanLinks[0].Hash, document.Hash);
    }

    [Fact]
    public void Waits_are_capped_at_ten()
    {
        var waits = string.Concat(Enumerable.Range(1, 12).Select(i => $"<Wait WaitType=\"W{i:D2}\" WaitTimeMs=\"{i}\" WaitCount=\"1\" />"));
        var plan = $"""<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><WaitStats>{waits}</WaitStats></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 1, 1, 1, plans: [plan])]);

        Assert.Equal(JournalBenchmarkBuilder.MaximumWaits, record.Variants[0].Waits.Count);
        Assert.Equal("W12", record.Variants[0].Waits[0].WaitType);
    }

    [Fact]
    public void Extreme_counters_do_not_overflow_cold_run_detection()
    {
        var max = long.MaxValue;
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, max, max, max, io: [new TableIoCounters("Orders", max, max, max, max, max, max, max, max)]),
            Run("measure", 2, max, max, max, io: [new TableIoCounters("Orders", max, max, max, max, max, max, max, max)]),
        ]);

        var orders = Assert.Single(record.Variants[0].TableIo);
        Assert.Equal(2, orders.ColdRuns);
        Assert.Equal(max, orders.LogicalReads);
        Assert.Equal(new CompareDistribution(max, max, max), record.Variants[0].CpuMilliseconds);
    }

    [Fact]
    public void Empty_input_yields_empty_record() =>
        Assert.Empty(JournalBenchmarkBuilder.Build([]).Variants);
}