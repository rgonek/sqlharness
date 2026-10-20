### Spec Compliance

- ✅ Spec compliant: all five picker sites keep their existing domain values and map values to readable labels (`PlanChanges.tsx:102`, `PlanWorkbench.tsx:231`, `PlanWorkbench.tsx:246`, `PlanWorkbench.tsx:259`, `PlanStatementSwitcher.tsx:18`). The occurrence picker still uses `choiceKey`; repetition and statement values remain numeric.
- ✅ Distinct reset paths are retained for case, repetition, document, and statement changes (`PlanWorkbench.tsx:231`, `PlanWorkbench.tsx:246`, `PlanWorkbench.tsx:259`, `PlanWorkbench.tsx:276`).
- ✅ The tests exercise every picker, selected node before case change, selected SQL span before repetition change, duplicate statement IDs across documents, omitted/empty document choices, changed cases, and document selection after closing/reopening Plans (`PlanWorkbench.test.tsx:97`, `PlanWorkbench.test.tsx:110`, `PlanWorkbench.test.tsx:115`, `PlanWorkbench.test.tsx:129`, `PlanWorkbench.test.tsx:136`, `PlanWorkbench.test.tsx:146`, `CompareDetail.test.tsx:259`, `CompareDetail.test.tsx:269`). Occurrence choices are selected and their result labels and plans are asserted (`PlanChanges.test.tsx:73`, `PlanChanges.test.tsx:95`).
- ⚠️ Verification commands were not rerun, per instruction. The implementer report records focused Vitest, typecheck, and lint success; browser sizing was checked from the retained ``.

### Strengths

- The trigger labels are decoupled from encoded keys through each Select item-label mapping; the document picker retains “All plan documents” and descriptive document labels (`PlanChanges.tsx:102`, `PlanWorkbench.tsx:259`).
- Case and repetition reset logic remains separate, and the test drives the real Workbench callback state through selected-node and SQL-selection actions (`PlanWorkbench.tsx:231`, `PlanWorkbench.tsx:246`, `PlanWorkbench.test.tsx:110`, `PlanWorkbench.test.tsx:136`).
- CompareDetail verifies that the second document remains selected after the Plans disclosure closes and reopens (`CompareDetail.test.tsx:269`, `CompareDetail.test.tsx:271`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- None.

#### Minor (Nice to Have)

- `task-7-report.md:45`: the report footer gives a full commit SHA that differs from the reviewed checkout. The actual reviewed HEAD is `d3dbd543654664430fc4e29b7b594e4707fc73da`. Correct the footer so the evidence artifact identifies the reviewed commit.

### Assessment

**Task quality:** Approved

**Reasoning:** The implementation preserves picker identities, labels, and distinct reset behavior, with focused interaction coverage for the requested edge cases. The stale report SHA is a minor evidence issue and does not change the reviewed diff.