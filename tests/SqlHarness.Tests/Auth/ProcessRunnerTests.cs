using System.Diagnostics;
using SqlHarness.Core.Auth;

namespace SqlHarness.Tests.Auth;

public sealed class ProcessRunnerTests
{
    [Fact]
    public void ProcessAbstractions_AreInternal()
    {
        Assert.False(typeof(ProcessResult).IsPublic);
        Assert.False(typeof(IProcessRunner).IsPublic);
        Assert.False(typeof(ProcessRunner).IsPublic);
    }

    [Fact]
    public async Task RunAsync_CancellationTerminatesEntireProcessTree()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var pidFile = Path.Combine(Path.GetTempPath(), $"sqlharness-{Guid.NewGuid():N}.pid");
        using var cancellation = new CancellationTokenSource();
        var runner = new ProcessRunner();
        var command = $"Set-Content -LiteralPath '{pidFile}' -Value $PID; while ($true) {{ Start-Sleep -Seconds 1 }}";

        try
        {
            var run = runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-Command", command],
                cancellationToken: cancellation.Token);
            var childPid = await WaitForPidAsync(pidFile);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(await WaitForExitAsync(childPid), "Canceled process remained alive.");
        }
        finally
        {
            if (File.Exists(pidFile))
                File.Delete(pidFile);
        }
    }

    private static async Task<int> WaitForPidAsync(string path)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path), out var pid))
                return pid;

            await Task.Delay(25);
        }

        throw new TimeoutException("Child process did not publish its process id.");
    }

    private static async Task<bool> WaitForExitAsync(int pid)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }
}
