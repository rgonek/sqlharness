using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ContractsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Documentation_covers_read_only_database_helpers()
    {
        var readme = ReadRepositoryFile("README.md");
        var agents = ReadRepositoryFile("AGENTS.md");
        var skill = ReadRepositoryFile("skills", "sqlharness", "SKILL.md");

        foreach (var doc in new[] { readme, agents, skill })
        {
            Assert.Contains("ping", doc, StringComparison.Ordinal);
            Assert.Contains("counts", doc, StringComparison.Ordinal);
            Assert.Contains("--exact", doc, StringComparison.Ordinal);
            Assert.Contains("--object", doc, StringComparison.Ordinal);
            Assert.True(
                doc.Contains("partition", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("approximate", StringComparison.OrdinalIgnoreCase),
                "Helper docs must describe counts partition/approximate defaults.");
            Assert.True(
                doc.Contains("arbitrary SQL", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("no user SQL", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("never accept arbitrary", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("does not accept arbitrary", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("fixed internal", StringComparison.OrdinalIgnoreCase),
                "Helper docs must state that helpers do not accept arbitrary SQL.");
        }

        Assert.Contains("Prefer `--json`", skill, StringComparison.Ordinal);
        Assert.Contains("sqlharness ping prod-eu", readme, StringComparison.Ordinal);
        Assert.Contains("sqlharness counts prod-eu", agents, StringComparison.Ordinal);
        Assert.Contains("sqlharness schema prod-eu", skill, StringComparison.Ordinal);
        Assert.Contains("--object dbo.Contracts", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_covers_database_space_inspection()
    {
        var readme = ReadRepositoryFile("README.md");
        var agents = ReadRepositoryFile("AGENTS.md");
        var skill = ReadRepositoryFile("skills", "sqlharness", "SKILL.md");

        foreach (var doc in new[] { readme, agents, skill })
        {
            Assert.Contains("space", doc, StringComparison.Ordinal);
            Assert.Contains("--top", doc, StringComparison.Ordinal);
            Assert.Contains("--object", doc, StringComparison.Ordinal);
            Assert.True(
                doc.Contains("DMV", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("dm_db", StringComparison.OrdinalIgnoreCase),
                "Space docs must describe read-only DMV inspection.");
            Assert.True(
                doc.Contains("shrink", StringComparison.OrdinalIgnoreCase)
                && doc.Contains("recovery", StringComparison.OrdinalIgnoreCase)
                && doc.Contains("compression", StringComparison.OrdinalIgnoreCase),
                "Space docs must prohibit shrink/recovery/compression mutation.");
            Assert.True(
                doc.Contains("diagnos", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("storage only", StringComparison.OrdinalIgnoreCase),
                "Space docs must state that the command diagnoses storage only.");
            Assert.Contains("allow-mutation", doc, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("sqlharness space prod-eu", readme, StringComparison.Ordinal);
        Assert.Contains("sqlharness space prod-eu", agents, StringComparison.Ordinal);
        Assert.Contains("sqlharness space prod-eu", skill, StringComparison.Ordinal);
        Assert.Contains("--top 25", skill, StringComparison.Ordinal);
        Assert.Contains("--object dbo.Contracts", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_covers_watch_and_snapshot_workflows()
    {
        var readme = ReadRepositoryFile("README.md");
        var agents = ReadRepositoryFile("AGENTS.md");
        var skill = ReadRepositoryFile("skills", "sqlharness", "SKILL.md");

        foreach (var doc in new[] { readme, agents, skill })
        {
            Assert.Contains("watch", doc, StringComparison.Ordinal);
            Assert.Contains("snapshot", doc, StringComparison.Ordinal);
            Assert.Contains("--until-unchanged", doc, StringComparison.Ordinal);
            Assert.Contains("--diff", doc, StringComparison.Ordinal);
            Assert.Contains("7", doc, StringComparison.Ordinal);
            Assert.Contains("8", doc, StringComparison.Ordinal);
            Assert.True(
                doc.Contains("snapshots", StringComparison.OrdinalIgnoreCase)
                && (doc.Contains("sensitive", StringComparison.OrdinalIgnoreCase)
                    || doc.Contains("locally sensitive", StringComparison.OrdinalIgnoreCase)),
                "Docs must warn that the snapshots directory holds sensitive result data.");
            Assert.True(
                doc.Contains("--force", StringComparison.Ordinal)
                && (doc.Contains("replace", StringComparison.OrdinalIgnoreCase)
                    || doc.Contains("overwrite", StringComparison.OrdinalIgnoreCase)),
                "Docs must state that --force is required to replace a snapshot.");
            Assert.True(
                doc.Contains("cell", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("never prints", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("does not print", StringComparison.OrdinalIgnoreCase)
                || doc.Contains("without cell", StringComparison.OrdinalIgnoreCase),
                "Docs must state that --diff never prints cell values.");
            Assert.True(
                doc.Contains("differences", StringComparison.OrdinalIgnoreCase)
                && (doc.Contains("valid comparison", StringComparison.OrdinalIgnoreCase)
                    || doc.Contains("not an execution", StringComparison.OrdinalIgnoreCase)
                    || doc.Contains("rather than", StringComparison.OrdinalIgnoreCase)),
                "Docs must state exit 8 means a valid comparison found differences.");
            Assert.Contains("--file", doc, StringComparison.Ordinal);
        }

        Assert.Contains(
            "sqlharness watch prod-eu --var tenant=acme --var env=uat --file .\\queries\\progress.sql --param target:int=1000 --until \"Imported >= 1000\" --interval 30 --max-duration 45m --json",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\\queries\\coverage.sql --name before-import --json",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\\queries\\coverage.sql --name before-import --diff --json",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("sqlharness watch prod-eu", agents, StringComparison.Ordinal);
        Assert.Contains("sqlharness snapshot prod-eu", agents, StringComparison.Ordinal);
        Assert.Contains("sqlharness watch prod-eu", skill, StringComparison.Ordinal);
        Assert.Contains("sqlharness snapshot prod-eu", skill, StringComparison.Ordinal);
        Assert.Contains("--until-unchanged", skill, StringComparison.Ordinal);
        Assert.Contains("--diff", skill, StringComparison.Ordinal);
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
    public void Operation_family_is_closed_to_query_compare_measure_gain_and_plan()
    {
        Assert.Equal(
            [
                typeof(SqlHarnessQueryOperation),
                typeof(SqlHarnessCompareOperation),
                typeof(SqlHarnessCompareMatrixOperation),
                typeof(SqlHarnessMeasureOperation),
                typeof(SqlHarnessGainOperation),
                typeof(SqlHarnessPlanOperation),
                typeof(SqlHarnessSchemaOperation),
                typeof(SqlHarnessPingOperation),
                typeof(SqlHarnessCountsOperation),
                typeof(SqlHarnessSpaceOperation),
                typeof(SqlHarnessWatchOperation),
                typeof(SqlHarnessSnapshotOperation),
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
        nameof(SqlHarnessGainOperation) => 4,
        nameof(SqlHarnessPlanOperation) => 5,
        nameof(SqlHarnessSchemaOperation) => 6,
        nameof(SqlHarnessPingOperation) => 7,
        nameof(SqlHarnessCountsOperation) => 8,
        nameof(SqlHarnessSpaceOperation) => 9,
        nameof(SqlHarnessWatchOperation) => 10,
        nameof(SqlHarnessSnapshotOperation) => 11,
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

    private static CompareVariantReport Variant(string name) =>
        new(name, new(1, 2, 3), new(1, 2, 3), new(1, 2, 3), new Dictionary<string, long>(), [], []);
}