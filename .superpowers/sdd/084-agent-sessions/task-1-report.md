# Plan 084 Task 1 implementation report

Status: implementation complete on `plan-084`; ready for task review.

## What changed

- `TranscriptMatcher` now upserts Codex and OpenCode conversation identities from `agent_session_id` before transcript reads, so links do not depend on index cursor timing or available read budget. Codex index entries still enrich titles; a bounded, one-time backfill per unresolved identity scans from the beginning when the saved cursor could have passed its entry.
- Added `RunTargetedAsync(IReadOnlyCollection<long>, CancellationToken)` and the held-connection overload used by the host. Targeted cycles select at most 500 pending operation IDs and limit Claude file reads to their pending-call cwds; Codex index reads are restricted to those operation IDs. Full sweeps continue to inspect all pending cwds and index entries.
- `DashboardHost` now checks its existing held journal connection every 250 ms while matching is enabled, debounces external `data_version` changes for 1.5 seconds, and keeps a separate 60-second full sweep. Retention remains on its own interval. It compares pending operation IDs around retention so a new pending call committed during retention is not hidden by retention's post-write data-version baseline.
- Extracted `JournalDataVersion.Read` for both the host observer and LiveFeed; each retains its own held connection and matcher-owned commits remain invisible to the host's observer connection.
- Added cursor-timing, Codex/OpenCode identity, fake-clock debounce, and 60-second sweep coverage. Existing 32 MiB cycle-budget and idle-retention tests remain in the focused test set.
- No schema or journal-floor changes; the branch remains at schema version 19.

## TDD evidence

RED command:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcher|FullyQualifiedName~DashboardHousekeeping"
```

The new `Codex_links_without_index_timing` test failed as expected: after the index cursor had advanced, no link existed for the later operation (`Assert.Single() Failure: The collection did not contain any matching items`). Suite output: `Failed: 1, Passed: 28, Skipped: 0`.

GREEN command, after implementation and again after formatting:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcher|FullyQualifiedName~DashboardHousekeeping"
```

Final output: `Passed! - Failed: 0, Passed: 31, Skipped: 0, Duration: 6 s`.

The fake-clock housekeeping tests confirm no targeted link at 1.0 seconds, a link after the 1.5-second debounce, and the full sweep still running at 60 seconds. `Reads_more_than_one_cycle_budget_across_multiple_cycles` passed in the same filter.

## Other checks

- `pwsh ./scripts/format-changed.ps1 -Mode working-tree -Fix` → `format: OK`.
- `pwsh ./scripts/format-changed.ps1 -Mode working-tree` → `format: OK`.
- `git diff --check` passed.
- The full `just verify` gate was not run; final Task 6 owns it.

## Files changed

- `.superpowers/sdd/084-agent-sessions/task-1-report.md`
- `plans/084-agent-sessions.md`
- `plans/README.md`
- `src/SqlHarness.Dashboard/DashboardHost.cs`
- `src/SqlHarness.Dashboard/JournalDataVersion.cs`
- `src/SqlHarness.Dashboard/LiveFeed.cs`
- `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs`
- `tests/SqlHarness.Tests/Dashboard/DashboardHousekeepingTests.cs`
- `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs`

## Self-review and concerns

The implementation keeps matcher writes on the host's observer connection, scopes targeted Claude reads to the pending operations' cwds, bounds targeted IDs at 500, and leaves periodic full scans in place for recovery and title refresh. Existing idle-shutdown and retention tests pass. No unresolved concerns identified. No merge, push, or live database mutation was performed.

## Round 1 review fixes

Review findings and regression coverage:

- RED: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~A_targeted_codex_backfill|FullyQualifiedName~Journal_commit_between_version_sample"` failed both regressions. The backfill test expected `Older title` but got null after a budget-exhausted cycle followed by a targeted cursor restart. The retention-window test timed out waiting for the targeted link after a journal commit between the initial data-version sample and pending-operation snapshot.
- GREEN: the same two-test filter passed `2/2` after the fixes.
- Final focused command: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcher|FullyQualifiedName~DashboardHousekeeping"` passed `33/33`, 0 skipped, duration 7 seconds.

The restarted backfill now includes all pending and unresolved null-title identities in its wanted set, and marks only identities considered by a completed scan. Successful and retained-away identities are pruned from the unresolved bookkeeping. The host samples `data_version` before and after the pending-operation snapshot; a change at either boundary refreshes idle activity and starts the debounce before retention advances the observer baseline. A test hook makes that snapshot window deterministic in housekeeping tests.

No other Task 1 behavior changed; the full `just verify` gate remains reserved for Task 6.
