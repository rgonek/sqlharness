## Task 1: Correct Settings and A/B selection semantics

**Files:** `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx`, `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx`, related focused tests, optionally a small dedicated A/B selector helper if justified.

- [ ] Read the spec, audit F1/F2 and existing test/API patterns; consult official Field/Switch/RadioGroup docs using the shadcn skill.
- [ ] Add meaningful failing tests for visible switch label click, description/error linkage and invalid-number no-save behavior; exclusive A/B keyboard selection across versions.
- [ ] Compose Settings with installed Field/FieldLabel/FieldDescription/FieldError and explicit IDs/ARIA relationships. Preserve descriptions, restart tags, sound controls, save/confirmation rules and responsive layout. Avoid blanket typography/color overrides.
- [ ] Replace independent Toggle A/B with exclusive groups. Prefer default RadioGroup if valid composition across columns is feasible; otherwise native radios are permitted for valid table markup. Preserve circular intent without custom reimplementation of radio keyboard logic. Name each group and each version unambiguously. Do not insert div wrappers inside table structure or use invalid nested group wrappers. Document the chosen default-vs-mock tradeoff.
- [ ] Run focused tests, typecheck and lint, inspect diff, commit only task-owned source/test files. Write report to the brief-adjacent task-1-report.md including changed paths, tests and tradeoffs. Do not run full gate or change plan/index.
