using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class MeasureParameterSetReportTests
{
    private const string MeasuredOrderRule =
        "In one-based round r, measured execution starts at index r modulo setCount and wraps in user-supplied order.";

    private const string PlanCacheWarning =
        "Parameter-set measurements use the observed server plan-cache state; SQLHarness did not clear or isolate the plan cache.";

    private const string PlanXmlSentinel = "PLAN-XML-SHOULD-NOT-LEAK";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Measured_sets_keep_independent_stability_plans_and_distributions()
    {
        var runs = new[]
        {
            Run("wide", 1, 100, 1000, 500, "WIDE-1", ["ZZZ"], tables: Table(("Clients", 500)), operators: [Op(4, "Table Scan", "Wide", false, false, false)]),
            Run("narrow", 1, 1, 100, 12, "STABLE-NARROW", ["BBB"], tables: Table(("Clients", 4), ("Orders", 8)), operators: [Op(1, "Index Seek", "Clients", false, false, false)]),
            Run("narrow", 2, 9, 400, 30, "STABLE-NARROW", ["AAA"], tables: Table(("Clients", 10)), operators: [Op(2, "Hash Match", "Orders", true, true, false)]),
            Run("wide", 2, 100, 1000, 500, "WIDE-2", ["ZZZ"], tables: Table(("Clients", 500)), operators: [Op(4, "Table Scan", "Wide", false, false, false)]),
            Run("wide", 3, 100, 1000, 500, "WIDE-1", ["MMM"], tables: Table(("Clients", 500)), operators: [Op(4, "Table Scan", "Wide", false, false, false)]),
            Run("narrow", 3, 5, 200, 18, "STABLE-NARROW", ["BBB", "bbb"], tables: Table(("Clients", 16), ("Orders", 2)), operators:
            [
                Op(1, "Index Seek", "Clients", false, false, false),
                Op(3, "Compute Scalar", null, false, false, true),
            ]),
        };
        var sets = Prepare(("narrow", ["id:int=1"]), ("wide", ["id:int=2"]));
        var execution = new MeasureParameterSetExecution(1, ["narrow", "wide"], runs);

        var report = MeasureParameterSetReportProjector.Project(Target(), repeat: 3, execution, sets);

        Assert.Equal(["narrow", "wide"], report.Sets.Select(set => set.Name));
        Assert.Equal(3, report.Repeat);
        Assert.Equal(6, report.MeasuredRunCount);
        Assert.Equal(1, report.SetupExecutionCount);
        Assert.Equal(["narrow", "wide"], report.WarmupOrder);
        Assert.Equal(MeasuredOrderRule, report.MeasuredOrderRule);
        Assert.Equal(PlanCacheWarning, report.PlanCacheWarning);
        Assert.Null(report.ArtifactDirectory);
        Assert.Equal(Target(), report.Target);

        var narrow = report.Sets[0];
        var wide = report.Sets[1];
        Assert.Equal(3, narrow.Repetitions);
        Assert.Equal(3, wide.Repetitions);
        Assert.True(narrow.ResultsStable);
        Assert.Equal("STABLE-NARROW", narrow.ResultHash);
        Assert.False(wide.ResultsStable);
        Assert.Null(wide.ResultHash);
        Assert.Equal(["BBB", "AAA", "bbb"], narrow.PlanHashes);
        Assert.Equal(["ZZZ", "MMM"], wide.PlanHashes);
        Assert.Equal(sets[0].ValueHash, narrow.ValueHash);
        Assert.Equal(sets[1].ValueHash, wide.ValueHash);
        Assert.NotEqual(narrow.ValueHash, wide.ValueHash);
        Assert.Equal([new MeasureParameterMetadata("@id", "int")], narrow.Parameters);
        Assert.Equal(new CompareDistribution(1, 5, 9), narrow.Metrics.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(100, 200, 400), narrow.Metrics.ElapsedTimeMilliseconds);
        Assert.Equal(new CompareDistribution(12, 18, 30), narrow.Metrics.LogicalReads);
        Assert.Equal(30, narrow.Metrics.TotalLogicalReadsByTable["Clients"]);
        Assert.Equal(10, narrow.Metrics.TotalLogicalReadsByTable["Orders"]);
        Assert.Equal(new CompareDistribution(4, 10, 16), narrow.Metrics.LogicalReadsByTable["Clients"]);
        Assert.Equal(new CompareDistribution(0, 2, 8), narrow.Metrics.LogicalReadsByTable["Orders"]);
        Assert.Equal(["ImplicitConversion", "PlanWarning", "SpillToTempDb"], narrow.Metrics.Warnings);
        Assert.Equal(
            [
                new CompareOperatorReport(1, "Index Seek", "Clients", false, false, false),
                new CompareOperatorReport(2, "Hash Match", "Orders", true, true, false),
                new CompareOperatorReport(3, "Compute Scalar", null, false, false, true),
            ],
            narrow.Metrics.Operators);
        Assert.Equal(new CompareDistribution(100, 100, 100), wide.Metrics.CpuTimeMilliseconds);
        Assert.Equal(new CompareDistribution(500, 500, 500), wide.Metrics.LogicalReads);
        Assert.DoesNotContain(wide.Metrics.Operators, op => op.PhysicalOp == "Index Seek");

        AssertSameMetrics(SqlHarnessModule.CreateVariantReport("narrow", RunsFor(runs, "narrow")), narrow.Metrics);
        AssertSameMetrics(SqlHarnessModule.CreateVariantReport("wide", RunsFor(runs, "wide")), wide.Metrics);
    }

    [Fact]
    public void Cross_set_summary_uses_median_extrema_and_input_order_ties()
    {
        var runs = new[]
        {
            Run("beta", 1, 5, 40, 100, "B", ["P"]),
            Run("gamma", 1, 9, 20, 10, "G", ["P"]),
            Run("alpha", 1, 5, 20, 100, "A", ["P"]),
        };
        var sets = Prepare(("alpha", ["id:int=1"]), ("beta", ["id:int=2"]), ("gamma", ["id:int=3"]));
        var report = MeasureParameterSetReportProjector.Project(
            Target(),
            repeat: 1,
            new MeasureParameterSetExecution(0, ["alpha", "beta", "gamma"], runs),
            sets);

        Assert.Equal(["alpha", "beta", "gamma"], report.Sets.Select(set => set.Name));
        Assert.Equal(0, report.SetupExecutionCount);
        Assert.Equal(3, report.MeasuredRunCount);
        var summary = report.CrossSetSummary;
        Assert.Equal("alpha", summary.MinimumMedianElapsedSet);
        Assert.Equal(20, summary.MinimumMedianElapsedMilliseconds);
        Assert.Equal("beta", summary.MaximumMedianElapsedSet);
        Assert.Equal(40, summary.MaximumMedianElapsedMilliseconds);
        Assert.Equal("alpha", summary.MinimumMedianCpuSet);
        Assert.Equal(5, summary.MinimumMedianCpuMilliseconds);
        Assert.Equal("gamma", summary.MaximumMedianCpuSet);
        Assert.Equal(9, summary.MaximumMedianCpuMilliseconds);
        Assert.Equal("gamma", summary.MinimumMedianReadsSet);
        Assert.Equal(10, summary.MinimumMedianLogicalReads);
        Assert.Equal("alpha", summary.MaximumMedianReadsSet);
        Assert.Equal(100, summary.MaximumMedianLogicalReads);
    }

    [Fact]
    public void Fully_tied_medians_select_the_earliest_set_for_both_extrema()
    {
        var runs = new[]
        {
            Run("third", 1, 4, 8, 16, "S", ["P"]),
            Run("first", 1, 4, 8, 16, "S", ["P"]),
            Run("second", 1, 4, 8, 16, "S", ["P"]),
        };
        var sets = Prepare(("first", ["id:int=1"]), ("second", ["id:int=2"]), ("third", ["id:int=3"]));
        var report = MeasureParameterSetReportProjector.Project(
            Target(),
            1,
            new MeasureParameterSetExecution(0, ["first", "second", "third"], runs),
            sets);

        var summary = report.CrossSetSummary;
        Assert.Equal("first", summary.MinimumMedianElapsedSet);
        Assert.Equal("first", summary.MaximumMedianElapsedSet);
        Assert.Equal(8, summary.MinimumMedianElapsedMilliseconds);
        Assert.Equal(8, summary.MaximumMedianElapsedMilliseconds);
        Assert.Equal("first", summary.MinimumMedianCpuSet);
        Assert.Equal("first", summary.MaximumMedianCpuSet);
        Assert.Equal(4, summary.MinimumMedianCpuMilliseconds);
        Assert.Equal(4, summary.MaximumMedianCpuMilliseconds);
        Assert.Equal("first", summary.MinimumMedianReadsSet);
        Assert.Equal("first", summary.MaximumMedianReadsSet);
        Assert.Equal(16, summary.MinimumMedianLogicalReads);
        Assert.Equal(16, summary.MaximumMedianLogicalReads);
    }

    [Fact]
    public void Serialized_report_keeps_names_types_hashes_and_metrics_without_values_or_paths()
    {
        const string secret = "acme-secret-884422";
        const string path = @"D:\tickets\measure-sets\orders.sql";
        const string queryPath = @"D:\src\queries\orders.sql";
        var narrowLabel = $"label:nvarchar={secret}";
        var narrowSource = $"source:nvarchar={path}";
        var wideLabel = "label:nvarchar=other-secret-991991";
        var wideSource = @"source:nvarchar=D:\tickets\measure-sets\other.sql";
        var query = $"SELECT @id, @label, @source -- {queryPath}";
        var sets = MeasureParameterSetValidator.Prepare(
            [],
            [
                new("narrow", ["id:int=717171", narrowLabel, narrowSource]),
                new("wide", ["id:int=828282", wideLabel, wideSource]),
            ],
            null,
            query);
        var narrowParameters = sets[0].Parameters;
        Assert.Equal(secret, Assert.Single(narrowParameters, parameter => parameter.Name == "@label").Value);
        Assert.Equal(path, Assert.Single(narrowParameters, parameter => parameter.Name == "@source").Value);

        var runs = new[]
        {
            Run("wide", 1, 8, 8686, 11, "RESULT-WIDE", ["PLAN-WIDE"], tables: Table(("Clients", 11))),
            Run("narrow", 1, 7, 4242, 9, "RESULT-NARROW", ["PLAN-NARROW"], tables: Table(("Clients", 9))),
        };
        var report = MeasureParameterSetReportProjector.Project(
            Target(),
            1,
            new MeasureParameterSetExecution(1, ["narrow", "wide"], runs),
            sets);

        Assert.Equal(
            [
                new MeasureParameterMetadata("@id", "int"),
                new MeasureParameterMetadata("@label", "nvarchar"),
                new MeasureParameterMetadata("@source", "nvarchar"),
            ],
            report.Sets[0].Parameters);
        Assert.Equal(4242, report.Sets[0].Metrics.ElapsedTimeMilliseconds.Median);
        Assert.Null(report.ArtifactDirectory);

        var json = JsonSerializer.Serialize(report, WebJson);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            [
                "target", "repeat", "measuredRunCount", "setupExecutionCount", "warmupOrder",
                "measuredOrderRule", "planCacheWarning", "sets", "crossSetSummary", "artifactDirectory",
            ],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(MeasuredOrderRule, root.GetProperty("measuredOrderRule").GetString());
        Assert.Equal(PlanCacheWarning, root.GetProperty("planCacheWarning").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("artifactDirectory").ValueKind);
        Assert.Equal(["narrow", "wide"], root.GetProperty("warmupOrder").EnumerateArray().Select(item => item.GetString()));

        var serializedSets = root.GetProperty("sets").EnumerateArray().ToArray();
        Assert.Equal(["narrow", "wide"], serializedSets.Select(set => set.GetProperty("name").GetString()));
        Assert.Equal(
            ["name", "parameters", "valueHash", "repetitions", "resultsStable", "resultHash", "metrics", "planHashes"],
            serializedSets[0].EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(sets[0].ValueHash, serializedSets[0].GetProperty("valueHash").GetString());
        Assert.Equal("RESULT-NARROW", serializedSets[0].GetProperty("resultHash").GetString());
        Assert.Equal(4242, serializedSets[0].GetProperty("metrics").GetProperty("elapsedTimeMilliseconds").GetProperty("median").GetInt64());
        Assert.Equal(["PLAN-NARROW"], serializedSets[0].GetProperty("planHashes").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            [
                new MeasureParameterMetadata("@id", "int"),
                new MeasureParameterMetadata("@label", "nvarchar"),
                new MeasureParameterMetadata("@source", "nvarchar"),
            ],
            serializedSets[0].GetProperty("parameters").EnumerateArray().Select(parameter =>
            {
                Assert.Equal(["name", "type"], parameter.EnumerateObject().Select(property => property.Name).ToArray());
                return new MeasureParameterMetadata(
                    parameter.GetProperty("name").GetString()!,
                    parameter.GetProperty("type").GetString()!);
            }));

        var summary = root.GetProperty("crossSetSummary");
        Assert.Equal(
            [
                "minimumMedianElapsedSet", "minimumMedianElapsedMilliseconds",
                "maximumMedianElapsedSet", "maximumMedianElapsedMilliseconds",
                "minimumMedianCpuSet", "minimumMedianCpuMilliseconds",
                "maximumMedianCpuSet", "maximumMedianCpuMilliseconds",
                "minimumMedianReadsSet", "minimumMedianLogicalReads",
                "maximumMedianReadsSet", "maximumMedianLogicalReads",
            ],
            summary.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("narrow", summary.GetProperty("minimumMedianElapsedSet").GetString());
        Assert.DoesNotContain("equivalence", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("regression", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret-991991", json, StringComparison.Ordinal);
        Assert.DoesNotContain(narrowLabel, json, StringComparison.Ordinal);
        Assert.DoesNotContain(narrowSource, json, StringComparison.Ordinal);
        Assert.DoesNotContain(wideLabel, json, StringComparison.Ordinal);
        Assert.DoesNotContain(wideSource, json, StringComparison.Ordinal);
        Assert.DoesNotContain("717171", json, StringComparison.Ordinal);
        Assert.DoesNotContain("828282", json, StringComparison.Ordinal);
        Assert.DoesNotContain(path, json, StringComparison.Ordinal);
        Assert.DoesNotContain(path.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal);
        Assert.DoesNotContain("orders.sql", json, StringComparison.Ordinal);
        Assert.DoesNotContain(queryPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain(query, json, StringComparison.Ordinal);
        Assert.DoesNotContain(PlanXmlSentinel, json, StringComparison.Ordinal);
        Assert.Contains(sets[0].ValueHash, json, StringComparison.Ordinal);
        Assert.Contains("PLAN-NARROW", json, StringComparison.Ordinal);
    }

    private static void AssertSameMetrics(CompareVariantReport expected, CompareVariantReport actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.CpuTimeMilliseconds, actual.CpuTimeMilliseconds);
        Assert.Equal(expected.ElapsedTimeMilliseconds, actual.ElapsedTimeMilliseconds);
        Assert.Equal(expected.LogicalReads, actual.LogicalReads);
        Assert.Equal(expected.Operators, actual.Operators);
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(Ordered(expected.TotalLogicalReadsByTable), Ordered(actual.TotalLogicalReadsByTable));
        Assert.Equal(Ordered(expected.LogicalReadsByTable), Ordered(actual.LogicalReadsByTable));
    }

    private static IEnumerable<KeyValuePair<string, TValue>> Ordered<TValue>(IReadOnlyDictionary<string, TValue> values) =>
        values.OrderBy(pair => pair.Key, StringComparer.Ordinal);

    private static IReadOnlyList<CollectedBenchmarkRun> RunsFor(IReadOnlyList<CollectedBenchmarkRun> runs, string name) =>
        runs.Where(run => run.Artifact.ParameterSet == name).ToArray();

    private static IReadOnlyList<PreparedMeasureParameterSet> Prepare(
        params (string Name, string[] Parameters)[] sets) =>
        MeasureParameterSetValidator.Prepare(
            [],
            sets.Select(set => new SqlHarnessParameterSetInput(set.Name, set.Parameters)).ToArray(),
            null,
            "SELECT @id");

    private static SqlHarnessTargetIdentityReport Target() =>
        new("server", "db", "actual-server", "actual-db", "profile");

    private static Dictionary<string, long> Table(params (string Name, long Reads)[] tables) =>
        tables.ToDictionary(table => table.Name, table => table.Reads, StringComparer.Ordinal);

    private static PlanOperator Op(
        int nodeId,
        string physicalOp,
        string? objectName,
        bool warnings,
        bool spill,
        bool conversion) =>
        new(nodeId, physicalOp, objectName, warnings, spill, conversion);

    private static CollectedBenchmarkRun Run(
        string set,
        int repetition,
        long cpu,
        long elapsed,
        long reads,
        string resultHash,
        IReadOnlyList<string> planHashes,
        IReadOnlyDictionary<string, long>? tables = null,
        IReadOnlyList<PlanOperator>? operators = null)
    {
        operators ??= [Op(1, "Index Seek", "Clients", false, false, false)];
        return new CollectedBenchmarkRun(
            new CompareRunArtifact(
                "measure",
                repetition,
                cpu,
                elapsed,
                reads,
                tables ?? Table(("Clients", reads)),
                resultHash,
                [PlanXmlSentinel],
                1,
                set),
            [new ExecutionPlan(operators)],
            planHashes);
    }
}