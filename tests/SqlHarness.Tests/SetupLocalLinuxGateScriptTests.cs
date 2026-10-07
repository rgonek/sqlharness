namespace SqlHarness.Tests;

public sealed class SetupLocalLinuxGateScriptTests
{
    private static string Script() => File.ReadAllText(RepositoryFile.Locate("scripts", "setup-linux-gate.ps1"));
    private static string VerifyScript() => File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

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
        Assert.Contains("grep -qxF \"# $bashrc_marker\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_never_writes_sqlharness_home_or_targets_json()
    {
        var script = Script();

        Assert.DoesNotContain("SQLHARNESS_HOME", script, StringComparison.Ordinal);
        Assert.DoesNotContain("targets.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_scripts_validate_the_wsl_home_probe_before_using_it()
    {
        var setupScript = Script();
        var verifyScript = VerifyScript();

        Assert.Contains("[string]::IsNullOrWhiteSpace($wslHome)", setupScript, StringComparison.Ordinal);
        Assert.Contains("[string]::IsNullOrWhiteSpace($wslHome)", verifyScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_verifies_the_chosen_distro_is_wsl2()
    {
        var script = Script();

        Assert.Contains("wsl -l -v", script, StringComparison.Ordinal);
        Assert.Contains("VERSION", script, StringComparison.Ordinal);
        Assert.Contains("version 2 is required", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_rejects_gate_clones_on_unsupported_filesystems()
    {
        var script = Script();

        Assert.True(
            script.Contains("findmnt", StringComparison.Ordinal) || script.Contains("df -T", StringComparison.Ordinal),
            "Expected findmnt or df -T filesystem probe.");
        Assert.Contains("unsupported filesystem", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_compares_installed_dotnet_version_before_skipping_sdk_install()
    {
        var script = Script();

        Assert.Contains("\"$dotnet_dir/dotnet\" --version", script, StringComparison.Ordinal);
        Assert.Contains("\"$installed_version\" = \"$sdk_version\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_probes_ca_certificates_before_installing_dependencies()
    {
        var script = Script();

        Assert.Contains("dpkg -s ca-certificates", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_apt_invocations_are_strictly_non_interactive()
    {
        var script = Script();

        Assert.DoesNotContain("sudo apt-get", script, StringComparison.Ordinal);
        Assert.Contains("sudo -n apt-get", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_provisions_the_node_version_pinned_by_nvmrc_with_a_verified_checksum()
    {
        var script = Script();

        Assert.Contains(".nvmrc", script, StringComparison.Ordinal);
        Assert.Contains("https://nodejs.org/dist/v$node_version/", script, StringComparison.Ordinal);
        Assert.Contains("SHASUMS256.txt", script, StringComparison.Ordinal);
        Assert.Contains("sha256sum -c", script, StringComparison.Ordinal);
        Assert.Contains("$HOME/.node/current", script, StringComparison.Ordinal);
        Assert.Contains("xz-utils", script, StringComparison.Ordinal);
    }
}