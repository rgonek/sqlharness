# Final Windows verification

Product revision: `5f05ca073887c58ecc22a567782124438baca07c`, branch `dashboard-quality-9`, 2026-11-11. Subsequent evidence/documentation changes do not change the product tree.

Command: `just verify -LogDirectory D:/temp/sqlharness-quality-gate-5f05ca07`. Actual exit code0; `verify: OK stages=5 11m47s`.

| Stage | Result |
|---|---|
| Dependencies | Skipped; lockfile unchanged |
| Build | PASS;0 compiler warnings/errors;40.7s |
| UI check | PASS;82 files,543 tests;typecheck/lint exit0;two existing OptimizationSection dependency warnings |
| Core | PASS;4873 passed,11 skipped,0 failed |
| MCP | PASS;537 passed,4 skipped,0 failed |
| Gate format | Skipped;0 uncommitted C# files |
| Explicit branch format | PASS;13 changed C# files against a274b0ec;exit0 |

The Windows recipe excludes tests whose names contain Integration. Linux, live SQL and native NVDA acceptance were not run by this gate.

Raw retained output: [build](verification/build.txt), [UI check](verification/ui-check.txt), [Core](verification/core-tests.txt), [MCP](verification/mcp-tests.txt), [branch formatting](verification/branch-format.txt). Full original logs stay at the unique directory above.

An earlier gate on da51aff7 stopped at UI538/539 because an OperationPage mock omitted view=changed and returned all-case data. The exact request paths/case list were corrected and OperationPage38/38 passed. A subsequent gate was intentionally interrupted after build when screenshot inspection required a narrow-table change. Neither attempt is reported as a successful gate.

Actual Release browser acceptance on the final product: [72/72 checks](browser/acceptance-5f05ca07/report.md), twelve axe scans with zero violations, all five20-sample responsiveness series pass. Native zoom200% and sampled contrast were repeated separately. [Scorecard](scorecard.md) keeps category15 below9 pending actual NVDA; the all-category goal is still open.

Whole-branch review covered a274b0ec..794a1e0e; all three findings were fixed and regression-checked. Later browser defects/fixes have scoped regression and actual-browser evidence in [browser iteration](browser-iteration.md). No claim of a second independent review approval is made.

No merge, push, publication, installed dashboard replacement, real SQL workload or mutation decision occurred. Only the isolated synthetic dashboard lifecycle was managed.
