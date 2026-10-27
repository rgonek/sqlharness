# Task 4 UI report

Commit: 8808f1f7. Files: `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx`, `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.test.tsx`.

Shows nullable provider affected-row total in mutation metrics (zero preserved; missing unavailable), and recorded request state, optional status and verification, and recorded timestamps in the existing Approval tab with Mutations link. No settings lookup or inferred lifecycle. Status is labeled recorded status rather than mutation result. Existing layout and controls retained.

TDD: from UI directory `./node_modules/.bin/vitest.cmd run src/pages/OperationPage.test.tsx --pool=threads --maxWorkers=1` initially failed 4 new cases because Rows affected was absent (30 passed); after implementation and scoping assertions to Approval panel, 34 passed. `npm run typecheck` passed (exit 0). `git diff --check` passed. Self-review inspected complete product diff and contract names in DashboardModels.cs. Focused tests cover 0 and positive, no linked request/old missing data, denial/null fields and recorded timestamps. No full suite, gate, live DB, merge or push performed.

Concern: request timestamps are displayed as the saved strings, deliberately without deriving events or settings. Backend controls eligibility and unknown outcomes; UI trusts nullable contract. Task 6 placeholder removal remains separate.
