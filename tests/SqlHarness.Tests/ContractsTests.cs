using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ContractsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Documentation_covers_watch_and_snapshot_workflows()
    {
        var readme = ReadRepositoryFile("README.md");
        var skill = ReadRepositoryFile("skills", "sqlharness", "SKILL.md");

        foreach (var doc in new[] { readme, skill })
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
        Assert.Contains("sqlharness watch prod-eu", skill, StringComparison.Ordinal);
        Assert.Contains("sqlharness snapshot prod-eu", skill, StringComparison.Ordinal);
        Assert.Contains("--until-unchanged", skill, StringComparison.Ordinal);
        Assert.Contains("--diff", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_covers_shared_measure_and_compare_parameter_sets()
    {
        var readme = ReadRepositoryFile("README.md");
        var skill = ReadRepositoryFile("skills", "sqlharness", "SKILL.md");
        var mcp = ReadRepositoryFile("docs", "mcp.md");
        const string example =
            "sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\\queries\\orders.sql --param tenant:nvarchar=acme --param-set .\\sets\\small.sqljson --param-set .\\sets\\large.sqljson --repeat 5 --json";

        var section = Section(readme, "## Parameter sets");
        Assert.Contains(example, section, StringComparison.Ordinal);
        foreach (var token in new[] { "--param-set", "--param", "parameterSets", "parameterSetFiles", "matrix", "2–32", "declared type, size, precision and scale", "inferred sizes", "one connection", "one connection per set", "64 KiB", ".sqlplan" })
            Assert.Contains(token, section, StringComparison.Ordinal);
        // Safety clauses: the files are locally sensitive and their values stay out of reports.
        Assert.Contains("files are locally sensitive", section, StringComparison.Ordinal);
        Assert.Contains("does not copy parameter values", section, StringComparison.Ordinal);
        Assert.Contains("UTF-8 BOM", section, StringComparison.Ordinal);
        Assert.Contains("JSON comments", section, StringComparison.Ordinal);
        Assert.Contains("trailing commas", section, StringComparison.Ordinal);
        Assert.Contains("accepted", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("](README.md#parameter-sets)", ReadRepositoryFile("AGENTS.md"), StringComparison.Ordinal);
        Assert.Contains("](../../README.md#parameter-sets)", skill, StringComparison.Ordinal);
        Assert.Contains(example, skill, StringComparison.Ordinal);
        foreach (var token in new[] { "`parameterSets`", "`parameterSetFiles`", "`matrix`", "matching names", "declared shapes", "one connection per set", "older matrix artifacts remain readable" })
            Assert.Contains(token, skill, StringComparison.Ordinal);
        Assert.Contains("`sqlharness_measure`", mcp, StringComparison.Ordinal);
        Assert.Contains("`sqlharness_compare`", mcp, StringComparison.Ordinal);
        Assert.Contains("`setName`", mcp, StringComparison.Ordinal);
        Assert.Contains("Older artifacts with only `parameterValue` remain readable", mcp, StringComparison.Ordinal);
        Assert.Contains("2–32 distinct values", mcp, StringComparison.Ordinal);

        var safety = Section(readme, "## Safety contract");
        Assert.Contains("Credential-redacted operation errors are stored in the activity journal by default.", safety, StringComparison.Ordinal);
        Assert.Contains("credential-redacted error messages", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("journal.storeSensitive", skill, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Parameter-sniffing checks in `measure` across multiple parameter sets.",
            readme,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Missing-index overlap analysis", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("A thin MCP facade over `SqlHarness.Core`.", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Skill_links_resolve_to_reference_sections()
    {
        var skill = ReadRepositoryFile("skills", "sqlharness", "SKILL.md");
        foreach (var (file, path) in new[] { ("README.md", new[] { "README.md" }), ("docs/mcp.md", new[] { "docs", "mcp.md" }) })
        {
            var slugs = HeadingSlugs(ReadRepositoryFile(path));
            var anchors = System.Text.RegularExpressions.Regex
                .Matches(skill, @"\]\(\.\./\.\./" + System.Text.RegularExpressions.Regex.Escape(file) + @"#([^)]+)\)")
                .Select(match => match.Groups[1].Value)
                .ToArray();
            if (file == "README.md")
                Assert.NotEmpty(anchors);
            Assert.All(anchors, anchor => Assert.Contains(anchor, slugs));
        }

        Assert.Contains("](../../docs/mcp.md)", skill, StringComparison.Ordinal);

        // The skill keeps one line of honest framing; README Safety contract is the authoritative statement.
        const string framing = "For enforcement, point profiles at a least-privilege (reader) database principal.";
        Assert.Contains(framing, skill, StringComparison.Ordinal);
        Assert.Contains(framing, Section(ReadRepositoryFile("README.md"), "## Safety contract"), StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_covers_deployed_mcp_server()
    {
        var readme = ReadRepositoryFile("README.md");
        var mcp = ReadRepositoryFile("docs", "mcp.md");

        Assert.Contains("docs/mcp.md", readme, StringComparison.Ordinal);
        Assert.Contains("The local MCP server over stdio is implemented", readme, StringComparison.Ordinal);


        foreach (var tool in new[]
        {
            "sqlharness_capabilities",
            "sqlharness_query",
            "sqlharness_measure",
            "sqlharness_compare",
            "sqlharness_watch",
            "sqlharness_snapshot",
            "sqlharness_plan",
            "sqlharness_artifact",
        })
        {
            Assert.Contains(tool, mcp, StringComparison.Ordinal);
        }

        Assert.Contains("sqlharness mcp serve <profile> --var key=value", mcp, StringComparison.Ordinal);
        Assert.Contains("--unsafe-direct", mcp, StringComparison.Ordinal);
        Assert.Contains("ModelContextProtocol", mcp, StringComparison.Ordinal);
        Assert.Contains("2.2.0", mcp, StringComparison.Ordinal);
        Assert.Contains("2025-11-25", mcp, StringComparison.Ordinal);
        Assert.Contains("65536", mcp, StringComparison.Ordinal);
        Assert.Contains("262144", mcp, StringComparison.Ordinal);
        Assert.Contains("4096..1048576", mcp, StringComparison.Ordinal);
        Assert.Contains("32768", mcp, StringComparison.Ordinal);
        Assert.Contains("AllowMutation: false", mcp, StringComparison.Ordinal);
        Assert.Contains("Exactly 8 tools are registered", mcp, StringComparison.Ordinal);
        Assert.Contains("Tool calls run concurrently", mcp, StringComparison.Ordinal);
        Assert.Contains("SQLHarness does not serialize benchmarks", mcp, StringComparison.Ordinal);
        foreach (var removed in new[] { "sqlharness_inspect", "sqlharness_validate", "busy", "includeDetails" })
            Assert.DoesNotContain(removed, mcp, StringComparison.Ordinal);
        Assert.Contains("watch_max_duration", mcp, StringComparison.Ordinal);
        Assert.Contains("snapshot_differences", mcp, StringComparison.Ordinal);
        Assert.Contains("Byte counts are not token counts", mcp, StringComparison.Ordinal);
        Assert.Contains("## Evidence scope", mcp, StringComparison.Ordinal);
        Assert.Contains("Not executed", mcp, StringComparison.Ordinal);

        var dryRun = Section(mcp, "### Parameters and dry runs");
        Assert.Contains("optional leading `@`", dryRun, StringComparison.Ordinal);
        Assert.Contains("same check as CLI `--dry-run`", dryRun, StringComparison.Ordinal);
        Assert.Contains("](../README.md#safety-contract)", dryRun, StringComparison.Ordinal);
        // README Safety contract owns the dry-run rule; the MCP doc and the skill point to it.
        var safety = Section(readme, "## Safety contract");
        foreach (var fact in new[]
        {
            "IfStatement at line 14",
            "parameterSets",
            "raw-token emission receipt",
            "runtime artifact storage, authentication, connection identity, catalog contents or database permissions",
            "missing_parameters",
            "never authorizes a later mutation",
            "The first executed result's `database` is the verified target",
        })
            Assert.Contains(fact, safety, StringComparison.Ordinal);
        Assert.Contains("Dapper", Section(readme, "## Safety contract"), StringComparison.Ordinal);
        Assert.Contains("Fixed-width `nchar`/`char`", Section(readme, "## Safety contract"), StringComparison.Ordinal);
    }

    [Fact]
    public void Agents_contract_links_resolve_to_reference_sections()
    {
        var agents = ReadRepositoryFile("AGENTS.md");
        var readme = ReadRepositoryFile("README.md");
        var headingSlugs = HeadingSlugs(readme);

        var anchors = System.Text.RegularExpressions.Regex
            .Matches(agents, @"\]\(README\.md#([^)]+)\)")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(anchors);
        Assert.All(anchors, anchor => Assert.Contains(anchor, headingSlugs));
        Assert.Contains("](docs/mcp.md)", agents, StringComparison.Ordinal);
        Assert.Contains("--allow-mutation --confirm-database", agents, StringComparison.Ordinal);
        Assert.Contains("verify-linux.ps1", agents, StringComparison.Ordinal);
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

    // The text from a heading line to the next heading of the same or a higher level; lines inside
    // fenced code blocks (for example a `# comment`) are never headings.
    private static string Section(string markdown, string heading)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var level = heading.TakeWhile(c => c == '#').Count();
        var start = Array.FindIndex(lines, line => line.TrimEnd() == heading);
        Assert.True(start >= 0, "Missing heading: " + heading);
        var end = lines.Length;
        var inFence = false;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;
            else if (!inFence && lines[i].StartsWith('#') && lines[i].TakeWhile(c => c == '#').Count() <= level && lines[i].TrimStart('#').StartsWith(' '))
            {
                end = i;
                break;
            }
        }

        return string.Join('\n', lines[start..end]);
    }

    // The paragraph (blank-line delimited) that starts with the given text.
    private static string Paragraph(string markdown, string start)
    {
        var paragraphs = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n");
        return Assert.Single(paragraphs, paragraph => paragraph.StartsWith(start, StringComparison.Ordinal));
    }

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
        Assert.Equal(8, cases.Select(c => c.Operation.GetType()).Distinct().Count());
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
    ];

    private static IReadOnlyList<(SqlHarnessOperation Operation, object Report, bool Compatible)> OperationReportCases()
    {
        var operations = Operations();
        var reports = Reports();
        var compatible = new HashSet<(int Operation, int Report)>
        {
            (0, 0), (1, 1), (2, 2), (3, 3), (3, 4), (4, 5), (5, 6), (6, 7), (7, 8),
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