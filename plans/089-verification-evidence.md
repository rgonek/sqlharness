# Plan 089 verification evidence

Implemented and Windows-verified on feature branch `plan-089`, based on `fab299f2abfc76b5f8b2eee2b07c91735bbf7059`. Final tested revision: `75d841d983645f189fea38c362165701ca3b8010`. Completion documentation changes no product files. No local integration or push was performed.

## Result and bounds

- Added `GET /api/agent-sessions/{kind}/{id}/operations?limit=500&cursor=<id>` and `JournalReader.AgentSessionOperations`. The shared selector filters membership before cursor/limit, merges child sessions in descending operation-ID order, deduplicates IDs, and projects at most 500 operations per page. Metadata resolution retains its existing journal-wide membership scan.
- Default limit is 500, clamped to 1–500. Cursor is exclusive; malformed/nonpositive cursors return 400 and unknown keys return 404. A single read snapshot covers each request. Lookahead sets nextCursor to the last returned ID only when another eligible row exists.
- The compatible detail response uses the same global 500-operation cap. Full membership counts and recency remain independent of pages. Foreign conversation members cannot consume page capacity.
- The UI uses the new endpoint as its operation stream, appends pages, shows loaded counts, preserves membership markers/internal filtering, and retains rows with Retry after a subsequent-page failure. Settings-save matching-off removes cached pages; the view redirects to ordinary sessions.

## Reachability fixtures

| Fixture | Evidence |
|---|---|
| Old exact behind 500 newer unassigned rows | Backend HTTP pages contain 500 then 1 items; the second page contains the old exact ID and has null nextCursor. Compatible detail still has 500 rows, while full counts remain 1 exact / 500 unassigned. |
| Foreign members in shared child | 501 newer foreign exact rows do not consume target page capacity; limit=1 returns the target exact row with terminal cursor. |
| Multiple children | Pages of 1 follow the globally descending IDs once each; first cursor equals the returned higher ID, second cursor is null. |
| New insert and retention between requests | The saved exclusive cursor excludes newer IDs and already-loaded rows; deleted rows disappear without crashes. |
| Full UI first page | IDs 1000–501 are 500 unassigned rows; cursor 501 loads exact ID 500 and its marker while retaining earlier rows. |
| Failure/retry and filtering | Three fixture rows then two older rows exercise retry with cursor 501, retained rows, order, membership, and internal toggle. |
| Matching off | Backend aggregate gating, UI redirect/request suppression, and real settings-save cache removal are covered. |

These are deterministic local journal/HTTP/UI fixtures, not live target-DB trials or measured agent-token savings. No HTTP UTF-8 byte economics measurement is claimed; operation projections are bounded by row count and existing projection limits. No persistent cross-request snapshot or guarantee under membership reclassification is introduced.

## Reviews and focused tests

- Routing: implementation Luna high; task review and re-review Luna xhigh; whole-branch review and final scoped review Sol low.
- Task 1 `00b8f86`: backend baseline 71/71; five new route tests initially failed with 404. Focused GREEN 76/76 and edge rerun 2/2. Task review approved.
- Task 2 `472f64a`: retained RED without paging, initial focused GREEN 7/7, and full UI check 347/347. Review requested later-page exact and settings-save cache regressions.
- Fix `5a78735`: full 500-row unassigned page then exact member; settings-save cache removal. Disabling cursor continuation/cache removal made the respective regressions fail; restored code passed AgentSessionPage 8/8 and focused SettingsPage 1/1. Luna xhigh scoped review approved both findings and corrected history heading.
- Formatting `86affc9`: three EOF corrections required by `.editorconfig` (`insert_final_newline = false`). Branch-mode formatting passed with direct exit 0. C# files are unchanged after that commit; the re-review observation that missing EOF newlines were a regression was resolved against the repository contract and formatter evidence.
- Sol low whole-branch review approved `5a78735` with no Critical/Important findings. One nonblocking Minor remains: the `limit=0` route test checks status without explicitly asserting item count; the clamp implementation was reviewed as correct.
- Final validation fix `75d841d` replaces unsupported Testing Library `exact` option with anchored `/^500$/`. Typecheck and page tests 8/8 passed; Sol low scoped review approved with no new breakage.

## Windows verification

Command: `just verify -LogDirectory <archive>/windows-gate-3` (executed originally in this plan's SDD workspace; logs archived unchanged).

Final run on `75d841d983645f189fea38c362165701ca3b8010`: **exit 0, verify: OK, 5 stages, 8m41s**.

| Stage | Result |
|---|---|
| UI install | Skipped: unchanged lockfile |
| Build | Passed, 22.9 s |
| UI check | Passed, 4m53s; typecheck, lint, 63 files / 349 tests |
| .NET tests | Passed, 3m47s; Core 4567 passed / 11 skipped; MCP 462 passed / 4 skipped |
| Working-tree format | Skipped: zero uncommitted C# changes; separate branch check passed |

Branch command: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef fab299f2abfc76b5f8b2eee2b07c91735bbf7059`; direct exit 0 in `branch-format-2.exit`, three changed C# files. The earlier attempt failed FINALNEWLINE and was corrected before the final reviews/gates.

Earlier gate attempts remain part of the record:

1. On `5a78735`, ui-check failed TS2769 in the newly added test despite Vitest 349/349; corrected in `75d841d`.
2. On `75d841d`, UI and MCP passed but `Matcher_commits_do_not_keep_an_idle_dashboard_up` failed at its title assertion (null). The same unchanged compiled test passed alone in 828 ms, then the complete third gate passed without product/test bypass changes. The failed attempt is retained as an intermittent housekeeping risk, not erased by the successful run.

Existing diagnostics remain disclosed: baseline Vite chunk-size warning, Node DEP0190 warning and refused localhost:3000 connection diagnostics in passing UI runs. Plan 091 owns UI-test network isolation.

## Boundaries and retained record

- Feature-branch verification only; no merge, integrated-tree gate, push, live database trial, browser visual inspection or Linux gate is claimed. Linux was not requested by this plan.
- Annotation/Optimization requests, authorization, mutation policy and journal schema were not changed.
- No discretionary scope rulings or parked blocking findings. The single remaining Minor is described above.
- Raw baseline/RED/GREEN/format/gate logs, direct exits, revision files, review packages, task reports, reviews and ledger are archived at `D:/temp/sqlharness-plan089-20261020-214305`. The plan-specific SDD scratch directory is removed after completion; sibling plans are untouched.
