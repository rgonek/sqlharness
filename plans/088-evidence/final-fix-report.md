# Plan 088 final review fix report

Base: `f65dd68d1db15a677ff1f4380a0b23dd404415e1` (`plan-088`). The final review found one Important UI defect and three Minor findings. The final review is preserved in [plans/088-evidence/final-review-f65dd68.md](final-review-f65dd68.md).

## Changes

- SQL selection now considers links from the selected plan document SHA and ordinal when the document filter is active. If no link in that document overlaps the SQL selection, the workbench preserves its current statement and displays an explicit no-match status.
- The UI regression uses two documents with overlapping source spans and the same node ID assigned to different statement indexes. It also checks the no-eligible-link path.
- The README now documents that sensitive storage must be enabled both when an operation is recorded and when its data is read. Turning the setting off hides retained plan/SQL data without deleting it.
- The API test seeds two cases, two repetitions and multiple documents, selects case B/repetition 2/document ordinal 1, and checks exact executed SQL and document-scoped links. The endpoint already handled these selectors correctly; no API production code changed.
- Deferred the 883-line `SourceMapBuilder` decomposition to the future inference-extension plan. No broad refactor was needed for these findings.
- Updated the Plan 088 plan, index, history and permanent evidence with the fix status, review boundaries and verification.

## Focused evidence

UI RED command:

```powershell
npm --prefix src/SqlHarness.Dashboard/ui test -- src/components/VariantPanel.test.tsx --reporter=dot
```

Result: expected failure in the new test. The test selected document B and statement 1, then selected SQL overlapping document A's statement 0 and document B's statement 1. The old handler changed the Statement selector to `0` (`expected 1; received 0`); 1 failed and 4 existing tests passed. npm printed an unknown-config warning for `--reporter`, which did not affect Vitest.

UI GREEN command:

```powershell
npm --prefix src/SqlHarness.Dashboard/ui run test -- src/components/VariantPanel.test.tsx --reporter=dot
```

Result after the UI fix: 1 file and 5 tests passed. The test then selects a range that overlaps only document A and confirms that document B keeps statement 1 selected and reports the missing match.

API coverage command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Plan_source_map_selects_case_repetition_and_document --logger "console;verbosity=minimal"
```

The first build found a nullable expected-array type mismatch in the new assertion; after making the expected element type explicit, the test passed 1/1 against the original endpoint. As a controlled regression check, I temporarily bypassed the repetition filter in `JournalReader.PlanSourceMap`; the test failed because the response included the other repetition's document. Restoring the original filter returned the test to 1/1. `JournalReader.cs` has no final diff.

Pre-gate checks passed:

```powershell
npm run typecheck
npm run lint -- src/components/plan/PlanWorkbench.tsx src/components/VariantPanel.test.tsx
git diff --check
```

Typecheck and lint exited 0. `git diff --check` exited 0 and emitted only the repository's LF-to-CRLF working-copy notices.

## Gate evidence

The first full gate used `D:/temp/sqlharness-plan088-finalfix-gate-20261018-0bf303e4`. Build passed in 1m18s, UI passed, and Core/MCP tests passed. Formatting then failed only because `DashboardServerTests.cs` lacked its final newline (`FINALNEWLINE`). The complete failure is in `format.log` in that directory. `just fix` returned `format: OK`; focused formatting then passed:

```powershell
dotnet format SqlHarness.sln --no-restore --include tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs --verify-no-changes
```

The formatting adjustment changed only the new test file's final newline. The full rerun used a new directory, preserving the first attempt:

```powershell
just verify -LogDirectory D:/temp/sqlharness-plan088-finalfix-gate-20261018-0bf303e4-formatted
```

Result: `verify: OK stages=5 6m15s` on base HEAD `f65dd68d1db15a677ff1f4380a0b23dd404415e1` plus the final UI implementation, UI test, API test and C# formatting adjustment. UI install skipped because the lockfile was unchanged. Build passed with 0 warnings and 0 errors; UI passed 59 files / 301 tests; Core passed 4,376 tests with 11 skipped; MCP passed 430 tests with 4 skipped; formatting passed for 1 changed C# file.

Logs are in `D:/temp/sqlharness-plan088-finalfix-gate-20261018-0bf303e4-formatted`; the first attempt and failed format log remain in the unsuffixed directory. The final UI log contains the existing `DEP0190` notice and two non-failing, unidentified `ECONNREFUSED` attempts to localhost ports over IPv6 and IPv4. These runs are not described as pristine-console evidence.

## Review boundaries and remaining work

- The final scoped Sol low re-review is archived in [final-rereview-f503c22.md](final-rereview-f503c22.md); it approved the fix wave with no new findings and marked it ready to merge.
- No live SQL Server plan, UAT or database write was performed. Their fidelity/acceptance remains unverified because the review scope used synthetic fixtures and no live validation was authorized.
- Linux verification was explicitly skipped and remains unverified.
- `SourceMapBuilder` remains concentrated at 883 lines; its decomposition is deferred to future inference-rule work. The review's other non-goals (expression lineage, module fetching, estimated plans, query capture, plan comparison and PostgreSQL graphs) remain outside Plan 088.
- At the time this feature-branch report was written, no merge, rebase, push or force operation had occurred.

## Commit

Committed as `f503c2203ff8c20356ed838a2c8ab29183722d94` (`feat(088): scope SQL selection to selected document`). The post-commit worktree is clean. The final verification evidence records the tested product tree and full gate log directory; the only documentation adjustment after that gate corrected Task 7's already-approved review status in the evidence table. The final scoped Sol low re-review is archived in [final-rereview-f503c22.md](final-rereview-f503c22.md) and approved the fix wave.
