# Task 1 report

Base: a2cfff69, branch plan-125-evidence-clarity. Scope: eligibility and operation failure diagnostics. No live DB, publish, restart, merge or push.

## Changes

- Added shared per-variant eligibility: recorded succeeded status, variant hash and at least one stored metric; policy verdict does not gate it. Summary allocates versions only for eligible variants; same-group, across-group, direct and equivalence paths filter using the same predicate. Frozen labels remain reserved.
- Failed/rejected operations prominently display the existing describeError sentence and stored message as text, or state that no message was recorded. Failed compare details use the regular detail view so retained variants remain accessible.
- Tests cover five non-success statuses, a failed policy with frozen labels, missing measurements, interrupted compare override, cancellation descriptions, unknown failure, text escaping, and retained variant display.
- Two pre-existing across-group tests constructed snapshots with zero metrics. Fixtures now retain one recorded metric on a non-matching/ambiguous case; their original assertions still check no cross-condition pairing or missing unique-case rows, respectively. This is necessary because fully unmeasured versions are no longer eligible.

## Verification and red evidence

- Initial baseline: system dotnet lacked pinned SDK; selected existing LOCALAPPDATA SDK 10.0.303. UI worktree initially had no dependencies. Baseline backend build ran while first tests were added: it reported 155 existing passing tests and 5 intended failures, not a standalone clean baseline.
- RED backend: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --no-restore --filter 'FullyQualifiedName~JournalReaderTests|FullyQualifiedName~OptimizationSummaryBuilderTests|FullyQualifiedName~OptimizationComparerTests'`: five statuses failed because bad0/bad1 became v0/v1 and valid hashes became v2/v3; 155 passed.
- RED UI: `npm test -- --run src/pages/OperationPage.test.tsx src/components/StatusBadge.test.tsx`: three cancellation reasons absent, 44 passed.
- Initial implementation backend run: 163 passed, 2 failed because above existing fixtures had no measurement. Adapted them without removing their core checks.
- GREEN backend same command: 167 passed, 0 failed.
- GREEN UI same command: 48 passed, 0 failed.
- `git diff --check`: passed. Full `just verify` deferred to Task 6.

## Setup incident and recovery

The first backend build implicitly launched npm ci against a worktree node_modules junction pointing at the main checkout. Concurrent UI baseline failed with missing vitest and happy-dom files, demonstrating impact to main dependencies. Stopped build; ran npm ci independently in both main and this worktree (512 packages each, success), removed shared junction through npm's replacement, and used SkipDashboardUi=true on backend runs. Main tracked status checked after repair: only its pre-existing plans/README.md modification and untracked plan 125. Both lockfile installs reported 9 npm audit findings (4 low, 1 moderate, 3 high, 1 critical); no audit fixes made.

## Self-review and limitations

Checked eligibility is shared by both summary and comparison, respects status over policy, preserves frozen ordinals, and leaves journal evidence untouched. Cross-group missing-case fixtures retain unknown-row assertions. React renders stored message via text nodes; no HTML injection. No new JournalReader integration test was added; existing JournalReader tests passed and retained-variant behavior is covered via UI synthetic fixture. Predicate's handling of historical all-null metric records merits integration review. Focused backend and UI evidence does not substitute for Task 6 final gate or browser verification.
- `npm run typecheck`: passed.

Commit: c96731130bda654a8fd29bea0ae478925c9859f3. Tracked worktree clean after commit.


## Review fixes round 1 (base c9673113)

- Eligibility no longer treats statement identity/text/plan metadata as a measurement: a confirmed statement needs a recorded counter or runtime value. Zero is accepted and ambiguous statement values do not qualify. JournalReader no longer turns an empty object collection into zero reads/scans.
- Cancelled status now participates in the badge dialog and prominent operation explanation; absent errorKind uses cancelled description. Rejected and missing-message behavior are explicitly covered.
- Added status-only no-pair tests for all five statuses, a direct pair with failed policy, a newer metadata-only attempt that cannot displace a valid pair, journal-retained failed attempt/history/metrics/detail tests, and UI badge boundaries. No live DB was accessed; journal tests use isolated temporary synthetic SQLite.
- RED backend: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --no-restore --filter 'FullyQualifiedName~JournalReaderTests|FullyQualifiedName~OptimizationSummaryBuilderTests|FullyQualifiedName~OptimizationComparerTests'` failed 1, passed 174: metadata-only row created a subject. Separate filter `FullyQualifiedName~Statement_without_object_counters_does_not_invent_zero_reads` failed 1: expected null, actual 0.
- RED UI: `npm test -- --run src/pages/OperationPage.test.tsx src/components/StatusBadge.test.tsx` failed 2, passed 49: cancelled detail reason absent and cancelled badge had no dialog button.
- GREEN backend: same three-class command passed 177, failed 0. GREEN UI: same two-file command passed 51, failed 0. `npm run typecheck` passed. `git diff --check` passed.
- Execution setup for subsequent tasks: `$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"` selects pinned SDK 10.0.303. Worktree has its own private `node_modules`, installed from its lockfile; do not use junctions. Set `-p:SkipDashboardUi=true` for focused backend tests to prevent implicit UI install/build.
- Self-review: confirmed non-success statuses cannot create pair evidence, success with failed policy remains eligible, history and retained detail remain inspectable, and metadata cannot supply numbers. Full gate remains Task 6.
Fix commit: 32f75a9385e8cb7ce2bb76c4014185cacae6d978.
