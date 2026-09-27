using System.Diagnostics;

using SqlHarness.Core.Auth;

namespace SqlHarness.Tests.Auth;

public sealed class ProcessRunnerTests
{
    // Poll budget for the publisher handshake (480 x 25 ms = 12 s). Slow PowerShell
    // start under parallel-suite load is tolerated; a publisher-side failure still
    // surfaces fast via the ERROR line below, so this budget only prices true hangs.
    private const int PidPublicationWaitAttempts = 480;
    private const int PidPublicationWaitDelayMs = 25;

    // Publisher contract, args: [0]=pidFile, [1]=childScript, [2]=childStderr, [3]=childExe.
    // The child -File path is embedded in quotes because Windows PowerShell 5.1
    // Start-Process joins -ArgumentList without quoting, which silently breaks on
    // temp paths containing spaces (child dies on arrival, tree assertions vacuous).
    // The append retry matters too: the test polls the pid file while the publisher
    // appends, and a share-read open would otherwise fail Add-Content, leaving exactly
    // one published line and a bare publication timeout.
    private const string ParentScriptTemplate = """
        $ErrorActionPreference = 'Stop'
        try {
            Set-Content -LiteralPath $args[0] -Value $PID
            $child = Start-Process -FilePath $args[3] -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $args[1] + '"')) -PassThru -WindowStyle Hidden -RedirectStandardError $args[2]
            $published = $false
            for ($i = 0; $i -lt 50 -and -not $published; $i++) {
                try { Add-Content -LiteralPath $args[0] -Value $child.Id -ErrorAction Stop; $published = $true }
                catch { if ($_.Exception -isnot [System.IO.IOException]) { throw }; Start-Sleep -Milliseconds 100 }
            }
            if (-not $published) { throw 'Child process id could not be published.' }
            while ($true) { Start-Sleep -Seconds 1 }
        } catch {
            Add-Content -LiteralPath $args[0] -Value ('ERROR: ' + $_.Exception.Message)
            throw
        }
        """;

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

        // The space in the directory name is deliberate: it pins the quoting fix.
        var testDirectory = Path.Combine(Path.GetTempPath(), $"sqlharness test-{Guid.NewGuid():N}");
        var pidFile = Path.Combine(testDirectory, "processes.pid");
        var childScript = Path.Combine(testDirectory, "child.ps1");
        var parentScript = Path.Combine(testDirectory, "parent.ps1");
        var childStandardError = Path.Combine(testDirectory, "child.stderr.log");
        Directory.CreateDirectory(testDirectory);
        await File.WriteAllTextAsync(childScript, "while ($true) { Start-Sleep -Seconds 1 }");
        await File.WriteAllTextAsync(parentScript, ParentScriptTemplate);
        using var cancellation = new CancellationTokenSource();
        var runner = new ProcessRunner();
        int[] processIds = [];
        Task<ProcessResult>? run = null;

        try
        {
            run = runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", parentScript, pidFile, childScript, childStandardError, "powershell.exe"],
                cancellationToken: cancellation.Token);
            processIds = await WaitForPidsAsync(pidFile, childStandardError, run);

            foreach (var pid in processIds)
            {
                Assert.True(
                    IsProcessAlive(pid),
                    $"Published process {pid} was already dead; tree-termination assertion would be vacuous.");
            }

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
            await CleanupTreeAsync(run, cancellation, processIds, pidFile, testDirectory);
        }
    }

    [Fact]
    public async Task PidPublicationError_ReportsDiagnosticsAndCleansUp()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var testDirectory = Path.Combine(Path.GetTempPath(), $"sqlharness test-{Guid.NewGuid():N}");
        var pidFile = Path.Combine(testDirectory, "processes.pid");
        var childScript = Path.Combine(testDirectory, "child.ps1");
        var parentScript = Path.Combine(testDirectory, "parent.ps1");
        var childStandardError = Path.Combine(testDirectory, "child.stderr.log");
        var missingChildExe = $"sqlharness-missing-{Guid.NewGuid():N}.exe";
        Directory.CreateDirectory(testDirectory);
        await File.WriteAllTextAsync(childScript, "while ($true) { Start-Sleep -Seconds 1 }");
        await File.WriteAllTextAsync(parentScript, ParentScriptTemplate);
        using var cancellation = new CancellationTokenSource();
        var runner = new ProcessRunner();
        Task<ProcessResult>? run = null;

        try
        {
            run = runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", parentScript, pidFile, childScript, childStandardError, missingChildExe],
                cancellationToken: cancellation.Token);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitForPidsAsync(pidFile, childStandardError, run));
            Assert.Contains("ERROR", exception.Message);
            Assert.Contains("line(s)", exception.Message);
        }
        finally
        {
            await CleanupTreeAsync(run, cancellation, [], pidFile, testDirectory);
        }

        Assert.False(
            Directory.Exists(testDirectory),
            "Publisher directory was not cleaned up after failure.");
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
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"sqlharness-pids-{Guid.NewGuid():N}.pid");

        try
        {
            var wait = WaitForPidsAsync(path);
            await Task.Delay(TimeSpan.FromSeconds(4));
            await File.WriteAllLinesAsync(path, ["123", "456"]);

            var publishedPids = await wait;

            Assert.Equal([123, 456], publishedPids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task CleanupTreeAsync(
        Task<ProcessResult>? run,
        CancellationTokenSource cancellation,
        int[] knownProcessIds,
        string pidFile,
        string testDirectory)
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

        var processIds = knownProcessIds
            .Concat(await ReadPublishedPidsAsync(pidFile))
            .Distinct()
            .ToArray();
        foreach (var pid in processIds)
            KillIfAlive(pid);

        if (Directory.Exists(testDirectory))
            Directory.Delete(testDirectory, recursive: true);
    }

    private static Task<int[]> ReadPublishedPidsAsync(string path) =>
        Task.FromResult(
            (TryReadPidLines(path) ?? [])
            .Select(line => int.TryParse(line, out var pid) ? pid : 0)
            .Where(pid => pid > 0)
            .ToArray());

    private static async Task<int[]> WaitForPidsAsync(
        string path,
        string? childStandardErrorPath = null,
        Task<ProcessResult>? run = null)
    {
        for (var attempt = 0; attempt < PidPublicationWaitAttempts; attempt++)
        {
            var lines = TryReadPidLines(path);
            if (lines is not null)
            {
                if (lines.Length == 2 && lines.All(line => int.TryParse(line, out _)))
                    return lines.Select(int.Parse).ToArray();

                if (lines.Any(line => line.StartsWith("ERROR", StringComparison.Ordinal)))
                    throw new InvalidOperationException(
                        "Process tree publisher reported an error." + Environment.NewLine +
                        DescribePidFileLines(lines) + Environment.NewLine +
                        DescribeRunTask(run) + Environment.NewLine +
                        DescribeChildStandardError(childStandardErrorPath));
            }

            await Task.Delay(PidPublicationWaitDelayMs);
        }

        throw new TimeoutException(
            $"Process tree did not publish both process ids after {PidPublicationWaitAttempts} attempts." +
            Environment.NewLine +
            DescribePidFile(path) + Environment.NewLine +
            DescribeRunTask(run) + Environment.NewLine +
            DescribeChildStandardError(childStandardErrorPath));
    }

    // Share-tolerant read: FileShare.ReadWrite so polling never denies the publisher's
    // append (a share-read open races Add-Content and silently drops the second line).
    // Returns null when the file is missing or momentarily locked exclusively.
    private static string[]? TryReadPidLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split(["\r\n", "\n"], StringSplitOptions.None);
            return lines is [.., ""] ? lines[..^1] : lines;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DescribePidFile(string path)
    {
        if (!File.Exists(path))
            return $"Pid file: not created at {path}.";

        var lines = TryReadPidLines(path);
        return lines is null
            ? $"Pid file: unreadable at {path} (locked or inaccessible)."
            : DescribePidFileLines(lines);
    }

    private static string DescribePidFileLines(string[] lines)
    {
        const int maxPreviewChars = 512;
        var preview = string.Join(" | ", lines.Take(8));
        if (preview.Length > maxPreviewChars)
            preview = preview[..maxPreviewChars] + "…";
        return $"Pid file: {lines.Length} line(s): {preview}.";
    }

    private static string DescribeRunTask(Task<ProcessResult>? run)
    {
        if (run is null)
            return "Publisher task: unknown (not provided).";
        if (run.IsCompletedSuccessfully)
            return $"Publisher task: {run.Status} (exit code {run.Result.ExitCode}).";
        if (run.IsFaulted)
            return $"Publisher task: Faulted ({run.Exception?.GetBaseException().Message}).";
        if (run.IsCanceled)
            return "Publisher task: Canceled.";
        return $"Publisher task: {run.Status} (still running).";
    }

    private static string DescribeChildStandardError(string? childStandardErrorPath)
    {
        if (childStandardErrorPath is null)
            return "Child stderr: no capture file configured.";
        try
        {
            if (!File.Exists(childStandardErrorPath))
                return $"Child stderr: file was not created at {childStandardErrorPath}.";

            const int maxTailChars = 4096;
            var content = File.ReadAllText(childStandardErrorPath);
            if (content.Length > maxTailChars)
                return $"Child stderr tail (truncated to {maxTailChars} chars): {content[^maxTailChars..]}";
            return $"Child stderr: {content}";
        }
        catch (Exception exception)
        {
            return $"Child stderr: unreadable at {childStandardErrorPath} ({exception.GetType().Name}: {exception.Message}).";
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
