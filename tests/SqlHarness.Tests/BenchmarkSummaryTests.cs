using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class BenchmarkSummaryTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public void Compare_projection_is_bounded_and_retains_core_metrics()
    {
        var baselineOps = Enumerable.Range(1, 20)
            .Select(i => new CompareOperatorReport(
                i,
                i <= 5 ? "Index Seek" : $"Scan{i:D2}",
                i <= 5 ? "dbo.Orders" : $"dbo.T{i:D2}",
                HasWarnings: i is 2 or 4,
                HasSpill: i == 3,
                HasImplicitConversion: i == 1))
            .ToArray();
        var candidateOps = Enumerable.Range(10, 20)
            .Select(i => new CompareOperatorReport(
                i + 100,
                i <= 12 ? "Hash Match" : $"Probe{i:D2}",
                i <= 12 ? "dbo.Lines" : $"dbo.C{i:D2}",
                HasWarnings: i is 11 or 15,
                HasSpill: i == 10,
                HasImplicitConversion: false))
            .Concat([
                new CompareOperatorReport(1, "Index Seek", "dbo.Orders", false, false, false),
            ])
            .ToArray();

        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("req-s", "req-d", "act-s", "act-d", "profile"),
            5,
            10,
            false,
            new CompareVariantReport(
                "baseline",
                new CompareDistribution(1, 2, 3),
                new CompareDistribution(4, 5, 6),
                new CompareDistribution(7, 8, 9),
                new Dictionary<string, long>(StringComparer.Ordinal) { ["dbo.Orders"] = 30 },
                baselineOps,
                ["ImplicitConversion", "PlanWarning", "SpillToTempDb"])
            {
                LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
                {
                    ["dbo.Orders"] = new(1, 5, 9),
                    ["dbo.Lines"] = new(2, 4, 6),
                },
            },
            new CompareVariantReport(
                "candidate",
                new CompareDistribution(10, 20, 30),
                new CompareDistribution(11, 21, 31),
                new CompareDistribution(12, 22, 32),
                new Dictionary<string, long>(StringComparer.Ordinal) { ["dbo.Lines"] = 40 },
                candidateOps,
                ["PlanWarning", "SpillToTempDb"])
            {
                LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
                {
                    ["dbo.Orders"] = new(0, 1, 2),
                    ["dbo.Lines"] = new(3, 5, 7),
                },
            },
            @"C:\tmp\artifacts")
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Multiset, false, null, 3, 1),
            Classification = new CompareClassificationReport("session-local", "read-only", "read-only"),
            Parameters =
            [
                new BenchmarkParameterReport("@CustomerId", "int", null, null, null),
            ],
        };

        var summary = BenchmarkSummaryProjector.Project(report);
        var json = JsonSerializer.Serialize(summary, summary.GetType(), WebJson);

        Assert.Equal(report.Target, summary.Target);
        Assert.Equal(report.Repetitions, summary.Repetitions);
        Assert.Equal(report.Classification, summary.Classification);
        Assert.Equal(report.Equivalence, summary.Equivalence);
        Assert.Equal(report.Parameters, summary.Parameters);
        Assert.Equal(report.ArtifactDirectory, summary.ArtifactDirectory);
        Assert.Equal(report.Baseline.CpuTimeMilliseconds, summary.Baseline.CpuTimeMilliseconds);
        Assert.Equal(report.Baseline.ElapsedTimeMilliseconds, summary.Baseline.ElapsedTimeMilliseconds);
        Assert.Equal(report.Baseline.LogicalReads, summary.Baseline.LogicalReads);
        Assert.Equal(report.Baseline.LogicalReadsByTable, summary.Baseline.LogicalReadsByTable);
        Assert.Equal(report.Baseline.Warnings, summary.Baseline.Warnings);
        Assert.Equal(report.Candidate.CpuTimeMilliseconds, summary.Candidate.CpuTimeMilliseconds);
        Assert.Equal(report.Candidate.LogicalReadsByTable, summary.Candidate.LogicalReadsByTable);
        Assert.Equal(report.Candidate.Warnings, summary.Candidate.Warnings);
        Assert.True(summary.NoteworthyOperators.Count <= 10);

        Assert.DoesNotContain("PlanXmls", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ResultHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("runs", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TotalLogicalReadsByTable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"operators\"", json, StringComparison.OrdinalIgnoreCase);

        Assert.All(
            summary.NoteworthyOperators,
            op => Assert.True(
                op.HasWarnings || op.HasSpill || op.HasImplicitConversion
                || !string.Equals(op.Side, "both", StringComparison.Ordinal),
                "Noteworthy operators must be one-side-only or carry a warning/spill/conversion."));
    }

    [Fact]
    public void Compare_noteworthy_operators_prefer_warnings_then_ordinal_physical_op_and_object()
    {
        var baseline = new[]
        {
            new CompareOperatorReport(1, "ZScan", "dbo.Z", false, false, false), // one-side only
            new CompareOperatorReport(2, "BSeek", "dbo.B", true, false, false),  // warning
            new CompareOperatorReport(3, "ASeek", "dbo.A", false, false, false),  // both sides, clean — excluded
            new CompareOperatorReport(4, "CHash", "dbo.C", false, true, false),   // spill
        };
        var candidate = new[]
        {
            new CompareOperatorReport(10, "ASeek", "dbo.A", false, false, false), // both sides, clean
            new CompareOperatorReport(11, "MJoin", "dbo.M", false, false, true),  // implicit conversion, one-side
            new CompareOperatorReport(12, "BSeek", "dbo.B", true, false, false),  // warning both
            new CompareOperatorReport(13, "YScan", "dbo.Y", false, false, false), // one-side only
        };

        var report = MinimalCompare(baseline, candidate);
        var summary = BenchmarkSummaryProjector.Project(report);

        Assert.Equal(5, summary.NoteworthyOperators.Count);
        // Warning/spill/conversion first, then PhysicalOp/Object ordinal among remaining one-side ops.
        Assert.Equal(
            [
                ("BSeek", "dbo.B"),
                ("CHash", "dbo.C"),
                ("MJoin", "dbo.M"),
                ("YScan", "dbo.Y"),
                ("ZScan", "dbo.Z"),
            ],
            summary.NoteworthyOperators.Select(op => (op.PhysicalOp, op.Object)).ToArray());
    }

    [Fact]
    public void Compare_noteworthy_operators_are_capped_at_ten()
    {
        var baseline = Enumerable.Range(0, 20)
            .Select(i => new CompareOperatorReport(i, $"Op{i:D2}", $"Obj{i:D2}", true, false, false))
            .ToArray();
        var candidate = Enumerable.Range(20, 20)
            .Select(i => new CompareOperatorReport(i, $"Op{i:D2}", $"Obj{i:D2}", true, false, false))
            .ToArray();

        var summary = BenchmarkSummaryProjector.Project(MinimalCompare(baseline, candidate));

        Assert.Equal(10, summary.NoteworthyOperators.Count);
    }

    [Fact]
    public void Compare_noteworthy_ors_attention_flags_within_same_physical_op_and_object_key()
    {
        // Same key on both sides → not one-side-only. First RelOp is clean; second has HasWarnings.
        // First()-only collapse would drop the flag and under-qualify the key.
        var baseline = new[]
        {
            new CompareOperatorReport(1, "Index Seek", "dbo.Orders", false, false, false),
            new CompareOperatorReport(2, "Index Seek", "dbo.Orders", true, false, false),
        };
        var candidate = new[]
        {
            new CompareOperatorReport(10, "Index Seek", "dbo.Orders", false, false, false),
            new CompareOperatorReport(11, "Index Seek", "dbo.Orders", false, true, false),
        };

        var summary = BenchmarkSummaryProjector.Project(MinimalCompare(baseline, candidate));

        var op = Assert.Single(summary.NoteworthyOperators);
        Assert.Equal("both", op.Side);
        Assert.Equal("Index Seek", op.PhysicalOp);
        Assert.Equal("dbo.Orders", op.Object);
        Assert.True(op.HasWarnings);
        Assert.True(op.HasSpill);
        Assert.False(op.HasImplicitConversion);
    }

    [Fact]
    public void Measure_projection_is_bounded_without_equivalence_fields()
    {
        var operators = Enumerable.Range(1, 30)
            .Select(i => new CompareOperatorReport(
                i,
                $"Op{i:D2}",
                $"dbo.T{i:D2}",
                HasWarnings: i % 3 == 0,
                HasSpill: i % 5 == 0,
                HasImplicitConversion: i % 7 == 0))
            .ToArray();

        var report = new SqlHarnessMeasureReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5,
            5,
            true,
            new CompareVariantReport(
                "measure",
                new CompareDistribution(1, 2, 3),
                new CompareDistribution(4, 5, 6),
                new CompareDistribution(7, 8, 9),
                new Dictionary<string, long>(StringComparer.Ordinal) { ["dbo.T"] = 12 },
                operators,
                ["PlanWarning"])
            {
                LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
                {
                    ["dbo.T"] = new(1, 2, 3),
                },
            },
            @"D:\artifacts\measure")
        {
            Classification = new BenchmarkClassificationReport("none", "read-only"),
            Parameters = [new BenchmarkParameterReport("@Id", "int", null, null, null)],
        };

        var summary = BenchmarkSummaryProjector.Project(report);
        var json = JsonSerializer.Serialize(summary, summary.GetType(), WebJson);

        Assert.Equal(report.Target, summary.Target);
        Assert.Equal(report.Repetitions, summary.Repetitions);
        Assert.Equal(report.ResultsStable, summary.ResultsStable);
        Assert.Equal(report.Classification, summary.Classification);
        Assert.Equal(report.Parameters, summary.Parameters);
        Assert.Equal(report.ArtifactDirectory, summary.ArtifactDirectory);
        Assert.Equal(report.Query.CpuTimeMilliseconds, summary.Query.CpuTimeMilliseconds);
        Assert.Equal(report.Query.ElapsedTimeMilliseconds, summary.Query.ElapsedTimeMilliseconds);
        Assert.Equal(report.Query.LogicalReads, summary.Query.LogicalReads);
        Assert.Equal(report.Query.LogicalReadsByTable, summary.Query.LogicalReadsByTable);
        Assert.Equal(report.Query.Warnings, summary.Query.Warnings);
        Assert.True(summary.NoteworthyOperators.Count <= 10);
        Assert.DoesNotContain("PlanXmls", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ResultHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("runs", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("equivalence", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baseline", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate", json, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            summary.NoteworthyOperators,
            op => Assert.True(op.HasWarnings || op.HasSpill || op.HasImplicitConversion));
    }

    private static SqlHarnessCompareReport MinimalCompare(
        IReadOnlyList<CompareOperatorReport> baseline,
        IReadOnlyList<CompareOperatorReport> candidate) =>
        new(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            1,
            2,
            true,
            new CompareVariantReport(
                "baseline",
                new(1, 1, 1),
                new(1, 1, 1),
                new(1, 1, 1),
                new Dictionary<string, long>(),
                baseline,
                []),
            new CompareVariantReport(
                "candidate",
                new(1, 1, 1),
                new(1, 1, 1),
                new(1, 1, 1),
                new Dictionary<string, long>(),
                candidate,
                []),
            null);
}