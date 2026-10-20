### Spec Compliance

- ✅ Spec compliant. `src/SqlHarness.Dashboard/ui/src/components/StatusBadge.tsx:20,22,30` places the tooltip trigger and dialog content under one uncontrolled Dialog root, with TooltipTrigger rendering DialogTrigger. The local wrappers forward trigger props to the Base UI primitives (`src/SqlHarness.Dashboard/ui/src/components/ui/tooltip.tsx:21-22`, `src/SqlHarness.Dashboard/ui/src/components/ui/dialog.tsx:12-13`).
- ✅ The existing StatusFields contract, badge branches/tiers, trigger text, dialog title and description, error text, and missing-message fallback remain (`src/SqlHarness.Dashboard/ui/src/components/StatusBadge.tsx:8-18,32-39`). The open trigger's aria-controls relationship is asserted (`src/SqlHarness.Dashboard/ui/src/components/StatusBadge.test.tsx:50-64`).
- ✅ Interaction requirements are covered: hover and focus expose the tooltip; Enter opens the dialog; Escape closes it and restores focus; click opens each badge's own dialog; the two error badges expose exactly two effective buttons (`src/SqlHarness.Dashboard/ui/src/components/StatusBadge.test.tsx:44-86`). The recorded browser fixture exercises the same lifecycle and reports both tooltip states, focus return, popup relation, and badge isolation (`:18-49`, `:2-8`).

### Strengths

- The component has one Dialog owner per failed/rejected badge, no manual open state, and no nested trigger element (`StatusBadge.tsx:20-30`).
- The new tests verify observable accessibility and interaction behavior, including focus return and separate error content for multiple badges (`StatusBadge.test.tsx:50-86`).
- The original explanatory text and safe text rendering remain intact (`StatusBadge.tsx:32-39`; `StatusBadge.test.tsx:25-42`).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

None.

#### Minor (Nice to Have)

None.

### Assessment

**Task quality:** Approved

**Reasoning:** The implementation matches the requested Base UI composition and preserves StatusBadge behavior and content. Focused test, typecheck, lint, and browser evidence is recorded in the report; this review did not rerun suites or claim the full Plan 100 gate.
