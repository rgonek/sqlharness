# Layout follow-up evidence

Current mock exports are updated in place under `docs/design/dashboard-2026-10-25`. The source is the operator-provided `D:\Dev\sqlharness-design-changes\2` directory. No second dated mock tree is retained.

Open `index.html` for matched reference/application captures. `interaction-results.json` records 28 browser checks at 1280px and 390px, with no console/page errors. `initial-results.json` records equal 28px Activity controls and 1px top/bottom switch insets. The mutation capture opens/closes a synthetic request and submits no decision.

The independent review found the saved-note accessible-name issue; the final saved-note button uses its text as its accessible name and a title for edit guidance. Browser verification confirms that fix.

## Reproduce browser checks

Use an isolated tools directory with `playwright` installed and its Chromium browser. Copy the files from `harness/` there so Node resolves that dependency. Set `SQLHARNESS_VISUAL_ROOT` to this checkout, or run from the checkout root. The scripts import this checkout's existing fixture factories and intercept API calls with synthetic data.

Serve the current built `src/SqlHarness.Dashboard/ui/dist` on `127.0.0.1:48138`, and the mock directory on `127.0.0.1:48137`, using `../visual/harness/static-server.mjs`. Run `layout-polish-capture.mjs`, `layout-polish-interactions.mjs`, `layout-polish-mutation.mjs` and `layout-polish-mock-details.mjs`. These are fixture-only browsers, not a server connected to the operator's journal or database.

## Final verification

`just verify -LogDirectory D:/temp/sqlharness-layout-polish-gate-serialized` exited 0: all five stages completed in 17m53s. Build, typecheck and lint passed; UI 408/408, Core/Dashboard/CLI 4771 passed with 11 existing skips, MCP 535 passed with 4 existing skips. Format was appropriately skipped because no C# files changed. Raw logs and result are in `accepted-gate/`.

Tested working tree is based on `386eac877dc0d5f1d1668ae3fcf969ba9b0dcfdb`. `tested-ui-source.json` records SHA-256 for all 219 UI input files, combined digest `c99599f41ff0420014729439df3d258c69f4186b92b74a451d049f97dff31f93`. The source digest was checked unchanged after the gate. The final source review reports no actionable regressions (`review.json`). Browser checks and the terminal-toast layering check pass. No real database mutation was submitted.

Local publication and dashboard restart were subsequently authorized by the operator; their receipt is recorded separately.

## Native-test scheduling diagnostic

The first complete UI run was 407/408: one navigation test still used the explicitly removed Browse all sessions link. Its cache-invalidation assertion is retained; return navigation now uses the test router.

A later run passed all UI tests (408/408), MCP (535 passed, 4 existing skips) and 4770 Core/Dashboard/CLI tests, but `Clear_waits_for_an_inflight_cycle_before_removing_its_rows` did not observe its Task.Run callback within its existing five-second bound. The unchanged test passes alone in 515 ms; see `inflight-isolated-diagnostic.log`. This is diagnostic evidence only, not full-suite acceptance.

The final full gate uses `parallelizeTestCollections=false`, `maxParallelThreads=1`, `diagnosticMessages=true` in the ignored `tests/SqlHarness.Tests/bin/Debug/net10.0/xunit.runner.json` file (snapshot: `core-runner-settings.json`). No C# source, tracked runner policy, timeout or test selection was changed. MCP uses its existing runner settings. The full gate still uses the repository's standard non-integration filter and all UI tests. Earlier failures are preserved; interrupted runs are not pass evidence.

## Local publication and restart

Published product commit `1f732184` from `dashboard-layout-polish` with `just publish fast` after the successful complete gate. Installed `C:\Users\rgone\.local\bin\sqlharness.exe`; installed/published SHA-256 both `C68C139903402815F700E45B977532F290480AE9D22F904087C4ED1AC304FCD2`.

Executed the operator-authorized `sqlharness dashboard --restart` successfully. The replacement dashboard is PID 34948 from the installed executable, at `http://127.0.0.1:47800/`. Authenticated HTTP returns 200 and both JS/CSS entry resources match the built files by SHA-256; see `publication.json`. The first HTTP probe stopped at the token hand-off HTML; the corrected read-only probe requested the clean origin after the cookie exchange and reused the already successful restart. Tokens/cookies are not recorded.

The initial publish ran out of C: disk space and left an invalid Release reference assembly. Dependencies of this worktree were relocated to `D:\temp\sqlharness-layout-polish-original-node_modules` with a junction at the original node_modules path; an additional complete copy is at `D:\temp\sqlharness-layout-polish-node_modules`. The failed Dashboard Release bin/obj outputs were moved, without deletion, to the ignored `artifacts/failed-publish-cache` directory. The operator also freed C: disk space. A fresh publication succeeded; all 219 tested UI source files remain byte-identical to the gate manifest. Publication logs retain the failures and accepted result.

The development dependency junction is a worktree-only cache arrangement; the installed executable is self-contained. Main was not merged or pushed. The publication verifier performs a real restart unless `-VerifyExistingRestart` is supplied; the browser fixture harnesses remain synthetic and do not submit real mutation decisions.
