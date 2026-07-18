using System.Diagnostics;

using SqlHarness.Core.Auth;

namespace SqlHarness.Tests.Auth;

public sealed class ProcessRunnerTests
{
    private const int PidPublicationWaitAttempts = 300;

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

        var testDirectory = Path.Combine(Path.GetTempPath(), $"sqlharness-{Guid.NewGuid():N}");
        var pidFile = Path.Combine(testDirectory, "processes.pid");
        var childScript = Path.Combine(testDirectory, "child.ps1");
        var parentScript = Path.Combine(testDirectory, "parent.ps1");
        Directory.CreateDirectory(testDirectory);
        await File.WriteAllTextAsync(childScript, "while ($true) { Start-Sleep -Seconds 1 }");
        await File.WriteAllTextAsync(
            parentScript,
            "Set-Content -LiteralPath $args[0] -Value $PID\n" +
            "$child = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $args[1]) -PassThru\n" +
            "Add-Content -LiteralPath $args[0] -Value $child.Id\n" +
            "while ($true) { Start-Sleep -Seconds 1 }");
        using var cancellation = new CancellationTokenSource();
        var runner = new ProcessRunner();
        int[] processIds = [];
        Task<ProcessResult>? run = null;

        try
        {
            run = runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", parentScript, pidFile, childScript],
                cancellationToken: cancellation.Token);
            processIds = await WaitForPidsAsync(pidFile);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            foreach (var pid in processIds)
            {
                Assert.True(
                    await WaitForExitAsync(pid),
                    $"Canceled process {pid} remained alive.");
            }
        }
        finally
        {
            cancellation.Cancel();
            if (run is not null)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            processIds = processIds
                .Concat(await ReadPublishedPidsAsync(pidFile))
                .Distinct()
                .ToArray();
            foreach (var pid in processIds)
                KillIfAlive(pid);

            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WaitForPidsAsync_WaitsWhilePublisherOwnsFileExclusively()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlharness-pids-{Guid.NewGuid():N}.pid");

        try
        {
            Task<int[]> wait;
            await using (var publisher = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                wait = WaitForPidsAsync(path);
                await Task.Yield();
            }

            await File.WriteAllLinesAsync(path, ["123", "456"]);
            var publishedPids = await wait;

            Assert.Equal([123, 456], publishedPids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WaitForPidsAsync_ToleratesSlowPublisher()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlharness-pids-{Guid.NewGuid():N}.pid");

        try
        {
            var wait = WaitForPidsAsync(path);
            await Task.Delay(TimeSpan.FromSeconds(8));
            await File.WriteAllLinesAsync(path, ["123", "456"]);

            var publishedPids = await wait;

            Assert.Equal([123, 456], publishedPids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<int[]> ReadPublishedPidsAsync(string path)
    {
        if (!File.Exists(path))
            return [];

        return (await File.ReadAllLinesAsync(path))
            .Select(line => int.TryParse(line, out var pid) ? pid : 0)
            .Where(pid => pid > 0)
            .ToArray();
    }

    private static async Task<int[]> WaitForPidsAsync(string path)
    {
        for (var attempt = 0; attempt < PidPublicationWaitAttempts; attempt++)
        {
            if (File.Exists(path))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(path);
                    if (lines.Length == 2 && lines.All(line => int.TryParse(line, out _)))
                        return lines.Select(int.Parse).ToArray();
                }
                catch (IOException)
                {
                    // The PowerShell publisher may still own the file exclusively.
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Process tree did not publish both process ids.");
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

    private static void KillIfAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
    }
}