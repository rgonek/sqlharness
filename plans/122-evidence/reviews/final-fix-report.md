# Plan 122 final fix wave

Base b30a11e1; product fixes 73189eae. Scope: remove contradictory statement-memory copy while preserving batch aggregate label; prune unavailable columns on view transition without effect-state cascade; add false truncation, measured-after grant/spill and concurrent legacy-key resume coverage. Existing migration fixture set version 21 but had retained the four v25 columns; migration therefore rolled back on duplicate column. Dropping those four columns in the fixture restores a valid v21 schema and exercises the complete migration. No product migration behavior changed.

## Verification and diagnosis

- `node_modules/.bin/vitest.cmd run src/components/ActivityOperations.test.tsx --pool=forks --maxWorkers=1`: passed 2 tests, 7.99 s.
- Same command with `--pool=threads`: passed 2 tests, 8.35 s.
- `node_modules/.bin/vitest.cmd run src/components/ActivityOperations.test.tsx src/components/optimization/IoComparison.test.tsx --pool=threads --maxWorkers=1`: passed 13 tests, 50.06 s under concurrent build/check load.
- `node_modules/.bin/vitest.cmd run src/components/optimization/IoComparison.test.tsx --pool=forks --maxWorkers=1`: passed 11 tests, 32.40 s.
- `npm run typecheck; npm run lint`: both passed, no warning emitted.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~Dashboard_evidence_' -p:BuildProjectReferences=false`: passed 2 tests after fixture correction. Earlier run failed because v25 columns remained in v21 fixture; reproduced twice before correction.
- Node 24.18.0, Vitest 5.0.3, no NODE environment variables. Inspected installed Vitest lifecycle: fork uses child_process.fork with piped stdio and advanced IPC; threads use Worker with piped stdio. Both successfully start and stop in these reproductions. Prior full-gate hang before first test is not explained by these observations, so no worker config or timeout was changed. Focused success does not establish full-gate success; controller must rerun the normal Windows gate.
- Final AST negative-spill check overhead and actual SQLite growth remain unmeasured; no performance claim or live provider acceptance follows.
- No live DB, install, merge, push or Linux gate.

## Format

`pwsh -NoProfile -File scripts/format-changed.ps1 -Mode branch -BaseRef main` initially failed on branch C# whitespace, imports and a static-local-function analyzer issue. Branch-scoped fix and recheck results are appended below.

Self-review: inspected product diff and format diff; view change prunes columns even on external prop rerender, retains LOB logical, and hides unsupported columns immediately. Measured assertions use distinct before/after values. The legacy migration fixture retains the preexisting old rows and verifies all eight concurrent resumes remain unmarked. Formatting commit a1e5cdb1 consists of whitespace, import ordering and adding static to a test local function; no intended runtime behavior change. `git diff --cached --check` passed for both commits.
`pwsh -NoProfile -File scripts/format-changed.ps1 -Mode branch -BaseRef main -Fix` succeeded; final HEAD a1e5cdb1 recheck without -Fix passed (31 files).
