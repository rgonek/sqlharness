using System.Text.RegularExpressions;

namespace SqlHarness.Tests;

public sealed class SetupLocalPostgresScriptTests
{
    [Fact]
    public void Setup_script_exists_with_fixed_playground_contract()
    {
        var script = File.ReadAllText(FindRepositoryFile("scripts", "setup-local-postgres.ps1"));

        Assert.Contains("sqlharness-pg", script, StringComparison.Ordinal);
        Assert.Contains("sqlharness-pg-data", script, StringComparison.Ordinal);
        Assert.Contains("5433", script, StringComparison.Ordinal);
        Assert.Contains("SQLHARNESS_PG_PLAYGROUND_PASSWORD", script, StringComparison.Ordinal);
        Assert.Contains("postgres:16", script, StringComparison.Ordinal);
        Assert.Contains("pagila", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_isready", script, StringComparison.Ordinal);
        Assert.Contains("engine", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("postgres", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Setup_script_never_writes_targets_json()
    {
        var script = File.ReadAllText(FindRepositoryFile("scripts", "setup-local-postgres.ps1"));

        Assert.DoesNotMatch(
            @"(?im)(Set-Content|Out-File|Add-Content).{0,120}targets\.json",
            script);
        Assert.DoesNotMatch(
            @"(?im)targets\.json.{0,120}(Set-Content|Out-File|Add-Content)",
            script);
    }

    [Fact]
    public void Setup_script_redacts_password_env_in_docker_args()
    {
        var script = File.ReadAllText(FindRepositoryFile("scripts", "setup-local-postgres.ps1"));

        Assert.Contains("Get-RedactedDockerArgs", script, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_PASSWORD=***", script, StringComparison.Ordinal);
        Assert.Contains("PGPASSWORD=***", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"Write-Host[^\r\n]*\$env:SQLHARNESS_PG_PLAYGROUND_PASSWORD"),
            script);
    }

    [Fact]
    public void Setup_script_drops_incomplete_pagila_database_after_failed_restore()
    {
        var script = File.ReadAllText(FindRepositoryFile("scripts", "setup-local-postgres.ps1"));

        Assert.Contains("DROP DATABASE IF EXISTS", script, StringComparison.Ordinal);
        Assert.Contains("Remove-IncompletePagilaDatabase", script, StringComparison.Ordinal);
        Assert.Contains("$databaseCreated", script, StringComparison.Ordinal);
        Assert.Contains("$restoreCompleted", script, StringComparison.Ordinal);
        Assert.Matches(
            @"(?s)\$databaseCreated\s*=\s*\$true.*?\$restoreCompleted\s*=\s*\$true.*?if\s*\(\s*\$databaseCreated\s*-and\s*-not\s*\$restoreCompleted\s*\)",
            script);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not locate repository file: {string.Join('/', path)}");
    }
}