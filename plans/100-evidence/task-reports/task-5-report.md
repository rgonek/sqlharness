# Task 5 implementation report

Implemented Plan 100 Task 5 on `plan-100`, starting at `fe20a52`.

## Changes

- Replaced the dimension filter, matrix limit, row, column and metric native selects with the installed Base UI Select primitive.
- Kept `all:`, `missing:` and `value:<value>` as distinct UI values. Empty concrete values render as `(empty)` for clarity while retaining the `value:` identity.
- Preserved dynamic option labels, numeric limit conversion, disabled column options and both axis auto-swap paths.
- Added StatsPage coverage for all four filter outcomes, the serialized ordinary/missing/empty requests and unfiltered query state, keyboard and pointer handling of a disabled axis, row/column swaps, metric output, and adapted existing filter/matrix interactions to Base UI.

## Verification

- `./node_modules/.bin/vitest run src/pages/StatsPage.test.tsx` — passed, 1 file / 13 tests.
- `npm run typecheck` — passed.
- `npm run lint` — passed.
- Browser fixture at 390px — document width remained 390px; all dimension filter choices appeared and the current row was disabled in the column menu. Screenshot and runner are in `../task-artifacts/task-5/`.
- No backend, SQL or live database changes.

## Round 1 review fix

The review found that the original All-values request assertion could match the initial unfiltered request. The filter test now renders `ProfileDimensionsPanel` with an isolated `QueryClient` using `staleTime: 0`, pins callbacks for `{ region: "east" }`, `{ region: null }`, `{ region: "" }` and `{}`, and checks that selecting All adds a new `/api/stats` request without a `dimensions` parameter. This keeps the request assertion meaningful despite the app harness's `staleTime: Infinity` cache.

Commands were run from `src/SqlHarness.Dashboard/ui`:

- `npm exec -- vitest run src/pages/StatsPage.test.tsx` — exit 0; 1 file / 13 tests passed. Raw output: ``.
- `npm run typecheck` — exit 0 (`tsc -b`). Raw output: ``.
- `npm run lint` — exit 0 (`oxlint`). Raw output: ``.
