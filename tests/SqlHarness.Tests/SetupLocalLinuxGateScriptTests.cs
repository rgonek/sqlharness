namespace SqlHarness.Tests;

public sealed class SetupLocalLinuxGateScriptTests
{
    private static string Script() => File.ReadAllText(RepositoryFile.Locate("scripts", "setup-linux-gate.ps1"));

    [Fact]
    public void Setup_script_installs_the_sdk_version_pinned_by_global_json_instead_of_hard_coding_one()
    {
        var script = Script();

        Assert.Contains("global.json", script, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json", script, StringComparison.Ordinal);
        Assert.DoesNotContain("9.0.316", script, StringComparison.Ordinal);
        Assert.Contains("dotnet-install.sh", script, StringComparison.Ordinal);
        Assert.Contains("--version \"$sdk_version\"", script, StringComparison.Ordinal);
        Assert.Contains("--install-dir \"$dotnet_dir\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_clones_the_gate_onto_a_case_sensitive_filesystem_and_proves_it()
    {
        var script = Script();

        Assert.Contains("src/sqlharness-gate", script, StringComparison.Ordinal);
        Assert.Contains("core.autocrlf false", script, StringComparison.Ordinal);
        Assert.Contains("case-probe", script, StringComparison.Ordinal);
        Assert.Contains("${probe^^}", script, StringComparison.Ordinal);
        Assert.Contains("case-insensitive filesystem", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_is_idempotent_and_never_blocks_on_interactive_input()
    {
        var script = Script();

        Assert.Contains("sudo -n true", script, StringComparison.Ordinal);
        Assert.Contains("-d \"$gate_clone/.git\"", script, StringComparison.Ordinal);
        Assert.Contains("grep -qF \"$bashrc_marker\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_never_writes_sqlharness_home_or_targets_json()
    {
        var script = Script();

        Assert.DoesNotContain("SQLHARNESS_HOME", script, StringComparison.Ordinal);
        Assert.DoesNotContain("targets.json", script, StringComparison.Ordinal);
    }
}