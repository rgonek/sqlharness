# Final dashboard test synchronization fix

## Review finding

> `tests/SqlHarness.Tests/Dashboard/DashboardHousekeepingTests.cs:509-513` — The second title append is not synchronized before the clock advances to the final sweep. Once `title-1` is observed, the preceding sweep's `Housekept` callback may already have incremented `passCount` to two. In that schedule, `WaitForAsync(passCount >= 2)` returns immediately after `appendRequested` is armed, and the next loop iteration at the two-minute deadline runs the matcher before `Housekept`. It exits for idle shutdown without appending or committing `title-2`, reproducing a timing-dependent failure in the test intended to repair the gate. Ensure the second transcript update is actually appended before advancing to the final sweep, for example by invoking the fixture append explicitly after observing `title-1`, or by waiting for a new callback beyond a captured pass count while advancing the fake clock enough to trigger it.

## Fix

The test waits for the initial matcher scan to finish before appending `title-1`. It then advances to the next 60-second sweep and observes the committed `title-1`. After that observation it synchronously appends `title-2` before advancing to the final sweep at the original two-minute idle deadline. The test asserts that `title-2` was committed before the host exits. Its cancellation and await in `finally` remain in place.

Only `DashboardHousekeepingTests.cs` changes behavior in this fix; no product code changed.

## Verification

Command:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~DashboardHousekeepingTests --logger "console;verbosity=normal"
```

Output summary: `Test Run Successful. Total tests: 17. Passed: 17. Failed: 0. Total time: 11.7861 seconds.`

No full gate or formatting command was run; both remain for the controller.
