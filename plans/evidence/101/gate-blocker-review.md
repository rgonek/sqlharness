### Spec Compliance

- ❌ Issues found: the saved publisher error is lost if error-marker `Add-Content` fails with a non-`IOException`; the nested catch rethrows the append error, so the original diagnostic does not reach the parent process (`tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs:48`). This misses the brief's original-error preservation requirement on that failure path.
- ⚠️ Cannot verify from diff: the reported RED/GREEN runs, focused test results, and formatter result are claims in `gate-blocker-report.md`; they were not independently rerun in this review as instructed. The full Windows gate is explicitly outside this task.

### Strengths

The change stays within the test helper and adds no `ProcessRunner` production edits. It retries only transient `IOException`s with a 50-attempt, 100 ms bound, saves the caught publisher error before retrying, and rethrows it after successful marker publication (`tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs:39-51`). The new Windows regression uses a publisher barrier and a read handle that permits reads while denying writes, so it holds the demonstrated sharing conflict across the append attempt (`tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs:164-205`).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs:48` — If `Add-Content` fails with a non-`IOException`, the nested `catch` throws that append failure instead of preserving `$publisherError`. The helper then reports the marker-write problem and drops the original publisher diagnostic, contrary to the brief. Preserve the original error in the surfaced error while still reporting the marker-write failure.

#### Minor (Nice to Have)

None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The deterministic lock and bounded retry directly address the demonstrated transient sharing violation, and the normal success path preserves the original publisher error. The unhandled non-sharing append-error path can still erase the diagnostic this helper exists to retain, so the explicit preservation requirement is not fully met.
