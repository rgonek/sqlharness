# Plan 088 verification evidence

Implementation is on feature branch `plan-088`, based on `272d5db`. Task 7 review is approved. The final-review fix wave and its Windows gate are complete; Sol low's scoped re-review of those final fixes remains pending. This is feature-branch evidence, not an integrated-tree gate.

## Task commits and reviews

| Task | Implementation range | Review result |
|---|---|---|
| 0 — renderer benchmark | `272d5db..cdfbb81` | `/root/review0`: approved after fix round 1 |
| 1 — plan documents and occurrences | `cdfbb81..924ce37` | `/root/review1`: spec compliant, quality approved |
| 2 — statement spans | `924ce37..b2d838a` | `/root/review2_sol` (Sol low): spec compliant, quality approved after fix round 1 |
| 3 — plan graph | `b2d838a..bab2f69` | `/root/review3` (Sol low): spec compliant, quality approved after fix round 1 |
| 4 — source map | `bab2f69..9f3933e` | `/root/review4` (Sol low): spec compliant, quality approved after fix rounds 1–2 |
| 5 — dashboard API | `9f3933e..1137891` | `/root/review5` (Sol low): spec compliant, quality approved |
| 6 — viewer and linking | `1137891..c1676d8` | `/root/review6` (Sol low): spec compliant, quality approved after fix round 1 |
| Gate fix — test expectations | `c1676d8..a737e6d` | Sol low `gate-fix-review.md`: approved, no findings; 65 focused tests passed |
| 7 — documentation and verification | `a737e6d..f65dd68` | `/root/review7` (Luna xhigh): spec compliant, quality approved |

The task reports, renderer benchmark, and gate-fix report/review are in [088-evidence](088-evidence/). UI synthetic-browser evidence confirms canvas interaction and Monaco integration; it is not live API or user-acceptance evidence. The whole-branch review of `f65dd68` is preserved as [final-review-f65dd68.md](088-evidence/final-review-f65dd68.md).

## Final gate

Command: `just verify -LogDirectory D:/temp/sqlharness-plan088-final-gate-a737e6d` on feature branch `plan-088`; tested HEAD was `a737e6d1a093319ab09af672ff7bd417ba686f9f` plus the two listed `PlanTree` deletions. The gate passed: `verify: OK stages=5 8m28s`. `ui-install` skipped because the lockfile was unchanged; build passed in 45.64s with zero warnings/errors; UI passed 59 files / 300 tests in 5m35s; Core passed 4,375 with 11 skipped in 2m31s; MCP passed 430 with 4 skipped in 2m42s; formatting skipped because no C# files changed. Logs are in `D:/temp/sqlharness-plan088-final-gate-a737e6d`.

The gate's UI log includes Node `DEP0190` and two non-failing localhost:3000 `ECONNREFUSED` diagnostics (IPv6 and IPv4), so this is not pristine-console evidence. Linux verification was not requested and remains unverified. No live database operation, merge, or push was performed. The user authorized a local merge after the green Windows gate; the controller will handle that after the requested reviews. No integrated-tree gate has run.

The earlier Task 7 gate on `c1676d8` failed two `BenchmarkRunTests` because whole-record expectations omitted the new in-memory `ExecutedSql` value. Commit `a737e6d` corrected only those test expectations, preserving JSON omission checks. Its focused regression set passed 65/65 and the Sol low scoped review approved it with no findings. The final gate above passed on the resulting feature branch.

The baseline gate at `272d5db` failed in `McpStderrLeakRegressionTests` before its stderr assertion: the client requested protocol `2026-07-28`, which the launched server did not support. The captured failure and available stage summaries are preserved in [the baseline output](088-evidence/evidence/baseline-verify-captured-output.txt). The original raw build, UI, and Core logs were overwritten, so their captured summaries cannot be independently checked against those full logs. This remains a baseline attribution risk; no unrelated MCP changes were made for it.

## Rulings carried from the task ledger

- Continue the bounded Plan 088 work despite the baseline MCP handshake failure; require a passing final gate and leave unrelated MCP behavior unchanged. If wrong, baseline failure attribution still needs separate diagnosis.
- Use a named internal 16 MiB per-operation budget for distinct raw UTF-8 plan documents; repeated SHAs cost zero and a later smaller document may fit after an omission. If wrong, large operations may omit too many or too few plans; the constant can be tuned.
- Bound graphs at 10,000 nodes and 1 MiB serialized UTF-8, and property responses at 128 KiB including the truncation marker. Preserve nodes and predicates or return an explicit error. If wrong, unusually large or verbose plans may error earlier than desired; limits can be tuned.
- Bound the complete manifest response at 1 MiB and source-map response at 16 MiB, including the envelope, SQL text, and links; return explicit 413 errors instead of clipping. If wrong, large operations may error earlier than desired; limits can be tuned.
- Preserve Plan Changes through a minimal adapter from `PlanGraph`, require the displayed variant/case/repetition/document selection, and show ambiguity when no occurrence is unique. If wrong, this adds reversible UI work or makes selection less convenient.

## Final review fix wave

The Sol low whole-branch review of `f65dd68` found one Important UI defect and three Minor findings; the full review and its declined-to-judge boundaries are retained in [final-review-f65dd68.md](088-evidence/final-review-f65dd68.md).

- **Fixed:** SQL selection now chooses statement links only from the selected document when the document filter is active. If that document has no overlapping link, the current statement stays selected and the UI reports the absence. The new interaction regression covers overlapping links from two documents, a reused node ID with different statement indexes, and an unmatched selection.
- **Fixed:** README now states that `journal.storeSensitive` must be enabled when the operation is recorded and at read time; disabling it hides retained data without deleting it.
- **Fixed with focused coverage:** the endpoint fixture includes multiple cases, repetitions, and documents. It selects case B, repetition 2, document ordinal 1, then asserts exact SQL and document-scoped links. The test passed on the unmodified endpoint; a temporary removal of repetition filtering made it fail with the extra repetition's document, and restoring the filter returned it to green.
- **Deferred minor:** keep the 883-line `SourceMapBuilder` intact; schedule decomposition with future inference-rule extensions. Cost if wrong: new inference rules may remain harder to review and extend.
- **Declined to verify:** live SQL Server plan fidelity and user acceptance were outside the synthetic-fixture scope and no live database/UAT was authorized. Cost if wrong: real server variants may expose gaps after merge.
- **Unverified by instruction:** Linux behavior remains unverified because Linux was explicitly skipped. Cost if wrong: Linux-specific behavior remains undiscovered.
- **Diagnostics:** preserve the two unidentified localhost `ECONNREFUSED` attempts and existing Node `DEP0190` as non-failing diagnostics, not pristine-console evidence. Cost if wrong: a hidden requestor or lifecycle issue may need later diagnosis.
- **Out of scope:** expression lineage, module fetching, estimated plans, query capture, plan comparison, and PostgreSQL graphs remain separate follow-up work. Cost if wrong: users needing these cases must wait for that follow-up.

Focused UI evidence: `npm --prefix src/SqlHarness.Dashboard/ui test -- src/components/VariantPanel.test.tsx --reporter=dot` failed for the expected bug (`Statement` changed from `1` to `0`, 1 failed/4 passed), then passed all 5 tests after the fix. npm emitted an unknown-config warning for `--reporter`; it did not affect the test result. Focused API evidence: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Plan_source_map_selects_case_repetition_and_document --logger "console;verbosity=minimal"` passed 1/1 on the existing endpoint. Temporarily removing the repetition filter made it fail because repetition 1's document was returned with repetition 2's; restoring the filter returned it to 1/1. The first API test build exposed a nullable expected-array type mismatch; the assertion was corrected before the test run. Typecheck (`npm run typecheck`), scoped lint (`npm run lint -- src/components/plan/PlanWorkbench.tsx src/components/VariantPanel.test.tsx`), and `git diff --check` passed before the gate. Commands and output are preserved in the unsuffixed final-fix log directory below.

The first full gate failed only at formatting because `DashboardServerTests.cs` lacked its final newline. `format.log` preserves that failure. `just fix` returned `format: OK`, and the focused `dotnet format SqlHarness.sln --no-restore --include tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs --verify-no-changes` check exited 0. No executable behavior changed after the failed gate.

Final-fix Windows gate command: `just verify -LogDirectory D:/temp/sqlharness-plan088-finalfix-gate-20261018-0bf303e4-formatted`. Tested product tree: HEAD `f65dd68d1db15a677ff1f4380a0b23dd404415e1` plus the final UI selection implementation, UI interaction test, and API selection test, including the final-newline formatting fix. Gate result: `verify: OK stages=5 6m15s`. UI install skipped because the lockfile was unchanged; build passed with 0 warnings/0 errors; UI passed 59 files / 301 tests; Core passed 4,376 tests with 11 skipped; MCP passed 430 tests with 4 skipped; formatting passed for 1 changed C# file. Logs are in `D:/temp/sqlharness-plan088-finalfix-gate-20261018-0bf303e4-formatted`.

The final UI log repeats Node `DEP0190` and two unidentified non-failing localhost `ECONNREFUSED` diagnostics to `::1:3000` and `127.0.0.1:3000`. They have no named caller/test and remain diagnostics, not pristine-console evidence. Linux verification was explicitly skipped; live SQL Server/UAT, merge, and push were not performed. Final scoped Sol low re-review remains pending.
