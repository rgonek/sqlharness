# Task 3 implementation report

## Status

Task 3 implementation is complete. The original immutable Plan 097 base is `898f8b20f40246b3d0ff5263c5969ed4e2130b12`; Task 3 started at `0b9c2e2c7304cd78e78523d87a19b3d953901546` after the Plan 096 merge. Correct-base branch formatting was committed as `aea93d65347e4b74af6e39f5266910f1b527cc10` (tree `66cb76325673e218d66492a7de33f1d66edc95b9`); the final Windows gate and scoped review of the final code/evidence are pending. The earlier `6d658da` gate was before correcting formatter coverage and is historical only. Task review and Sol low whole-branch review found no product findings on their reviewed tree.

## Implementation

Added a served-MCP regression to `tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs` that sends 33 uniquely named, uniquely valued parameter sets in two measurement calls of 32 and 1. It runs the actual `SqlHarnessModule` over MCP stream transport with an in-memory fake SQL session factory and temporary artifact home. The test asserts each expected name appears once in its operation report, each typed ID reaches SQL twice under the existing warmup plus one measured execution for `repeat: 1`, the operations have separate labels and artifact IDs, and both artifact IDs are recoverable through `sqlharness_artifact`.

The two measurement calls and two artifact recovery reads record actual JSON-RPC transport bytes. The focused regression reported 4 calls (2 measure, 2 recovery), 3,200 UTF-8 request bytes, 78,187 UTF-8 response bytes, 33 fake sessions, 66 SQL query executions, and zero live database connections. This is deterministic fixture evidence only; it makes no token-efficiency, actual-agent, live-database, or cross-call atomicity claim.

The full Core run before correction exposed two stale singleton-minimum expectations in `CompareMatrixTests`; these were updated to reflect the reviewed 1..32 contract, and the CLI text-matrix singleton has explicit acceptance coverage. The focused matrix suite passed 64/64. A dashboard shutdown request-capture failure from that earlier Core run passed in an isolated rerun (1/1); it was not reproduced. The final full gate also passed.

Current runtime and human-facing MCP/README contracts state 1..32. The archived `Fixtures/OperationAnnotations/off/tools-list.json` snapshot retains its historical pre-change listing; it is not the current served-schema contract and was left unchanged. Task 2's scratch report was copied to `plans/evidence/097/task-reports/task-2-report.md` and removed from tracking. Task 1 reports and RED logs are retained in the evidence archive.

## Verification

- Focused 32+1 regression: passed 1/1; raw output `plans/evidence/097/raw/task-3-regression-green.log`.
- Full MCP suite: 505 passed, 4 skipped, total 509; raw output `plans/evidence/097/raw/task-3-mcp-suite.log`.
- Core `CompareMatrixTests`: 64/64 passed; raw output `plans/evidence/097/raw/task-3-core-focused.log`.
- Dashboard shutdown focused rerun: 1/1 passed in 150 ms; raw output `plans/evidence/097/raw/task-3-dashboard-shutdown-focused.log`.
- Initial Task 3-only formatter check against `0b9c2e2` covered two files and passed, but did not meet the required immutable-base scope.
- Correct-base formatter check against `898f8b20f40246b3d0ff5263c5969ed4e2130b12` initially failed exit 2 on the 28-file C# diff. Branch `-Fix` then normalized 18 files (17 final-newline corrections and one whitespace wrap); direct post-commit check passed `format: OK`, exit 0. Full raw outputs are archived under `plans/evidence/097/raw/`.
- An earlier `just verify -LogDirectory D:/temp/sqlharness-plan097-verify` passed on `6d658da` (five stages in 8m46s; Core 4,652/11 skipped; MCP 505/4 skipped; UI 64 files/372 tests; build 0 warnings/errors), but preceded correct-base formatting and is not final verification. UI emitted a non-failing Node `DEP0190` diagnostic for a child process using `shell: true`.
- Final `just verify -LogDirectory D:/temp/sqlharness-plan097-final-verify` is running on `aea93d6`; record its exit and stage results before calling the branch verified.

See `plans/097-verification-evidence.md` for exact tested revision/tree, commands, prior failure classification, output counts and evidence limits.

## Commits and self-review

- `a3c869b test(097): prove split 32+1 batch workflow`
- `6d658da fix(097): normalize regression test formatting`
- `aea93d6 fix(097): normalize original-base branch formatting`

No unrelated files were included; the existing untracked `noop` remains untouched. No integration, push, live database access, paid trial or Linux gate occurred.

Controller completion: final corrected-tree gate initially failed one ProcessRunner timeout; focused rerun passed 1/1 in 506ms. Full unchanged-tree retry passed on aea93d6 (exit0, 7m57s; Core4652/11skip, MCP505/4skip, UI372). No source/test change for timeout. Final scoped documentation approval pending.
