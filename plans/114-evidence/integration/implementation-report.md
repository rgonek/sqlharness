# Plan 114 integrated candidate

## Integration inputs

- Feature branch before integration: `1cdd45c2421276b41db6e954b1ad964c8c42b06e` (`plan-114`).
- `main` selected for integration: `51ee23bcc1dcc44355c1a364fbfd6584c0a06dcd`, including Plan 104 implementation.
- Merge commit: `530fb151ea35a51413db17e2abc09f9a272f4e84` (parents `1cdd45c2` and `51ee23bc`); subsequent commits `35dc2d1d` and `d02cefd1` change documentation/evidence only.
- Integration is being prepared only in `C:/Users/rgone/MultiCliProfiles/codex/account-b/worktrees/0474/sqlharness`. The primary checkout's dirty files were not touched.

## Merge resolution

The merge had one content conflict in `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`. The resolution preserves Plan 104's measure/compare contract wording, artifact metric provenance and missing-evidence semantics, page cursors/limits, and artifact size/clipping guidance, while retaining Plan 114's `statement-summary` field semantics, no-operator/no-repetition contract, exact statement identity guidance, and separate default page limit. The fixed/request schemas and section enum retain the merged `statement-summary` entry. All other files, including Plan 104's implementation, tests, docs, and evidence, merged automatically.

Independent integration source reviews are approved: [task review](task-review.md) and [whole-branch review](whole-review.md). Review diff packages are temporary scratch and are intentionally not tracked.

The first combined gate attempt ran `just verify -LogDirectory plans/114-evidence/integration/verification` against `530fb151`; it exited 1 after 14m46s. Build passed in 2m37s (0 warnings/errors) and UI passed in 9m22s. Core had 4,716 passed, 11 skipped, and two timeouts in the unrelated dashboard-startup and PID-publication tests. MCP had 525 passed, 4 skipped, and two failures: request-mode `tools/list` measured 33,055 bytes against the former 32,768-byte test ceiling, and the artifact cursor description lacked the literal `continuation`. Full failed-stage logs are retained under `verification-attempt-1/`.

The catalog length value was only a regression-test threshold (`McpLimits.ToolsListBudgetBytes` had no server-runtime use). Following the user's explicit steering, the test-only ceiling is now named `ToolsListRegressionTestBudgetBytes` and set to 36 KiB; `docs/mcp/tools.md` identifies it as a test ceiling, not a runtime protocol or response-size limit. Result-size limits are unchanged. The merged catalog keeps the reviewed Plan 104 and Plan 114 wording; the cursor now explicitly says to use continuation. A focused MCP schema test passed after an intermediate wording trim (28,686 B off; 32,294 B annotation-on; 32,750 B request-mode); these intermediate figures are not the final restored wording's byte measurements.

With the reviewed wording restored, the focused catalog measurement reported 29,028 B annotations-off, 32,636 B annotations-on, and 33,092 B request-mode, all below the user-authorized 36,864 B regression-test ceiling. No runtime result/request limits changed. After aligning stale test phrase expectations to the preserved merged descriptions, `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config|FullyQualifiedName~Tools_list_explains_optimization_evidence_semantics_in_both_modes" --logger "console;verbosity=detailed"` passed 2/2.

After the catalog wording/test follow-up was committed at `574fbfa8`, the full Windows gate `just verify -LogDirectory plans/114-evidence/integration/verification-attempt-2` passed in 12m40s (exit 0): build succeeded with 0 warnings/errors; UI had 372 passed; Core had 4,718 passed and 11 skipped; MCP had 527 passed and 4 skipped. There were no failures. The final gate logs are retained under `verification-attempt-2/`. It tested the complete product/test tree at `574fbfa8`.

The explicit branch formatter `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 51ee23bcc1dcc44355c1a364fbfd6584c0a06dcd` first found a terminal-newline issue in six changed C# files. `just fix` covered only working-tree changes and reported zero files, so the repository formatter was applied in branch scope with `-Fix`; it normalized those six files. The explicit branch formatter was rerun with the same base and passed (exit 0; 15 branch C# files checked). `git diff --ignore-space-at-eol --exit-code 574fbfa8 -- src tests` then exited 0, confirming the final source/test delta from the full-gate revision is whitespace-only. The initial failed formatter output, branch-scoped fix output, and successful final check are retained as `format-branch.log`, `format-fix-branch.log`, and `format-branch-final.log`.

Integration source reviews and the scoped catalog follow-up reviews are approved; see [task review](task-review.md), [whole-branch review](whole-review.md), [catalog task review](catalog-fix-task-review.md), and [catalog whole-branch review](catalog-fix-whole-review.md). The user-authorized 36 KiB catalog regression-test ceiling and measured final request-mode size (33,092 bytes; 3,772 bytes below the ceiling) preserve readable Plan 104/114 contracts without changing runtime result/request limits.

The candidate is ready for controller review and local fast-forward into the primary checkout. This worktree does not claim that fast-forward has happened.

No live database, Linux gate, push, or primary-checkout mutation is part of this integration task.

## Local primary integration

On 2026-10-24 the operator selected local merge. The primary checkout `D:/Dev/sqlharness` fast-forwarded from `51ee23bc` to `48a80042`. Exact `src`/`tests` equality with the reviewed final candidate was verified (exit 0); comparison against the full-gate revision `574fbfa8` ignoring end-of-line whitespace also exited 0. No redundant full gate was run after this unchanged-candidate fast-forward. The caller's local README, plan-index additions and untracked plans are preserved separately from the integration commits. No push or publication occurred.
