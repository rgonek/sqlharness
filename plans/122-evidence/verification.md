# Plan 122 implementation evidence

All six tasks, including UI, are implemented on `plan-122-backend`. The branch was rebased cleanly onto main `b7a820f5` after the operator expanded the initial backend-only scope. Final verified product revision: `50a4ffb9`. Sol low whole-branch and scoped reviews are approved; Terra high approved the final verification correction. All 14 offline browser states passed. Full Windows `just verify` passed (exit 0, 14m40s).

Terra medium implemented each task and its fixes; separate Terra high task reviews approved the backend and UI scopes. The [execution ledger](execution-ledger.md) preserves task ranges, review loops, scope changes and rulings. Pre-rebase hashes in older reports are historical; the final review range starts at `b7a820f5`.

## Task evidence

| Scope | Implementation and tests | Final task review |
|---|---|---|
| 1: session boundary | [Report](reviews/task-1-report.md) | [Approved](reviews/task-1-review.md) |
| 2: timing, provenance and LOB | [Backend](reviews/task-2-report.md), [UI](reviews/task-2-ui-report.md) | [Backend](reviews/task-2-rereview-2.md), [UI](reviews/task-2-ui-rereview-1.md) |
| 3: bounded SQL preview | [Backend](reviews/task-3-report.md), [UI](reviews/task-3-ui-report.md) | [Backend](reviews/task-3-review.md), [UI](reviews/task-3-ui-review.md) |
| 4: provider rows and recorded approval | [Backend](reviews/task-4-report.md), [UI](reviews/task-4-ui-report.md) | [Backend](reviews/task-4-rereview-1.md), [UI](reviews/task-4-ui-review.md) |
| 5: statement grants and spill | [Backend](reviews/task-5-report.md), [UI](reviews/task-5-ui-report.md) | [Backend](reviews/task-5-rereview-1.md), [UI](reviews/task-5-ui-review.md) |
| 6: unsupported placeholders | [Report](reviews/task-6-report.md) | [Approved](reviews/task-6-rereview-1.md) |

Focused tests are recorded with their exact commands and limitations in the reports. UI checks that used `--pool=threads --maxWorkers=1` are focused checks, not the normal full gate. An early full UI attempt was stopped after discovering a temporary incomplete dependency directory; it is not pass evidence. That directory was moved outside the worktree. The final dependency directory is a local copy with an identical installed-lockfile stamp; no junction or writes to main's dependency tree remain.

## Verification boundaries

Windows command: `just verify -LogDirectory plans/122-evidence/gate-<revision>`. The `b30a11e1` attempt was controller-interrupted during a slow buffered UI stage; it does not establish a product defect or a fixed worker hang. The `a1e5cdb1` attempt passed build with zero warnings, UI 406/406 and MCP 535 passed / 4 skipped, but Core had 4 failures, 4767 passed and 11 skipped. See its [Core log](gate-a1e5cdb1/test-SqlHarness.Tests.log). Correction `50a4ffb9` fixed the fixture/contract failures and received [Terra high approval](reviews/verification-correction-review.md). Final gate on that revision passed: Core 4771 passed / 11 skipped, MCP 535 passed / 4 skipped, UI 406 passed / 69 files; build passed. [Final logs](gate-50a4ffb9/build.log). Branch formatting separately passed for 32 changed C# files (the normal gate skips committed clean-tree files). UI emitted Node DEP0190 from the tooling child-process launch; retained in the raw log, with no failing check or runtime behavior change. Linux, live target database behavior, installation, merge and push are not claimed.

The [offline browser harness](visual/capture.mjs) uses the actual built UI and the existing Plan 121 fixture harness. All API calls are intercepted in its isolated browser context. It neither seeds a journal nor connects to a database. Its images illustrate the retained contracts, not production measurements. [Gallery](visual/index.html), [machine-readable results](visual/results.json): 14 desktop/mobile states passed, including removal of stale Physical selections while preserving statement LOB logical. No page errors or whole-page overflow; local table scrolling is permitted.

No historical values are backfilled. Tests exercise old and resumed keys, unsupported marker versions, mixed comparisons, the real extraction cap and retained compressed raw plans without new scalar reconstruction or GET writes. Mutation evidence uses a one-use preparation token hash plus prepared-request digest; the raw token is confined to the internal broker transport and is absent from ordinary JSON and `ToString`.

## Accepted implementation limits

- A negative spill finding is supported only for an AST-proven literal-only SELECT with complete visible runtime and no unsupported warnings or hidden-work indicators. Most real queries without a visible supported spill warning return `null`; this is not a claim that no spill occurred.
- Unknown warning forms remain unavailable. Known supported spill warnings can establish a positive result. Multiple ambiguous sources do not produce aggregated grants.
- Offline extraction timings varied too much to establish reliable overhead. The earlier record-size probe measured serialized JSON, not SQLite pages; additional ScriptDom work in the final fix was not included in that probe. No live performance claim is made.
- SQL containing embedded NUL has an unavailable preview rather than a misleading prefix/truncation flag. Other previews are bounded to 240 Unicode code points.
- Failed best-effort journaling and deduplicated joined callers may have unavailable lifecycle evidence. Only the exactly linked canonical request may supply confirmed affected rows.
