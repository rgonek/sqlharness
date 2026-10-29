# Task 4 report

Implemented shared version table across condition groups with scope union, group/version identity, A/B URL state and return links, stale selection recovery, row counts, approximate case rows and copied summary, and selected-version SQL/plan access. Removed replaced group selector and its obsolete tests. Approximate comparisons without paired cases suppress metric totals; VersionPlan selects a retained action case for `all` when no paired cases exist. Exact same-group verdict logic remains in place.

Files: OptimizationSection, VersionTable, ComparisonPanel, VersionPlan, SessionVersionDiff, router, sessionSearch and optimizationMarkdown; corresponding focused tests in four optimization test files.

RED: `npm --prefix src/SqlHarness.Dashboard/ui run test -- OptimizationSection VersionTable ComparisonPanel VersionPlan` produced 6 expected failures and 28 passes before production edits (missing shared table, selection identity/stale B, row counts, approximate rows, plan fallback). Initial attempt before dependency restore failed because vitest was absent.

GREEN: `npm run test -- OptimizationSection VersionTable ComparisonPanel VersionPlan` from UI directory passed 35 tests in 4 files, 72.88s. `npm run typecheck` passed (exit 0). `git diff --check` passed. One redundant fetch stub was removed after this run; the affected OptimizationSection suite was rerun separately.

Self-review: tests cover duplicate version labels in groups, group B URL, stale B and endpoint 404 recovery, scope union, zero-pair plan and absent totals, approximate row evidence and missing counts. The SQL diff uses selected A/B options so duplicated labels can select different hashes. Return links carry groupB. Full `just verify`, broad UI suite and browser acceptance deferred to Task 5. No DB writes, merge, push, publication or installation. An incomplete local npm directory `src/SqlHarness.Dashboard/ui/node_modules-incomplete-123` remains self-ignored; its cleanup command was rejected with `blocked by policy`, no reason supplied. The clean install finished successfully.

Post-cleanup rerun: `npm run test -- OptimizationSection -t "stale group B resets to the selected group defaults"` passed the entire OptimizationSection file: 15 passed. npm parsed `-t` as config and ran the file rather than filtering it. Commit: `41cf268b`.

## Task 4 review fix, round 1

Commit `06043305` preserves groupB only when a candidate B exists, and retains a validated explicit A when invalid B or a 404 clears the candidate. Added cross-group nondefault A with unset B, invalid-B and 404 A-preservation assertions, and distinct missing-row/explicit-varies assertions.

RED: `npm run test -- OptimizationSection VersionTable` in the UI directory: 2 failed, 19 passed, demonstrating lost nondefault A and cleared valid A. GREEN: same command: 21 passed in 2 files (36.77s), including the 404 assertion. `git diff --check` passed. No signature changed; typecheck was not rerun. Full gate and browser remain Task 5.
