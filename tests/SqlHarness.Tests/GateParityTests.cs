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

        Assert.Equal(windows, linux);
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
        Assert.Equal("dotnet format SqlHarness.sln --no-restore --verify-no-changes", StageCommand(windows, "format"));
        Assert.Equal("dotnet format SqlHarness.sln --no-restore --verify-no-changes", StageCommand(linux, "format"));
    }

    private const string VerbGroup = @"(?<verb>restore|build|test|format)";
    private const string Body = @"(?:[^""'\}\r\n]|""[^""]*""|'[^']*')*";
    private const string LinuxBody = @"(?:[^""'\r\n]|""[^""]*"")*";
    private const string YamlBody = @"(?:[^""'\r\n]|""[^""]*""|'[^']*')*";

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