using System.Text.RegularExpressions;

namespace SqlHarness.Tests;

public sealed class GateParityTests
{
    private static readonly string[] ExpectedVerbs = ["restore", "build", "test", "format"];

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_runs_the_same_four_stages_in_the_same_order(string path)
    {
        var verbs = StageVerbs(File.ReadAllText(RepositoryFile.Locate(path.Split('/'))));

        Assert.Equal(ExpectedVerbs, verbs);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_builds_and_formats_identically(string path)
    {
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

        Assert.Contains("-warnaserror", StageCommand(content, "build"), StringComparison.Ordinal);
        Assert.Contains("--no-restore", StageCommand(content, "build"), StringComparison.Ordinal);
        Assert.Contains("--verify-no-changes", StageCommand(content, "format"), StringComparison.Ordinal);
        Assert.Contains("SqlHarness.sln", StageCommand(content, "format"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_runs_the_same_non_integration_test_selection(string path)
    {
        var command = StageCommand(File.ReadAllText(RepositoryFile.Locate(path.Split('/'))), "test");

        Assert.Contains("--no-build", command, StringComparison.Ordinal);
        Assert.Contains("--filter", command, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName!~Integration", command, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_gates_name_the_same_test_filter_value()
    {
        var windows = FilterValue(File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1")));
        var linux = FilterValue(File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1")));

        Assert.Equal("FullyQualifiedName!~Integration", windows);
        Assert.Equal(windows, linux);
    }

    [Fact]
    public void Neither_gate_runs_integration_tests_unfiltered()
    {
        foreach (var path in new[] { "scripts/verify.ps1", "scripts/verify-linux.ps1" })
        {
            var command = StageCommand(File.ReadAllText(RepositoryFile.Locate(path.Split('/'))), "test");

            Assert.Matches(@"--filter\s+['""]FullyQualifiedName!~Integration['""]", command);
        }
    }

    [Fact]
    public void The_two_gates_have_identical_restore_commands()
    {
        var windows = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1")), "restore");
        var linux = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1")), "restore");

        Assert.Equal(windows, linux);
    }

    [Fact]
    public void The_two_gates_have_identical_build_commands()
    {
        var windows = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1")), "build");
        var linux = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1")), "build");

        Assert.Equal(windows, linux);
    }

    [Fact]
    public void The_two_gates_have_identical_format_commands()
    {
        var windows = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1")), "format");
        var linux = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1")), "format");

        Assert.Equal(FormatShape(linux), FormatShape(windows));
    }

    [Fact]
    public void The_two_gates_have_equal_test_commands_modulo_the_linux_logger()
    {
        var windows = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1")), "test");
        var linux = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1")), "test");

        Assert.Equal(linux, windows + " --logger \"console;verbosity=normal\"");
    }

    [Fact]
    public void The_windows_test_command_does_not_include_a_logger()
    {
        var windows = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1")), "test");

        Assert.DoesNotContain("--logger", windows, StringComparison.Ordinal);
    }

    [Fact]
    public void The_only_logger_occurrence_is_in_the_linux_test_command()
    {
        foreach (var path in new[] { "scripts/verify.ps1", "scripts/verify-linux.ps1" })
        {
            var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));
            foreach (var verb in ExpectedVerbs)
            {
                var command = StageCommand(content, verb);
                if (path == "scripts/verify-linux.ps1" && verb == "test")
                {
                    Assert.Single(Regex.Matches(command, @"--logger"));
                }
                else
                {
                    Assert.DoesNotContain("--logger", command, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void StageCommand_ignores_comment_shadow_above_wrapped_command()
    {
        var content = """
            # dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror }
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", command);
    }

    [Fact]
    public void StageCommand_comment_shadow_does_not_hide_real_command_drift()
    {
        var baseline = """
            # dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror }
            """;
        var candidate = """
            # dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary }
            """;

        var baselineCommand = StageCommand(baseline, "build");
        var candidateCommand = StageCommand(candidate, "build");

        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", baselineCommand);
        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary", candidateCommand);
        Assert.NotEqual(baselineCommand, candidateCommand);
    }

    [Fact]
    public void StageCommand_ignores_trailing_comment_outside_windows_wrapper()
    {
        var content = """
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror } # it's fine
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", command);
        Assert.DoesNotContain("it's fine", command, StringComparison.Ordinal);
    }

    [Fact]
    public void StageCommand_ignores_trailing_comment_outside_linux_wrapper()
    {
        var content = """
            Invoke-GateStage -Name 'build' -Arguments @($gateClone) -Lines @(
                'dotnet build SqlHarness.sln --no-restore -warnaserror'
            ) # it's fine
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", command);
        Assert.DoesNotContain("it's fine", command, StringComparison.Ordinal);
    }

    [Fact]
    public void StageCommand_extracts_full_command_when_quoted_argument_contains_closing_brace()
    {
        var content = """
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --property:Foo="a}b" }
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln --property:Foo=\"a}b\"", command);
    }

    [Fact]
    public void StageCommand_preserves_windows_single_quoted_filter()
    {
        var content = """
            Invoke-Stage -Name 'test' -Action { dotnet test --filter 'FullyQualifiedName!~Integration' }
            """;

        var command = StageCommand(content, "test");

        Assert.Equal("dotnet test --filter \"FullyQualifiedName!~Integration\"", command);
    }

    [Fact]
    public void StageCommand_drops_trailing_unquoted_comment_from_yaml_run()
    {
        var content = """
            steps:
              - run: dotnet build SqlHarness.sln # note
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln", command);
        Assert.DoesNotContain("# note", command, StringComparison.Ordinal);
    }

    [Fact]
    public void The_extractor_returns_the_expected_commands_for_the_real_gate_files()
    {
        var windows = File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1"));
        var linux = File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

        Assert.Equal("dotnet restore SqlHarness.sln", StageCommand(windows, "restore"));
        Assert.Equal("dotnet restore SqlHarness.sln", StageCommand(linux, "restore"));
        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", StageCommand(windows, "build"));
        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", StageCommand(linux, "build"));
        Assert.Equal("dotnet test SqlHarness.sln --no-build --filter \"FullyQualifiedName!~Integration\"", StageCommand(windows, "test"));
        Assert.Equal("dotnet test SqlHarness.sln --no-build --filter \"FullyQualifiedName!~Integration\" --logger \"console;verbosity=normal\"", StageCommand(linux, "test"));
        Assert.Equal("dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes", FormatShape(StageCommand(windows, "format")));
        Assert.Equal("dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes", FormatShape(StageCommand(linux, "format")));
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_scopes_the_format_check_to_changed_files(string path)
    {
        var command = StageCommand(File.ReadAllText(RepositoryFile.Locate(path.Split('/'))), "format");

        Assert.Contains("--include", command, StringComparison.Ordinal);
        Assert.Contains("--verify-no-changes", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_routes_stage_output_through_the_shared_gate_output(string path)
    {
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

        Assert.Contains("gate-output.ps1", content, StringComparison.Ordinal);
        Assert.Contains("Invoke-GateStage", content, StringComparison.Ordinal);
        Assert.Contains("Get-GateSummaryLine", content, StringComparison.Ordinal);
        Assert.Contains("Get-StageFailureText", content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_gate_checks_the_files_the_working_tree_changed()
    {
        var content = File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1"));

        Assert.Contains("format-changed.ps1", content, StringComparison.Ordinal);
        Assert.Contains("-ListOnly", content, StringComparison.Ordinal);
        Assert.DoesNotContain("-Mode branch", content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_linux_gate_checks_the_files_the_branch_changed_because_the_clone_only_holds_commits()
    {
        // The gate clone is reset to the branch tip, so a working-tree diff there is
        // always empty. Scope the check to what the branch changed over origin/main.
        var content = File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

        Assert.Contains("format-changed.ps1", content, StringComparison.Ordinal);
        Assert.Contains("-Mode branch", content, StringComparison.Ordinal);
        Assert.Contains("-ListOnly", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_still_checks_formatting_over_the_whole_solution()
    {
        var content = File.ReadAllText(RepositoryFile.Locate(".github", "workflows", "ci.yml"));

        Assert.Equal("dotnet format --no-restore --verify-no-changes", StageCommand(content, "format"));
    }

    [Fact]
    public void The_justfile_runs_the_gates_and_can_rewrite_formatting()
    {
        var justfile = File.ReadAllText(RepositoryFile.Locate("justfile"));

        Assert.Matches(@"(?m)^verify\b", justfile);
        Assert.Matches(@"(?m)^verify-linux\b", justfile);
        Assert.Matches(@"(?m)^fix\b", justfile);
        Assert.Contains("scripts/verify.ps1", justfile, StringComparison.Ordinal);
        Assert.Contains("scripts/verify-linux.ps1", justfile, StringComparison.Ordinal);
        Assert.Contains("scripts/format-changed.ps1 -Fix", justfile, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verification_contract_uses_the_justfile_rather_than_the_raw_scripts()
    {
        var verification = ReadRepositoryFile("docs", "agents", "verification.md");

        Assert.Contains("just verify", verification, StringComparison.Ordinal);
        Assert.Contains("just verify-linux", verification, StringComparison.Ordinal);
        Assert.Contains("just fix", verification, StringComparison.Ordinal);
    }

    private static readonly string[] UiCommands =
    [
        "npm ci --prefix src/SqlHarness.Dashboard/ui",
        "npm run check --prefix src/SqlHarness.Dashboard/ui",
    ];

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Every_gate_installs_and_checks_the_ui_before_restore(string path)
    {
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));
        var restore = content.IndexOf("dotnet restore", StringComparison.Ordinal);

        Assert.True(restore > 0, "the gate must restore");
        foreach (var command in UiCommands)
        {
            var index = content.IndexOf(command, StringComparison.Ordinal);
            Assert.True(index >= 0, $"{path} must run '{command}'");
            Assert.True(index < restore, $"{path} must run '{command}' before dotnet restore");
        }
    }

    [Fact]
    public void Ci_and_release_take_node_from_nvmrc()
    {
        foreach (var path in new[] { ".github/workflows/ci.yml", ".github/workflows/release.yml" })
        {
            var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));
            Assert.Contains("actions/setup-node@", content, StringComparison.Ordinal);
            Assert.Contains("node-version-file: .nvmrc", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Linux_gate_puts_the_provisioned_node_first_and_checks_its_version()
    {
        var content = File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

        Assert.Contains("$HOME/.node/current/bin", content, StringComparison.Ordinal);
        Assert.Contains("cat .nvmrc", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_gate_rejects_a_windows_interop_node_with_a_setup_hint()
    {
        // Without a provisioned Linux node, WSL interop resolves node.exe from the Windows PATH
        // (/mnt/...). It prints its version with CRLF, so a plain comparison would report a
        // misleading "differs from .nvmrc" for the same version.
        var content = File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

        Assert.Contains("command -v node", content, StringComparison.Ordinal);
        Assert.Contains("/mnt/*", content, StringComparison.Ordinal);
        Assert.Contains("No Linux node on PATH", content, StringComparison.Ordinal);
    }

    private const string VerbGroup = @"(?<verb>restore|build|test|format)";
    private const string Body = @"(?:[^""'\}\r\n]|""[^""]*""|'[^']*')*";

    // The bash array expansion "${changedFiles[@]}" is a double-quoted token, which
    // the Linux body already accepts; single-quoted bash lines are literal here.
    private const string LinuxBody = @"(?:[^""'\r\n]|""[^""]*"")*";
    private const string YamlBody = @"(?:[^""'\r\n]|""[^""]*""|'[^']*')*";

    internal static string FormatShape(string command) =>
        Regex.Replace(
            command,
            "--include\\s+(?:\"\\$\\{changedFiles\\[@\\]\\}\"|\\$changedFiles)",
            "--include $changedFiles");

    private static string ReadRepositoryFile(params string[] parts) =>
        File.ReadAllText(RepositoryFile.Locate(parts));

    [Fact]
    public void FormatShape_hides_only_the_expansion_syntax_of_the_include_list()
    {
        var windows = "dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes";
        var linux = "dotnet format SqlHarness.sln --no-restore --include \"${changedFiles[@]}\" --verify-no-changes";

        Assert.Equal(windows, FormatShape(linux));
    }

    [Fact]
    public void FormatShape_keeps_drift_in_the_format_flags_visible()
    {
        var windows = "dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes";
        var linux = "dotnet format SqlHarness.sln --no-restore --include \"${changedFiles[@]}\"";

        Assert.NotEqual(windows, FormatShape(linux));
    }

    internal static string[] StageVerbs(string content)
    {
        var pattern = $@"(?:\{{\s*dotnet {VerbGroup}\b{Body}\s*\}})|(?:'dotnet {VerbGroup}\b{LinuxBody}')|(?:(?m)^\s*(?:-\s+)?run:\s*dotnet {VerbGroup}\b{YamlBody})";
        return Regex.Matches(content, pattern)
            .Select(match => match.Groups["verb"].Value)
            .ToArray();
    }

    internal static string StageCommand(string content, string verb)
    {
        var pattern = $@"(?:\{{\s*(?<command>dotnet {verb}\b{Body})\s*\}})|(?:'(?<command>dotnet {verb}\b{LinuxBody})')|(?:(?m)^\s*(?:-\s+)?run:\s*(?<command>dotnet {verb}\b{YamlBody}))";

        var match = Regex.Match(content, pattern);
        if (match.Success)
        {
            var command = Regex.Replace(match.Groups["command"].Value, @"\s+#.*$", "");
            return NormalizeCommand(command);
        }

        Assert.Fail($"No 'dotnet {verb}' command was found.");
        return null!;
    }

    private static string NormalizeCommand(string command) =>
        command.Trim().Replace('\'', '"');

    private static string FilterValue(string content) =>
        Regex.Match(content, @"--filter\s+['""]?(?<value>[^'""\s]+)")
            .Groups["value"]
            .Value;
}