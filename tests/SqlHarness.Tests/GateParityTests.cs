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

    internal static string[] StageVerbs(string content)
    {
        const string VerbGroup = "(restore|build|test|format)";
        var pattern = $@"(?:\{{ dotnet {VerbGroup}\b[^}}\r\n]*\}})|(?:'dotnet {VerbGroup}\b[^'\r\n]*')|(?:(?m)^\s*(?:-\s+)?run:\s*dotnet {VerbGroup}\b.*$)";
        return Regex.Matches(content, pattern)
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value)
            .ToArray();
    }

    internal static string StageCommand(string content, string verb)
    {
        var match = Regex.Match(content, $@"\{{\s*(dotnet {verb}\b[^}}\r\n]*)\s*\}}");
        if (match.Success)
        {
            return NormalizeCommand(match.Groups[1].Value);
        }

        match = Regex.Match(content, $@"'(dotnet {verb}\b[^'\r\n]*)'");
        if (match.Success)
        {
            return NormalizeCommand(match.Groups[1].Value);
        }

        match = Regex.Match(content, $@"(?m)^\s*(?:-\s+)?run:\s*(dotnet {verb}\b.*)$");
        if (match.Success)
        {
            return NormalizeCommand(match.Groups[1].Value);
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