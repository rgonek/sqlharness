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

    private static string[] StageVerbs(string content) =>
        Regex.Matches(content, @"\bdotnet\s+(restore|build|test|format)\b")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static string StageCommand(string content, string verb)
    {
        var match = Regex.Match(content, @"'?\bdotnet\s+" + verb + @"\b[^\r\n]*'?");

        Assert.True(match.Success, $"No 'dotnet {verb}' command was found.");
        var normalized = match.Value.Trim();

        if (normalized.Length >= 2 && normalized[0] == '\'' && normalized[^1] == '\'')
        {
            normalized = normalized[1..^1].Trim();
        }

        if (normalized.EndsWith('}'))
        {
            normalized = normalized[..^1].Trim();
        }

        return normalized.Replace('\'', '"');
    }

    private static string FilterValue(string content) =>
        Regex.Match(content, @"--filter\s+['""]?(?<value>[^'""\s]+)")
            .Groups["value"]
            .Value;
}