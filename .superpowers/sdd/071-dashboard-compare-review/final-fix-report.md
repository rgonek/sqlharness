# Plan 071 final review fix report

## Outcome

Added complete retained SQL-version inventories to each session subject/scope summary. The session page can now offer the full `v0` through latest hash choices immediately, even when the visible compare-attempt list has loaded only its first 100-row page or has no attempts for that scope on the current page. Attempt history remains paginated. The inventory carries only version labels and hashes, with payload size O(distinct retained versions).

When distinct hashes share a version label, the selectors append the final eight hash characters so the SQL choices are distinguishable. SQL text is still fetched only for the two selected hashes.

## Regression coverage

- UI test seeds a 100-item first compare page plus a next cursor, exposes two scopes, and leaves the target scope's attempts unloaded. Its complete aggregate inventory still provides `v0` and `v3` before **Load more compare attempts** is used.
- UI test verifies colliding version labels receive short hash suffixes.
- Journal builder test verifies full per-scope inventories retain all distinct version/hash pairs and keep different scopes separate.
- Dashboard serialization tests verify `scopeVersions`, `CompareScopeVersions`, and `CompareSqlVersion` match TypeScript contracts. HTTP session test checks the complete inventory is present while the compare-attempt endpoint remains paginated.

The regression tests were run before production changes and failed because the complete inventory was absent and duplicate labels rendered identically.

## Commands and results

- `npm run test -- src/components/SessionVersionDiff.test.tsx src/components/CompareSessionOverview.test.tsx` — passed, 17/17 tests.
- `dotnet test tests/SqlHarness.Tests --no-restore --filter 'FullyQualifiedName~CompareHistoryBuilderTests|FullyQualifiedName~DashboardContractTests|FullyQualifiedName~Compare_history_endpoint_pages_attempt_summaries'` — passed, 17/17 tests.
- `npm run check` — typecheck and lint passed; all 233 UI tests passed across 45 files. The full-suite run printed two native EventSource connection-refused AggregateErrors for `::1:3000`/`127.0.0.1:3000` and the existing Node `[DEP0190]` warning. Isolated `CompareSessionOverview.test.tsx` and `LivePage.test.tsx` each passed (10/10) without those traces. `new EventSource('/api/live')` is used only by `LivePage`, whose tests install a fake EventSource; the new session-page test stubs its API requests. No app/network behavior was changed for this output.
- `npm run build` — exit 0. Emitted main bundle (1,329.14 kB), lazy Monaco bundle (2,722.79 kB), and same-origin editor worker (303.93 kB).
- First `just verify` — UI-check, restore, build, and tests passed; format failed because the four changed C# files needed the repository's no-final-newline convention. `just fix` completed with `format: OK`.
- Final `just verify` — passed all six stages: UI-install skipped (lockfile unchanged), UI-check, restore, build, test, and format. Result: `verify: OK stages=6 3m04s`.
- `git diff --check` — passed; only Git's LF-to-CRLF worktree notices were printed for the two edited TypeScript files.

## Scope and constraints

No SQL text is included in the inventory; it does not trigger N+1 SQL fetches or introduce a metrics comparison across attempts. Existing SQL-only version diff behavior, CSP, endpoint bounds, and visible attempt pagination are unchanged. Linux verification and the existing manual browser fixture were not rerun for this focused selector fix.
