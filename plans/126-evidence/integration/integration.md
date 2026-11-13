# Plan126 integration with current main

Incoming quality branch:c062822a. Main/origin main before integration:85d1c95e. Base:a274b0ec. Main independently integrated Plan125 at ef580dc; its contributor completeness, statement availability, page-server IO and regression tests are preserved alongside quality/navigation/accessibility work.

Resolution and combined verification are complete. The merge was prepared in the existing quality worktree. The untracked main-worktree design proposal is preserved at `D:/temp/sqlharness-quality-main-20261113/untracked-dashboard-quality-design.md`, SHA256 `BB100F32CA6B59EE75D38169531B5187ADCC8EE660AC8E45E389306C7DFCCB44`. User explicitly authorized merge and push; no force push, live SQL or installed dashboard changes are part of integration.

## Resolution checks

- Independent integration [review](review.md): one P2 fixed with an observed red/green regression (9/9 green).
- Focused C# evidence/reader/agent optimization suite: 304 passed, zero failures, exit 0; see logs/backend-focused.log. Initial compile exposed two merged helper calls missing the seed argument; both were corrected.
- UI typecheck and lint exit 0 (two existing hook-dependency warnings). Focused UI suite: 147/148 initially, then the corrected VersionTable file 16/16; the final full gate will independently check all UI tests.
- The approximate outcome regression explicitly preserves raw reads 100/50 while withholding an IO verdict across different conditions.

Full Windows verification and actual synthetic browser checks ran from the integrated main checkout before push. Previous quality scores and native accessibility evidence remain tied to their recorded revisions, not relabeled as new integration evidence.

## Destination checkout integration

Local merge `b1799cac` integrated the resolved tree `a3133652` into main with identical product paths. The first Windows gate passed build but stopped at UI (569 passed, one failed). A retained main condition routed failed comparisons around the quality branch's unavailable-evidence fallback. The isolated regression reproduced the failure. A naive routing-only fix exposed an incoming fixture that supplied successful comparison data for a failed attempt; the final implementation preserves main's successful-only comparison rule while sharing the retained diagnostic/partial-measurement fallback.

Fix `0420529c` passed OperationPage/OperationFailure 45/45 and a focused assertion that a failed operation never requests eligible optimization evidence. Final Windows verification and Release browser checks run on this revision in `D:/Dev/sqlharness`. Explicit branch formatting against `85d1c95e` passed all 13 changed C# files; the later fix changes only TypeScript. Release build passed with zero warnings/errors. Full logs are in logs/; first-gate failures are retained separately.

## Integrated Release browser acceptance

Actual Release dashboard on `0420529c`, fresh isolated home `D:/temp/sqlharness-quality-integration-home-20261113`: **67/67 functional checks passed**, zero browser/request errors, **13 axe scans with zero violations**. The frozen fixture contains 500 sessions, 10,000 operations, 50 versions and 20 cases. Performance series were intentionally excluded while the gate ran; no new performance or native screen-reader claim is made. Actual DTO checks, comparison dimensions, failed-attempt diagnostics, navigation context, reflow, both themes, copy and focus interactions are recorded in [summary](browser/summary.json), [raw compressed report](browser/acceptance.json.gz), and [harness report](browser/report.md).

The synthetic dashboard was stopped after acceptance. The operator's SQLHarness home and installed dashboard were not used or restarted. Full local screenshots/DTO artifacts remain in `D:/temp/sqlharness-quality-main-20261113/browser`; representative failure/outcome screenshots are retained here.

## Final Windows gate

Tested revision: `0420529c0c212c003e97ed2a3abd4a82bfb9d5c4`, actual destination checkout `D:/Dev/sqlharness`, 2026-11-13. Command: `just verify -LogDirectory D:/temp/sqlharness-quality-main-gate-final-20261113`, process-local TEMP/TMP `C:/s126m`.

**Exit0, verify: OK, five stages,14m36s.** Build passed; UI typecheck/lint passed (two existing hook-dependency warnings),82 files/570 tests passed; Core4919 passed/11 skipped; MCP537 passed/4 skipped; zero test failures. Gate formatting skipped the committed tree, complemented by the successful explicit13-file branch check above. [Summary](logs/verify-final-summary.log), [UI](logs/final-gate/ui-check.log), [Core](logs/final-gate/test-SqlHarness.Tests.log), [MCP](logs/final-gate/test-SqlHarness.Mcp.Tests.log), [build](logs/final-gate/build.log), [branch formatting](logs/branch-format.log).

The final evidence/plan commit changes documentation and artifacts only. Product paths remain identical to tested0420529c. Linux was not run; the Windows gate excludes Integration-named tests. No database workload, approval, installation or operator-dashboard restart was performed. The existing quality worktree and pre-merge backup branch are retained.
