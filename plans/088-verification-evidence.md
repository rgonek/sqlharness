# Plan 088 verification evidence

Implementation is on feature branch `plan-088`, based on `272d5db`. The final tested product revision is `a737e6d1a093319ab09af672ff7bd417ba686f9f` plus the working-tree deletion of the unused `PlanTree.tsx` and `PlanTree.test.tsx`. Tasks 0–6 passed their task reviews; Task 7 documentation, dead-path cleanup, and Windows branch gate are complete. Task 7 review and the requested Sol low whole-branch review remain pending. This is feature-branch evidence, not an integrated-tree gate.

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
| 7 — documentation and verification | this evidence commit (`docs(088): record plan 088 evidence`) | Task review pending |

The task reports, renderer benchmark, and gate-fix report/review are in [088-evidence](088-evidence/). UI synthetic-browser evidence confirms canvas interaction and Monaco integration; it is not live API or user-acceptance evidence.

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

## Deferred review notes

The task ledger carries three minor observations to the final review: the source-map API had limited multi-case/multi-document selection coverage; a SQL selection can search links outside the selected document and choose a statement absent from the visible document; and the source-map builder is large and combines several responsibilities. Task 6 also recorded two non-failing localhost connection-refused diagnostics and the existing `DEP0190` notice; these are not pristine-console evidence. The final review should triage the deferred observations against the completed branch.
