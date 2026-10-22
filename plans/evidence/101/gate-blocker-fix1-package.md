diff --git a/.superpowers/sdd/101-dashboard-operation-annotations-setting/ProcessRunnerTests.cs.round1-review.base b/tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs
index 5a6fb87..95fbacc 100644
--- a/.superpowers/sdd/101-dashboard-operation-annotations-setting/ProcessRunnerTests.cs.round1-review.base
+++ b/tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs
@@ -27,6 +27,7 @@ public sealed class ProcessRunnerTests
         $ErrorActionPreference = 'Stop'
         try {
             Set-Content -LiteralPath $args[0] -Value $PID
+            if ($args.Count -gt 6 -and $args[6] -eq 'synthetic-error') { throw 'Original publisher failure.' }
             $child = Start-Process -FilePath $args[3] -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $args[1] + '"')) -PassThru -WindowStyle Hidden -RedirectStandardError $args[2]
             $published = $false
             for ($i = 0; $i -lt 50 -and -not $published; $i++) {
@@ -45,7 +46,15 @@ public sealed class ProcessRunnerTests
             $errorPublished = $false
             for ($i = 0; $i -lt 50 -and -not $errorPublished; $i++) {
                 try { Add-Content -LiteralPath $args[0] -Value $errorLine -ErrorAction Stop; $errorPublished = $true }
-                catch { if ($_.Exception -isnot [System.IO.IOException]) { throw }; Start-Sleep -Milliseconds 100 }
+                catch {
+                    $markerWriteError = $_.Exception
+                    if ($markerWriteError -isnot [System.IO.IOException]) {
+                        $failureMessage = 'Could not publish the original publisher error marker. Original publisher error: ' + $publisherError.Exception.Message + '. Marker write failure (' + $markerWriteError.GetType().FullName + '): ' + $markerWriteError.Message
+                        $failures = [System.Exception[]]@($publisherError.Exception, $markerWriteError)
+                        throw [System.AggregateException]::new($failureMessage, $failures)
+                    }
+                    Start-Sleep -Milliseconds 100
+                }
             }
             if (-not $errorPublished) { throw ('Could not publish the original publisher error: ' + $publisherError.Exception.Message) }
             throw $publisherError
@@ -204,6 +213,47 @@ public sealed class ProcessRunnerTests
         }
     }
 
+    [WindowsFact]
+    public async Task PidPublicationError_PreservesOriginalWhenMarkerWriteFails()
+    {
+        var testDirectory = Path.Combine(Path.GetTempPath(), $"sqlharness test-{Guid.NewGuid():N}");
+        var pidFile = Path.Combine(testDirectory, "processes.pid");
+        var childScript = Path.Combine(testDirectory, "child.ps1");
+        var parentScript = Path.Combine(testDirectory, "parent.ps1");
+        var childStandardError = Path.Combine(testDirectory, "child.stderr.log");
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
+                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", parentScript, pidFile, childScript, childStandardError, "unused-child.exe", errorCatchStarted, resumeErrorCatch, "synthetic-error"],
+                cancellationToken: cancellation.Token);
+
+            await WaitForFileAsync(errorCatchStarted);
+            File.Delete(pidFile);
+            Directory.CreateDirectory(pidFile);
+            await File.WriteAllTextAsync(resumeErrorCatch, "resume");
+
+            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
+            Assert.Equal(1, result.ExitCode);
+            Assert.Contains("Original publisher failure.", result.StandardError);
+            Assert.Contains("Marker write failure", result.StandardError);
+            Assert.Contains("UnauthorizedAccessException", result.StandardError);
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
