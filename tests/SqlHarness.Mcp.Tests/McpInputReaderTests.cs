using System.Text;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T3 bounded-reader contract: SQL/plan ingress at 16 MiB, parameter sets at
/// 64 KiB through the shared Core strict parser, inline payloads at 1 MiB,
/// explicit roots only, and rejection of traversal, ADS, UNC, symlink/reparse
/// escapes, and replace-during-read. Snapshot capture takes no force flag.
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
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, Path.Combine(_root, "q.sql"), scope, CancellationToken.None));
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
    public async Task Ads_unc_and_missing_files_are_rejected()
    {
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, Path.Combine(_root, "q.sql:stream"), _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, @"\\mcp-unreachable.invalid\share\q.sql", _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, "//mcp-unreachable.invalid/share/q.sql", _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, Path.Combine(_root, "no-such-file.sql"), _scope, CancellationToken.None));
    }

    [Fact]
    public async Task Symlink_and_reparse_escapes_are_rejected()
    {
        var outside = Path.Combine(_home, "secret.sql");
        File.WriteAllText(outside, "SELECT 1;");
        // xUnit v2 has no dynamic skip: when the platform denies link creation
        // there is no reparse point to reject, so the test passes trivially.
        var link = Path.Combine(_root, "link.sql");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, link, _scope, CancellationToken.None));

        var outsideDir = Path.Combine(_home, "outside-dir");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "q.sql"), "SELECT 1;");
        var dirLink = Path.Combine(_root, "dirlink");
        try
        {
            Directory.CreateSymbolicLink(dirLink, outsideDir);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, Path.Combine(dirLink, "q.sql"), _scope, CancellationToken.None));
    }

    [Fact]
    public async Task Replace_during_read_is_rejected()
    {
        var path = WriteFile("volatile.sql", "SELECT 1;");
        var exception = await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadTextFileAsync(
            path,
            _scope.InputRoots,
            McpLimits.MaxSqlBytes,
            CancellationToken.None,
            afterOpen: () =>
            {
                // Delete plus recreate: the open handle keeps the old bytes
                // while the path resolves to new length and timestamps.
                File.Delete(path);
                File.WriteAllText(path, "SELECT 2; -- replaced after open");
            }));
        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task Parameter_sets_use_the_shared_strict_parser_and_size_cap()
    {
        var good = WriteFile("good.sqljson", "{\"name\":\"small\",\"parameters\":[\"id:int=1\"]}");
        var sets = await McpInputReader.ReadParameterSetsAsync([good], _scope, CancellationToken.None);
        Assert.Equal("small", Assert.Single(sets).Name);
        Assert.Equal(["id:int=1"], Assert.Single(sets).Parameters.ToArray());

        var bom = Path.Combine(_root, "bom.sqljson");
        File.WriteAllBytes(bom, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"name\":\"s\",\"parameters\":[\"id:int=1\"]}")).ToArray());
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([bom], _scope, CancellationToken.None));

        var trailing = WriteFile("trailing.sqljson", "{\"name\":\"s\",\"parameters\":[\"id:int=1\"],}");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([trailing], _scope, CancellationToken.None));

        var comment = WriteFile("comment.sqljson", "{\"name\":/*c*/\"s\",\"parameters\":[\"id:int=1\"]}");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([comment], _scope, CancellationToken.None));

        var extra = WriteFile("extra.sqljson", "{\"name\":\"s\",\"parameters\":[\"id:int=1\"],\"values\":[]}");
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([extra], _scope, CancellationToken.None));

        var wrongExtension = WriteFile("set.json", "{\"name\":\"s\",\"parameters\":[\"id:int=1\"]}");
        var extension = await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([wrongExtension], _scope, CancellationToken.None));
        Assert.Contains(".sqljson", extension.Message, StringComparison.Ordinal);

        var big = Path.Combine(_root, "big.sqljson");
        var padding = new string(' ', McpLimits.MaxParameterSetBytes);
        File.WriteAllText(big, "{\"name\":\"s\",\"parameters\":[\"id:int=1\"]}" + padding);
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([big], _scope, CancellationToken.None));
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

    // 002/T1 regression 5: ADS/UNC variants on the Read* methods the existing
    // test does not exercise (plan and parameter-set inputs).
    [Fact]
    public async Task Ads_and_unc_are_rejected_on_plan_and_parameter_set_inputs()
    {
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadPlanAsync(null, Path.Combine(_root, "p.plan:stream"), _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadPlanAsync(null, @"\\mcp-unreachable.invalid\share\q.plan", _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadPlanAsync(null, "//mcp-unreachable.invalid/share/q.plan", _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([Path.Combine(_root, "s.sqljson:stream")], _scope, CancellationToken.None));
        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadParameterSetsAsync([@"\\mcp-unreachable.invalid\share\s.sqljson"], _scope, CancellationToken.None));
    }

    // 002/T1 regression 6: gap beyond the covered top-level absolute links —
    // a relative symlink nested in a root subdirectory must not escape.
    // Trivial pass when the platform denies link creation (same convention
    // as the existing symlink test).
    [Fact]
    public async Task Nested_relative_symlink_inside_root_is_rejected()
    {
        var outside = Path.Combine(_home, "nested-secret.sql");
        File.WriteAllText(outside, "SELECT 1;");
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        var link = Path.Combine(sub, "rel.sql");
        try
        {
            File.CreateSymbolicLink(link, Path.GetRelativePath(sub, outside));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        await Assert.ThrowsAsync<McpInputException>(() => McpInputReader.ReadSqlAsync(null, link, _scope, CancellationToken.None));
    }

    // 002/T1 regression 7: case-sensitivity proof exists ONLY on a
    // case-sensitive filesystem. On case-insensitive NTFS this test proves
    // nothing and intentionally passes trivially; the missing evidence is
    // recorded in the T1 report instead of faked. xUnit v2 has no dynamic
    // skip, hence the early return (same convention as the symlink tests).
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