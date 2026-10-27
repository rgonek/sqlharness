# Task 5 UI report

Commit: 1c8c0d376e315fb99d1f1281e27c656f66732d72 (parent 8808f1f7). Scope: nullable statement grant and spill types and display in shared statement metric details; no backend changes.

Files: `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.tsx`, `src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.test.tsx`.

TDD: before implementation, `.\node_modules\.bin\vitest.cmd run src/components/optimization/IoComparison.test.tsx --pool=threads --maxWorkers=1` failed the two added tests (6 passed). After implementation and correcting locale-dependent test fixture, from `src/SqlHarness.Dashboard/ui`, `.\node_modules\.bin\vitest.cmd run src/components/optimization/IoComparison.test.tsx src/components/optimization/StatementRows.test.tsx --pool=threads --maxWorkers=1` passed 13 tests in 2 files; `npm run typecheck` passed. `git diff --check` passed (Git reported LF-to-CRLF warning for test file).

Self-review: null and zero are distinct; spill true/false/null are Yes/No/—; grant values are per-side with no sum or delta. Existing single-case unavailability, selected-run repetition and exact plan links remain in place. Shared measured view displays after only. Backend owns evidence eligibility and attribution. Focused tests and typecheck only; full gate, live database, browser validation, merge and push were not performed.
