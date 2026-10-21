# Plan 091 verification evidence

## Scope and review status

Plan [091](091-ui-test-network-isolation.md) adds strict per-test network isolation to the dashboard UI test harness and explicit fixtures to the request-owning tests. The Task 1 review and final Sol low whole-branch/scoped re-review are approved with no open findings. The plan is DONE on the feature branch; local fast-forward integration into `main` is pending.

- Worktree: `D:/Dev/sqlharness/.worktrees/plan-091`, branch `plan-091`.
- Final verified product/test revision: `857402d91c5fa69c011c65c3923821a91087b993` (`test(091): match dashboard fixtures by method`), tree `5dc911fd1a32b65684f8880dd8715b2a85f85918`. Documentation-only commit `56c0403` followed the gate; this administrative update follows the approved review. The original Task 1 implementation commit is `838c3d252ee8639f728447efa2aa6a7e6485bb06`.
- Parent implementation commit: `1823f1a` (`test(091): isolate dashboard test network activity`).
- No product code, API contract, or dependency version changed. The changes are test harness code, test assertions and scenario-owned response fixtures.
- No live database, dashboard server, credentials, merge, or push was used. Linux was not run.

## Request ownership and before/after evidence

The pre-change full UI check passed 63 files / 349 tests but emitted two `AggregateError` diagnostics for connections refused at `::1:3000` and `127.0.0.1:3000`. Test-scoped tracing identified `src/router.test.tsx > unknown urls keep the navigation and show not found`: rendering the fallback route mounted the shared `AppLayout`, whose `useProfiles` and `useSettings` queries made unowned `GET /api/profiles` and `GET /api/settings` requests. The test had no matching fixture, so `fetch` reached the default localhost URL.

The Task 1 strict-guard discovery also recorded requests from other tests before their fixtures were completed. [`task-1-round1-unmatched-owners.txt`](D:/temp/sqlharness-plan091-gate-20261021/sdd-evidence/task-1-round1-unmatched-owners.txt) maps each method/path to its exact test name; the owners include Approval, SettingsPage, OperationPage, AgentSessionPage, CompareDetail, OptimizationSection, ComparisonPanel, PlanChanges, PlanWorkbench, VariantPanel and SessionsPage tests. Those routes are now explicit in the owning scenario or local component helper, including intentional 404, 409, 422 and 503 responses. No catch-all route was added.

The test setup now wraps global `fetch` before each test. Requests must match a registered method/path; an unmatched method/path and Vitest test name are retained for the teardown assertion even when the API client catches the rejected fetch. Teardown unmounts the DOM, cancels QueryClient work registered by `renderApp`, checks for retained unexpected requests, and restores native `fetch` in `finally`. Bare fixture paths now mean GET only; write fixtures declare `POST`, `PUT` or `PATCH` in the key. Exact query matches retain precedence over path-only matches. Regression tests prove a PUT to a registered GET path is rejected, an explicitly registered POST is served (including a `Request` input), test-to-test fixture cleanup works, rendered-query errors retain diagnostics, and a pending query is canceled at teardown. Settings, approval, transcript-clear, and annotation tests now declare the actual mutation verb. The intentional API-error assertions and SQL diff size-limit assertion remain.

The final whole-branch review's Important finding that method-blind fixtures could return 200 for an unregistered write is addressed by `857402d`. The final Sol low scoped re-review approved that fix, with no new Critical, Important or Minor findings. Its review and the archived whole-branch review are available at `D:/temp/sqlharness-plan091-gate-20261021/sdd-evidence/whole-rereview.md` and `D:/temp/sqlharness-plan091-gate-20261021/sdd-evidence/whole-review.md`.

## Windows verification

Final command: `just verify -LogDirectory D:/temp/sqlharness-plan091-final-gate-20261021`

The command exited 0: `verify: OK stages=5 9m03s`. It ran on the clean source tree at revision `857402d91c5fa69c011c65c3923821a91087b993`.

| Stage | Result |
|---|---|
| UI install | Skipped: lockfile unchanged |
| Build | Passed, 1m23s; 0 .NET warnings and 0 errors |
| UI check | Passed; typecheck and oxlint passed; 64 files and 356 tests passed in 309.15s |
| .NET tests | Passed: `SqlHarness.Tests` 4,588 passed / 11 skipped; `SqlHarness.Mcp.Tests` 480 passed / 4 skipped |
| Format | Skipped: 0 changed C# files |

The UI log contains the existing non-failing Node `[DEP0190]` child-process warning. The .NET build has zero warnings and errors. The UI log contains no `ECONNREFUSED`, unmatched-request, or unhandled-rejection diagnostic. This closes the deferred Plan 084 UI diagnostic Minor; it does not describe the log as warning-free.

Final gate logs are retained outside the Git worktree at `D:/temp/sqlharness-plan091-final-gate-20261021/`. Copies of the build, UI check and both .NET test logs are archived at `D:/temp/sqlharness-plan091-gate-20261021/sdd-evidence/` as `final-fix-build.log`, `final-fix-ui-check.log`, `final-fix-test-SqlHarness.Tests.log` and `final-fix-test-SqlHarness.Mcp.Tests.log`. UI install and format were skipped, so no logs were emitted for those stages. Task 1 baseline, trace, RED/GREEN, request-owner and review artifacts are archived in that same `sdd-evidence/` directory.

## Final method-aware fixture fix

Regression command: `vitest run src/test/network.test.ts --reporter=verbose` from `src/SqlHarness.Dashboard/ui` using `.\node_modules\.bin\vitest.cmd`. Before implementation, the new wrong-method test failed because `PUT /api/settings` resolved with the registered GET 200 response; the explicit `POST /api/prices/refresh` fixture failed as unmatched. This was the expected RED result.

The consolidated focused rerun passed 9 files / 118 tests. `npm.cmd run lint` and `npm.cmd run typecheck` passed. The required full Windows gate above passed after the test fix commit.

## Completion boundary

Task 2 gate and evidence work is complete, and the final Sol low scoped review of `857402d` is approved. Plan 091 is DONE on the feature branch; local merge into `main` is pending. No merge or push is claimed in this evidence. The product gate need not be repeated for docs-only changes.
