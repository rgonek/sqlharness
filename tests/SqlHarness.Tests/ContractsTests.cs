using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ContractsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Skill_does_not_depend_on_files_outside_its_package()
    {
        var skill = ReadRepositoryFile("skills", "sqlharness", "SKILL.md");
        var links = System.Text.RegularExpressions.Regex.Matches(skill, @"\]\(([^)]+)\)");
        Assert.All(links.Cast<System.Text.RegularExpressions.Match>(), link =>
            Assert.DoesNotContain("..", link.Groups[1].Value.Split('/')));
    }

    [Fact]
    public void Agents_contract_links_resolve_to_reference_sections()
    {
        var agents = ReadRepositoryFile("AGENTS.md");
        var links = System.Text.RegularExpressions.Regex.Matches(agents, @"\]\(([^)]+)\)");
        Assert.NotEmpty(links);
        Assert.All(links.Cast<System.Text.RegularExpressions.Match>(), link =>
        {
            var target = link.Groups[1].Value.Split('#', 2);
            var document = ReadRepositoryFile(target[0].Split('/'));
            if (target.Length == 2)
                Assert.Contains(target[1], HeadingSlugs(document));
        });
    }

    [Fact]
    public void Readme_links_into_mcp_doc_resolve_to_headings()
    {
        var readme = ReadRepositoryFile("README.md");
        var slugs = HeadingSlugs(ReadRepositoryFile("docs", "mcp.md"));
        var anchors = System.Text.RegularExpressions.Regex
            .Matches(readme, @"\]\(docs/mcp\.md#([^)]+)\)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(anchors);
        Assert.All(anchors, anchor => Assert.Contains(anchor, slugs));
    }

    private static HashSet<string> HeadingSlugs(string markdown) =>
        System.Text.RegularExpressions.Regex
            .Matches(markdown, @"^#{1,6}\s+(.+?)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => GitHubSlug(match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

    private static string GitHubSlug(string heading)
    {
        var builder = new System.Text.StringBuilder(heading.Length);
        foreach (var character in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
                builder.Append(character);
            else if (character == ' ')
                builder.Append('-');
        }

        return builder.ToString();
    }

    private static string ReadRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Could not locate repository file: {string.Join('/', path)}");
    }

    [Fact]
    public void Exit_codes_are_stable()
    {
        Assert.Equal(0, (int)SqlHarnessExitCode.Success);
        Assert.Equal(2, (int)SqlHarnessExitCode.Safety);
        Assert.Equal(3, (int)SqlHarnessExitCode.Authentication);
        Assert.Equal(4, (int)SqlHarnessExitCode.TargetMismatch);
        Assert.Equal(5, (int)SqlHarnessExitCode.SqlExecution);
        Assert.Equal(6, (int)SqlHarnessExitCode.LocalStorage);
        Assert.Equal(7, (int)SqlHarnessExitCode.WatchMaxDuration);
        Assert.Equal(8, (int)SqlHarnessExitCode.SnapshotDifferences);
    }

    [Fact]
    public void Operation_family_is_closed_to_query_compare_measure_and_plan()
    {
        Assert.Equal(
            [
                typeof(SqlHarnessQueryOperation),
                typeof(SqlHarnessCompareOperation),
                typeof(SqlHarnessCompareMatrixOperation),
                typeof(SqlHarnessMeasureOperation),
                typeof(SqlHarnessPlanOperation),
                typeof(SqlHarnessWatchOperation),
                typeof(SqlHarnessSnapshotOperation),
                typeof(SqlHarnessArtifactReadOperation),
                typeof(SqlHarnessMutationOperation),
                typeof(SqlHarnessAnnotationOperation),
            ],
            typeof(SqlHarnessOperation).Assembly.GetTypes()
                .Where(t => t.BaseType == typeof(SqlHarnessOperation))
                .OrderBy(OperationOrder));
    }

    private static int OperationOrder(Type operationType) => operationType.Name switch
    {
        nameof(SqlHarnessQueryOperation) => 0,
        nameof(SqlHarnessCompareOperation) => 1,
        nameof(SqlHarnessCompareMatrixOperation) => 2,
        nameof(SqlHarnessMeasureOperation) => 3,
        nameof(SqlHarnessPlanOperation) => 4,
        nameof(SqlHarnessWatchOperation) => 5,
        nameof(SqlHarnessSnapshotOperation) => 6,
        nameof(SqlHarnessArtifactReadOperation) => 7,
        nameof(SqlHarnessMutationOperation) => 8,
        nameof(SqlHarnessAnnotationOperation) => 9,
        _ => int.MaxValue,
    };

    [Fact]
    public void Measure_operation_exposes_the_required_contract()
    {
        var target = new SqlTargetRequest(
            "test",
            new Dictionary<string, string> { ["env"] = "a" });
        var operation = new SqlHarnessMeasureOperation(target, "CREATE TABLE #t(Id int)", "SELECT 1", ["id=1"], 30, 5);

        Assert.Equal(target, operation.Target);
        Assert.Equal("CREATE TABLE #t(Id int)", operation.SetupSql);
        Assert.Equal("SELECT 1", operation.QuerySql);
        Assert.Equal(["id=1"], operation.Parameters);
        Assert.Equal(30, operation.TimeoutSeconds);
        Assert.Equal(5, operation.Repeat);
    }

    [Fact]
    public void Compare_report_json_projects_results_equivalent_and_equivalence_for_ordered_default()
    {
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5,
            10,
            true,
            Variant("baseline"),
            Variant("candidate"),
            null);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report, WebJson));
        var root = document.RootElement;

        Assert.True(root.GetProperty("resultsEquivalent").GetBoolean());
        var equivalence = root.GetProperty("equivalence");
        Assert.Equal((int)ResultComparisonMode.Ordered, equivalence.GetProperty("mode").GetInt32());
        Assert.True(equivalence.GetProperty("equivalent").GetBoolean());
        Assert.Equal(0, equivalence.GetProperty("differingPositions").GetInt64());
        Assert.Equal(0, equivalence.GetProperty("baselineOnlyCount").GetInt64());
        Assert.Equal(0, equivalence.GetProperty("candidateOnlyCount").GetInt64());
        Assert.Equal(report.ResultsEquivalent, report.Equivalence.Equivalent);
    }

    [Fact]
    public void Compare_report_json_nulls_equivalence_fields_for_off_mode()
    {
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5,
            10,
            null,
            Variant("baseline"),
            Variant("candidate"),
            null)
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null),
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report, WebJson));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("resultsEquivalent").ValueKind);
        var equivalence = root.GetProperty("equivalence");
        Assert.Equal((int)ResultComparisonMode.Off, equivalence.GetProperty("mode").GetInt32());
        Assert.Equal(JsonValueKind.Null, equivalence.GetProperty("equivalent").ValueKind);
        Assert.Equal(JsonValueKind.Null, equivalence.GetProperty("differingPositions").ValueKind);
        Assert.Equal(JsonValueKind.Null, equivalence.GetProperty("baselineOnlyCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, equivalence.GetProperty("candidateOnlyCount").ValueKind);
    }

    [Fact]
    public void Report_contract_covers_the_closed_operation_family()
    {
        var operations = typeof(SqlHarnessOperation).Assembly.GetTypes()
            .Where(t => t.BaseType == typeof(SqlHarnessOperation))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            operations,
            OperationReportContract.KnownOperations
                .Select(t => t.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Null_report_is_compatible_with_every_operation()
    {
        foreach (var operation in Operations())
            OperationReportContract.AssertCompatible(operation, null);
    }

    [Fact]
    public void Every_operation_accepts_only_its_own_report_types()
    {
        var cases = OperationReportCases();
        Assert.Equal(9, cases.Select(c => c.Operation.GetType()).Distinct().Count());
        foreach (var (operation, report, compatible) in cases)
        {
            var exception = Record.Exception(() =>
                OperationReportContract.AssertCompatible(operation, report));
            Assert.True(
                compatible == (exception is null),
                $"operation={operation.GetType().Name} report={report.GetType().Name} " +
                $"expectedCompatible={compatible} error={exception?.Message}");
        }
    }

    private static IReadOnlyList<SqlHarnessOperation> Operations() =>
    [
        new SqlHarnessQueryOperation(Target(), "SELECT 1", [], 30, 100, false, null),
        new SqlHarnessCompareOperation(Target(), null, "SELECT 1", "SELECT 2", [], 30, 1),
        new SqlHarnessCompareMatrixOperation(Target(), null, "SELECT 1", "SELECT 2", [], 30, 1, "BatchSize:int=1,2"),
        new SqlHarnessMeasureOperation(Target(), null, "SELECT 1", [], 30, 1),
        new SqlHarnessPlanOperation("<ShowPlanXML/>", new OutputFootprint(0, 0)),
        new SqlHarnessWatchOperation(Target(), "SELECT 1", [], 30, 100, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), null, 3),
        new SqlHarnessSnapshotOperation(Target(), "SELECT 1", [], 30, 100, "before", false, false),
        new SqlHarnessArtifactReadOperation("root", "artifact", "summary"),
        new SqlHarnessMutationOperation(Target(), "UPDATE dbo.T SET Value = 1", [], 30, 100, null),
    ];

    private static IReadOnlyList<object> Reports() =>
    [
        new SqlHarnessQueryReport(Identity(), "read-only", [], [], -1, 0, "hash", new OutputFootprint(0, 0)),
        new SqlHarnessCompareReport(Identity(), 1, 2, true, Variant("baseline"), Variant("candidate"), null),
        new SqlHarnessCompareMatrixReport("@BatchSize", "int", []),
        new SqlHarnessMeasureReport(Identity(), 1, 2, true, Variant("measure"), null),
        new SqlHarnessMeasureSetReport(
            Identity(), 1, 2, 1, [], "rule", "warning", [],
            new MeasureCrossSetSummary(null, 0, null, 0, null, 0, null, 0, null, 0, null, 0), null),
        new DistilledPlan([]),
        new SqlHarnessWatchReport(Identity(), 1, 10, WatchExitReason.ConditionMet, []),
        new SqlHarnessSnapshotReport(Identity(), "before", SnapshotVerdict.Stored, 0, []),
        new SqlHarnessArtifactReadReport(new object(), null),
        new SqlHarness.Core.Mutations.SqlHarnessMutationPrepared(new SqlHarness.Core.Mutations.MutationRequest(
            "test", new Dictionary<string, string>(), "sqlserver", "server", "database", "UPDATE dbo.T SET Value = 1",
            [], 30, 100, "dml", [], "session")),
        new SqlHarnessDryRunReport("mutation", "batch", "dml", [], 1),
        new SqlHarness.Core.Mutations.MutationBrokerResult("success", null, null),
    ];

    private static IReadOnlyList<(SqlHarnessOperation Operation, object Report, bool Compatible)> OperationReportCases()
    {
        var operations = Operations();
        var reports = Reports();
        var compatible = new HashSet<(int Operation, int Report)>
        {
            (0, 0), (0, 10), (1, 1), (1, 10), (2, 2), (2, 10), (3, 3), (3, 4), (3, 10), (4, 5), (5, 6), (6, 7), (7, 8),
            (8, 0), (8, 9), (8, 10), (8, 11),
        };
        var cases = new List<(SqlHarnessOperation, object, bool)>();
        for (var o = 0; o < operations.Count; o++)
            for (var r = 0; r < reports.Count; r++)
                cases.Add((operations[o], reports[r], compatible.Contains((o, r))));
        return cases;
    }

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static SqlHarnessTargetIdentityReport Identity() =>
        new("s", "d", "s", "d", "profile");

    private static CompareVariantReport Variant(string name) =>
        new(name, new(1, 2, 3), new(1, 2, 3), new(1, 2, 3), new Dictionary<string, long>(), [], []);
}