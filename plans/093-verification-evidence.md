# Plan 093 verification evidence

## Status

Plan 093 is **IMPLEMENTED, REVIEW BLOCKED** on branch `plan-093`. Current product revision is `e7eaddec15fdac850ae4b00d714b59b932c9e8a8`. The Task 2 specification/quality review approved the earlier implementation before the final fix wave (`task-2-review.md`). Scoped Sol re-review marked the original starvation finding ADDRESSED but found a new Important performance issue in the fix. Under the one-final-fix-wave ruling, that finding is deferred to [Plan 102](102-bounded-table-identity-selection.md); this branch is not DONE or merge-ready. The final Windows gate failed at its CLI test stage, as detailed below.

## Revisions and changes

- Task implementation base: `4235ca3d5847e739fab31ccd2de2ef3bafafc1fe`.
- Branch formatting base: `fab299f2abfc76b5f8b2eee2b07c91735bbf7059`.
- `45dbd5c` preserves complete `LogicalReadsByTable` keys in the benchmark and artifact projections, adds CLI/MCP identity, budget and schema regressions, and documents runtime behavior.
- `4a58c15` commits the exact EOF formatting corrections produced by the branch formatter.
- `e7eadde` selects complete metric entries against the full containing projection, continuing after entries rejected by the budget; tests cover 3,005-, 3,505-, and 5,005-character highest-ranked names.

## Regression and suite results

Task 1's intentional RED is recorded in the [Task 1 report](../.superpowers/sdd/093-identity-safe-output-clipping/task-1-report.md): CLI had 9 duplicate-key failures with 130 passes; MCP had 2 duplicate-key failures with 41 passes. The failures came from clipping distinct keys before `ToDictionary`.

The original focused CLI command
`dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~AgentOutputTests|FullyQualifiedName~ArtifactReaderTests"`
passed 140/140. The original focused MCP command
`dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~McpTokenBudgetTests`
passed 43/43. On the current product revision, the covering CLI command passed 145/145 and the MCP command passed 45/45. Dedicated CLI compare and MCP compare boundary filters passed 3/3 each for 3,005-, 3,505-, and 5,005-character names.

Each affected full test project ran once on the current product revision:

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`: 4,583 passed, 29 skipped, 1 failed, 4,613 total, duration 2m36s. The failure was `DashboardHousekeepingTests.Matcher_commits_do_not_keep_an_idle_dashboard_up` (`matchedTitle` was null at `Assert.StartsWith("title-", matchedTitle)`). A narrow rerun of `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~Matcher_commits_do_not_keep_an_idle_dashboard_up` passed 1/1 in 952 ms. The full CLI suite ran concurrently with MCP; the isolated pass is consistent with timing sensitivity, but the cause is unproven.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj`: 467 passed, 4 skipped, 471 total, duration 2m39s.

## Bounded-output measurements and evidence limits

The following byte measurements are from deterministic fixtures on the pre-final-fix product tree `4a58c151c5ee3b0826a0781b52b7140e31d5f8e6`: CLI compare responses measured 1,203 bytes at `maxCellChars=0`, 1,223 at `4`, and 9,346 at `2048` (65,536-byte budget); artifact identity responses measured 527, 507, and 4,581 bytes; normal-input schema measured 638 bytes; MCP compare identity responses measured 1,345 bytes at zero and 1,369 at four characters (4,096-byte budget). The pre-final-fix 5,005-character table case emitted 1,272 bytes, retained the short median 29 on both compare sides, excluded the full long name and its clipped prefix, and counted two omitted entries. On final product `e7eadde`, the 3,005/3,505/5,005 highest-ranked cases verify later-fit selection and test the complete response cap, but no final byte measurements were logged.

These are in-process renderer/adapter fixture measurements: 10 CLI render calls and 3 MCP adaptation calls, with zero database executions and zero provider/paid-agent calls. They do not claim live database or actual-agent evidence. Additional measurements and the exact self-review are in the [Task 2 report](../.superpowers/sdd/093-identity-safe-output-clipping/task-2-report.md).

## Formatting and gate

- `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef fab299f2abfc76b5f8b2eee2b07c91735bbf7059 -Fix`: `format: OK`; log `D:/temp/plan093-branch-format-fix.log`.
- The first direct branch check exited 1 for final-newline formatting in three changed C# files. The formatter's corrections were committed as `4a58c15`.
- A subsequent direct check after adding final boundary rows exited 2 for final-newline diagnostics in the two changed test files; the formatter applied its exact corrections.
- Final direct branch check on `e7eadde` with the same base exited 0 and printed `format: OK`; log `D:/temp/sqlharness-plan093-format-fix-e7eadde.log`.
- `git diff --check` passed.
- An earlier gate on `4a58c151c5ee3b0826a0781b52b7140e31d5f8e6` passed (`verify: OK stages=5 12m04s`), but it is superseded by the review fix and is not final evidence.
- Final command `just verify -LogDirectory D:/temp/sqlharness-plan093-final-verify` on the current product tree `e7eadde` **failed** at `stage=test` with exit 1 after 7m39s. `ui-install` was skipped; build and UI check passed (`ui-check ok 4m54s`). MCP tests passed 467/4 skipped. CLI tests failed 4,577 passed/11 skipped/1 failed; the failure was `DashboardHousekeepingTests.Matcher_commits_do_not_keep_an_idle_dashboard_up`, where `matchedTitle` was null at `Assert.StartsWith("title-", matchedTitle)`. Exact failed-stage log: `D:/temp/sqlharness-plan093-final-verify/test-SqlHarness.Tests.log`; MCP log: `D:/temp/sqlharness-plan093-final-verify/test-SqlHarness.Mcp.Tests.log`. No rerun was made after inspecting these logs.

## Review findings and ruling

The whole-branch report is retained locally at `.superpowers/sdd/093-identity-safe-output-clipping/whole-review.md`. It found that a highest-ranked oversized identity could suppress later fitting entries. The fix in `e7eadde` is **ADDRESSED** per scoped Sol re-review: entry selection continues in significance order and tests retain `dbo.short` median 29 with truthful omissions for CLI compare, artifact metrics, and MCP compare.

The scoped re-review report is retained locally at `.superpowers/sdd/093-identity-safe-output-clipping/whole-rereview.md`. It found a new **Important**: `SelectTableReadMaps` serializes the full containing projection for each candidate entry, causing quadratic work and allocations on high-cardinality maps. The review asks for bounded incremental fit work without losing later fitting entries, plus a high-cardinality regression or measured stress check at default and maximum response budgets. The one-final-fix-wave ruling defers this product fix to [Plan 102](102-bounded-table-identity-selection.md), which specifies incremental byte accounting while keeping the final envelope check. This is a known review blocker, not an approved residual or a passing whole-branch verdict. Separately, the final gate repeated the unrelated dashboard matcher failure seen in the full CLI suite. The isolated test passed once (1/1 in 952 ms), consistent with timing sensitivity, but its root cause is unproven. Plan 093 remains REVIEW BLOCKED and is not merge-ready.

No Linux verification, merge, push, live database access, or paid provider use occurred. Fixture tests and the gate's database tests are not evidence of live-database behavior.
