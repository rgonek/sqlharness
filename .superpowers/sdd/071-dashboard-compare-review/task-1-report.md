# Task 1 report: provider-neutral SQL editor and diff helpers

## Requirements completed

- Added provider-neutral SQL viewer/merge props, dialect selection, formatting normalization, bounded preview and combined byte/line checks in `ui/src/lib/sqlDiff.ts`.
- Added pure bounded unified diff formatting in `ui/src/lib/unifiedDiff.ts`; oversized direct calls throw `RangeError` before invoking `diff`.
- Added the Monaco React adapter with private PostgreSQL and T-SQL language IDs, local editor worker, read-only options, and light/dark themes following the document `dark` class.
- Added lazy React wrappers and the `loadUnifiedDiffText()` helper in `ui/src/components/SqlEditors.tsx`. The dashboard entrypoint imports this module so production build output includes its deferred Monaco assets; it does not render the wrappers.
- Moved the existing SQL diff limit helpers out of `SqlDiff.tsx` without changing its rendered behavior.
- Added the requested dependency ranges and helper tests. No CSP code changed.

## RED evidence

Command: `npm run test -- src/lib/sqlDiff.test.ts src/lib/unifiedDiff.test.ts`

Expected initial failure: both test suites failed to resolve `./sqlDiff` and `./unifiedDiff`, because the helper modules did not yet exist. During implementation, three unified-diff assertions also caught the package's no-final-newline markers and line matching; newline termination was normalized and all exact output cases then passed.

## GREEN evidence

- Focused tests: `npm run test -- src/lib/sqlDiff.test.ts src/lib/unifiedDiff.test.ts` — 2 files and 11 tests passed.
- Full UI check: `npm run check` — typecheck, lint, 38 test files and 200 tests passed. The check script printed Node's existing `DEP0190` child-process warning; it exited successfully.
- Production build: `npm run build` — succeeded.
- `git diff --check` — succeeded.

## Build and CSP evidence

Production outputs:

| Asset | Bytes |
| --- | ---: |
| Main `index-CHix8JWs.js` | 1,301,867 |
| Lazy `monaco-B23yeIax.js` | 2,722,015 |
| Lazy Monaco CSS `monaco-DjpsE8to.css` | 99,095 |
| Editor worker `editor.worker-DWPaKKV_.js` | 303,930 |
| Main CSS `index-BLid8emI.css` | 70,244 |

The main bundle contains dynamic import references to the separate Monaco chunk and stylesheet; the editor payload is in those lazy assets. The Monaco chunk references the emitted relative `editor.worker-DWPaKKV_.js`, and that asset is built beside the other dashboard assets. The output contained no `eval(` bootstrap. `DashboardSecurity.cs` is unchanged from `HEAD`; its CSP still has `script-src 'self'`, `style-src 'self' 'unsafe-inline'`, and `img-src 'self' data:`.

## Concerns

- The lazy vendor chunk still contains Monaco's inert default CDN fallback string and its internal blob-worker fallback implementation. Runtime setup calls `loader.config({ monaco })` and installs `MonacoEnvironment.getWorker` returning the emitted editor worker, so the configured path uses the local package and same-origin worker. The strings remain in the emitted vendor source because they are included by the upstream packages; this is a static-output caveat against a literal interpretation of the brief's no-CDN/no-blob reference check.
- Vite reports the 2.72 MB lazy Monaco chunk as larger than its default 500 kB advisory threshold. The main bundle does not contain that editor payload.
