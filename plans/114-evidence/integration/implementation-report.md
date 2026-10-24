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

The integrated tree is being rerun with the full Windows gate. Its final result and the required post-commit branch formatter result will be recorded here when complete.

No live database, Linux gate, push, or primary-checkout mutation is part of this integration task.
