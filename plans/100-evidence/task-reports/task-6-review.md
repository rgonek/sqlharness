### Spec Compliance

- ✅ Spec compliant. Both selectors use controlled values and preserve the existing route update path. Group changes select the first sorted scope and reset exactly `a`, `b`, `case`, `agg`, and `view` (src/SqlHarness.Dashboard/ui/src/components/optimization/OptimizationSection.tsx:69, :82); scope changes use the same reset path (:94). Tests verify each cleared URL key, the first scope, and resulting A/B defaults (src/SqlHarness.Dashboard/ui/src/components/optimization/OptimizationSection.test.tsx:125, :140).
- ⚠️ Cannot verify from the supplied test evidence: task-6-report.md names focused-final.log, typecheck.log, and lint.log under src/SqlHarness.Dashboard/ui/scratch/task-6, but that directory is absent. The report claims the commands passed; I did not rerun them per instruction. The unchanged request builder is parameterized by current comparison state (OptimizationSection.tsx:61-67; api/queries.ts:192-200).

### Strengths

- Explicit item labels map fingerprints to group labels and scopes to readable names (OptimizationSection.tsx:80, :92).
- The original multi-choice guards remain, and tests cover hidden one-choice Products controls plus per-subject accessible names (OptimizationSection.tsx:78, :90; OptimizationSection.test.tsx:148).
- VersionTable remains native: A and B use separate radio names, and the test selects both sides within the named versions table (VersionTable.tsx:21-22; OptimizationSection.test.tsx:79-84).
- The changed tests cover group and scope resets for every dependent URL key and validate resulting A/B choices and the All cases default (OptimizationSection.test.tsx:105-145).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- None.

#### Minor (Nice to Have)

- OptimizationSection.test.tsx:112 checks the initially URL-selected group only for presence; the label assertions at :117 and :138, :139 occur after a selection. Add an assertion that a non-default group and scope selected by the initial URL render their readable labels. The implementation supplies the label mappings, so this is a coverage gap rather than a found runtime defect.
- OptimizationSection.test.tsx:125 verifies navigation state and resulting selections, but not the comparison request after a group or scope change. Add a focused assertion that the subsequent request carries the selected group/scope and reset/default comparison parameters. The unchanged query builder uses those values (api/queries.ts:192-200); the diff does not test the emitted request.

### Assessment

**Task quality:** Approved

**Reasoning:** The implementation preserves the existing selection, reset, conditional-rendering, and request-construction paths while adapting the controls to Base UI. The remaining code-review items are focused test-coverage improvements; the task report's validation output is unavailable for inspection.

