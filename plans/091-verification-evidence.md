# Plan 091 verification evidence

## Scope and review status

Plan [091](091-ui-test-network-isolation.md) adds strict per-test network isolation to the dashboard UI test harness and explicit fixtures to the request-owning tests. This record covers the Task 1 review evidence and final Windows gate for the implementation. The Task 1 scoped review is approved; the final Sol low whole-branch review is pending. Plan 091 remains in progress until that review and any resulting documentation follow-up are complete.

- Worktree: `D:/Dev/sqlharness/.worktrees/plan-091`, branch `plan-091`.
- Tested revision: `838c3d252ee8639f728447efa2aa6a7e6485bb06` (`test(091): fail tests on swallowed network leaks`), tree `1d30db02289bacbc78910dead1954c33eca28efc`.
- Parent implementation commit: `1823f1a` (`test(091): isolate dashboard test network activity`).
- No product code, API contract, or dependency version changed. The changes are test harness code, test assertions and scenario-owned response fixtures.
- No live database, dashboard server, credentials, merge, or push was used. Linux was not run.

## Request ownership and before/after evidence

The pre-change full UI check passed 63 files / 349 tests but emitted two `AggregateError` diagnostics for connections refused at `::1:3000` and `127.0.0.1:3000`. Test-scoped tracing identified `src/router.test.tsx > unknown urls keep the navigation and show not found`: rendering the fallback route mounted the shared `AppLayout`, whose `useProfiles` and `useSettings` queries made unowned `GET /api/profiles` and `GET /api/settings` requests. The test had no matching fixture, so `fetch` reached the default localhost URL.

The Task 1 strict-guard discovery also recorded requests from other tests before their fixtures were completed. `task-1-round1-unmatched-owners.txt` maps each method/path to its exact test name; the owners include Approval, SettingsPage, OperationPage, AgentSessionPage, CompareDetail, OptimizationSection, ComparisonPanel, PlanChanges, PlanWorkbench, VariantPanel and SessionsPage tests. Those routes are now explicit in the owning scenario or local component helper, including intentional 404, 409, 422 and 503 responses. No catch-all route was added.

The test setup now wraps global `fetch` before each test. Requests must match a registered route; an unmatched method/path and Vitest test name are retained for the teardown assertion even when the API client catches the rejected fetch. Teardown unmounts the DOM, cancels QueryClient work registered by `renderApp`, checks for retained unexpected requests, and restores native `fetch` in `finally`. Regression tests cover method/path reporting, isolation between route tables, rendered-query error handling, and a pending query canceled at teardown. The intentional API-error assertions and the SQL diff size-limit assertion remain.

## Windows verification

Command: `just verify -LogDirectory D:/temp/sqlharness-plan091-gate-20261021`

The command exited 0: `verify: OK stages=5 11m23s`. It ran on the clean source tree at revision `838c3d252ee8639f728447efa2aa6a7e6485bb06`.

| Stage | Result |
|---|---|
| UI install | Passed, 52.6s |
| Build | Passed, 1m33s; 0 .NET warnings and 0 errors |
| UI check | Passed; typecheck and oxlint passed; 64 files and 354 tests passed in 399.25s |
| .NET tests | Passed: `SqlHarness.Tests` 4,588 passed / 11 skipped; `SqlHarness.Mcp.Tests` 480 passed / 4 skipped |
| Format | Skipped: 0 changed C# files |

The UI log contains the existing non-failing Node `[DEP0190]` child-process warning. The build log contains the existing non-failing Vite chunk-size warning for bundles over 500 kB; the .NET build itself has zero warnings and errors. The UI log contains no `ECONNREFUSED`, unmatched-request, or unhandled-rejection diagnostic. This closes the deferred Plan 084 UI diagnostic Minor; it does not describe the log as warning-free.

All raw logs are retained outside the Git worktree at `D:/temp/sqlharness-plan091-gate-20261021/`: `ui-install.log`, `build.log`, `ui-check.log`, `test-SqlHarness.Tests.log`, and `test-SqlHarness.Mcp.Tests.log`. Task 1 baseline, trace, RED/GREEN, request-owner and review artifacts remain in `.superpowers/sdd/091-ui-test-network-isolation/`.

## Completion boundary

Task 2 gate and evidence work is complete. The broader Sol low whole-branch review remains pending with the controller. Any controller-requested docs-only amendment should update this evidence, the Plan 091 status/checklist, the queue/history and Plan 084's Minor disposition. The product gate need not be repeated for docs-only changes.
