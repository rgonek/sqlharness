using System.Diagnostics;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class SmokeTests
{
    [Fact]
    public async Task VersionOptionSucceedsAndEmitsVersion()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("--version");

        Assert.True(exit == 0, standardError);
        Assert.Matches(@"\d+\.\d+\.\d+", standardOutput);
    }

    [Fact]
    public async Task Root_help_lists_watch_and_snapshot()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("watch", standardOutput, StringComparison.Ordinal);
        Assert.Contains("snapshot", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_help_lists_stop_and_bound_options()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("watch", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("--until", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--until-unchanged", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--interval", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--max-duration", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--file", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--json", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_help_explains_error_text_cell_limit()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("query", "--help");

        Assert.True(exit == 0, standardError);
        var help = Flatten(standardOutput);
        Assert.Contains(AgentOutputLimits.CellCharactersDescription, help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_help_lists_name_diff_and_force_options()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("snapshot", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("--name", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--diff", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--force", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--file", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--json", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Measure_help_exposes_parameter_set_and_benchmark_options()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("measure", "--help");

        Assert.True(exit == 0, standardError);
        var help = Flatten(standardOutput);
        Assert.Contains("--query", help, StringComparison.Ordinal);
        Assert.Contains("--setup", help, StringComparison.Ordinal);
        Assert.Contains("--repeat", help, StringComparison.Ordinal);
        Assert.Contains("--timeout", help, StringComparison.Ordinal);
        Assert.Contains("--json", help, StringComparison.Ordinal);
        Assert.Contains("--json-summary", help, StringComparison.Ordinal);
        Assert.Contains("--param-set", help, StringComparison.Ordinal);
        Assert.Contains("--param", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_help_exposes_matrix_and_equivalence_options()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("compare", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("--matrix", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--compare-results", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--setup", standardOutput, StringComparison.Ordinal);
    }

    private static string Flatten(string text) =>
        string.Join(' ', text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(params string[] args)
    {
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "sqlharness.dll");
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{cliAssembly}\" {string.Join(' ', args)}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        Assert.NotNull(process);
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardOutput, standardError);
    }
}
