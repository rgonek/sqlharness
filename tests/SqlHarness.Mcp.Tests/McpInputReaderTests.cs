using System.Text;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T3 bounded-reader contract: SQL/plan ingress at 16 MiB, parameter sets at
/// 64 KiB through the shared Core strict parser, inline payloads at 1 MiB,
/// explicit roots only, and rejection of traversal, UNC, and oversized input.
/// Snapshot capture takes no force flag.
/// Only synthetic HOME and temp directories are used.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpInputReaderTests : IDisposable
{
    private const string ProfileName = "mcp-t3-reader";

    private readonly string _home;
    private readonly string? _savedHome;
    private readonly string _targetsFile;
    private readonly string _root;
    private readonly McpScope _scope;

    public McpInputReaderTests()
    {
        _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-t3-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
        _targetsFile = Path.Combine(_home, "targets.json");
        File.WriteAllText(
            _targetsFile,
            "{\""
            + ProfileName
            + "\": {\"server\": \"mcp-unreachable.invalid\", \"database\": \"reportdb\", "
            + "\"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"integrated\"}}");
        _root = Path.Combine(_home, "inputs");
        Directory.CreateDirectory(_root);
        _scope = McpScope.Create(
            new McpServerOptions
            {
                Profile = ProfileName,
                Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
                InputRoots = [_root],
            },
            ProfileStore.Load(_targetsFile));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private McpScope ScopeWithoutRoots() => McpScope.Create(
        new McpServerOptions
        {
            Profile = ProfileName,
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
        },
        ProfileStore.Load(_targetsFile));

    [Fact]
    public async Task Rooted_files_read_exactly_and_inline_passes_through()
    {
        var path = WriteFile("q.sql", "SELECT 1;");
        Assert.Equal("SELECT 1;", await McpInputReader.ReadSqlAsync(null, path, _scope, CancellationToken.None));
        Assert.Equal("SELECT @a;", await McpInputReader.ReadSqlAsync("SELECT @a;", null, _scope, CancellationToken.None));
        Assert.Equal("<plan/>", await McpInputReader.ReadPlanAsync(null, WriteFile("p.plan", "<plan/>"), _scope, CancellationToken.None));
    }

    [Fact]
    public async Task Sql_source_requires_exactly_one_side()
    {
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync("SELECT 1", WriteFile("q.sql", "SELECT 1"), _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, null, _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync("  ", "  ", _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadPlanAsync("<a/>", WriteFile("p.plan", "<a/>"), _scope, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_roots_disable_every_file_input()
    {
        var scope = ScopeWithoutRoots();
        Assert.Empty(scope.InputRoots);
        var exception = await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, Path.Combine(_root, "q.sql"), scope, CancellationToken.None));
        Assert.Contains("disabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--input-root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sqlharness_capabilities inputRoots is empty", exception.Message, StringComparison.Ordinal);
        Assert.Contains("inline", exception.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync(["a.sqljson"], scope, CancellationToken.None));
    }

    [Fact]
    public async Task Traversal_and_outside_roots_are_rejected()
    {
        var outside = Path.Combine(_home, "outside.sql");
        File.WriteAllText(outside, "SELECT 1;");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, outside, _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, Path.Combine(_root, "..", "outside.sql"), _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, "relative.sql", _scope, CancellationToken.None));

        // A sibling whose name merely extends the root prefix is still outside.
        var sibling = _root + "2";
        Directory.CreateDirectory(sibling);
        var siblingFile = Path.Combine(sibling, "q.sql");
        File.WriteAllText(siblingFile, "SELECT 1;");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, siblingFile, _scope, CancellationToken.None));
    }

    [Fact]
    public async Task Outside_root_error_lists_configured_roots_points_to_capabilities_and_echoes_the_path()
    {
        var outside = Path.Combine(_home, "outside.sql");
        File.WriteAllText(outside, "SELECT 1;");

        var exception = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadSqlAsync(null, outside, _scope, CancellationToken.None));

        Assert.Contains("outside every configured input root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sqlharness_capabilities", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Assert.Single(_scope.InputRoots), exception.Message, StringComparison.Ordinal);
        Assert.Contains(outside, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_root_error_with_several_roots_lists_each_of_them()
    {
        var second = Path.Combine(_home, "inputs-b");
        Directory.CreateDirectory(second);
        var roots = new[] { _root, second };

        var exception = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadTextFileAsync(Path.Combine(_home, "outside.sql"), roots, McpLimits.MaxSqlBytes, CancellationToken.None));

        foreach (var root in roots)
            Assert.Contains(root, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("First ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_root_error_with_too_many_roots_gives_count_and_paging_pointer_not_a_full_list()
    {
        var roots = Enumerable.Range(0, McpInputReader.MaxListedRoots + 4)
            .Select(index => Path.Combine(Path.GetPathRoot(_home)!, "r" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        var exception = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadTextFileAsync(Path.Combine(_home, "outside.sql"), roots, McpLimits.MaxSqlBytes, CancellationToken.None));

        var total = roots.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"First {McpInputReader.MaxListedRoots} of {total}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("inputRootCursor", exception.Message, StringComparison.Ordinal);
        Assert.Contains(roots[0], exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(roots[^1], exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Configured roots (", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_root_error_with_long_roots_states_the_count_when_not_even_one_fits()
    {
        var roots = new[] { Path.Combine(_home, new string('a', McpInputReader.MaxListedRootCharacters)), _root };

        var exception = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadTextFileAsync(Path.Combine(_home, "outside.sql"), roots, McpLimits.MaxSqlBytes, CancellationToken.None));

        Assert.Contains("2 input roots are configured", exception.Message, StringComparison.Ordinal);
        Assert.Contains("inputRootCursor", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(roots[0], exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejection_reasons_are_distinct_and_echo_the_caller_path()
    {
        async Task<string> Message(string path) =>
            (await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, path, _scope, CancellationToken.None))).Message;

        var relative = await Message("relative.sql");
        Assert.Contains("not an absolute path", relative, StringComparison.Ordinal);
        Assert.Contains("relative.sql", relative, StringComparison.Ordinal);

        var malformedPath = Path.Combine(_root, "bad\0name.sql");
        var malformed = await Message(malformedPath);
        Assert.Contains("not a valid path", malformed, StringComparison.Ordinal);

        const string unc = @"\\mcp-unreachable.invalid\share\q.sql";
        var uncMessage = await Message(unc);
        Assert.Contains("UNC", uncMessage, StringComparison.Ordinal);
        Assert.Contains(unc, uncMessage, StringComparison.Ordinal);

        var missing = Path.Combine(_root, "no-such-file.sql");
        var missingMessage = await Message(missing);
        Assert.Contains("cannot be read", missingMessage, StringComparison.Ordinal);
        Assert.Contains(missing, missingMessage, StringComparison.Ordinal);

        var blank = (await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadTextFileAsync("  ", _scope.InputRoots, McpLimits.MaxSqlBytes, CancellationToken.None))).Message;
        Assert.Contains("blank", blank, StringComparison.Ordinal);

        Assert.Equal(5, new[] { relative, malformed, uncMessage, missingMessage, blank }.Distinct().Count());
    }

    [Fact]
    public async Task Echoed_path_preserves_caller_supplied_diagnostics()
    {
        var path = Path.Combine(_root, "Password=hunter2;x.sql");

        var exception = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadSqlAsync(null, path, _scope, CancellationToken.None));

        Assert.Contains("Password=hunter2;x.sql", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Echoed_path_is_truncated_and_the_error_result_fits_the_minimum_budget()
    {
        var path = Path.Combine(_root, new string('p', 3000) + ".sql");

        var exception = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadSqlAsync(null, path, _scope, CancellationToken.None));

        Assert.Contains("[truncated,", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('p', McpInputReader.MaxEchoedPathCharacters + 50), exception.Message, StringComparison.Ordinal);
        AssertFitsMinimumBudget(exception.Message);

        // Worst case: the maximum number of listed roots plus a maximum-length path.
        var roots = Enumerable.Range(0, McpInputReader.MaxListedRoots)
            .Select(index => Path.Combine(Path.GetPathRoot(_home)!, "r" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        var outside = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadTextFileAsync(Path.Combine(_home, new string('o', 3000) + ".sql"), roots, McpLimits.MaxSqlBytes, CancellationToken.None));
        AssertFitsMinimumBudget(outside.Message);
    }

    [Theory]
    [InlineData("//admin:S3cret@srv/share/q.sql")]
    [InlineData(@"\\admin:S3cret@srv\share\q.sql")]
    [InlineData("postgres://u:S3cret@h/db")]
    public async Task Echoed_path_masks_url_and_unc_userinfo(string path)
    {
        var exception = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadSqlAsync(null, path, _scope, CancellationToken.None));

        Assert.DoesNotContain("S3cret", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]@", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_root_message_preserves_the_caller_path_for_diagnostics()
    {
        var path = Path.Combine(_home, "Password=hunter2;outside.sql");

        var exception = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadSqlAsync(null, path, _scope, CancellationToken.None));

        Assert.Contains("outside every configured input root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Password=hunter2;outside.sql", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MCP_result_redacts_an_exact_known_secret_in_a_caller_path()
    {
        const string knownSecret = "mcp-reader-known-secret-4197";
        var path = Path.Combine(_home, "secret=" + knownSecret + ";outside.sql");
        var inputError = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadSqlAsync(null, path, _scope, CancellationToken.None));
        var result = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, inputError.Message),
            "sqlharness_query",
            knownSecrets: [knownSecret]);
        var text = Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()).Text;

        Assert.DoesNotContain(knownSecret, text, StringComparison.Ordinal);
        Assert.Contains("outside every configured input root", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worst_case_rejection_messages_stay_within_the_default_cell_limit()
    {
        var longPath = Path.Combine(Path.GetPathRoot(_home)!, new string('o', 3000) + ".sql");
        foreach (var count in new[] { 1, 3, McpInputReader.MaxListedRoots, McpInputReader.MaxListedRoots + 4 })
        {
            var roots = Enumerable.Range(0, count)
                .Select(index => Path.Combine(Path.GetPathRoot(_home)!, new string('r', 17) + index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();
            var exception = await Assert.ThrowsAsync<McpInputException>(
                () => McpInputReader.ReadTextFileAsync(longPath, roots, McpLimits.MaxSqlBytes, CancellationToken.None));
            Assert.True(exception.Message.Length <= McpLimits.DefaultMaximumCellCharacters, $"{count} roots: {exception.Message.Length}");
            Assert.Contains("[truncated,", exception.Message, StringComparison.Ordinal);
        }

        var tooLongRoots = new[] { Path.Combine(_home, new string('a', 400)) };
        var single = await Assert.ThrowsAsync<McpInputException>(
            () => McpInputReader.ReadTextFileAsync(longPath, tooLongRoots, McpLimits.MaxSqlBytes, CancellationToken.None));
        Assert.True(single.Message.Length <= McpLimits.DefaultMaximumCellCharacters);
    }

    private static void AssertFitsMinimumBudget(string message)
    {
        var result = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, message),
            "sqlharness_query",
            new McpResultBudget(McpLimits.MinCallToolResultBudgetBytes));
        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.MinCallToolResultBudgetBytes);
    }

    [Fact]
    public async Task Oversized_sql_files_and_inline_payloads_are_rejected()
    {
        var big = Path.Combine(_root, "big.sql");
        File.WriteAllText(big, new string('x', (int)McpLimits.MaxSqlBytes + 1));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, big, _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(new string('y', (int)McpLimits.MaxInlineBytes + 1), null, _scope, CancellationToken.None));
        McpInputReader.ThrowIfInlineTooLarge(new string('z', (int)McpLimits.MaxInlineBytes));
    }

    [Fact]
    public async Task Parameter_sets_use_the_shared_parser_and_size_cap()
    {
        var good = WriteFile("good.sqljson", "{\"name\":\"small\",\"parameters\":[\"id:int=1\"]}");
        var sets = await McpInputReader.ReadParameterSetsAsync([good], _scope, CancellationToken.None);
        Assert.Equal("small", Assert.Single(sets).Name);
        Assert.Equal(["id:int=1"], Assert.Single(sets).Parameters.ToArray());

        var bom = Path.Combine(_root, "bom.sqljson");
        File.WriteAllBytes(bom, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"name\":\"s\",\"parameters\":[\"id:int=1\"]}")).ToArray());
        var bomSet = await McpInputReader.ReadParameterSetsAsync([bom], _scope, CancellationToken.None);
        Assert.Equal("s", Assert.Single(bomSet).Name);

        var trailing = WriteFile("trailing.sqljson", "{\"name\":\"s\",\"parameters\":[\"id:int=1\"],}");
        await McpInputReader.ReadParameterSetsAsync([trailing], _scope, CancellationToken.None);

        var comment = WriteFile("comment.sqljson", "{\"name\":/*c*/\"s\",\"parameters\":[\"id:int=1\"]}");
        await McpInputReader.ReadParameterSetsAsync([comment], _scope, CancellationToken.None);

        var extra = WriteFile("extra.sqljson", "{\"name\":\"s\",\"parameters\":[\"id:int=1\"],\"values\":[]}");
        var unknown = await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([extra], _scope, CancellationToken.None));
        Assert.Contains(extra, unknown.Message, StringComparison.Ordinal);
        Assert.Contains("unknown property 'values'", unknown.Message, StringComparison.Ordinal);

        var wrongExtension = WriteFile("set.json", "{\"name\":\"s\",\"parameters\":[\"id:int=1\"]}");
        var extension = await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([wrongExtension], _scope, CancellationToken.None));
        Assert.Contains(".sqljson", extension.Message, StringComparison.Ordinal);
        Assert.Contains(wrongExtension, extension.Message, StringComparison.Ordinal);

        var big = Path.Combine(_root, "big.sqljson");
        var padding = new string(' ', McpLimits.MaxParameterSetBytes);
        File.WriteAllText(big, "{\"name\":\"s\",\"parameters\":[\"id:int=1\"]}" + padding);
        var oversized = await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([big], _scope, CancellationToken.None));
        Assert.Contains(big, oversized.Message, StringComparison.Ordinal);
        Assert.Contains("64 KiB", oversized.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_parser_rejects_oversized_bytes_even_without_filesystem_read()
    {
        Assert.Throws<ParameterSetFileException>(() =>
            ParameterSetFileReader.Parse(new byte[McpLimits.MaxParameterSetBytes + 1]));
    }

    // 002/T1 regression 1 (defect: must FAIL on unchanged src on Windows):
    // a root with a trailing separator still admits legal files inside it.
    [Fact]
    public async Task Root_with_trailing_separator_still_reads_files_inside()
    {
        var scope = McpScope.Create(
            new McpServerOptions
            {
                Profile = ProfileName,
                Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
                InputRoots = [_root + Path.DirectorySeparatorChar],
            },
            ProfileStore.Load(_targetsFile));
        var path = WriteFile("trailing-root.sql", "SELECT 1;");
        Assert.Equal("SELECT 1;", await McpInputReader.ReadSqlAsync(null, path, scope, CancellationToken.None));
    }

    // 002/T1 regression 2: explicit boundary — a file directly in the root
    // reads, while root + "suffix" stays outside (via ReadPlanAsync, a method
    // the existing sibling-prefix test does not exercise).
    [Fact]
    public async Task Root_boundary_admits_direct_files_and_rejects_extended_prefix_sibling()
    {
        Assert.Equal(
            "<plan/>",
            await McpInputReader.ReadPlanAsync(null, WriteFile("boundary.plan", "<plan/>"), _scope, CancellationToken.None));
        var sibling = _root + "suffix";
        Directory.CreateDirectory(sibling);
        var siblingFile = Path.Combine(sibling, "b.plan");
        File.WriteAllText(siblingFile, "<plan/>");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadPlanAsync(null, siblingFile, _scope, CancellationToken.None));
    }

    // 002/T1 regression 3 (defect: must FAIL on unchanged src): fail closed
    // on a filesystem root - rejected either at scope creation or at reader
    // level. The brief allows either phase, so this test accepts a
    // creation-throw of any startup/input exception type OR successful
    // creation followed by reader rejection of a file under the filesystem
    // root. Fail-closed is the documented choice, not a locked type or phase.
    // Old code passes via the reader leg for the wrong reason (the double-separator defect rejects everything); the true T2 RED would be a trim without explicit filesystem-root rejection.
    [Fact]
    public async Task Filesystem_root_is_rejected_at_scope_creation()
    {
        var filesystemRoot = Path.GetPathRoot(_root);
        Assert.False(string.IsNullOrEmpty(filesystemRoot));
        McpScope scope;
        try
        {
            scope = McpScope.Create(
                new McpServerOptions
                {
                    Profile = ProfileName,
                    Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
                    InputRoots = [filesystemRoot!],
                },
                ProfileStore.Load(_targetsFile));
        }
        catch (McpStartupException)
        {
            return;
        }
        catch (McpInputException)
        {
            return;
        }

        var path = WriteFile("filesystem-root-probe.sql", "SELECT 1;");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, path, scope, CancellationToken.None));
    }

    // 002/T1 regression 4: empty roots disable plan and parameter-set file
    // inputs too (the existing test covers SQL and one parameter-set path).
    [Fact]
    public async Task Empty_roots_reject_plan_and_parameter_set_file_inputs()
    {
        var scope = ScopeWithoutRoots();
        Assert.Empty(scope.InputRoots);
        var plan = WriteFile("empty-roots.plan", "<plan/>");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadPlanAsync(null, plan, scope, CancellationToken.None));
        var sets = WriteFile("empty-roots.sqljson", "{\"name\":\"s\",\"parameters\":[\"id:int=1\"]}");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([sets], scope, CancellationToken.None));
    }

    // 002/T1 regression 7: case-sensitivity proof exists ONLY on a
    // case-sensitive filesystem. On case-insensitive NTFS this test proves
    // nothing and intentionally passes trivially; the missing evidence is
    // recorded in the T1 report instead of faked. xUnit v2 has no dynamic
    // skip, hence the early return.
    [Fact]
    public async Task Case_only_sibling_is_rejected_on_case_sensitive_filesystems()
    {
        var probe = Path.Combine(_home, "caseprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        if (Directory.Exists(probe.ToUpperInvariant()))
            return; // Case-insensitive FS: no proof possible here. See T1 report.

        var sibling = Path.Combine(
            Path.GetDirectoryName(_root)!,
            Path.GetFileName(_root).ToUpperInvariant());
        Directory.CreateDirectory(sibling);
        var siblingFile = Path.Combine(sibling, "q.sql");
        File.WriteAllText(siblingFile, "SELECT 1;");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, siblingFile, _scope, CancellationToken.None));
    }
}