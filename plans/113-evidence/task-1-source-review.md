# Plan 113 Task 1: landed behavior and evidence

Inspected source revision: `51ee23bcc1dcc44355c1a364fbfd6584c0a06dcd` (branch `plan-113`).

## Plan 076 implementation evidence

- `src/SqlHarness.Core/CompareMatrixRunner.cs`: `CompareMatrixCellFailedException` accepts and retains `completedRuns`; `RunAsync` appends measured artifacts only after a cell completes, tags each with its zero-based matrix cell, and passes the accumulated runs when a later cell fails. The failing cell does not append a run, and the sequential loop throws before later values start.
- `src/SqlHarness.Core/SqlHarnessModule.cs`: the failed-matrix outcome is built with `failed.CompletedRuns` (source references at lines 779 and 797); its helper maps those runs to `BenchmarkRuns` (line 1168).
- `src/SqlHarness.Core/Journal/JournalingModule.cs:115`: completed failed outcomes with nonempty `BenchmarkRuns` are sent to the journal builder.
- Historical observation `docs/observations/partial-compare-journal-coverage.md` records Plan 076 as resolving completed-cell journal retention: completed cells are journaled and the failing cell contributes no metrics. It retains the earlier observed event and explicitly says the access-denied cause was not found.
- `git show --stat 57a1e7d` identifies the Plan 076 task commit as `Test journaling of partial compare runs`, adding `tests/SqlHarness.Tests/Journal/JournalingBenchmarkTests.cs`. The present test `Failed_exit_outcome_with_completed_runs_is_journaled` asserts metrics are written for a failed outcome that carries completed matrix runs. This is an actual checked-in implementation/test, not ancestry-only evidence.

## Current artifact publication retry

At `src/SqlHarness.Core/ArtifactDirectoryPublisher.cs`, `Publish` creates staging, calls the writer once, and calls `PublishStaging`. The retry loop catches only `IOException` where all are true: Windows OS; `HResult == 0x80070005`; fewer than three retries have already occurred (`attempt < 3`); staging still exists; final directory does not exist. It sleeps 100 ms and retries only `_moveDirectory(staging, directory)`. It does not repeat `writeStaging`, SQL execution, or artifact creation. After the initial attempt this permits at most three retries (four move attempts total). If the predicate stops matching or attempts are exhausted, the original exception propagates to `Publish`, which attempts cleanup while preserving the primary error.

Existing tests in `tests/SqlHarness.Tests/ArtifactDirectoryPublisherTests.cs`:

- Windows-only `when_windows_directory_move_is_temporarily_denied_then_publish_reuses_staging`: first move throws the matching HRESULT, second move succeeds; asserts two move attempts, one staging write, and the published marker.
- Windows-only `when_windows_directory_move_stays_denied_then_publish_stops_and_cleans_up`: matching denial persists; asserts the same exception instance, four attempts, and empty storage after cleanup.
- `Move_failure_leaves_no_final_directory_and_preserves_the_original` and `Move_that_publishes_then_throws_removes_the_final_directory` cover nonmatching move failure and cleanup/error preservation.

No tests were run for this inspection task; Task 2 owns focused execution. These fixtures use an injected move delegate and a temporary filesystem. They verify deterministic synthetic conditions, not a live database, installed binary, real external file handle, or recurrence of the historical event.

## Historical event and review-focus assessment

The preserved 2026-10-15 observation says a three-cell comparison completed two cells, then returned `local_storage_failed` after an access-denied rename of the third cell's `.staging-<guid>` directory. The two prior cell artifacts were returned and readable, but the operation had no journal metrics/statements/table-IO rows. The observation states that the failing cell had no final directory, cleanup ran, and the loaded binary was not proven identical to repository HEAD. Its suspected external process/indexer/antivirus handle is explicitly a hypothesis; cause remains unresolved. Plan 076 resolved completed-cell journal retention, not the cause of the rename denial.

Plan 113 review focus applicability:

1. **Legacy/unavailable fields:** not exercised by this publisher retry path; no artifact schema or metric availability is interpreted here.
2. **Identities:** applicable at the operation boundary. The retry is for the same staging and final paths, and the 076 code tags completed runs with the exact zero-based matrix cell. No parameter values are added to journal identity.
3. **Budgets:** no diagnostic/output budget is changed or measured by this task. Retry count and delay are finite and unchanged.
4. **Errors/negative results:** applicable. The persistent-denial fixture requires four attempts and cleanup while preserving the original exception; completed cells survive separately in the partial result/journal path.
5. **Synthetic versus live:** applicable. The publisher tests are injected synthetic filesystem failures; this evidence does not claim live DB acceptance, installed-binary behavior, external-process attribution, or a reproduced historical cause.

## Drift scan

`git diff --name-status 677b2f93 51ee23bc --` reported no changes in the following five reviewed paths:

- `src/SqlHarness.Core/CompareMatrixRunner.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Core/Journal/JournalingModule.cs`
- `src/SqlHarness.Core/ArtifactDirectoryPublisher.cs`
- `tests/SqlHarness.Tests/ArtifactDirectoryPublisherTests.cs`

The broader seven-path command also included the two current matrix/journal test files and reported no changed paths. No production behavior was edited in this task.
