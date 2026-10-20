# Dashboard housekeeping gate repair

## Finding

The earlier Plan 093 final gate reported `Matcher_commits_do_not_keep_an_idle_dashboard_up` failing because `agent_conversations.title` was still null. The diagnosis traced this to treating completed housekeeping callbacks as matcher scans: callbacks run every 250 ms with matching enabled, while full matcher scans run on the 60-second sweep. The title records were appended after a matcher scan, so callback counts did not prove that an eligible later scan had consumed them. No product defect was established.

The first revised focused run exposed another fixture race: while the test waited at the two-minute boundary, every ordinary callback kept appending titles, so the final database title was `title-3` rather than the phase-specific `title-2`. The hook now appends only when the test arms one update, keeping housekeeping callbacks separate from matcher-cycle evidence.

## Repair and coverage

`Matcher_commits_do_not_keep_an_idle_dashboard_up` now observes ordered phases: the initial operation link, the first post-scan title append, the next 60-second sweep's committed `title-1`, one explicitly armed second append, and the final 60-second sweep at the original two-minute idle deadline. It verifies the dashboard remains alive after the first matcher-owned commit, exits at the original deadline, and has committed `title-2` before exit. This retains evidence of multiple matcher-owned updates without requiring callback counts to stand in for scans.

The host is started with a cancellation token and the test cancels and awaits it in `finally`, so failed assertions do not leave a dashboard running.

## Verification

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~DashboardHousekeepingTests --logger "console;verbosity=normal"` — initial revised attempt: failed at the final title assertion (`expected title-2`, `actual title-3`), exposing repeated appends from ordinary housekeeping callbacks.
- The same focused command after arming exactly one append per observed phase — passed, 17/17 tests.
- `git diff --check` — passed before commit.
- Full `just verify` was not run; the controller owns the combined gate after review.

No matcher or idle-shutdown product code changed. The previous gate's null-title failure and this test's ordered committed-title observations support the synchronization diagnosis; they do not establish a full-gate result.
