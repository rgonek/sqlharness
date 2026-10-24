using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ArtifactReaderTests
{
    private const string Secret = "SECRET-9f2c-artifact-content-must-not-leak";

    [Fact]
    public void Read_section_returns_producer_reference_only_after_owner_check()
    {
        using var temp = new TempDirectory();
        var id = "owned-artifact";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            manifestVersion = 1,
            artifactKind = "compare",
            reportFile = "report.json",
            sections = new[] { "matrix-cells" },
            producerOperationRef = "producer-1",
            owner = new { profile = "p", vars = new Dictionary<string, string>(), engine = "postgres", server = "s", database = "d" },
        }));
        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), "{\"cases\":[]}");

        var wrongOwner = new ArtifactOwner("other", new Dictionary<string, string>(), "postgres", "s", "d");
        Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSectionWithMetadata(
            temp.Path, id, "matrix-cells", wrongOwner));

        var resolved = ArtifactReader.ReadSectionWithMetadata(temp.Path, id, "matrix-cells",
            new ArtifactOwner("p", new Dictionary<string, string>(), "postgres", "s", "d"));
        Assert.Equal("producer-1", resolved.ProducerOperationRef);
    }

    [Fact]
    public void Legacy_manifest_read_has_unavailable_producer_reference()
    {
        using var temp = new TempDirectory();
        var id = "legacy-without-producer";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"matrix-cells\"]}");
        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), "{\"cases\":[]}");

        var resolved = ArtifactReader.ReadSectionWithMetadata(temp.Path, id, "matrix-cells");

        Assert.Null(resolved.ProducerOperationRef);
    }

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
            manifestVersion = 1,
            artifactKind,
            reportFile = "report.json",
            sections = new[] { "plan" },
        }));
        const string firstPlanName = "measure-001-000-000.plan.json";
        const string secondPlanName = "measure-002-000-000.plan.json";
        const string planJson = "{\"statements\":[{\"root\":{\"physicalOp\":\"Scan\"}},{\"root\":{\"physicalOp\":\"Filter\"}}]}";
        File.WriteAllText(Path.Combine(directory, "plans", firstPlanName), planJson);
        File.WriteAllText(Path.Combine(directory, "plans", secondPlanName), planJson);
        File.WriteAllLines(Path.Combine(directory, "runs.jsonl"), new[]
        {
            JsonSerializer.Serialize(new
            {
                variant = "measure",
                repetition = 1,
                parameterSet,
                planJsonFiles = new[] { firstPlanName },
            }),
            JsonSerializer.Serialize(new
            {
                variant = "measure",
                repetition = 2,
                parameterSet,
                planJsonFiles = new[] { secondPlanName },
            }),
        });

        var defaulted = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("", Statement: 1, ParameterSet: parameterSet, Repetition: 1)));
        var section = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("query", Statement: 1, ParameterSet: parameterSet, Repetition: 1)));

        Assert.Equal(section.ArtifactId, defaulted.ArtifactId);
        Assert.Equal("query", defaulted.Variant);
        Assert.Equal(section.ParameterSet, defaulted.ParameterSet);
        Assert.Equal(section.Statement, defaulted.Statement);
        Assert.Equal(section.StatementCount, defaulted.StatementCount);
        Assert.Equal(1, defaulted.Statement);
        Assert.Equal(section.Repetition, defaulted.Repetition);
        Assert.Equal(1, defaulted.Repetition);
        Assert.Equal(section.Selection, defaulted.Selection);
        Assert.Equal(section.AvailableRepetitions, defaulted.AvailableRepetitions);
        Assert.Equal(new[] { 1, 2 }, defaulted.AvailableRepetitions);
        Assert.Equal(section.NodeCount, defaulted.NodeCount);
        Assert.Equal(section.Nodes, defaulted.Nodes);
        Assert.Equal("query", section.Variant);
        Assert.Equal(parameterSet, section.ParameterSet);
        Assert.Equal(1, section.Repetition);
        Assert.Equal("Filter", Assert.Single(section.Nodes).Operator.PhysicalOp);
    }

    [Theory]
    [InlineData("Query")]
    [InlineData("baseline")]
    [InlineData(" query")]
    public void Plan_section_does_not_repair_explicit_nonempty_measure_variants(string variant)
    {
        using var temp = new TempDirectory();
        const string id = "plan-invalid-measure-variant";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"measure\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");

        var error = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector(variant)));

        Assert.Equal(SqlHarnessExitCode.Safety, error.ExitCode);
        Assert.Contains("query", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("measure", "candidate", "query")]
    [InlineData("measure-set", "baseline", "query")]
    [InlineData("compare", "query", "baseline, candidate")]
    public void Plan_section_invalid_variant_reports_values_for_manifest_kind(
        string artifactKind, string variant, string acceptedValues)
    {
        using var temp = new TempDirectory();
        const string id = "plan-invalid-variant-diagnostic";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            manifestVersion = 1,
            artifactKind,
            reportFile = "report.json",
            sections = new[] { "plan" },
            owner = new { profile = "p", vars = new Dictionary<string, string>(), engine = "sqlserver", server = "s", database = "d" },
        }));

        var wrongOwner = new ArtifactOwner("other", new Dictionary<string, string>(), "sqlserver", "s", "d");
        var scopeError = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(
            temp.Path, id, "plan", wrongOwner, planSelector: new ArtifactPlanSelector(variant)));
        Assert.Contains("not available in the current scope", scopeError.Message, StringComparison.Ordinal);

        var error = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(
            temp.Path, id, "plan", planSelector: new ArtifactPlanSelector(variant)));

        Assert.Equal(SqlHarnessExitCode.Safety, error.ExitCode);
        Assert.Contains($"for {artifactKind}", error.Message, StringComparison.Ordinal);
        Assert.Contains(acceptedValues, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_plan_section_requires_an_explicit_variant()
    {
        using var temp = new TempDirectory();
        const string id = "plan-empty-compare-variant";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");

        var omittedError = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(temp.Path, id, "plan"));
        var emptyError = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("")));

        Assert.Equal(SqlHarnessExitCode.Safety, omittedError.ExitCode);
        Assert.Equal(SqlHarnessExitCode.Safety, emptyError.ExitCode);
    }

    [Fact]
    public void Non_plan_sections_reject_plan_selectors_before_reading_the_section()
    {
        using var temp = new TempDirectory();
        const string id = "non-plan-selector";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"measure\",\"reportFile\":\"report.json\",\"sections\":[\"summary\"]}");

        var error = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(temp.Path, id, "summary",
            planSelector: new ArtifactPlanSelector("")));

        Assert.Equal(SqlHarnessExitCode.Safety, error.ExitCode);
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
            lines.Add(JsonSerializer.Serialize(new
            {
                variant = "baseline",
                repetition,
                cpuTimeMilliseconds = repetition,
                elapsedTimeMilliseconds = repetition,
                logicalReads = repetition,
                logicalReadsByTable = new { },
                resultHash = "hash",
                messageCount = 0,
                planFiles = Array.Empty<string>(),
                planJsonFiles = new[] { name }
            }));
        }
        File.WriteAllText(Path.Combine(directory, "runs.jsonl"), string.Join(Environment.NewLine, lines));

        var selected = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Statement: 0)));
        Assert.Equal(2, selected.Repetition);
        Assert.Equal("last-measured", selected.Selection);
        Assert.True(selected.PlanShapeStable);
        Assert.Equal(85, selected.NodeCount);
        Assert.Equal(85, selected.Nodes.Count);
        Assert.Null(selected.Continuation);
        var noNodeSpace = Assert.IsType<ArtifactPlanSection>(AgentOutputProjection.Project(selected, 128, int.MaxValue, out _, 1));
        Assert.Empty(noNodeSpace.Nodes);
        Assert.Null(noNodeSpace.Continuation);
        Assert.Equal(selected.Cursor, noNodeSpace.Cursor);
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(32)]
    public void Plan_projection_counts_a_clipped_statement_once_independent_of_node_count(int nodeCount)
    {
        var section = SyntheticPlanSection(
            Enumerable.Range(0, nodeCount).Select(index => SyntheticPlanNode(index)).ToArray(),
            new string('s', 600));

        var projected = Assert.IsType<ArtifactPlanSection>(
            AgentOutputProjection.Project(section, 512, int.MaxValue, out var omitted, 65536));

        Assert.Equal(1, omitted);
        Assert.Equal(512, projected.StatementText!.Length);
    }

    [Fact]
    public void Plan_projection_counts_a_clipped_predicate_once()
    {
        var section = SyntheticPlanSection(
            [SyntheticPlanNode(0, new string('p', 600))], "short statement");

        var projected = Assert.IsType<ArtifactPlanSection>(
            AgentOutputProjection.Project(section, 512, int.MaxValue, out var omitted, 65536));

        Assert.Equal(1, omitted);
        Assert.Equal(512, projected.Nodes[0].Operator.Predicate!.Length);
    }

    [Fact]
    public void Plan_projection_counts_only_retained_clipping_when_a_node_does_not_fit()
    {
        var section = SyntheticPlanSection(
            [SyntheticPlanNode(0, new string('p', 600))], new string('s', 600));

        var projected = Assert.IsType<ArtifactPlanSection>(
            AgentOutputProjection.Project(section, 512, int.MaxValue, out var omitted, 1));

        Assert.Empty(projected.Nodes);
        Assert.Null(projected.Continuation);
        Assert.Contains("cannot fit", projected.PageError, StringComparison.Ordinal);
        Assert.Equal(512, projected.StatementText!.Length);
        Assert.Equal(2, omitted); // one omitted node and one retained clipped statement; rejected predicate is not returned.
    }

    [Fact]
    public void Plan_projection_counts_clipped_statement_when_there_are_no_nodes()
    {
        var section = SyntheticPlanSection([], new string('s', 600));

        var projected = Assert.IsType<ArtifactPlanSection>(
            AgentOutputProjection.Project(section, 512, int.MaxValue, out var omitted, 65536));

        Assert.Empty(projected.Nodes);
        Assert.Equal(512, projected.StatementText!.Length);
        Assert.Equal(1, omitted);
    }

    private static ArtifactPlanSection SyntheticPlanSection(IReadOnlyList<ArtifactPlanNode> nodes, string statementText) =>
        new("artifact", "baseline", null, 0, 1, 1, "last-measured", [1], null, nodes.Count, nodes, null)
        { StatementText = statementText };

    private static ArtifactPlanNode SyntheticPlanNode(int index, string? predicate = null) =>
        new(index == 0 ? "0" : $"0/{index - 1}", index == 0 ? null : "0",
            new PlanNode("Scan", null, null, null, 1, 1, 1, 0.1, predicate, [], []));

    [Fact]
    public void Plan_section_keeps_measured_runs_without_saved_plan_evidence_in_inventory()
    {
        using var temp = new TempDirectory();
        const string id = "stored-plan-gap";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(Path.Combine(directory, "plans"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");
        var runLines = new List<string>();
        foreach (var repetition in new[] { 1, 2 })
        {
            var name = $"baseline-{repetition:D3}-000-000.plan.json";
            File.WriteAllText(Path.Combine(directory, "plans", name),
                "{\"statements\":[{\"root\":{\"physicalOp\":\"Scan\",\"children\":[]}}]}");
            runLines.Add(JsonSerializer.Serialize(new
            {
                variant = "baseline",
                repetition,
                planJsonFiles = new[] { name }
            }));
        }
        runLines.Add(JsonSerializer.Serialize(new
        {
            variant = "baseline",
            repetition = 3,
            planJsonFiles = Array.Empty<string>()
        }));
        File.WriteAllText(Path.Combine(directory, "runs.jsonl"), string.Join(Environment.NewLine, runLines));

        var defaultSelection = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline")));
        Assert.Contains("Repetition 3", defaultSelection.Message, StringComparison.Ordinal);
        Assert.Contains("no saved plan evidence", defaultSelection.Message, StringComparison.OrdinalIgnoreCase);

        var earlierSelection = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(temp.Path, id, "plan",
            planSelector: new ArtifactPlanSelector("baseline", Repetition: 2)));
        Assert.Equal(2, earlierSelection.Repetition);
        Assert.Equal(new[] { 1, 2, 3 }, earlierSelection.AvailableRepetitions);
        Assert.Null(earlierSelection.PlanShapeStable);
        Assert.NotEmpty(earlierSelection.EvidenceGaps);
    }

    [Fact]
    public void Plan_section_hint_carries_the_overlap_verdict()
    {
        using var temp = new TempDirectory();
        const string id = "overlap-include-gap";
        WriteOverlapArtifact(temp.Path, id, PlanJson(OrdersHint(schema: null, database: null)), Inventory(
            "AdventureWorks",
            [Table("DBO", "orders", found: true, truncated: false, Ix("IX_C", ["CustomerId"], ["Status"]))]));

        var section = ReadOverlapPlan(temp.Path, id);
        var json = SectionJson(section);
        var hint = json.GetProperty("missingIndexes")[0];
        var overlap = hint.GetProperty("overlap");

        Assert.False(hint.TryGetProperty("schema", out _));
        Assert.False(hint.TryGetProperty("database", out _));
        Assert.Equal("Orders", hint.GetProperty("table").GetString());
        Assert.Equal("include-gap", overlap.GetProperty("verdict").GetString());
        Assert.Equal("IX_C", overlap.GetProperty("index").GetProperty("name").GetString());
        Assert.Equal("Total", Assert.Single(overlap.GetProperty("missingIncludeColumns").EnumerateArray()).GetString());
        Assert.Empty(section.EvidenceGaps);

        var nextPage = ReadOverlapPlan(temp.Path, id, cursor: 1);
        Assert.Empty(nextPage.MissingIndexes);
    }

    [Fact]
    public void Legacy_artifact_without_inventory_reads_without_overlap()
    {
        using var temp = new TempDirectory();
        const string id = "overlap-legacy";
        WriteOverlapArtifact(temp.Path, id, PlanJson(OrdersHint(), OrdersHint()), inventoryJson: null);

        var section = ReadOverlapPlan(temp.Path, id);
        var json = SectionJson(section);

        Assert.Equal(2, json.GetProperty("missingIndexes").GetArrayLength());
        Assert.All(json.GetProperty("missingIndexes").EnumerateArray(), hint => Assert.False(hint.TryGetProperty("overlap", out _)));
        Assert.Equal(
            ["Index inventory was not captured for this artifact; missing-index hints have no overlap verdict."],
            section.EvidenceGaps);
    }

    [Fact]
    public void Not_found_table_gives_no_verdict()
    {
        using var temp = new TempDirectory();
        const string id = "overlap-not-found";
        WriteOverlapArtifact(temp.Path, id, PlanJson(OrdersHint(), OrdersHint()), Inventory(
            "AdventureWorks",
            [Table("dbo", "Orders", found: false, truncated: false, Ix("IX_C", ["CustomerId"], ["Total"]))]));

        var section = ReadOverlapPlan(temp.Path, id);
        var json = SectionJson(section);

        Assert.Equal(2, json.GetProperty("missingIndexes").GetArrayLength());
        Assert.All(json.GetProperty("missingIndexes").EnumerateArray(), hint => Assert.False(hint.TryGetProperty("overlap", out _)));
        Assert.Equal(
            ["dbo.Orders was not found when the index inventory was captured."],
            section.EvidenceGaps);
    }

    [Fact]
    public void Skipped_table_gives_no_verdict_and_inventory_gaps_are_surfaced()
    {
        using var temp = new TempDirectory();
        const string id = "overlap-skipped";
        const string skipGap = "Index inventory covers the first 8 hinted tables; 3 more were skipped.";
        WriteOverlapArtifact(temp.Path, id, PlanJson(OrdersHint(), OrdersHint()), Inventory(
            "AdventureWorks",
            [Table("dbo", "Customers", found: true, truncated: false, Ix("IX_C", ["CustomerId"]))],
            skipGap));

        var section = ReadOverlapPlan(temp.Path, id);
        var json = SectionJson(section);

        Assert.Equal(2, json.GetProperty("missingIndexes").GetArrayLength());
        Assert.All(json.GetProperty("missingIndexes").EnumerateArray(), hint => Assert.False(hint.TryGetProperty("overlap", out _)));
        Assert.Equal([skipGap], section.EvidenceGaps);
    }

    [Fact]
    public void Truncated_index_list_downgrades_new_shape_to_unavailable()
    {
        using var temp = new TempDirectory();
        const string id = "overlap-truncated";
        WriteOverlapArtifact(
            temp.Path,
            id,
            PlanJson(
                OrdersHint(),
                OrdersHint(equality: ["Status"], include: []),
                OrdersHint(table: "Payments", equality: ["Zip"], include: [])),
            Inventory(
                "AdventureWorks",
                [
                    Table("dbo", "Orders", found: true, truncated: true, Ix("IX_C", ["CustomerId"], ["Status"])),
                    Table("dbo", "Payments", found: true, truncated: false, Ix("IX_P", ["Id"])),
                ]));

        var section = ReadOverlapPlan(temp.Path, id);
        var hints = SectionJson(section).GetProperty("missingIndexes");
        var kept = hints[0].GetProperty("overlap");
        var fresh = hints[2].GetProperty("overlap");

        Assert.Equal("include-gap", kept.GetProperty("verdict").GetString());
        Assert.Equal("IX_C", kept.GetProperty("index").GetProperty("name").GetString());
        Assert.False(hints[1].TryGetProperty("overlap", out _));
        Assert.Equal("new-shape", fresh.GetProperty("verdict").GetString());
        Assert.False(fresh.TryGetProperty("index", out _));
        Assert.Equal(
            ["dbo.Orders has more than 64 indexes; a new-shape verdict cannot be trusted."],
            section.EvidenceGaps);
    }

    [Fact]
    public void Invalid_inventory_file_is_a_gap_not_a_failure()
    {
        using var temp = new TempDirectory();
        const string id = "overlap-corrupt";
        WriteOverlapArtifact(temp.Path, id, PlanJson(OrdersHint()), "{");

        var section = ReadOverlapPlan(temp.Path, id);
        var hint = SectionJson(section).GetProperty("missingIndexes")[0];

        Assert.Equal("Scan", Assert.Single(section.Nodes).Operator.PhysicalOp);
        Assert.Equal("Orders", hint.GetProperty("table").GetString());
        Assert.False(hint.TryGetProperty("overlap", out _));
        Assert.Equal(["Index inventory is unreadable."], section.EvidenceGaps);
    }

    [Fact]
    public void Hint_for_another_database_has_no_verdict()
    {
        using var temp = new TempDirectory();
        const string id = "overlap-other-database";
        WriteOverlapArtifact(
            temp.Path,
            id,
            PlanJson(OrdersHint(database: "Contoso"), OrdersHint(database: "adventureworks")),
            Inventory(
                "AdventureWorks",
                [Table("dbo", "Orders", found: true, truncated: false, Ix("IX_C", ["CustomerId"], ["Status"]))]));

        var section = ReadOverlapPlan(temp.Path, id);
        var hints = SectionJson(section).GetProperty("missingIndexes");

        Assert.False(hints[0].TryGetProperty("overlap", out _));
        Assert.Equal("include-gap", hints[1].GetProperty("overlap").GetProperty("verdict").GetString());
        Assert.Empty(section.EvidenceGaps);
    }

    private static ArtifactPlanSection ReadOverlapPlan(string root, string id, int cursor = 0) =>
        Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(root, id, "plan", cursor: cursor,
            planSelector: new ArtifactPlanSelector("baseline")));

    private static JsonElement SectionJson(ArtifactPlanSection section)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(section, ArtifactDirectoryPublisher.JsonOptions));
        return document.RootElement.Clone();
    }

    private static void WriteOverlapArtifact(string root, string id, string planJson, string? inventoryJson)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(directory, "plans"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");
        const string planName = "baseline-001-000-000.plan.json";
        File.WriteAllText(Path.Combine(directory, "plans", planName), planJson);
        File.WriteAllText(Path.Combine(directory, "runs.jsonl"),
            "{\"variant\":\"baseline\",\"repetition\":1,\"planJsonFiles\":[\"" + planName + "\"]}");
        if (inventoryJson is not null)
            File.WriteAllText(Path.Combine(directory, "index-inventory.json"), inventoryJson);
    }

    private static string PlanJson(params object[] hints) =>
        JsonSerializer.Serialize(new
        {
            statements = new[]
            {
                new
                {
                    root = new { physicalOp = "Scan" },
                    missingIndexes = hints,
                },
            },
        });

    private static object OrdersHint(
        string[]? equality = null,
        string[]? include = null,
        string? schema = "dbo",
        string? database = null,
        string table = "Orders",
        double impact = 87.5) => new
        {
            database,
            schema,
            table,
            equalityColumns = equality ?? ["CustomerId"],
            inequalityColumns = Array.Empty<string>(),
            includeColumns = include ?? ["Total"],
            impact,
        };

    private static string Inventory(string database, IReadOnlyList<InventoryTable> tables, params string[] gaps) =>
        JsonSerializer.Serialize(new IndexInventory(database, DateTimeOffset.UnixEpoch, tables, gaps), ArtifactDirectoryPublisher.JsonOptions);

    private static InventoryTable Table(string schema, string table, bool found, bool truncated, params InventoryIndex[] indexes) =>
        new(schema, table, found, indexes, truncated);

    private static InventoryIndex Ix(string name, string[] keys, string[]? include = null) =>
        new(2, name, "NONCLUSTERED", keys, include ?? [], false, false, false, false, false);

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
    public void Statements_section_persists_source_scoped_plan_diagnostics_for_offline_reads()
    {
        using var temp = new TempDirectory();
        const string sqlServerPlan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT first"><QueryPlan DegreeOfParallelism="2" CompileTime="7" CompileCPU="3">
                  <MemoryGrantInfo RequestedMemory="8" GrantedMemory="6" MaxUsedMemory="4" />
                  <RelOp NodeId="0" PhysicalOp="Index Seek" />
                </QueryPlan></StmtSimple>
                <StmtSimple StatementText="SELECT second"><QueryPlan DegreeOfParallelism="1" CompileTime="0" CompileCPU="0">
                  <MemoryGrantInfo RequestedMemory="0" GrantedMemory="0" MaxUsedMemory="0" />
                  <RelOp NodeId="0" PhysicalOp="Index Seek" />
                </QueryPlan></StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        const string absentPlan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT absent"><QueryPlan><RelOp NodeId="0" PhysicalOp="Index Seek" /></QueryPlan></StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        const string secondDocumentPlan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT third"><QueryPlan CompileTime="14" CompileCPU="5">
                  <MemoryGrantInfo RequestedMemory="16" GrantedMemory="14" MaxUsedMemory="12" />
                  <RelOp NodeId="0" PhysicalOp="Index Seek" />
                </QueryPlan></StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        const string batchPlan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><QueryPlan CompileTime="11" CompileCPU="8">
                <MemoryGrantInfo RequestedMemory="12" GrantedMemory="10" MaxUsedMemory="9" />
                <RelOp NodeId="0" PhysicalOp="Index Seek" />
              </QueryPlan></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        const string postgresPlan = """[{"Plan":{"Node Type":"Seq Scan","Relation Name":"items"}}]""";
        var hiddenPlan = StatementMetricsTests.JournalPlan("SELECT dbo.fn_scalar() FROM dbo.fn_multi()", 1, 1, "0x01", 0, 1,
            udfElapsedTime: 4, elapsedTime: 10,
            operators: [("Compute Scalar", "", [(0L, 0L, 0L)]), ("Table-valued function", "[dbo].[fn_multi]", [(0L, 0L, 0L)])]);
        var report = CompareReport();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 30, 40, 0, new Dictionary<string, long>(), "hash-a", [sqlServerPlan, secondDocumentPlan], 0, "set-a"),
            new CompareRunArtifact("baseline", 2, 31, 41, 0, new Dictionary<string, long>(), "hash-b", [absentPlan], 0, "set-a"),
            new CompareRunArtifact("baseline", 3, 33, 43, 0, new Dictionary<string, long>(), "hash-d", [hiddenPlan], 0, "set-a")
            {
                TableIo = [new TableIoCounters("Orders", 1, 3, 0, 0, 0, 0, 0, 0)],
            },
            new CompareRunArtifact("candidate", 1, 32, 42, 0, new Dictionary<string, long>(), "hash-c", [postgresPlan], 0, "set-b"),
            new CompareRunArtifact("baseline", 4, 34, 44, 0, new Dictionary<string, long>(), "hash-e", [batchPlan], 0, "set-a"),
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");

        var section = Assert.IsType<ArtifactStatementsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "statements"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(section, ArtifactDirectoryPublisher.JsonOptions));
        var rows = json.RootElement.GetProperty("statements").EnumerateArray().ToArray();

        Assert.Equal(5, rows.Length);
        var first = Assert.Single(rows, row => row.GetProperty("parameterSet").GetString() == "set-a"
            && row.GetProperty("repetition").GetInt32() == 1 && row.GetProperty("statementOrdinal").GetInt32() == 0);
        var second = Assert.Single(rows, row => row.GetProperty("parameterSet").GetString() == "set-a"
            && row.GetProperty("repetition").GetInt32() == 1 && row.GetProperty("statementOrdinal").GetInt32() == 1);
        var absent = Assert.Single(rows, row => row.GetProperty("parameterSet").GetString() == "set-a"
            && row.GetProperty("repetition").GetInt32() == 2);
        Assert.Equal(8, first.GetProperty("planDiagnostics")[0].GetProperty("grantRequestedKb").GetInt64());
        Assert.Equal(6, first.GetProperty("planDiagnostics")[0].GetProperty("grantGrantedKb").GetInt64());
        Assert.Equal(4, first.GetProperty("planDiagnostics")[0].GetProperty("grantMaxUsedKb").GetInt64());
        Assert.Equal(2, first.GetProperty("planDiagnostics")[0].GetProperty("degreeOfParallelism").GetInt32());
        Assert.Equal(7, first.GetProperty("planDiagnostics")[0].GetProperty("compileTimeMs").GetInt64());
        Assert.Equal(3, first.GetProperty("planDiagnostics")[0].GetProperty("compileCpuMs").GetInt64());
        Assert.Equal(1, second.GetProperty("planDiagnostics")[0].GetProperty("degreeOfParallelism").GetInt32());
        Assert.Equal(0, second.GetProperty("planDiagnostics")[0].GetProperty("grantGrantedKb").GetInt64());
        Assert.Equal(0, second.GetProperty("planDiagnostics")[0].GetProperty("grantMaxUsedKb").GetInt64());
        Assert.Equal(0, second.GetProperty("planDiagnostics")[0].GetProperty("compileTimeMs").GetInt64());
        Assert.Equal(0, second.GetProperty("planDiagnostics")[0].GetProperty("compileCpuMs").GetInt64());
        var third = Assert.Single(rows, row => row.GetProperty("repetition").GetInt32() == 1
            && row.GetProperty("statementOrdinal").GetInt32() == 2);
        Assert.Equal(14, third.GetProperty("planDiagnostics")[0].GetProperty("grantGrantedKb").GetInt64());
        Assert.Equal(1, third.GetProperty("planDiagnostics")[0].GetProperty("planDocumentOrdinal").GetInt32());
        Assert.True(absent.GetProperty("planDiagnostics")[0].GetProperty("grantGrantedKb").ValueKind == JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, absent.GetProperty("planDiagnostics")[0].GetProperty("grantMaxUsedKb").ValueKind);
        Assert.Equal(JsonValueKind.Null, absent.GetProperty("planDiagnostics")[0].GetProperty("compileCpuMs").ValueKind);
        Assert.Equal("statement", first.GetProperty("planDiagnostics")[0].GetProperty("scope").GetString());
        Assert.Equal("sql-server-showplan-query-plan", first.GetProperty("planDiagnostics")[0].GetProperty("source").GetString());
        var partial = Assert.Single(rows, row => row.GetProperty("repetition").GetInt32() == 3);
        Assert.Equal("partial", partial.GetProperty("counterAttribution").GetString());
        Assert.Contains(partial.GetProperty("hiddenWork").EnumerateArray(), item => item.GetProperty("kind").GetString() == "scalar-udf");
        Assert.Contains(partial.GetProperty("hiddenWork").EnumerateArray(), item => item.GetProperty("kind").GetString() == "multi-statement-tvf");
        Assert.Equal(JsonValueKind.Null, partial.GetProperty("planDiagnostics")[0].GetProperty("compileTimeMs").ValueKind);
        var batch = Assert.Single(section.RunPlanDiagnostics);
        Assert.Equal("baseline", batch.Variant);
        Assert.Equal("set-a", batch.ParameterSet);
        Assert.Equal(4, batch.Repetition);
        Assert.Equal("batch", batch.Diagnostic.Scope);
        Assert.Equal(11, batch.Diagnostic.CompileTimeMs);
        var postgresCase = Assert.Single(section.PlanDiagnosticCases, item => item.Variant == "candidate");
        Assert.Equal("unavailable", postgresCase.Availability);
        Assert.Null(postgresCase.Source);
    }

    [Fact]
    public void Saved_numeric_diagnostics_keep_statement_io_and_run_distributions_source_scoped()
    {
        using var temp = new TempDirectory();
        static string Plan(long requested, long reads, long lobReads, long scans) => $$"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT * FROM dbo.Orders"><QueryPlan CompileTime="7" CompileCPU="3">
                  <MemoryGrantInfo RequestedMemory="{{requested}}" GrantedMemory="6" MaxUsedMemory="4" />
                  <RelOp NodeId="0" PhysicalOp="Index Seek">
                    <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="1" ActualExecutions="1" ActualLogicalReads="{{reads}}" ActualLobLogicalReads="{{lobReads}}" ActualScans="{{scans}}" /></RunTimeInformation>
                    <IndexScan><Object Table="[dbo].[Orders]" /></IndexScan>
                  </RelOp>
                </QueryPlan></StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 10, 12, 20, new Dictionary<string, long>(), "hash-1", [Plan(8, 5, 2, 3)], 0, "set-a")
            {
                TableIo = [new TableIoCounters("Orders", 2, 0, 0, 0, 0, 0, 0, 0)],
                TableIoComplete = true,
            },
            new CompareRunArtifact("baseline", 2, 11, 13, 21, new Dictionary<string, long>(), "hash-2", [Plan(12, 7, 4, 5)], 0, "set-a")
            {
                TableIo = [new TableIoCounters("Orders", 6, 0, 0, 0, 0, 0, 0, 0)],
                TableIoComplete = true,
            },
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), runs, "wind");
        var artifactId = Path.GetFileName(directory);
        var statements = ArtifactReader.ReadSection(temp.Path, artifactId, "statements");
        var metrics = ArtifactReader.ReadSection(temp.Path, artifactId, "metrics");

        using var statementJson = JsonDocument.Parse(JsonSerializer.Serialize(statements, ArtifactDirectoryPublisher.JsonOptions));
        var row = Assert.Single(statementJson.RootElement.GetProperty("statements").EnumerateArray(),
            item => item.GetProperty("repetition").GetInt32() == 1);
        Assert.Equal("available", row.GetProperty("objectIoAvailability").GetString());
        var objectIo = Assert.Single(row.GetProperty("objects").EnumerateArray());
        Assert.Equal(5, objectIo.GetProperty("logicalReads").GetInt64());
        Assert.Equal(2, objectIo.GetProperty("lobLogicalReads").GetInt64());
        Assert.Equal(3, objectIo.GetProperty("showplanActualScans").GetInt64());
        var projectedStatements = Assert.IsType<ArtifactStatementsSection>(
            AgentOutputProjection.Project(Assert.IsType<ArtifactStatementsSection>(statements), 512, 128, out _, 65536));
        Assert.Equal(5, Assert.Single(projectedStatements.Statements, item => item.Repetition == 1).Objects![0].LogicalReads);

        using var metricsJson = JsonDocument.Parse(JsonSerializer.Serialize(metrics, ArtifactDirectoryPublisher.JsonOptions));
        var variant = Assert.Single(metricsJson.RootElement.GetProperty("variants").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "baseline");
        var scan = Assert.Single(metricsJson.RootElement.GetProperty("statisticsIoScanCountDistributions").EnumerateArray());
        Assert.Equal("Orders", scan.GetProperty("table").GetString());
        Assert.Equal("available", scan.GetProperty("availability").GetString());
        Assert.Equal(2, scan.GetProperty("sampleCount").GetInt32());
        Assert.Equal(4, scan.GetProperty("scanCount").GetProperty("median").GetInt64());
        var diagnostic = Assert.Single(metricsJson.RootElement.GetProperty("planDiagnosticDistributions").EnumerateArray());
        Assert.Equal("statement", diagnostic.GetProperty("scope").GetString());
        Assert.Equal("set-a", diagnostic.GetProperty("parameterSet").GetString());
        Assert.Equal(2, diagnostic.GetProperty("sampleCount").GetInt32());
        Assert.Equal(10, diagnostic.GetProperty("grantRequestedKb").GetProperty("distribution").GetProperty("median").GetInt64());
        Assert.Equal(3, diagnostic.GetProperty("compileCpuMs").GetProperty("distribution").GetProperty("median").GetInt64());
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("oversized")]
    [InlineData("unreadable")]
    public void Metrics_refuse_corrupt_oversized_or_unreadable_advertised_statement_evidence(string failure)
    {
        using var temp = new TempDirectory();
        var report = CompareReport();
        var run = new CompareRunArtifact("baseline", 1, 10, 12, 20,
            new Dictionary<string, long>(), "hash", [FixturePlan()], 0);
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, [run], "bad-statements");
        var statementsPath = Path.Combine(directory, "statements.json");
        switch (failure)
        {
            case "corrupt":
                File.WriteAllText(statementsPath, "GARBAGE-" + Secret);
                break;
            case "oversized":
                using (var stream = new FileStream(statementsPath, FileMode.Create, FileAccess.Write))
                    stream.SetLength(ArtifactReader.MaxReportBytes + 1);
                break;
            case "unreadable":
                File.Delete(statementsPath);
                Directory.CreateDirectory(statementsPath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }

        var exception = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "metrics"));

        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Metrics_keep_valid_legacy_statement_artifacts_without_diagnostic_fields_unavailable()
    {
        using var temp = new TempDirectory();
        var report = CompareReport();
        var run = new CompareRunArtifact("baseline", 1, 10, 12, 20,
            new Dictionary<string, long>(), "hash", [FixturePlan()], 0);
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, [run], "legacy-statements");
        File.WriteAllText(Path.Combine(directory, "statements.json"), """
            {"artifactId":"legacy","artifactKind":"compare","statements":[
              {"variant":"baseline","matrixCell":null,"statementOrdinal":0,"statementHash":"legacy-hash",
               "cpuTimeMilliseconds":null,"elapsedTimeMilliseconds":null,"degreeOfParallelism":null,"topOperators":[]}
            ],"omittedStatements":0}
            """);

        var metrics = Assert.IsType<ArtifactMetricsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "metrics"));

        Assert.Equal("unavailable", metrics.PlanDiagnosticDistributionsAvailability);
        Assert.Empty(metrics.PlanDiagnosticDistributions);
        Assert.Equal("unavailable", metrics.StatisticsIoScanCountAvailability);
        Assert.Empty(metrics.StatisticsIoScanCountDistributions);
    }

    [Fact]
    public void Plan_diagnostic_distributions_keep_each_query_plan_source_across_repetitions()
    {
        using var temp = new TempDirectory();
        static string Plans(long first, long second) => $$"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch>
                <Statements><StmtSimple StatementText="SELECT 1">
                  <QueryPlan CompileTime="{{first}}"><MemoryGrantInfo RequestedMemory="{{first}}" /><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan>
                  <QueryPlan CompileTime="{{second}}"><MemoryGrantInfo RequestedMemory="{{second}}" /><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan>
                </StmtSimple></Statements>
                <QueryPlan CompileTime="{{first}}"><MemoryGrantInfo RequestedMemory="{{first}}" /><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan>
                <QueryPlan CompileTime="{{second}}"><MemoryGrantInfo RequestedMemory="{{second}}" /><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan>
              </Batch></BatchSequence>
            </ShowPlanXML>
            """;
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 10, 12, 20, new Dictionary<string, long>(), "hash-1", [Plans(10, 20)], 0),
            new CompareRunArtifact("baseline", 2, 11, 13, 21, new Dictionary<string, long>(), "hash-2", [Plans(12, 22)], 0),
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), runs, "distinct-query-plans");

        var statements = Assert.IsType<ArtifactStatementsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "statements"));
        using var statementsJson = JsonDocument.Parse(JsonSerializer.Serialize(statements, ArtifactDirectoryPublisher.JsonOptions));
        var statementDiagnostics = statementsJson.RootElement.GetProperty("statements").EnumerateArray()
            .SelectMany(row => row.GetProperty("planDiagnostics").EnumerateArray())
            .ToArray();
        Assert.Equal(new[] { 0, 1 }, statementDiagnostics.Select(item => item.GetProperty("planQueryOrdinal").GetInt32()).Distinct().Order());
        Assert.Equal(new long[] { 10, 20, 12, 22 }, statementDiagnostics.Select(item => item.GetProperty("compileTimeMs").GetInt64()));
        Assert.Equal(new[] { 2, 3 }, statementsJson.RootElement.GetProperty("runPlanDiagnostics").EnumerateArray()
            .Select(item => item.GetProperty("diagnostic").GetProperty("planQueryOrdinal").GetInt32()).Distinct().Order());

        var metrics = Assert.IsType<ArtifactMetricsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "metrics"));
        using var metricsJson = JsonDocument.Parse(JsonSerializer.Serialize(metrics, ArtifactDirectoryPublisher.JsonOptions));
        var distributions = metricsJson.RootElement.GetProperty("planDiagnosticDistributions").EnumerateArray()
            .Where(item => item.GetProperty("variant").GetString() == "baseline")
            .OrderBy(item => item.GetProperty("scope").GetString(), StringComparer.Ordinal)
            .ThenBy(item => item.GetProperty("planQueryOrdinal").GetInt32())
            .ToArray();

        Assert.Equal(4, distributions.Length);
        Assert.Equal(new[] { 2, 3, 0, 1 }, distributions.Select(item => item.GetProperty("planQueryOrdinal").GetInt32()));
        Assert.All(distributions, item => Assert.Equal(2, item.GetProperty("sampleCount").GetInt32()));
        Assert.Equal(new long[] { 10, 20, 10, 20 }, distributions.Select(item => item.GetProperty("compileTimeMs").GetProperty("distribution").GetProperty("min").GetInt64()));
        Assert.Equal(new long[] { 12, 22, 12, 22 }, distributions.Select(item => item.GetProperty("compileTimeMs").GetProperty("distribution").GetProperty("max").GetInt64()));
    }

    [Fact]
    public void Metrics_do_not_aggregate_ambiguous_legacy_query_plan_sources()
    {
        using var temp = new TempDirectory();
        static string Plans(long first, long second) => $$"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch>
                <Statements><StmtSimple StatementText="SELECT 1">
                  <QueryPlan CompileTime="{{first}}"><MemoryGrantInfo RequestedMemory="{{first}}" /><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan>
                  <QueryPlan CompileTime="{{second}}"><MemoryGrantInfo RequestedMemory="{{second}}" /><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan>
                </StmtSimple></Statements>
                <QueryPlan CompileTime="{{first}}"><MemoryGrantInfo RequestedMemory="{{first}}" /><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan>
                <QueryPlan CompileTime="{{second}}"><MemoryGrantInfo RequestedMemory="{{second}}" /><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan>
              </Batch></BatchSequence>
            </ShowPlanXML>
            """;
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 10, 12, 20, new Dictionary<string, long>(), "hash-1", [Plans(10, 20)], 0),
            new CompareRunArtifact("baseline", 2, 11, 13, 21, new Dictionary<string, long>(), "hash-2", [Plans(12, 22)], 0),
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), runs, "legacy-ambiguous-query-plans");
        var statementsPath = Path.Combine(directory, "statements.json");
        var statementsJson = JsonNode.Parse(File.ReadAllText(statementsPath))!.AsObject();
        foreach (var statement in statementsJson["statements"]!.AsArray())
            foreach (var diagnostic in statement!["planDiagnostics"]!.AsArray())
                diagnostic!.AsObject().Remove("planQueryOrdinal");
        foreach (var diagnostic in statementsJson["runPlanDiagnostics"]!.AsArray())
            diagnostic!["diagnostic"]!.AsObject().Remove("planQueryOrdinal");
        File.WriteAllText(statementsPath, statementsJson.ToJsonString(ArtifactDirectoryPublisher.JsonOptions));

        var metrics = Assert.IsType<ArtifactMetricsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "metrics"));

        Assert.Equal("partial", metrics.PlanDiagnosticDistributionsAvailability);
        Assert.Empty(metrics.PlanDiagnosticDistributions);
        Assert.True(metrics.OmittedPlanDiagnosticDistributions > 0);
    }

    [Fact]
    public void Metrics_preserve_unique_legacy_query_plan_sources_without_an_ordinal()
    {
        using var temp = new TempDirectory();
        static string Plan(long value) => $$"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT 1">
                <QueryPlan CompileTime="{{value}}"><MemoryGrantInfo RequestedMemory="{{value}}" /><RelOp NodeId="0" PhysicalOp="Constant Scan" /></QueryPlan>
              </StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 10, 12, 20, new Dictionary<string, long>(), "hash-1", [Plan(10)], 0),
            new CompareRunArtifact("baseline", 2, 11, 13, 21, new Dictionary<string, long>(), "hash-2", [Plan(12)], 0),
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), runs, "legacy-unique-query-plan");
        var statementsPath = Path.Combine(directory, "statements.json");
        var statementsJson = JsonNode.Parse(File.ReadAllText(statementsPath))!.AsObject();
        foreach (var statement in statementsJson["statements"]!.AsArray())
            foreach (var diagnostic in statement!["planDiagnostics"]!.AsArray())
                diagnostic!.AsObject().Remove("planQueryOrdinal");
        File.WriteAllText(statementsPath, statementsJson.ToJsonString(ArtifactDirectoryPublisher.JsonOptions));

        var metrics = Assert.IsType<ArtifactMetricsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "metrics"));

        Assert.Equal("available", metrics.PlanDiagnosticDistributionsAvailability);
        var distribution = Assert.Single(metrics.PlanDiagnosticDistributions);
        Assert.Equal(2, distribution.SampleCount);
        Assert.Null(distribution.PlanQueryOrdinal);
        Assert.Equal(10, distribution.CompileTimeMs.Distribution!.Min);
        Assert.Equal(12, distribution.CompileTimeMs.Distribution!.Max);
    }

    [Fact]
    public void Statement_object_io_distinguishes_zero_partial_and_absent_counters_and_reports_omissions()
    {
        using var temp = new TempDirectory();
        static string Plan(string objects) => """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT values"><QueryPlan>
            """ + objects + """
              </QueryPlan></StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        static string ObjectOperator(string table, string counters) => $"""
            <RelOp NodeId="0" PhysicalOp="Table Scan"><RunTimeInformation><RunTimeCountersPerThread Thread="0" {counters} /></RunTimeInformation><TableScan><Object Table="{table}" /></TableScan></RelOp>
            """;
        var zeroPlan = Plan(ObjectOperator("[dbo].[Zero]", "ActualRows=\"1\" ActualExecutions=\"1\" ActualLogicalReads=\"0\" ActualLobLogicalReads=\"0\" ActualScans=\"0\""));
        var partialPlan = Plan(ObjectOperator("[dbo].[Partial]", "ActualRows=\"1\" ActualExecutions=\"1\" ActualLogicalReads=\"9\""));
        var absentPlan = Plan(ObjectOperator("[dbo].[Absent]", "ActualRows=\"1\" ActualExecutions=\"1\""));
        var manyObjectsPlan = Plan(string.Concat(Enumerable.Range(0, 65).Select(index => ObjectOperator(
            $"[dbo].[Object{index:D2}]",
            "ActualRows=\"1\" ActualExecutions=\"1\" ActualLogicalReads=\"1\" ActualLobLogicalReads=\"0\" ActualScans=\"0\""))));
        var runs = new[] { zeroPlan, partialPlan, absentPlan, manyObjectsPlan }
            .Select((plan, index) => new CompareRunArtifact("baseline", index + 1, 1, 2, 0,
                new Dictionary<string, long>(), $"hash-{index}", [plan], 0)).ToArray();
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), runs, "io-completeness");
        var section = Assert.IsType<ArtifactStatementsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "statements"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(section, ArtifactDirectoryPublisher.JsonOptions));
        var rows = json.RootElement.GetProperty("statements").EnumerateArray().ToArray();

        var zero = Assert.Single(rows, row => row.GetProperty("repetition").GetInt32() == 1);
        Assert.Equal("available", zero.GetProperty("objectIoAvailability").GetString());
        var zeroObject = Assert.Single(zero.GetProperty("objects").EnumerateArray());
        Assert.Equal(0, zeroObject.GetProperty("logicalReads").GetInt64());
        Assert.Equal(0, zeroObject.GetProperty("lobLogicalReads").GetInt64());
        Assert.Equal(0, zeroObject.GetProperty("showplanActualScans").GetInt64());

        var partial = Assert.Single(rows, row => row.GetProperty("repetition").GetInt32() == 2);
        Assert.Equal("partial", partial.GetProperty("objectIoAvailability").GetString());
        var partialObject = Assert.Single(partial.GetProperty("objects").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, partialObject.GetProperty("logicalReads").ValueKind);
        Assert.Equal(JsonValueKind.Null, partialObject.GetProperty("lobLogicalReads").ValueKind);
        Assert.Equal(JsonValueKind.Null, partialObject.GetProperty("showplanActualScans").ValueKind);

        var absent = Assert.Single(rows, row => row.GetProperty("repetition").GetInt32() == 3);
        Assert.Equal("partial", absent.GetProperty("objectIoAvailability").GetString());
        var absentObject = Assert.Single(absent.GetProperty("objects").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, absentObject.GetProperty("logicalReads").ValueKind);
        Assert.Equal(JsonValueKind.Null, absentObject.GetProperty("lobLogicalReads").ValueKind);
        Assert.Equal(JsonValueKind.Null, absentObject.GetProperty("showplanActualScans").ValueKind);

        var manyObjects = Assert.Single(rows, row => row.GetProperty("repetition").GetInt32() == 4);
        Assert.Equal(64, manyObjects.GetProperty("objects").GetArrayLength());
        Assert.Equal(1, manyObjects.GetProperty("omittedObjects").GetInt32());
    }

    [Fact]
    public void Noncanonical_showplan_namespaces_do_not_produce_sql_server_diagnostics()
    {
        const string wrongRootNamespace = """
            <ShowPlanXML xmlns="urn:unrelated" xmlns:sql="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <sql:BatchSequence><sql:Batch><sql:Statements>
                <sql:StmtSimple StatementText="SELECT 1"><sql:QueryPlan CompileTime="9" CompileCPU="4">
                  <sql:MemoryGrantInfo RequestedMemory="8" GrantedMemory="6" MaxUsedMemory="5" />
                </sql:QueryPlan></sql:StmtSimple>
              </sql:Statements></sql:Batch></sql:BatchSequence>
            </ShowPlanXML>
            """;
        const string wrongDiagnosticNamespace = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" xmlns:other="urn:unrelated">
              <BatchSequence><Batch><Statements>
                <other:StmtSimple StatementText="SELECT 2"><other:QueryPlan CompileTime="9" CompileCPU="4">
                  <other:MemoryGrantInfo RequestedMemory="8" GrantedMemory="6" MaxUsedMemory="5" />
                </other:QueryPlan></other:StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        foreach (var plan in new[] { wrongRootNamespace, wrongDiagnosticNamespace })
        {
            var run = new CompareRunArtifact("measure", 1, 1, 1, 1,
                new Dictionary<string, long>(), "hash", [plan], 0);
            var section = ArtifactStatementProjector.Project(ArtifactReader.MeasureKind, [run]);

            Assert.Equal(PlanMetrics.Empty, PlanMetricsExtractor.Extract(plan));
            Assert.Equal("unavailable", section.PlanDiagnosticsAvailability);
            Assert.Equal("unavailable", Assert.Single(section.PlanDiagnosticCases).Availability);
            Assert.Null(Assert.Single(section.PlanDiagnosticCases).Source);
            Assert.Empty(section.RunPlanDiagnostics);
            Assert.All(section.Statements, statement => Assert.Empty(statement.PlanDiagnostics!));
        }
    }

    [Fact]
    public void Statements_section_preserves_repetition_identity_attribution_and_hidden_work()
    {
        using var temp = new TempDirectory();
        var longFunctionName = string.Concat(Enumerable.Repeat("𝒇", 140));
        var scalar = StatementMetricsTests.JournalPlan("SELECT dbo.fn_scalar()", 1, 1, "0x01", 0, 1,
            udfElapsedTime: 28, elapsedTime: 29,
            operators: [("Compute Scalar", "", [(0L, 0L, 0L)])])
            .Replace("UdfElapsedTime=\"28\"", $"UdfElapsedTime=\"28\" FunctionName=\"dbo.{longFunctionName}\"", StringComparison.Ordinal);
        var report = CompareReport();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 30, 40, 0, new Dictionary<string, long>(), "hash", [scalar], 0)
            {
                TableIo = [new TableIoCounters("", 0, 0, 0, 0, 0, 0, 0, 0)],
            },
            new CompareRunArtifact("baseline", 2, 31, 41, 0, new Dictionary<string, long>(), "hash", [scalar], 0)
            {
                TableIo = [new TableIoCounters("", 0, 0, 0, 0, 0, 0, 0, 0)],
            },
        };

        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");
        var section = Assert.IsType<ArtifactStatementsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "statements"));

        using var sectionJson = JsonDocument.Parse(JsonSerializer.Serialize(section, ArtifactDirectoryPublisher.JsonOptions));
        Assert.Equal(1, sectionJson.RootElement.GetProperty("evidenceVersion").GetInt32());
        Assert.Equal("collected-statement-counters", sectionJson.RootElement.GetProperty("coverageBasis").GetString());
        Assert.Contains("does not establish complete query cost", sectionJson.RootElement.GetProperty("coverageDescription").GetString(), StringComparison.Ordinal);
        Assert.Equal([1, 2], section.Statements.Select(item =>
        {
            using var rowJson = JsonDocument.Parse(JsonSerializer.Serialize(item, ArtifactDirectoryPublisher.JsonOptions));
            return rowJson.RootElement.GetProperty("repetition").GetInt32();
        }).ToArray());
        Assert.All(section.Statements, item =>
        {
            using var rowJson = JsonDocument.Parse(JsonSerializer.Serialize(item, ArtifactDirectoryPublisher.JsonOptions));
            Assert.Equal("complete", rowJson.RootElement.GetProperty("counterAttribution").GetString());
            Assert.True(rowJson.RootElement.GetProperty("statementIdentityComplete").GetBoolean());
            Assert.Equal("scalar-udf", rowJson.RootElement.GetProperty("hiddenWork")[0].GetProperty("kind").GetString());
            Assert.True(rowJson.RootElement.GetProperty("hiddenWork")[0].GetProperty("nameTruncated").GetBoolean());
            Assert.InRange(Encoding.UTF8.GetByteCount(rowJson.RootElement.GetProperty("hiddenWork")[0].GetProperty("name").GetString()!), 1, 512);
            Assert.NotEmpty(rowJson.RootElement.GetProperty("limitations").EnumerateArray());
        });
    }

    [Fact]
    public void Statements_section_reports_hidden_work_rows_omitted_by_the_per_statement_cap()
    {
        using var temp = new TempDirectory();
        var functions = Enumerable.Range(0, 18)
            .Select(index => ("Table-valued function", $"[dbo].[fn_{index:D2}]", new[] { (0L, 0L, 0L) }))
            .ToArray();
        var plan = StatementMetricsTests.JournalPlan("SELECT * FROM dbo.fn_many()", 1, 1, "0x01", 0, 1,
            operators: functions);
        var run = new CompareRunArtifact("baseline", 1, 1, 2, 0, new Dictionary<string, long>(), "hash", [plan], 0);
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), [run], "many-hidden");

        var section = Assert.IsType<ArtifactStatementsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "statements"));
        using var rowJson = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.Single(section.Statements), ArtifactDirectoryPublisher.JsonOptions));

        Assert.Equal(16, rowJson.RootElement.GetProperty("hiddenWork").GetArrayLength());
        Assert.Equal(2, rowJson.RootElement.GetProperty("hiddenWorkOmitted").GetInt32());
        Assert.Contains("Multi-statement TVF", rowJson.RootElement.GetProperty("limitations")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_statement_rows_and_postgres_artifacts_report_unavailable_attribution()
    {
        using var temp = new TempDirectory();
        const string legacyId = "legacy-statements";
        var legacyDirectory = Path.Combine(temp.Path, legacyId);
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"statements\"]}");
        File.WriteAllText(Path.Combine(legacyDirectory, "statements.json"),
            "{\"statements\":[{\"variant\":\"baseline\",\"statementOrdinal\":0,\"statementHash\":\"" + new string('a', 64) + "\",\"topOperators\":[]}],\"omittedStatements\":0}");

        var legacy = Assert.IsType<ArtifactStatementsSection>(
            ArtifactReader.ReadSection(temp.Path, legacyId, "statements"));
        using var legacyJson = JsonDocument.Parse(JsonSerializer.Serialize(legacy, ArtifactDirectoryPublisher.JsonOptions));
        Assert.Equal("unavailable", legacyJson.RootElement.GetProperty("statements")[0]
            .GetProperty("counterAttribution").GetString());
        Assert.Equal("unavailable", legacyJson.RootElement.GetProperty("statements")[0]
            .GetProperty("objectIoAvailability").GetString());

        var postgres = new CompareRunArtifact("baseline", 1, 0, 1, 0,
            new Dictionary<string, long>(), "hash", ["{\"Plan\":{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"orders\"}}"], 0);
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), [postgres], "postgres");
        var pgSection = Assert.IsType<ArtifactStatementsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "statements"));
        using var pgJson = JsonDocument.Parse(JsonSerializer.Serialize(pgSection, ArtifactDirectoryPublisher.JsonOptions));
        Assert.Equal("unavailable", pgJson.RootElement.GetProperty("evidenceAvailability").GetString());
        Assert.Empty(pgSection.Statements);
    }

    [Fact]
    public void Statements_section_marks_incomplete_identity_partial_and_keeps_node_collisions_separate()
    {
        using var temp = new TempDirectory();
        var plan = StatementMetricsTests.CollidingNodeIdPlan();
        var run = new CompareRunArtifact("candidate", 7, 1, 2, 3,
            new Dictionary<string, long>(), "hash", [plan], 0, "large")
        {
            MatrixCell = 3,
            TableIo = [new TableIoCounters("Orders", 1, 3, 0, 0, 0, 0, 0, 0)],
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), [run], "identity");
        var section = Assert.IsType<ArtifactStatementsSection>(
            ArtifactReader.ReadSection(temp.Path, Path.GetFileName(directory), "statements"));

        Assert.Equal(2, section.Statements.Count);
        Assert.All(section.Statements, item =>
        {
            Assert.Equal("candidate", item.Variant);
            Assert.Equal("large", item.ParameterSet);
            Assert.Equal(3, item.MatrixCell);
            using var rowJson = JsonDocument.Parse(JsonSerializer.Serialize(item, ArtifactDirectoryPublisher.JsonOptions));
            Assert.Equal(7, rowJson.RootElement.GetProperty("repetition").GetInt32());
            Assert.Equal("partial", rowJson.RootElement.GetProperty("counterAttribution").GetString());
            Assert.False(rowJson.RootElement.GetProperty("statementIdentityComplete").GetBoolean());
        });
        Assert.Equal(2, section.Statements.SelectMany(item => item.TopOperators)
            .Where(item => item.NodeId == 1).Count());
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
            "baseline", null, null, ordinal, new string('a', 64), 1, 2, 1, [])
        {
            Repetition = ordinal + 1,
            CounterAttribution = "partial",
            StatementIdentityComplete = false,
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, "statements.json"), JsonSerializer.Serialize(
            new ArtifactStatementsSection(id, "compare", statements, 0)
            {
                EvidenceVersion = 1,
                EvidenceAvailability = "available",
                CoverageBasis = "collected-statement-counters",
                CoverageDescription = "Attribution describes collected statement-counter coverage only; it does not establish complete query cost.",
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var first = Assert.IsType<ArtifactStatementsSection>(ArtifactReader.ReadSection(temp.Path, id, "statements"));
        Assert.Equal(128, first.Statements.Count);
        Assert.Equal(1, first.Statements[0].Repetition);
        Assert.Equal(128, first.Continuation);
        var projected = Assert.IsType<ArtifactStatementsSection>(AgentOutputProjection.Project(first, 512, 128, out _, 65536));
        Assert.Equal(128, projected.Statements.Count);
        Assert.Equal(128, projected.Continuation);

        var second = Assert.IsType<ArtifactStatementsSection>(ArtifactReader.ReadSection(temp.Path, id, "statements", cursor: 128));
        Assert.Equal(128, second.Statements[0].StatementOrdinal);
        Assert.Equal(129, second.Statements[0].Repetition);
        var projectedSecond = Assert.IsType<ArtifactStatementsSection>(AgentOutputProjection.Project(second, 512, 128, out _, 65536));
        Assert.Equal(256, projectedSecond.Continuation);

        var last = Assert.IsType<ArtifactStatementsSection>(ArtifactReader.ReadSection(temp.Path, id, "statements", cursor: 256));
        var projectedLast = Assert.IsType<ArtifactStatementsSection>(AgentOutputProjection.Project(last, 512, 128, out _, 65536));
        Assert.Equal(44, projectedLast.Statements.Count);
        Assert.Null(projectedLast.Continuation);
    }

    [Fact]
    public void Statement_summary_reads_compact_cpu_elapsed_records_in_source_order()
    {
        using var temp = new TempDirectory();
        const string id = "statement-summary-pages";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"statements\"]}");
        var source = new[]
        {
            new ArtifactStatementMetric("baseline", "set-界", 7, 2, "hash-\"one", 12, 34, 4, [new StatementOperatorSummary(1, "Index Seek", "dbo.T", null, 1, 1, 1)]),
            new ArtifactStatementMetric("candidate", null, null, 0, "hash-two", null, 56, null, []),
            new ArtifactStatementMetric("candidate", "set-b", 3, 1, "hash-three", 78, null, null, []),
        };
        var stored = JsonNode.Parse(JsonSerializer.Serialize(
            new ArtifactStatementsSection(id, "compare", source, 0), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        var legacyStatement = stored["statements"]![1]!.AsObject();
        legacyStatement.Remove("cpuTimeMilliseconds");
        legacyStatement.Remove("elapsedTimeMilliseconds");
        File.WriteAllText(Path.Combine(directory, "statements.json"), stored.ToJsonString());

        var first = Assert.IsType<ArtifactStatementSummarySection>(ArtifactReader.ReadSection(
            temp.Path, id, "statement-summary", cursor: 0, pageSize: 2));
        Assert.Equal(id, first.ArtifactId);
        Assert.Equal("compare", first.ArtifactKind);
        Assert.Equal(0, first.Cursor);
        Assert.Equal(2, first.Continuation);
        Assert.Equal(0, first.OmittedStatements);
        Assert.Equal(
            [("baseline", "set-界", 7, 2, "hash-\"one", (long?)12, (long?)34),
             ("candidate", null, null, 0, "hash-two", null, null)],
            first.Statements.Select(statement => (statement.Variant, statement.ParameterSet, statement.MatrixCell,
                statement.StatementOrdinal, statement.StatementHash, statement.CpuTimeMilliseconds,
                statement.ElapsedTimeMilliseconds)).ToArray());

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var rows = json.RootElement.GetProperty("statements").EnumerateArray().ToArray();
        Assert.Equal(["variant", "parameterSet", "matrixCell", "statementOrdinal", "statementHash", "cpuTimeMilliseconds", "elapsedTimeMilliseconds"],
            rows[0].EnumerateObject().Select(property => property.Name).ToArray());
        Assert.False(rows[0].TryGetProperty("topOperators", out _));
        Assert.False(rows[0].TryGetProperty("repetition", out _));
        Assert.False(rows[0].TryGetProperty("planDiagnostics", out _));
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("cpuTimeMilliseconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("elapsedTimeMilliseconds").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("sql", out _));

        var second = Assert.IsType<ArtifactStatementSummarySection>(ArtifactReader.ReadSection(
            temp.Path, id, "statement-summary", cursor: first.Continuation, pageSize: 2));
        var last = Assert.Single(second.Statements);
        Assert.Equal(("candidate", "set-b", 3, 1, "hash-three", (long?)78, (long?)null),
            (last.Variant, last.ParameterSet, last.MatrixCell, last.StatementOrdinal, last.StatementHash,
                last.CpuTimeMilliseconds, last.ElapsedTimeMilliseconds));
        Assert.Null(second.Continuation);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(65536)]
    public void Statement_projection_budgets_hidden_work_and_counts_response_omissions(int budget)
    {
        var hidden = Enumerable.Range(0, 16).Select(index => new ArtifactHiddenWork(
            "multi-statement-tvf", $"[dbo].[函\"\\\n{index}-{new string('界', 160)}]", false, index + 1, null)).ToArray();
        var statement = new ArtifactStatementMetric("candidate", "set-a", 7, 3, new string('a', 64),
            1, 2, 1, [])
        {
            Repetition = 2,
            CounterAttribution = "complete",
            StatementIdentityComplete = true,
            PlanDocumentOrdinal = 4,
            PlanStatementIndex = 1,
            PlanHash = new string('b', 64),
            HiddenWork = hidden,
            HiddenWorkOmitted = 2,
            Limitations = ["Multi-statement TVF work may not be fully represented by collected statement counters."],
        };
        var section = new ArtifactStatementsSection("artifact", "compare", [statement], 0)
        {
            EvidenceAvailability = "available",
            CoverageBasis = "collected-statement-counters",
        };

        var projected = Assert.IsType<ArtifactStatementsSection>(AgentOutputProjection.Project(
            section, 128, int.MaxValue, out _, budget, AgentProjectionProfile.Mcp));
        var returned = Assert.Single(projected.Statements);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.InRange(serialized.Length, 1, budget);
        Assert.Equal(("candidate", "set-a", 7, 3, 2, "complete", true),
            (returned.Variant, returned.ParameterSet, returned.MatrixCell, returned.StatementOrdinal, returned.Repetition, returned.CounterAttribution, returned.StatementIdentityComplete));
        Assert.Equal(2 + hidden.Length - returned.HiddenWork.Count, returned.HiddenWorkOmitted);
        Assert.All(returned.HiddenWork, item =>
        {
            var source = hidden[checked((int)item.Executions!.Value - 1)];
            Assert.Equal(item.NameTruncated, !string.Equals(item.Name, source.Name, StringComparison.Ordinal));
            Assert.StartsWith(item.Name, source.Name, StringComparison.Ordinal);
        });
        Assert.Contains(returned.Limitations, item => item.Contains("Multi-statement TVF", StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(1, returned.HiddenWork.Count).Select(index => (long)index), returned.HiddenWork.Select(item => item.Executions!.Value));

        var legacy = section with { Statements = [statement with { HiddenWorkOmitted = null }] };
        var legacyProjected = Assert.IsType<ArtifactStatementsSection>(AgentOutputProjection.Project(
            legacy, 128, int.MaxValue, out _, 4096, AgentProjectionProfile.Mcp));
        Assert.Null(Assert.Single(legacyProjected.Statements).HiddenWorkOmitted);
    }

    [Theory]
    [InlineData(null, 128)]
    [InlineData(1, 1)]
    [InlineData(512, 512)]
    public void Plan_source_windows_reconstruct_513_nodes_under_small_and_large_budgets(int? pageSize, int expectedSourceWindow)
    {
        using var temp = new TempDirectory();
        const string id = "plan-window-513";
        WritePlanArtifact(temp.Path, id, 513);

        foreach (var budget in new[] { 4096, 65536 })
        {
            var paths = new List<string>();
            int? cursor = 0;
            var pageCount = 0;
            while (cursor is { } current)
            {
                Assert.InRange(++pageCount, 1, 513);
                var source = Assert.IsType<ArtifactPlanSection>(ReadSectionWithPageSize(temp.Path, id, "plan",
                    cursor: current, planSelector: new ArtifactPlanSelector("baseline"), pageSize: pageSize));
                Assert.InRange(source.Nodes.Count, 1, expectedSourceWindow);
                if (current == 0)
                    Assert.Equal(Math.Min(expectedSourceWindow, 513), source.Nodes.Count);
                var projected = Assert.IsType<ArtifactPlanSection>(
                    AgentOutputProjection.Project(source, 128, int.MaxValue, out _, budget, AgentProjectionProfile.Mcp));
                Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length, 1, budget);
                Assert.NotEmpty(projected.Nodes);
                paths.AddRange(projected.Nodes.Select(node => node.NodePath));
                if (projected.Continuation is int next)
                    Assert.True(next > current, $"Continuation cursor must advance beyond {current}, but was {next}.");
                cursor = projected.Continuation;
            }

            Assert.Equal(513, paths.Count);
            Assert.Equal(513, paths.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(Enumerable.Range(0, 513).Select(index => index == 0 ? "0" : $"0/{index - 1}"), paths);
        }
    }

    [Theory]
    [InlineData(null, 32)]
    [InlineData(1, 1)]
    [InlineData(512, 65)]
    public void Matrix_source_windows_reconstruct_inventory_under_small_and_large_budgets(int? pageSize, int expectedSourceWindow)
    {
        using var temp = new TempDirectory();
        const string id = "matrix-window-65";
        WriteMatrixArtifact(temp.Path, id, 65);

        foreach (var budget in new[] { 4096, 65536 })
        {
            var indexes = new List<int>();
            int? cursor = 0;
            var pageCount = 0;
            while (cursor is { } current)
            {
                Assert.InRange(++pageCount, 1, 65);
                var source = Assert.IsType<ArtifactMatrixCellsSection>(
                    ReadSectionWithPageSize(temp.Path, id, "matrix-cells", cursor: current, pageSize: pageSize));
                Assert.InRange(source.Cases.Count, 1, expectedSourceWindow);
                if (current == 0)
                    Assert.Equal(Math.Min(expectedSourceWindow, 65), source.Cases.Count);
                var projected = Assert.IsType<ArtifactMatrixCellsSection>(
                    AgentOutputProjection.Project(source, 128, int.MaxValue, out _, budget, AgentProjectionProfile.Mcp));
                Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length, 1, budget);
                Assert.NotEmpty(projected.Cases);
                indexes.AddRange(projected.Cases.Select(item => item.Index));
                if (projected.Continuation is int next)
                    Assert.True(next > current, $"Continuation cursor must advance beyond {current}, but was {next}.");
                cursor = projected.Continuation;
            }

            Assert.Equal(65, indexes.Count);
            Assert.Equal(65, indexes.Distinct().Count());
            Assert.Equal(Enumerable.Range(0, 65), indexes);
        }
    }

    private static object ReadSectionWithPageSize(string root, string id, string section,
        int? cursor = null, ArtifactPlanSelector? planSelector = null, bool includeText = false, int? pageSize = null)
    {
        var method = typeof(ArtifactReader).GetMethod(nameof(ArtifactReader.ReadSection), BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.NotNull(parameters.SingleOrDefault(parameter => parameter.Name == "pageSize"));
        var arguments = parameters.Select(parameter => parameter.Name switch
        {
            "root" => (object)root,
            "artifactId" => id,
            "section" => section,
            "cursor" => cursor,
            "planSelector" => planSelector,
            "includeText" => includeText,
            "pageSize" => pageSize,
            _ => parameter.DefaultValue,
        }).ToArray();
        try
        {
            return method.Invoke(null, arguments)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static void WritePlanArtifact(string root, string id, int nodeCount)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(directory, "plans"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");
        var tree = JsonSerializer.Serialize(new
        {
            statements = new[] { new { root = new { physicalOp = "Root", children = Enumerable.Range(1, nodeCount - 1)
                .Select(index => new { physicalOp = $"Scan-{index:D3}", children = Array.Empty<object>() }).ToArray() } } },
        });
        const string planName = "baseline-001-000-000.plan.json";
        File.WriteAllText(Path.Combine(directory, "plans", planName), tree);
        File.WriteAllText(Path.Combine(directory, "runs.jsonl"), JsonSerializer.Serialize(new
        {
            variant = "baseline",
            repetition = 1,
            planJsonFiles = new[] { planName },
        }));
    }

    private static void WriteMatrixArtifact(string root, string id, int count)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"matrix-cells\"]}");
        var cases = Enumerable.Range(0, count).Select(index => new
        {
            index,
            artifactId = $"cell-{index:D3}",
            hasComparison = true,
            equivalenceAvailability = "compared",
            baseline = new { cpuMilliseconds = 1, elapsedMinimum = 1, elapsedMaximum = 2 },
            candidate = new { cpuMilliseconds = 2, elapsedMinimum = 2, elapsedMaximum = 3 },
            delta = new { cpuMilliseconds = 1, elapsedMinimum = 1, elapsedMaximum = 1 },
            warnings = new[] { new string('w', 128) },
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), JsonSerializer.Serialize(new { cases }));
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
            new AgentMedianMetrics(1, 2, 3), new AgentMedianMetrics(4, 5, 6), new AgentMedianMetrics(3, 3, 3), ["warning"])
        {
            SetName = $"set-{index:D2}",
        }).ToArray();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), JsonSerializer.Serialize(new
        {
            cases,
        }, jsonOptions));

        var first = ArtifactReader.ReadSection(temp.Path, id, "matrix-cells");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(32, json.RootElement.GetProperty("cases").GetArrayLength());
        Assert.Equal("set-00", json.RootElement.GetProperty("cases")[0].GetProperty("setName").GetString());
        Assert.Equal(4m, json.RootElement.GetProperty("cases")[0].GetProperty("candidate").GetProperty("cpuMilliseconds").GetDecimal());
        Assert.Equal(32, json.RootElement.GetProperty("continuation").GetInt32());
        var second = Assert.IsType<ArtifactMatrixCellsSection>(ArtifactReader.ReadSection(temp.Path, id, "matrix-cells", cursor: 32));
        Assert.Equal(8, second.Cases.Count);
        Assert.Null(second.Continuation);
        Assert.Equal(Enumerable.Range(32, 8), second.Cases.Select(cell => cell.Index));
        Assert.Equal(Enumerable.Range(32, 8).Select(index => $"set-{index:D2}"), second.Cases.Select(cell => cell.SetName));

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
        Assert.Null(legacy.Cases[0].SetName);
        Assert.Null(legacy.Coverage);
        Assert.Null(legacy.Cases[0].Regression);
        Assert.Null(legacy.Cases[0].Baseline.ElapsedMinimum);
        Assert.Null(legacy.Cases[0].MismatchReasons);
    }

    [Fact]
    public void Matrix_index_preserves_coverage_and_decision_fields_across_pages()
    {
        using var temp = new TempDirectory();
        const string id = "matrix-index-d4";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"matrix-cells\"]}");
        var coverage = MatrixExecutionCoverage.AllCompleted(40);
        var cases = Enumerable.Range(0, 40).Select(index => new AgentMatrixCase(
            index, $"cell-{index:D3}", true, "compared",
            new AgentMedianMetrics(1, 2, 3) { ElapsedMinimum = 1, ElapsedMaximum = 4 },
            new AgentMedianMetrics(4, 5, 6) { ElapsedMinimum = 4, ElapsedMaximum = 8 },
            new AgentMedianMetrics(3, 3, 3), ["warning"])
        {
            SetName = $"set-{index:D2}",
            Regression = new AgentMatrixRegression(index == 7 ? "fail" : index == 15 ? "inconclusive" : "pass",
                index == 7 ? ["elapsed-regressed"] : index == 15 ? ["unstable-elapsed"] : []),
            BaselineStable = true,
            CandidateStable = true,
            SchemaEquivalent = true,
        }).ToArray();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), JsonSerializer.Serialize(new
        {
            cases,
            coverage,
        }, jsonOptions));

        var recovered = new List<AgentMatrixCase>();
        int? cursor = 0;
        while (cursor is { } pageCursor)
        {
            var page = Assert.IsType<ArtifactMatrixCellsSection>(
                ArtifactReader.ReadSection(temp.Path, id, "matrix-cells", cursor: pageCursor));
            Assert.Equal(40, page.Coverage!.RequestedCases);
            Assert.Equal(40, page.Coverage.CompletedCases);
            Assert.Equal(0, page.Coverage.NotExecutedCases);
            Assert.Null(page.Coverage.FailedCase);
            recovered.AddRange(page.Cases);
            cursor = page.Continuation;
            if (page.Continuation is { } next)
                Assert.True(next > pageCursor);
        }

        Assert.Equal(40, recovered.Count);
        Assert.Equal(Enumerable.Range(0, 40), recovered.Select(item => item.Index));
        Assert.Equal(40, recovered.Select(item => item.Index).Distinct().Count());
        Assert.Equal("fail", recovered[7].Regression!.Verdict);
        Assert.Equal("inconclusive", recovered[15].Regression!.Verdict);
        Assert.Equal(1m, recovered[0].Baseline.ElapsedMinimum);
        Assert.Equal($"cell-000", recovered[0].ArtifactId);
    }

    [Fact]
    public void Operators_plan_refs_round_trip_through_the_offline_reader_to_the_identified_node()
    {
        using var temp = new TempDirectory();
        var xml = StatementMetricsTests.CollidingNodeIdPlan();
        var baseline = BenchmarkReports.CreateVariantReport(
            "baseline",
            [
                StatementMetricsTests.MeasuredRun("baseline", 1, xml),
                StatementMetricsTests.MeasuredRun("baseline", 5, xml),
            ]);
        var candidate = BenchmarkReports.CreateVariantReport(
            "candidate",
            [
                StatementMetricsTests.MeasuredRun("candidate", 1, xml),
                StatementMetricsTests.MeasuredRun("candidate", 5, xml),
            ]);
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5, 4, true, baseline, candidate, null);
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 1, 2, 3, new Dictionary<string, long>(), "h", [xml], 0),
            new CompareRunArtifact("baseline", 5, 1, 2, 3, new Dictionary<string, long>(), "h", [xml], 0),
            new CompareRunArtifact("candidate", 1, 4, 5, 6, new Dictionary<string, long>(), "h", [xml], 0),
            new CompareRunArtifact("candidate", 5, 4, 5, 6, new Dictionary<string, long>(), "h", [xml], 0),
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");
        var id = Path.GetFileName(directory);
        var operators = Assert.IsType<ArtifactOperatorsSection>(
            ArtifactReader.ReadSection(temp.Path, id, "operators"));
        var scan = Assert.Single(operators.Operators, op => op.PhysicalOp == "Clustered Index Scan");
        Assert.NotEmpty(scan.PlanRefs!);
        Assert.True(operators.SourceIsCapped);
        Assert.Equal("both", scan.Side);
        Assert.Null(scan.HasWarnings);
        Assert.Null(scan.HasSpill);
        Assert.Null(scan.HasImplicitConversion);
        using (var scanJson = JsonDocument.Parse(JsonSerializer.Serialize(scan, ArtifactDirectoryPublisher.JsonOptions)))
        {
            Assert.False(scanJson.RootElement.TryGetProperty("hasWarnings", out _));
            Assert.False(scanJson.RootElement.TryGetProperty("hasSpill", out _));
            Assert.False(scanJson.RootElement.TryGetProperty("hasImplicitConversion", out _));
            Assert.True(scanJson.RootElement.GetProperty("baseline").GetProperty("hasSpill").GetBoolean());
            Assert.True(scanJson.RootElement.GetProperty("candidate").GetProperty("hasSpill").GetBoolean());
        }

        foreach (var href in scan.PlanRefs!)
        {
            Assert.Equal(id, href.ArtifactId);
            var page = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(
                temp.Path,
                href.ArtifactId,
                "plan",
                cursor: href.Cursor,
                planSelector: new ArtifactPlanSelector(href.Variant, href.Statement, href.ParameterSet, href.Repetition)));
            var node = Assert.Single(page.Nodes, item => item.NodePath == href.NodePath);
            Assert.Equal("Clustered Index Scan", node.Operator.PhysicalOp);
            Assert.Equal(1, node.Operator.NodeId);
            Assert.Contains(node.Operator.Warnings, warning => warning.Contains("SpillToTempDb", StringComparison.Ordinal));
            Assert.Equal(href.Repetition, page.Repetition);
            Assert.Equal(href.Statement, page.Statement);
            Assert.Equal("explicit", page.Selection);
        }
    }

    [Fact]
    public void Operators_plan_ref_does_not_fall_back_to_another_repetition_when_evidence_is_missing()
    {
        using var temp = new TempDirectory();
        var xml = StatementMetricsTests.CollidingNodeIdPlan();
        var candidate = BenchmarkReports.CreateVariantReport(
            "candidate", [StatementMetricsTests.MeasuredRun("candidate", 5, xml)]);
        var report = CompareReport() with { Candidate = candidate };
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 1, 2, 3, new Dictionary<string, long>(), "h", [FixturePlan()], 0),
            new CompareRunArtifact("candidate", 5, 4, 5, 6, new Dictionary<string, long>(), "h", [xml], 0),
        };
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(report, runs, "wind");
        var id = Path.GetFileName(directory);
        var operators = Assert.IsType<ArtifactOperatorsSection>(
            ArtifactReader.ReadSection(temp.Path, id, "operators"));
        var href = Assert.Single(Assert.Single(operators.Operators, op => op.HasSpill == true).PlanRefs!);
        Assert.Equal(5, href.Repetition);

        var runsPath = Path.Combine(directory, "runs.jsonl");
        File.WriteAllText(runsPath, """
            {"variant":"baseline","repetition":1,"planJsonFiles":["baseline-001-000-000.plan.json"]}
            {"variant":"candidate","repetition":1,"planJsonFiles":["candidate-005-001-000.plan.json"]}
            {"variant":"candidate","repetition":5,"planJsonFiles":[]}
            """);
        var missing = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(
            temp.Path,
            href.ArtifactId,
            "plan",
            cursor: href.Cursor,
            planSelector: new ArtifactPlanSelector(href.Variant, href.Statement, href.ParameterSet, href.Repetition)));
        Assert.Contains("no saved plan evidence", missing.Message, StringComparison.Ordinal);
        Assert.Contains("5", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Operators_section_omits_plan_refs_for_legacy_reports_and_names_the_limitation()
    {
        using var temp = new TempDirectory();
        const string id = "legacy-operators";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(directory);
        var report = CompareReport();
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"operators\"]}");
        File.WriteAllText(Path.Combine(directory, "report.json"),
            JsonSerializer.Serialize(report with { ArtifactDirectory = directory }, ArtifactDirectoryPublisher.JsonOptions));

        var operators = Assert.IsType<ArtifactOperatorsSection>(
            ArtifactReader.ReadSection(temp.Path, id, "operators"));
        Assert.All(operators.Operators, op => Assert.Null(op.PlanRefs));
        Assert.Contains(operators.EvidenceGaps!, gap => gap.Contains("plan source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_section_reads_legacy_nodes_without_node_id()
    {
        using var temp = new TempDirectory();
        const string id = "legacy-plan-nodes";
        var directory = Path.Combine(temp.Path, id);
        Directory.CreateDirectory(Path.Combine(directory, "plans"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");
        File.WriteAllText(Path.Combine(directory, "plans", "baseline-001-000-000.plan.json"),
            """{"statements":[{"root":{"physicalOp":"Scan","objectName":"dbo.T"}}]}""");
        File.WriteAllText(Path.Combine(directory, "runs.jsonl"),
            """{"variant":"baseline","repetition":1,"planJsonFiles":["baseline-001-000-000.plan.json"]}""");

        var page = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(
            temp.Path, id, "plan", planSelector: new ArtifactPlanSelector("baseline")));
        var node = Assert.Single(page.Nodes);
        Assert.Equal("0", node.NodePath);
        Assert.Null(node.Operator.NodeId);
        Assert.Equal("Scan", node.Operator.PhysicalOp);
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

    [Fact]
    public void Dotted_id_under_deep_parent_round_trips_with_owner_checks()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "deep", new string('p', 120), "artifacts");
        Directory.CreateDirectory(root);
        var id = "2026.10.16T000000000Z-host.example-ab12cd34ef";
        var owner = new ArtifactOwner("profile", new Dictionary<string, string>(), "sqlserver", "sql-a", "AdventureWorks");
        var report = CompareReport() with
        {
            Target = new SqlHarnessTargetIdentityReport("sql-a", "AdventureWorks", "sql-a", "AdventureWorks", "profile", Engine: "sqlserver"),
            ArtifactDirectory = Path.Combine(root, id),
        };
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(
            ArtifactManifest.ForReport(report, owner), ArtifactDirectoryPublisher.JsonOptions));
        File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, ArtifactDirectoryPublisher.JsonOptions));

        Assert.True(ArtifactIds.IsValid(id));
        var summary = Assert.IsType<CompareBenchmarkSummary>(ArtifactReader.ReadSection(root, id, "summary", owner));
        Assert.Equal(report.Baseline.ElapsedTimeMilliseconds, summary.Baseline.ElapsedTimeMilliseconds);
        var foreign = Assert.Throws<ArtifactReadException>(() =>
            ArtifactReader.ReadSection(root, id, "summary", owner with { Server = "sql-b" }));
        Assert.Equal("The artifact is not available in the current scope.", foreign.Message);

        var prefix = id[..8];
        Assert.True(ArtifactIds.IsValid(prefix));
        var clipped = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(root, prefix, "summary", owner));
        Assert.Equal("Unknown artifact id.", clipped.Message);
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