using System.Diagnostics;

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
    public async Task Root_help_lists_read_only_helpers()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("ping", standardOutput, StringComparison.Ordinal);
        Assert.Contains("counts", standardOutput, StringComparison.Ordinal);
        Assert.Contains("schema", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Counts_help_lists_exact_and_selection_options()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("counts", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("--exact", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--table", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--like", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--top", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema_help_lists_object_option()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("schema", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("--object", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--filter", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ping_help_lists_timeout_option()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("ping", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("--timeout", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--json", standardOutput, StringComparison.Ordinal);
    }

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