using System.Text.RegularExpressions;

namespace SqlHarness.Tests;

public sealed class VerifyLinuxScriptTests
{
    private static string Script() => File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

    [Fact]
    public void Linux_gate_runs_the_three_ci_stages_in_order()
    {
        // Distinct, because the test stage runs one command per test project and both spell
        // the same verb. GateParityTests pins the order of the distinct verbs for all three
        // surfaces; this one is the cheap local check that the bash block really holds them.
        var stages = Regex.Matches(Script(), @"\bdotnet\s+(restore|build|test|format)\b")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();

        Assert.Equal(["build", "test", "format"], stages);
    }

    [Fact]
    public void Linux_gate_uses_the_shared_stage_commands()
    {
        var script = Script();

        Assert.DoesNotContain("dotnet restore", script, StringComparison.Ordinal);
        Assert.Contains("dotnet build SqlHarness.sln -warnaserror", script, StringComparison.Ordinal);
        // One command per test project, because the stage starts them all at once. The
        // solution is not named: that is the form that runs the projects one after another.
        Assert.DoesNotContain("dotnet test SqlHarness.sln", script, StringComparison.Ordinal);
        Assert.Contains(
            "'dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter \"FullyQualifiedName!~Integration\" --logger \"console;verbosity=normal\" &',",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-build --filter \"FullyQualifiedName!~Integration\" --logger \"console;verbosity=normal\" &',",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet format SqlHarness.sln --no-restore --include \"${changedFiles[@]}\" --verify-no-changes",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_gate_names_the_failing_project_of_its_parallel_test_stage()
    {
        // Both projects write into one stage log, so the ==> lines and the stage-failure:
        // marker are the only thing that tell a reader which project broke. The marker names
        // the project whose exit code the script returns, which is what
        // Get-ReportedFailureStage turns into the summary line.
        //
        // Every assertion reads the test stage's OWN block, never the whole script. `set +e`
        // appears in the build stage too, so a whole-script search is satisfied by the build
        // stage's copy and says nothing about this one - and without `set +e` here the block
        // still runs under `set -euo pipefail`, so the first non-zero `wait` aborts the script
        // before the second project is joined and before either marker is printed.
        var script = Script();
        var testStage = script.IndexOf("Invoke-LinuxStage -Name 'test'", StringComparison.Ordinal);
        Assert.True(testStage > 0, "the Linux gate must run a test stage");

        // Cut at the next stage, so no assertion below can be answered by another stage's
        // bash. The cut is asserted rather than assumed: the stage after this one is format,
        // so a block that still held it would carry `dotnet format`, and an extraction that
        // quietly returned the whole file would fail this rather than passing every search
        // vacuously. The script-level assertion is what stops the pair from passing for the
        // wrong reason - a stage that stopped existing entirely must not look like a clean cut.
        var block = script.Substring(testStage);
        var next = block.IndexOf("Invoke-LinuxStage -Name", 1);
        if (next > 0) { block = block.Substring(0, next); }

        Assert.Contains("dotnet format", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet format", block, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(block, @"'set \+e',"));

        // Both projects are joined before either exit code is read: one wait per pid, and
        // `set -e` restored after them so the markers below are reached only once.
        Assert.Equal(2, Regex.Matches(block, @"'wait ""\$\w+_pid""").Count);
        Assert.Contains("'set -e',", block, StringComparison.Ordinal);

        // The ==&gt; lines and the stage-failure: marker each name one project, and the marker
        // is written only by the project whose exit code the script returns.
        Assert.Contains("'if [ \"$tests_status\" -ne 0 ]; then echo \"==> tests/SqlHarness.Tests FAILED (exit $tests_status)\" >&2; fi',", block, StringComparison.Ordinal);
        Assert.Contains("'if [ \"$mcp_status\" -ne 0 ]; then echo \"==> tests/SqlHarness.Mcp.Tests FAILED (exit $mcp_status)\" >&2; fi',", block, StringComparison.Ordinal);
        Assert.Contains("'if [ \"$tests_status\" -ne 0 ]; then echo \"stage-failure: tests/SqlHarness.Tests\" >&2; exit \"$tests_status\"; fi',", block, StringComparison.Ordinal);
        Assert.Contains("'if [ \"$mcp_status\" -ne 0 ]; then echo \"stage-failure: tests/SqlHarness.Mcp.Tests\" >&2; exit \"$mcp_status\"; fi',", block, StringComparison.Ordinal);
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