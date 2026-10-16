using System.Text.RegularExpressions;

namespace SqlHarness.Tests;

public sealed class VerifyLinuxScriptTests
{
    private static string Script() => File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

    [Fact]
    public void Linux_gate_runs_the_three_ci_stages_in_order()
    {
        var stages = Regex.Matches(Script(), @"\bdotnet\s+(restore|build|test|format)\b")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(["build", "test", "format"], stages);
    }

    [Fact]
    public void Linux_gate_uses_the_shared_stage_commands()
    {
        var script = Script();

        Assert.DoesNotContain("dotnet restore", script, StringComparison.Ordinal);
        Assert.Contains("dotnet build SqlHarness.sln -warnaserror", script, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test SqlHarness.sln --no-build --filter \"FullyQualifiedName!~Integration\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet format SqlHarness.sln --no-restore --include \"${changedFiles[@]}\" --verify-no-changes",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_gate_exports_the_sdk_path_inline_and_never_sources_a_shell_rc_file()
    {
        var script = Script();

        Assert.Contains("export PATH=", script, StringComparison.Ordinal);
        Assert.Contains(".dotnet", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*(?:source|\.)\s+\S*(?:bashrc|profile)", script);
    }

    [Fact]
    public void Linux_gate_names_the_failing_command_of_its_coupled_build_stage()
    {
        // The build stage runs the UI check and the build in one bash script. Both
        // failures are echoed into the log so a red stage names its cause, but only the
        // command whose exit code the script returns writes `stage-failure:` - without
        // that, a UI-only failure is reported as "stage=build" carrying the UI check's
        // exit code.
        var script = Script();
        var buildStage = script.IndexOf("Invoke-LinuxStage -Name 'build'", StringComparison.Ordinal);
        Assert.True(buildStage > 0, "the Linux gate must run a build stage");

        Assert.Contains("'if [ \"$build_status\" -ne 0 ]; then echo \"stage-failure: build\" >&2; exit \"$build_status\"; fi',", script, StringComparison.Ordinal);
        Assert.Contains("'if [ \"$ui_check_status\" -ne 0 ]; then echo \"stage-failure: ui-check\" >&2; exit \"$ui_check_status\"; fi',", script, StringComparison.Ordinal);
        Assert.Contains("Get-ReportedFailureStage -Name $Name -Result $result", script, StringComparison.Ordinal);
        Assert.Contains("-FailureStage $reported", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_gate_syncs_the_gate_clone_and_never_writes_this_working_tree()
    {
        var script = Script();

        Assert.Contains("git fetch", script, StringComparison.Ordinal);
        Assert.Contains("--git-common-dir", script, StringComparison.Ordinal);
        Assert.Contains("git reset --hard FETCH_HEAD", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)Set-Content|Out-File", script);
        Assert.DoesNotContain("SQLHARNESS_HOME", script, StringComparison.Ordinal);

        var removals = Regex.Matches(script, @"(?m)Remove-Item[^\r\n]*").Select(match => match.Value).ToArray();
        Assert.All(removals, removal => Assert.Contains("$stageFile", removal, StringComparison.Ordinal));
    }

    [Fact]
    public void Linux_gate_delivers_bash_as_an_lf_file_and_never_through_a_pipe()
    {
        var script = Script();

        Assert.Contains("New-TemporaryFile", script, StringComparison.Ordinal);
        Assert.Contains("-replace \"`r`n\", \"`n\"", script, StringComparison.Ordinal);
        Assert.Contains("UTF8Encoding", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\|\s*&\s*wsl", script);
    }

    [Fact]
    public void Linux_gate_normalises_windows_path_separators_before_wslpath()
    {
        var script = Script();

        Assert.Contains(@"-replace '\\', '/'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_gate_names_the_failing_stage_and_propagates_its_exit_code()
    {
        var script = Script();

        Assert.Contains("Get-GateSummaryLine -Gate 'verify-linux'", script, StringComparison.Ordinal);
        Assert.Contains("Get-StageLine -Result $result", script, StringComparison.Ordinal);
        Assert.Matches(@"\$LASTEXITCODE", script);
        Assert.Matches(@"(?m)^\s*exit \$result\.ExitCode\s*$", script);
    }
}