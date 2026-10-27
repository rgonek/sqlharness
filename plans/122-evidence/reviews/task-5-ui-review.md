### Spec Compliance

- ✅ UI scope compliant: per-side nullable grants retain KB units and zero, and spill renders true/false/null as Yes/No/— (`src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.tsx:24`, `:29`); no grant aggregation or delta is introduced. The existing single-case unavailability guard remains in effect (`src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx:45`, `:51`).
- ✅ Selected-run repetition and exact plan links remain exposed (`StatementRows.tsx:30`, `:69`, `:70`; `IoComparison.tsx:44`); the measured view renders only after-side metric values (`StatementRows.tsx:27`, `:29`, `:74`).
- ⚠️ Backend extraction eligibility, historical-session isolation, browser behavior and full verification gate cannot be established from this UI diff; they require their respective backend review and final validation. Reported focused test/typecheck results were not rerun and raw output was not provided in the report.

### Strengths

- The tests exercise zero versus missing grants, tri-state spills, provenance and the all-cases guard (`src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.test.tsx:68`, `:80`).
- The UI types align with the nullable per-side backend model (`src/SqlHarness.Dashboard/ui/src/api/types.ts:187`; `src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs:165`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- None.

#### Minor (Nice to Have)

- `src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.test.tsx:68`: the new tests exercise comparison only; add a measured-view case with distinct before/after values if expanding coverage, to protect the after-only contract and measured plan link.
- The implementer reported an LF-to-CRLF warning from `git diff --check`; record or resolve the line-ending noise if it recurs in the final gate.

### Assessment

**Task quality:** Approved.

**Reasoning:** The small presentation change preserves nullable semantics and the existing source context. The remaining measured-view coverage gap is non-blocking because the relevant rendering path is explicit.

**Focused outside-diff checks:** Checked `OptimizationModels.cs` and `OptimizationComparer.cs` / `JournalReader.cs` for the named risk of UI field-name mismatch and per-side availability; checked `StatementRows.tsx:69-74` for the named risk that measured rows lose the exact after-plan link. No tests rerun.
