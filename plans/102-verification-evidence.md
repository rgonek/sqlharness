# Plans 093/102 remediation evidence

## State

Implementation completed on isolated branch `plan-093`, based at `1266881c3e340ac3e4d5e2abeebd5725100145f9` for this remediation. The combined branch base is `fab299f2abfc76b5f8b2eee2b07c91735bbf7059`. The operator subsequently authorized local integration; push, live database and paid-provider trials remain outside this work. Plans 093 and 102 are DONE on this feature branch. Both task reviews, the combined whole-branch review and final scoped re-review are approved with no open findings. Final direct formatting and Windows gate exited 0 on `76ebb0d`; the successful final result below supersedes earlier failed/interrupted runs. Locally fast-forward integrated into `main` at `6f07792`; not pushed.

## Reviewed implementation

- `476f493`: serialize an empty containing projection once and each source entry once. Charge exact JSON singleton-property bytes (braces excluded), retained-entry commas and one shared compare-map budget. Continue after rejected entries, retain significance/round-robin order, and count source entries minus retained entries once. Existing reserve, schemas, limits and final envelope measurement remain intact.
- `57dcf64`: assert every retained escaped/multibyte identity and its min/median/max against source in high-cardinality measure, both compare maps and artifact fixtures.
- Luna xhigh task review approved the implementation; its Important test-attribution gap was fixed and scoped re-review marked ADDRESSED, with no new findings.

## Performance evidence and limits

The deterministic fixture contains 128 unique entries, with 9917 serialized UTF-8 source-map bytes. It includes quotes, backslashes, a control character and multibyte names. Values below are local observations, not model-token measurements or portable latency guarantees.

| Shape | Before allocated bytes | After allocated bytes | Before ms | After ms | Response bytes before/after |
|---|---:|---:|---:|---:|---:|
| Measure | 1912024 | 327944 | 384.74 | 10.73 | 10512 / 10512 |
| Compare | 3581016 | 436064 | 279.51 | 4.83 | 20828 / 20828 |
| Artifact | Not measured | 227960 | Not measured | 2.67 | Not measured / 10299 |

Response sizes include the CLI newline. Artifact and mostly-rejected pre-fix performance baselines were not captured; no before/after savings are claimed for them. This evidence limitation was explicitly accepted while retaining deterministic work-bound and correctness coverage for all affected shapes.

The internal accounting regression checks one fixed-shell serialization and exact singleton-property accounting once for every source entry. An allocation regression supplements this structural bound; wall time is not asserted. The 4096-byte rejection fixtures place sixteen 5000-character high-ranked names ahead of short escaped/multibyte entries, retain later fitting entries, verify every retained value and truthful omissions, and assert the complete response byte ceiling. All tests use in-process fixtures with zero real database executions and zero provider calls.

## Dashboard gate repair

`03cdd6c` and final correction `ec21e6a` change only `DashboardHousekeepingTests.Matcher_commits_do_not_keep_an_idle_dashboard_up`. Housekeeping callbacks are not evidence of a matcher sweep: polling is 250 ms, while sweeps use 60 seconds of injected-clock time. The test previously appended titles after matching and used callback counts to infer consumption. The final repair observes the initial matcher cycle, appends `title-1`, advances to an eligible sweep and observes its commit, then appends `title-2` synchronously before advancing to the original two-minute idle deadline. It verifies the second commit and shutdown. Cancellation/await cleanup runs in `finally`. Product matcher and idle policy are unchanged.

The first revised test exposed repeated title appends (`title-3` instead of `title-2`). Luna xhigh approved the first task repair, but Sol's whole-branch review found a remaining callback-count race before the second append. `ec21e6a` removes that dependency by appending synchronously. Final focused `DashboardHousekeepingTests` passed 17/17 in 11.7861 seconds. Sol scoped re-review marked its Important ADDRESSED with no new findings; all product and test review findings are closed, subject to actual final format/gate results.

## Focused verification

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~High_cardinality_table_selection_stress|FullyQualifiedName~Table_identity_selection_accounts_fixed_shell_and_each_source_entry_once|FullyQualifiedName~Table_identity_selection_skips_oversized_entries_and_keeps_later_fitting_entries|FullyQualifiedName~Agent_compare_keeps_fitting_table_after_oversized_highest_ranked_identity"`: 8 passed.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~AgentOutputTests`: 87 passed before the attribution-only amendment.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter FullyQualifiedName~McpTokenBudgetTests`: 45 passed.
- Attribution fix filter (high-cardinality stress, rejected entries and accounting tests): 5 passed on `57dcf64`.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~DashboardHousekeepingTests`: 17 passed on `03cdd6c`.

## Final delivery checks

- Tested revision: `76ebb0d6f4cc39ebad84064cd148a219bb898c20`; full git tree: `10e85e28a48fc1462c66150e56fdcea045cb31ba`. Later closure changes are documentation only.
- Branch formatting base: `fab299f2abfc76b5f8b2eee2b07c91735bbf7059`. Final `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef <base> -Fix` exited 0; exact formatter EOF output committed. Final direct check without `-Fix` exited 0 (`format: OK`); log `D:/temp/sqlharness-plan102-final-format-check.log`.
- `just verify -LogDirectory D:/temp/sqlharness-plan102-final-verify`: direct exit 0, `verify: OK stages=5 13m00s`. UI install skipped because lockfile unchanged; build 1m04s, UI check 9m52s, .NET tests 3m07s, gate formatter skipped committed unchanged files (separate branch check above covers them).
- Gate CLI project: 4583 passed, 11 skipped, 0 failed (4594 total); MCP: 467 passed, 4 skipped, 0 failed (471 total). Logs: `D:/temp/sqlharness-plan102-final-verify/test-SqlHarness.Tests.log` and `test-SqlHarness.Mcp.Tests.log`; console `D:/temp/sqlharness-plan102-final-verify-console.log`.
- Superseded `cabecd4` gate at `D:/temp/sqlharness-plan102-verify` was interrupted after its build when review identified the remaining test race. It is not passing final evidence. Historical Plan 093 failed gates remain in [093 evidence](093-verification-evidence.md).
- Sol low combined review closed the original repeated-serialization finding and raised only the second-title append race. Scoped final re-review of `cabecd4..ec21e6a` marked that race ADDRESSED, with no new breakage or remaining findings. The final formatter added only an EOF change beyond that reviewed behavioral tree.
- Out-of-scope review classes (provider usage/live DB, Linux, integration/push) are accepted evidence boundaries, not deferred product findings. Linux remains unverified; zero real DB/provider calls claimed only for the synthetic fixtures.

## Ruling retained for delivery

Accept the measured pre-fix baseline limited to measure/compare, retaining deterministic work-bound and correctness coverage for artifact/rejected-entry cases rather than rebuilding the old tree solely for their missing measurements. Reason: meaningful RED and exact once-per-entry accounting establish the defect and fix. Cost if wrong: artifact/rejected-entry before/after performance savings remain unquantified; none are claimed.

## Local integration

The operator selected local merge. On 2026-10-20, clean `main` at `fab299f2` was fast-forwarded to `6f0779285e68775bf068157278745ceb8918b351`. `git diff --exit-code 76ebb0d HEAD -- src tests scripts docs/mcp/runtime.md` exited 0 after integration: source, tests, scripts and runtime guidance match the Windows-verified product exactly. No new integrated-tree gate was run because there is no product change or unresolved risk. The following integration record commit is documentation only. Nothing was pushed; the feature worktree remains available with its review reports.