# Final Windows verification

Final product: `85c75cb2f2219ac36c0c4e89ea362e99968f0c71`, branch `dashboard-quality-9`, 2026-11-11.

Command: `just verify -LogDirectory D:/temp/sqlharness-quality-gate-85c75cb2`. Actual exit code0; `verify: OK stages=5 11m48s`.

| Stage | Result |
|---|---|
| Dependencies | Skipped; lockfile unchanged |
| Build | PASS;0 compiler warnings/errors;55.3s |
| UI check | PASS;82 files,544 tests;typecheck/lint exit0;two existing OptimizationSection dependency warnings |
| Core | PASS;4873 passed,11 skipped,0 failed |
| MCP | PASS;537 passed,4 skipped,0 failed |
| Gate format | Skipped;0 uncommitted C# files |
| Explicit branch format | PASS;13 changed C# files against a274b0ec;C# unchanged by the late copy-focus fix |

Raw output: [build](verification/85c75cb2/build.txt), [UI](verification/85c75cb2/ui-check.txt), [Core](verification/85c75cb2/core-tests.txt), [MCP](verification/85c75cb2/mcp-tests.txt), [branch format](verification/branch-format.txt). Original logs stay in the unique directory above.

The Windows recipe excludes tests whose names contain Integration. Linux and live SQL were not exercised.

## Browser and native acceptance

- Broad synthetic journal-to-DTO-to-UI matrix on5f05ca07: [72/72 checks](browser/acceptance-5f05ca07/report.md), twelve axe scans with zero violations, all five20-sample responsiveness series pass. Native200% zoom/reflow and sampled text/control/graph contrast pass in both themes.
- The only later product change is CopyButton pending-focus handling in85c75cb2. The new regression failed before the fix; focused CopyButton/ReproScriptButtons11/11 then pass. [Five actual Release browser checks](browser/copy-focus-85c75cb2/copy-focus.json) cover busy focus, duplicate activation, keyboard order, manual fallback, candidate copy and full6000-line original SQL.
- [Actual NVDA on85c75cb2](browser/native-accessibility/nvda-acceptance.md):17 steps pass, including spoken policy Fail/elapsed-regressed, read-only SQL, copy success and retained copy focus. All three served entry/dynamic CSS asset hashes match the current build; source diff is empty.
- [Current scorecard](scorecard.md):all14 categories9.0-9.2. Scope and native-study limitations remain explicit.

The previous full Windows gate on5f05ca07 also passed (UI543, Core4873/11skips, MCP537/4skips); its raw logs remain in verification/. It is historical evidence, not substituted for the final85c75cb2 gate. Earlier failed/interrupted attempts are described in the review/browser iteration reports.

Whole-branch review covered a274b0ec..794a1e0e; all three findings were fixed and regression-checked. Later browser/native fixes have scoped regressions and actual-browser evidence. No second independent review approval is claimed.

No merge, push, publication, installed dashboard replacement, real SQL workload or mutation decision occurred. Only the isolated synthetic dashboard lifecycle was managed.
