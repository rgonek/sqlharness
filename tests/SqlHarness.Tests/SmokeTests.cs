using System.Diagnostics;

namespace SqlHarness.Tests;

public sealed class SmokeTests
{
    [Fact]
    public async Task VersionOptionSucceedsAndEmitsVersion()
    {
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "sqlharness.dll");
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{cliAssembly}\" --version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        Assert.NotNull(process);
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, standardError);
        Assert.Matches(@"\d+\.\d+\.\d+", standardOutput);
    }
}