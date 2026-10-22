 tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs | 74 ++++++++++++++++++++++-
 1 file changed, 71 insertions(+), 3 deletions(-)
diff --git a/tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs b/tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs
index df13775..5a6fb87 100644
--- a/tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs
+++ b/tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs
@@ -8,42 +8,54 @@ namespace SqlHarness.Tests.Auth;
 public sealed class ProcessRunnerTests
 {
     // Poll budget for the publisher handshake (480 x 25 ms = 12 s). Slow PowerShell
     // start under parallel-suite load is tolerated; a publisher-side failure still
     // surfaces fast via the ERROR line below, so this budget only prices true hangs.
     private const int PidPublicationWaitAttempts = 480;
     private const int PidPublicationWaitDelayMs = 25;
     private const int DirectoryDeleteRetryAttempts = 10;
     private const int DirectoryDeleteRetryDelayMs = 100;
 
-    // Publisher contract, args: [0]=pidFile, [1]=childScript, [2]=childStderr, [3]=childExe.
+    // Publisher contract, args: [0]=pidFile, [1]=childScript, [2]=childStderr, [3]=childExe,
+    // with optional [4]=errorCatchStarted and [5]=resumeErrorCatch barriers for tests.
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
-            Add-Content -LiteralPath $args[0] -Value ('ERROR: ' + $_.Exception.Message)
-            throw
+            $publisherError = $_
+            if ($args.Count -gt 5) {
+                Set-Content -LiteralPath $args[4] -Value 'waiting'
+                while (-not (Test-Path -LiteralPath $args[5])) { Start-Sleep -Milliseconds 10 }
+            }
+            $errorLine = 'ERROR: ' + $publisherError.Exception.Message
+            $errorPublished = $false
+            for ($i = 0; $i -lt 50 -and -not $errorPublished; $i++) {
+                try { Add-Content -LiteralPath $args[0] -Value $errorLine -ErrorAction Stop; $errorPublished = $true }
+                catch { if ($_.Exception -isnot [System.IO.IOException]) { throw }; Start-Sleep -Milliseconds 100 }
+            }
+            if (-not $errorPublished) { throw ('Could not publish the original publisher error: ' + $publisherError.Exception.Message) }
+            throw $publisherError
         }
         """;
 
     [Fact]
     public void ProcessAbstractions_AreInternal()
     {
         Assert.False(typeof(ProcessResult).IsPublic);
         Assert.False(typeof(IProcessRunner).IsPublic);
         Assert.False(typeof(ProcessRunner).IsPublic);
     }
@@ -142,20 +154,63 @@ public sealed class ProcessRunnerTests
         finally
         {
             await CleanupTreeAsync(run, cancellation, [], pidFile, testDirectory);
         }
 
         Assert.False(
             Directory.Exists(testDirectory),
             "Publisher directory was not cleaned up after failure.");
     }
 
+    [WindowsFact]
+    public async Task PidPublicationError_RetriesErrorMarkerAfterTransientSharingViolation()
+    {
+        var testDirectory = Path.Combine(Path.GetTempPath(), $"sqlharness test-{Guid.NewGuid():N}");
+        var pidFile = Path.Combine(testDirectory, "processes.pid");
+        var childScript = Path.Combine(testDirectory, "child.ps1");
+        var parentScript = Path.Combine(testDirectory, "parent.ps1");
+        var childStandardError = Path.Combine(testDirectory, "child.stderr.log");
+        var missingChildExe = Path.Combine(testDirectory, "missing-child.exe");
+        var errorCatchStarted = Path.Combine(testDirectory, "error-catch-started");
+        var resumeErrorCatch = Path.Combine(testDirectory, "resume-error-catch");
+        Directory.CreateDirectory(testDirectory);
+        await File.WriteAllTextAsync(childScript, "while ($true) { Start-Sleep -Seconds 1 }");
+        await File.WriteAllTextAsync(parentScript, ParentScriptTemplate);
+        using var cancellation = new CancellationTokenSource();
+        var runner = new ProcessRunner();
+        Task<ProcessResult>? run = null;
+
+        try
+        {
+            run = runner.RunAsync(
+                "powershell.exe",
+                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", parentScript, pidFile, childScript, childStandardError, missingChildExe, errorCatchStarted, resumeErrorCatch],
+                cancellationToken: cancellation.Token);
+
+            await WaitForFileAsync(errorCatchStarted);
+            using (var publisherLock = new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.Read))
+            {
+                await File.WriteAllTextAsync(resumeErrorCatch, "resume");
+                await Task.Delay(250);
+            }
+
+            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
+                () => WaitForPidsAsync(pidFile, childStandardError, run));
+            Assert.Contains("ERROR", exception.Message);
+            Assert.Contains("line(s)", exception.Message);
+        }
+        finally
+        {
+            await CleanupTreeAsync(run, cancellation, [], pidFile, testDirectory);
+        }
+    }
+
     [Fact]
     public async Task WaitForPidsAsync_WaitsWhilePublisherOwnsFileExclusively()
     {
         var path = Path.Combine(Path.GetTempPath(), $"sqlharness-pids-{Guid.NewGuid():N}.pid");
 
         try
         {
             Task<int[]> wait;
             await using (var publisher = new FileStream(
                 path,
@@ -354,20 +409,33 @@ public sealed class ProcessRunnerTests
         }
 
         throw new TimeoutException(
             $"Process tree did not publish both process ids after {PidPublicationWaitAttempts} attempts." +
             Environment.NewLine +
             DescribePidFile(path) + Environment.NewLine +
             DescribeRunTask(run) + Environment.NewLine +
             DescribeChildStandardError(childStandardErrorPath));
     }
 
+    private static async Task WaitForFileAsync(string path)
+    {
+        for (var attempt = 0; attempt < 200; attempt++)
+        {
+            if (File.Exists(path))
+                return;
+
+            await Task.Delay(25);
+        }
+
+        throw new TimeoutException($"Publisher did not create the synchronization file: {path}");
+    }
+
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
