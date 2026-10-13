using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ArtifactReaderTests
{
    private const string Secret = "SECRET-9f2c-artifact-content-must-not-leak";

    [Theory]
    [InlineData("measure", null)]
    [InlineData("measure-set", "set-a")]
    public void Plan_section_maps_public_query_variant_to_stored_measure_runs(string artifactKind, string? parameterSet)
    {
        using var temp = new TempDirectory();
        var id = "plan-" + artifactKind;
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(Path.Combine(directory, "plans"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            manifestVersion = 1, artifactKind, reportFile = "report.json", sections = new[] { "plan" },
        }));
        const string planName = "measure-001-000-000.plan.json";
        File.WriteAllText(Path.Combine(directory, "plans", planName),
            "{\"statements\":[{\"root\":{\"physicalOp\":\"Scan\"}}]}");
        File.WriteAllText(Path.Combine(directory, "runs.jsonl"), JsonSerializer.Serialize(new
        {
            variant = "measure", repetition = 1, parameterSet, planJsonFiles = new[] { planName },
        }));

        var section = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("query", ParameterSet: parameterSet)));

        Assert.Equal("query", section.Variant);
        Assert.Equal(parameterSet, section.ParameterSet);
        Assert.Equal(1, section.Repetition);
        Assert.Equal("Scan", Assert.Single(section.Nodes).Operator.PhysicalOp);
    }

    [Fact]
    public void Plan_section_refuses_non_object_runs_index_rows_as_corrupt_evidence()
    {
        using var temp = new TempDirectory();
        const string id = "plan-non-object-run";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");
        File.WriteAllText(Path.Combine(directory, "runs.jsonl"), "[]");

        Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline")));
    }

    [Fact]
    public void Plan_section_selects_measured_repetitions_and_paginates_every_flat_node()
    {
        using var temp = new TempDirectory();
        const string id = "stored-plan-pages";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(Path.Combine(directory, "plans"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");
        var deepBranch = "{\"physicalOp\":\"Deep0\",\"children\":[]}";
        for (var depth = 1; depth < 15; depth++)
            deepBranch = "{\"physicalOp\":\"Deep" + depth + "\",\"children\":[" + deepBranch + "]}";
        var children = new[] { deepBranch }.Concat(Enumerable.Range(1, 69)
            .Select(i => "{\"physicalOp\":\"Scan\",\"objectName\":\"dbo.T" + i + "\",\"predicate\":\"id > 0\"}"));
        var tree = "{\"physicalOp\":\"Join\",\"logicalOp\":\"Inner\",\"indexName\":\"IX_root\",\"estimatedRows\":1,\"actualRows\":2,\"children\":[" +
            string.Join(',', children) + "]}";
        var lines = new List<string>();
        foreach (var repetition in new[] { 1, 2 })
        {
            var name = $"baseline-{repetition:D3}-000-000.plan.json";
            var repetitionTree = repetition == 2 ? tree.Replace("\"actualRows\":2", "\"actualRows\":900", StringComparison.Ordinal) : tree;
            File.WriteAllText(Path.Combine(directory, "plans", name), "{\"statements\":[{\"root\":" + repetitionTree + "},{\"root\":{\"physicalOp\":\"Other\"}}],\"added\":true}");
            lines.Add(JsonSerializer.Serialize(new { variant = "baseline", repetition, cpuTimeMilliseconds = repetition,
                elapsedTimeMilliseconds = repetition, logicalReads = repetition, logicalReadsByTable = new {}, resultHash = "hash",
                messageCount = 0, planFiles = Array.Empty<string>(), planJsonFiles = new[] { name } }));
        }
        File.WriteAllText(Path.Combine(directory, "runs.jsonl"), string.Join(Environment.NewLine, lines));

        var selected = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Statement: 0)));
        Assert.Equal(2, selected.Repetition);
        Assert.Equal("last-measured", selected.Selection);
        Assert.True(selected.PlanShapeStable);
        Assert.Equal(85, selected.NodeCount);
        Assert.Equal(32, selected.Nodes.Count);
        Assert.Equal(32, selected.Continuation);
        var noNodeSpace = Assert.IsType<ArtifactPlanSection>(AgentOutputProjection.Project(selected, 128, int.MaxValue, out _, 1));
        Assert.Empty(noNodeSpace.Nodes);
        Assert.Null(noNodeSpace.Continuation);
        Assert.Contains("cannot fit", noNodeSpace.PageError, StringComparison.Ordinal);
        Assert.Empty(selected.Nodes[0].Operator.Children);
        Assert.Null(selected.Nodes[0].Operator.Predicate);
        Assert.Equal("0", selected.Nodes[1].ParentPath);
        Assert.DoesNotContain("<ShowPlanXML", JsonSerializer.Serialize(selected), StringComparison.Ordinal);

        var seen = new List<string>();
        ArtifactPlanSection page = selected;
        while (true)
        {
            var projected = Assert.IsType<ArtifactPlanSection>(AgentOutputProjection.Project(page, 128, int.MaxValue, out _, 4096));
            seen.AddRange(projected.Nodes.Select(node => node.NodePath));
            if (projected.Continuation is not int next) break;
            Assert.Equal(page.Cursor + projected.Nodes.Count, next);
            page = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
                cursor: next, planSelector: new ArtifactPlanSelector("baseline", Statement: 0)));
        }
        Assert.Equal(85, seen.Distinct().Count());
        Assert.Equal("0", seen[0]);
        Assert.Equal("0/0", seen[1]);
        Assert.Equal("0/69", seen[^1]);
        Assert.Contains("0/0/0/0/0/0/0/0/0", seen);
        var textIncluded = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Statement: 0, Repetition: 1), includeText: true));
        Assert.Equal("id > 0", textIncluded.Nodes[16].Operator.Predicate);

        var explicitSelection = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Statement: 1, Repetition: 1, ParameterSet: null), includeText: true));
        Assert.Equal(1, explicitSelection.Repetition);
        Assert.Equal("explicit", explicitSelection.Selection);
        Assert.Equal(2, explicitSelection.StatementCount);
        Assert.Equal("Other", Assert.Single(explicitSelection.Nodes).Operator.PhysicalOp);
        File.WriteAllText(Path.Combine(directory, "plans", "baseline-002-000-000.plan.json"),
            "{\"statements\":[{\"root\":" + tree.Replace("Join", "Hash Join", StringComparison.Ordinal) + "},{\"root\":{\"physicalOp\":\"Other\"}}]}");
        var changedShape = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Statement: 0)));
        Assert.False(changedShape.PlanShapeStable);
        var repetitionTwoPath = Path.Combine(directory, "plans", "baseline-002-000-000.plan.json");
        var changedIndexPlan = tree.Replace("\"actualRows\":2", "\"actualRows\":900", StringComparison.Ordinal)
            .Replace("IX_root", "IX_other", StringComparison.Ordinal);
        File.WriteAllText(repetitionTwoPath, "{\"statements\":[{\"root\":" + changedIndexPlan + "},{\"root\":{\"physicalOp\":\"Other\"}}]}");
        var changedIndex = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Statement: 0)));
        Assert.False(changedIndex.PlanShapeStable);
        var changedPredicatePlan = changedIndexPlan.Replace("id > 0", "id > 1", StringComparison.Ordinal);
        File.WriteAllText(repetitionTwoPath, "{\"statements\":[{\"root\":" + changedPredicatePlan + "},{\"root\":{\"physicalOp\":\"Other\"}}]}");
        var changedPredicate = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Statement: 0)));
        Assert.False(changedPredicate.PlanShapeStable);
        var missingRepetition = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Repetition: 9)));
        Assert.Contains("available repetitions: 1, 2", missingRepetition.Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(directory, "plans", "baseline-001-000-000.plan.json"),
            "{\"statements\":[{\"root\":{\"children\":[]}}]}");
        var incompleteComparison = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Repetition: 2)));
        Assert.Null(incompleteComparison.PlanShapeStable);
        Assert.NotEmpty(incompleteComparison.EvidenceGaps);
        Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Repetition: 1)));
    }

    [Fact]
    public void CompareWorkflow_SummaryMatchesSavedReport_WithoutRebenchmark()
    {
        using var temp = new TempDirectory();
        var report = CompareReport();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 1, 2, 3,
                new Dictionary<string, long> { ["Clients"] = 3 }, "HASH", [FixturePlan()], 0),
            new CompareRunArtifact("candidate", 1, 4, 5, 6,
                new Dictionary<string, long> { ["Clients"] = 6 }, "HASH", [FixturePlan()], 0),
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");
        var id = Path.GetFileName(directory);
        var before = FileHashes(directory);

        Assert.True(File.Exists(Path.Combine(directory, "manifest.json")));

        var summary = ArtifactReader.ReadSection(temp.Path, id, "summary");
        var metrics = Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(temp.Path, id, "metrics"));
        var operators = Assert.IsType<ArtifactOperatorsSection>(ArtifactReader.ReadSection(temp.Path, id, "operators"));

        var expected = BenchmarkSummaryProjector.Project(report with { ArtifactDirectory = directory });
        Assert.Equal(
            JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        Assert.Equal("compare", metrics.ArtifactKind);
        Assert.Equal(["baseline", "candidate"], metrics.Variants.Select(variant => variant.Name).ToArray());
        Assert.Equal(20, metrics.Variants[0].ElapsedTimeMilliseconds.Median);
        Assert.Equal(5, metrics.Variants[1].ElapsedTimeMilliseconds.Median);
        Assert.NotNull(metrics.Equivalence);
        Assert.True(metrics.Equivalence!.Equivalent);
        Assert.Contains(operators.Operators, op => op.PhysicalOp == "Index Seek");

        var payload = JsonSerializer.Serialize(new { summary, metrics, operators });
        Assert.DoesNotContain("HASH", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("planFiles", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runs.jsonl", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", payload, StringComparison.Ordinal);

        // The reader only opens manifest.json and report.json: nothing is re-run
        // and no artifact file changes.
        Assert.Equal(before, FileHashes(directory));
    }

    [Fact]
    public void Statements_section_returns_per_statement_metrics_without_sql_text()
    {
        using var temp = new TempDirectory();
        var report = CompareReport();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 30, 40, 0,
                new Dictionary<string, long>(), "hash", [MultiStatementPlan()], 0),
        };

        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");
        var section = ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "statements");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(section, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var statements = json.RootElement.GetProperty("statements").EnumerateArray().ToArray();

        Assert.Equal(2, statements.Length);
        Assert.Equal([0, 1], statements.Select(item => item.GetProperty("statementOrdinal").GetInt32()).ToArray());
        Assert.Equal(30, statements.Sum(item => item.GetProperty("cpuTimeMilliseconds").GetInt64()));
        Assert.Equal(40, statements.Sum(item => item.GetProperty("elapsedTimeMilliseconds").GetInt64()));
        Assert.All(statements, item => Assert.Equal(64, item.GetProperty("statementHash").GetString()!.Length));
        Assert.Equal(2, statements[0].GetProperty("degreeOfParallelism").GetInt32());
        var statementPayload = JsonSerializer.Serialize(section);
        Assert.Contains("Index Seek", statementPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE dbo.SecretTable", statementPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void Statements_section_accepts_a_continuation_cursor()
    {
        using var temp = new TempDirectory();
        const string id = "statements-pages";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"statements\"]}");
        var statements = Enumerable.Range(0, 300).Select(ordinal => new ArtifactStatementMetric(
            "baseline", null, null, ordinal, new string('a', 64), 1, 2, 1, [])).ToArray();
        File.WriteAllText(Path.Combine(directory, "statements.json"), JsonSerializer.Serialize(
            new ArtifactStatementsSection(id, "compare", statements, 0),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var first = Assert.IsType<ArtifactStatementsSection>(ArtifactReader.ReadSection(temp.Path, id, "statements"));
        Assert.Equal(300, first.Statements.Count);
        var projected = Assert.IsType<ArtifactStatementsSection>(AgentOutputProjection.Project(first, 512, 128, out _, 65536));
        Assert.Equal(128, projected.Statements.Count);
        Assert.Equal(128, projected.Continuation);

        var second = Assert.IsType<ArtifactStatementsSection>(ArtifactReader.ReadSection(temp.Path, id, "statements", cursor: 128));
        Assert.Equal(128, second.Statements[0].StatementOrdinal);
        var projectedSecond = Assert.IsType<ArtifactStatementsSection>(AgentOutputProjection.Project(second, 512, 128, out _, 65536));
        Assert.Equal(256, projectedSecond.Continuation);

        var last = Assert.IsType<ArtifactStatementsSection>(ArtifactReader.ReadSection(temp.Path, id, "statements", cursor: 256));
        var projectedLast = Assert.IsType<ArtifactStatementsSection>(AgentOutputProjection.Project(last, 512, 128, out _, 65536));
        Assert.Equal(44, projectedLast.Statements.Count);
        Assert.Null(projectedLast.Continuation);
    }

    [Fact]
    public void Matrix_case_inventory_pages_preserve_the_agent_case_schema_and_cursor()
    {
        using var temp = new TempDirectory();
        const string id = "matrix-index";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"matrix-cells\"]}");
        var cases = Enumerable.Range(0, 40).Select(index => new AgentMatrixCase(index, $"cell-{index:D3}", true, "compared",
            new AgentMedianMetrics(1, 2, 3), new AgentMedianMetrics(4, 5, 6), new AgentMedianMetrics(3, 3, 3), ["warning"])).ToArray();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), JsonSerializer.Serialize(new
        {
            cases,
        }, jsonOptions));

        var first = ArtifactReader.ReadSection(temp.Path, id, "matrix-cells");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(32, json.RootElement.GetProperty("cases").GetArrayLength());
        Assert.Equal(4m, json.RootElement.GetProperty("cases")[0].GetProperty("candidate").GetProperty("cpuMilliseconds").GetDecimal());
        Assert.Equal(32, json.RootElement.GetProperty("continuation").GetInt32());
        var second = Assert.IsType<ArtifactMatrixCellsSection>(ArtifactReader.ReadSection(temp.Path, id, "matrix-cells", cursor: 32));
        Assert.Equal(8, second.Cases.Count);
        Assert.Null(second.Continuation);
        Assert.Equal(Enumerable.Range(32, 8), second.Cases.Select(cell => cell.Index));

        var smallPage = Assert.IsType<ArtifactMatrixCellsSection>(AgentOutputProjection.Project(first, 128, 0, out _, 4096));
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(smallPage, jsonOptions).Length, 1, 4096);
        Assert.InRange(smallPage.Cases.Count, 1, 31);
        Assert.Equal(smallPage.Cases.Count, smallPage.Continuation);

        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), JsonSerializer.Serialize(new
        {
            cells = Enumerable.Range(0, 40).Select(index => new { index, artifactId = $"cell-{index:D3}" }).ToArray(),
        }, jsonOptions));
        var legacy = Assert.IsType<ArtifactMatrixCellsSection>(ArtifactReader.ReadSection(temp.Path, id, "matrix-cells"));
        Assert.Equal("unavailable", legacy.Cases[0].EquivalenceAvailability);
        Assert.Null(legacy.Cases[0].Equivalent);
        Assert.Null(legacy.Cases[0].Baseline.CpuMilliseconds);
        Assert.Equal("cell-000", legacy.Cases[0].ArtifactId);
    }

    [Fact]
    public void Statement_metrics_keep_nested_relop_counters_and_objects_with_their_owner()
    {
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements><StmtSimple StatementText="safe">
                <QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Nested Loops" EstimateRows="2">
                    <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="2" ActualExecutions="1" /></RunTimeInformation>
                    <NestedLoops>
                      <RelOp NodeId="1" PhysicalOp="Index Seek" EstimateRows="3">
                        <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="7" ActualExecutions="4" /></RunTimeInformation>
                        <IndexScan><Object Table="[dbo].[Child]" Index="[IX_Child]" /></IndexScan>
                      </RelOp>
                    </NestedLoops>
                  </RelOp>
                </QueryPlan>
              </StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        var statements = StatementMetricsExtractor.Extract(plan, out _);
        var operators = Assert.Single(statements).TopOperators.ToDictionary(item => item.NodeId);

        Assert.Equal(2, operators[0].ActualRows);
        Assert.Equal(1, operators[0].Executions);
        Assert.Null(operators[0].Object);
        Assert.Equal(7, operators[1].ActualRows);
        Assert.Equal(4, operators[1].Executions);
        Assert.Equal("[dbo].[Child]", operators[1].Object);
        Assert.Equal("[IX_Child]", operators[1].Index);
    }

    [Fact]
    public void MeasureAndMeasureSet_AllSectionsRead()
    {
        using var temp = new TempDirectory();
        var writer = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch);
        var measure = new SqlHarnessMeasureReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5, 5, true, Variant("query"), null);
        var measureSet = new SqlHarnessMeasureSetReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5, 10, 1, ["small", "large"], "Measured in user order.", "Shared plan cache.",
            [
                new MeasureParameterSetReport("small", [new MeasureParameterMetadata("n", "int")],
                    "vh1", 5, true, "RH", Variant("query"), ["ph1"]),
                new MeasureParameterSetReport("large", [new MeasureParameterMetadata("n", "int")],
                    "vh2", 5, false, "RH", Variant("query"), ["ph2"]),
            ],
            new MeasureCrossSetSummary("small", 3, "large", 3, "small", 2, "large", 2, "small", 4, "large", 4),
            null);
        var run = new CompareRunArtifact("query", 1, 1, 2, 3,
            new Dictionary<string, long>(), "HASH", [FixturePlan()], 0);

        var measureId = Path.GetFileName(writer.Write(measure, [run], "m"));
        var setId = Path.GetFileName(writer.Write(measureSet, [run, run with { ParameterSet = "large" }], "s"));

        var measureMetrics = Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(temp.Path, measureId, "metrics"));
        Assert.Equal(["query"], measureMetrics.Variants.Select(variant => variant.Name).ToArray());
        Assert.Null(measureMetrics.Equivalence);

        var setMetrics = Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(temp.Path, setId, "metrics"));
        Assert.Equal(["small", "large"], setMetrics.Variants.Select(variant => variant.Name).ToArray());

        var setOperators = Assert.IsType<ArtifactOperatorsSection>(ArtifactReader.ReadSection(temp.Path, setId, "operators"));
        Assert.Equal(ArtifactReader.MeasureSetKind, setOperators.ArtifactKind);
        Assert.True(setOperators.Operators.Count <= 10);

        Assert.IsType<MeasureBenchmarkSummary>(ArtifactReader.ReadSection(temp.Path, measureId, "summary"));
        Assert.IsType<MeasureSetBenchmarkSummary>(ArtifactReader.ReadSection(temp.Path, setId, "summary"));
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("-leading-dash")]
    [InlineData("trailing-dot.")]
    public void UnknownOrTraversalId_IsRefusedWithoutContent(string id)
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal(SqlHarnessExitCode.Safety, exception.ExitCode);
        Assert.Equal("Unknown artifact id.", exception.Message);
    }

    [Fact]
    public void OverlongId_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, new string('a', 129), "summary"));

        Assert.Equal("Unknown artifact id.", exception.Message);
    }

    [Fact]
    public void LegacyArtifactWithoutManifest_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();
        var id = "legacy-artifact";
        Directory.CreateDirectory(Path.Combine(temp.Path, id));
        File.WriteAllText(Path.Combine(temp.Path, id, "report.json"), """{"kind":"compare"}""");
        File.WriteAllText(Path.Combine(temp.Path, id, "queries.jsonl"), Secret);

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal(SqlHarnessExitCode.Safety, exception.ExitCode);
        Assert.Contains("no versioned manifest", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptManifest_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();
        var id = "corrupt-manifest";
        Directory.CreateDirectory(Path.Combine(temp.Path, id));
        File.WriteAllText(Path.Combine(temp.Path, id, "manifest.json"), "{not json " + Secret);

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact manifest is invalid.", exception.Message);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedManifestVersion_IsRefused()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "future-artifact",
            """{"manifestVersion":999,"artifactKind":"compare","reportFile":"report.json","sections":["summary"]}""",
            """{"target":null}""");

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Contains("manifest version 999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestWithUnknownKind_IsRefused()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "odd-kind",
            """{"manifestVersion":1,"artifactKind":"snapshot","reportFile":"report.json","sections":["summary"]}""",
            """{}""");

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact manifest is invalid.", exception.Message);
    }

    [Fact]
    public void ManifestPointingOutsideReportJson_IsRefused()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "remapped",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"other.json","sections":["summary"]}""",
            """{}""");
        File.WriteAllText(Path.Combine(temp.Path, id, "other.json"), Secret);

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact manifest is invalid.", exception.Message);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionOutsideManifestMapping_IsRefused()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "narrow",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary"]}""",
            """{}""");

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "metrics"));

        Assert.Contains("not available", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSection_IsRefused()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, "whatever", "plans"));

        Assert.Contains("summary, metrics, operators", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptReport_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "corrupt-report",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary","metrics","operators"]}""",
            "GARBAGE-" + Secret);

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact report is invalid.", exception.Message);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestKindMismatchingReportContent_IsRefused()
    {
        using var temp = new TempDirectory();
        var measure = new SqlHarnessMeasureReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            1, 1, true, Variant("query"), null);
        var run = new CompareRunArtifact("measure", 1, 1, 2, 3,
            new Dictionary<string, long>(), "HASH", [FixturePlan()], 0);
        var id = Path.GetFileName(new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(measure, [run], "m"));
        File.WriteAllText(
            Path.Combine(temp.Path, id, "manifest.json"),
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary","metrics","operators"]}""");

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Equal("Artifact report is invalid.", exception.Message);
    }

    [Fact]
    public void OversizeReport_IsRefusedWithoutReading()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "oversize",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary"]}""",
            """{}""");
        using (var stream = new FileStream(Path.Combine(temp.Path, id, "report.json"), FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(ArtifactReader.MaxReportBytes + 1);
        }

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Contains("read limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizeManifest_IsRefusedWithoutReading()
    {
        using var temp = new TempDirectory();
        var id = WriteRawArtifact(temp.Path, "oversize-manifest",
            """{"manifestVersion":1}""",
            """{}""");
        using (var stream = new FileStream(Path.Combine(temp.Path, id, "manifest.json"), FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(ArtifactReader.MaxManifestBytes + 1);
        }

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Contains("read limit", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\outside-target", (int)FileAttributes.Directory, true)]
    [InlineData(null, (int)(FileAttributes.Directory | FileAttributes.ReparsePoint), true)]
    [InlineData("relative-target", (int)FileAttributes.Directory, true)]
    [InlineData(null, (int)FileAttributes.Directory, false)]
    [InlineData(null, (int)FileAttributes.Normal, false)]
    public void LinkOrReparsePredicate_RefusesEscapes(string? linkTarget, int attributes, bool expected)
    {
        Assert.Equal(expected, ArtifactReader.IsEscapeLink(linkTarget, (FileAttributes)attributes));
    }

    [Fact]
    public void SymlinkEscape_IsRefusedWithoutContent()
    {
        using var temp = new TempDirectory();
        var outside = Path.Combine(temp.Path, "outside-secret");
        Directory.CreateDirectory(outside);
        var secretId = WriteRawArtifact(outside, "secret-artifact",
            """{"manifestVersion":1,"artifactKind":"compare","reportFile":"report.json","sections":["summary","metrics","operators"]}""",
            Secret);
        var link = Path.Combine(temp.Path, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(outside, secretId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // capability guard: xUnit v2 has no dynamic skip
            // Creating links needs OS privilege; traversal batteries above always run.
            return;
        }

        var thrown = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, "linked", "summary"));

        Assert.Equal("Unknown artifact id.", thrown.Message);
        Assert.DoesNotContain(Secret, thrown.Message, StringComparison.Ordinal);
    }

    // 003/T2: CLI-compat guards. The offline reader takes no owner (opt-in
    // enforcement lives on the MCP path per plans/003-scope-contract.md), so
    // owner metadata — present, foreign, or absent — never changes CLI reads.

    [Fact]
    public void ManifestWithOwnerMetadata_RemainsReadableByCli()
    {
        using var temp = new TempDirectory();
        var id = WriteOwnedCliArtifact(temp.Path, """{"profile": "cli-owner", "vars": {"tenant": "acme"}, "engine": "sqlserver", "server": "safe-server", "database": "safe-db"}""");

        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "summary"));
        var metrics = Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(temp.Path, id, "metrics"));
        Assert.Equal(["baseline", "candidate"], metrics.Variants.Select(variant => variant.Name).ToArray());
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "operators"));
    }

    [Fact]
    public void ManifestWithForeignOwner_RemainsReadableByCli()
    {
        using var temp = new TempDirectory();
        var id = WriteOwnedCliArtifact(temp.Path, """{"profile": "other-profile", "vars": {"tenant": "other"}, "engine": "sqlserver", "server": "other-server", "database": "other-db"}""");

        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "summary"));
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "metrics"));
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "operators"));
    }

    [Fact]
    public void ManifestV1WithoutOwner_RemainsReadableByCli()
    {
        using var temp = new TempDirectory();
        var id = WriteOwnedCliArtifact(temp.Path, ownerJson: null);

        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "summary"));
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "metrics"));
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "operators"));
    }

    private static string WriteOwnedCliArtifact(string root, string? ownerJson)
    {
        var report = CompareReport();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 1, 2, 3,
                new Dictionary<string, long> { ["Clients"] = 3 }, "HASH", [FixturePlan()], 0),
            new CompareRunArtifact("candidate", 1, 4, 5, 6,
                new Dictionary<string, long> { ["Clients"] = 6 }, "HASH", [FixturePlan()], 0),
        };
        var directory = new CompareArtifactWriter(root, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");
        if (ownerJson is not null)
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            var trimmed = File.ReadAllText(manifestPath).TrimEnd();
            Assert.EndsWith("}", trimmed);
            File.WriteAllText(manifestPath, trimmed[..^1] + ",\"owner\": " + ownerJson + "}");
        }

        return Path.GetFileName(directory);
    }

    private static string WriteRawArtifact(string root, string id, string manifest, string report)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest, Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "report.json"), report, Encoding.UTF8);
        return id;
    }

    private static SqlHarnessCompareReport CompareReport() => new(
        new SqlHarnessTargetIdentityReport("safe-server", "safe-db", "safe-server", "safe-db", "profile"),
        5, 10, true, Variant("baseline", new CompareDistribution(10, 20, 30), flagged: true), Variant("candidate", new CompareDistribution(4, 5, 6)), null);

    private static CompareVariantReport Variant(string name, CompareDistribution? elapsed = null, bool flagged = false) => new(
        name,
        new CompareDistribution(1, 2, 3),
        elapsed ?? new CompareDistribution(2, 3, 4),
        new CompareDistribution(3, 4, 5),
        new Dictionary<string, long> { ["Clients"] = 4 },
        [new CompareOperatorReport(1, "Index Seek", "Clients", flagged, false, false)],
        flagged ? ["plan-warning"] : []);

    private static string FixturePlan() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "distiller-sample.sqlplan"));

    private static string MultiStatementPlan() => """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
        <StmtSimple StatementText="UPDATE dbo.SecretTable SET A = 1"><QueryPlan DegreeOfParallelism="2"><QueryTimeStats CpuTime="10" ElapsedTime="15"/><RelOp NodeId="0" PhysicalOp="Index Seek" EstimateRows="2"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="4" ActualExecutions="1"/></RunTimeInformation><IndexScan><Object Table="[dbo].[SecretTable]" Index="[IX_A]"/></IndexScan></RelOp></QueryPlan></StmtSimple>
        <StmtSimple StatementText="UPDATE dbo.SecretTable SET B = 2"><QueryPlan DegreeOfParallelism="1"><QueryTimeStats CpuTime="20" ElapsedTime="25"/><RelOp NodeId="0" PhysicalOp="Table Scan" EstimateRows="3"><RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="6" ActualExecutions="2"/></RunTimeInformation><TableScan><Object Table="[dbo].[SecretTable]"/></TableScan></RelOp></QueryPlan></StmtSimple>
        </Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    private static IReadOnlyDictionary<string, string> FileHashes(string directory)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            hashes[Path.GetRelativePath(directory, file)] =
                Convert.ToHexString(SHA256.HashData(stream));
        }
        return hashes;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-artifact-read-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
