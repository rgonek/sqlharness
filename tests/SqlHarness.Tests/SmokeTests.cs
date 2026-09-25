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
        Assert.Contains("space", standardOutput, StringComparison.Ordinal);
        Assert.Contains("watch", standardOutput, StringComparison.Ordinal);
        Assert.Contains("snapshot", standardOutput, StringComparison.Ordinal);
        Assert.Contains("qstop", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qstop_help_lists_bounds_without_mutation()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("qstop", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("--top", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--window", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--timeout", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--json", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--allow-mutation", standardOutput, StringComparison.Ordinal);
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
    public async Task Space_help_lists_top_and_object_options()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("space", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("--top", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--object", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--json", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--timeout", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ping_help_lists_timeout_option()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("ping", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains("--timeout", standardOutput, StringComparison.Ordinal);
        Assert.Contains("--json", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Measure_help_distinguishes_param_set_from_param_and_matrix()
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
        Assert.Contains("compare --matrix", help, StringComparison.Ordinal);
        Assert.Contains("single set", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one parameter", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("two query variants", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fresh connection", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one session", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Compare_help_keeps_matrix_contract()
    {
        var (exit, standardOutput, standardError) = await RunCliAsync("compare", "--help");

        Assert.True(exit == 0, standardError);
        Assert.Contains(
            "One matrix dimension only; at least two typed values; sequential user-supplied order. A new connection and one setup per value. The first failure stops the run. Completed cell artifacts remain. Ticket SQL stays outside the application repository",
            Flatten(standardOutput),
            StringComparison.Ordinal);
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