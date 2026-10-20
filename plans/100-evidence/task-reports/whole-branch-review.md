# Plan 100 whole-branch independent review


> Archive note: relative source and evidence links resolve from this archived location; review findings and verdict are unchanged.
Base `344e5b709c2ad6c6c7c77c58f754525d0175ca54`; head `6e3d90b`. Read-only review against `../../100-shadcn-primitive-adoption.md`, the revised 2026-10-18 design, AGENTS.md, the complete product/test/docs diff, task reviews, and `../../100-verification-evidence.md`. Large archived failed-test transcripts were assessed through their task summaries and final evidence, not treated as new failures. No commands were rerun.

## Strengths

- All 19 planned pickers use explicit value/label mappings and controlled callbacks; the dimension filter retains distinct All/missing/empty-concrete encodings. The integration tests exercise selection effects and reset chains, not only trigger text.
- The independent Collapsibles and multiple compare Accordion retain descendants with `keepMounted`. The tests and both-theme browser fixtures cover hidden content, independent reopening, retained diff mode and editor sizing.
- StatusBadge composes one Base UI trigger inside its Dialog root and tests tooltip, keyboard activation, Escape and focus return. The table conversions preserve row headers and the native A/B radio groups; approval dispatch and policy remain unchanged.
- The final inventory classifies exactly four semantic source sites. Source provenance is recorded, no new dependency was introduced by this branch, and the final Windows gate passed on product revision `3caacc3`. The later head changes are evidence/documentation only.

## Issues

### Critical (Must Fix)

None.

### Important (Should Fix)

None.

### Minor (Nice to Have)

1. `src/SqlHarness.Dashboard/ui/src/components/optimization/OptimizationSection.test.tsx:112`: The initial URL-selected nondefault group/scope is checked for presence but not for its rendered readable labels. The implementation supplies labels and subsequent selections are asserted. Add a direct initial-label assertion in a follow-up if this area changes.
2. `src/SqlHarness.Dashboard/ui/src/components/optimization/OptimizationSection.test.tsx:125`: Group/scope tests assert navigation state and resulting defaults but do not inspect the emitted comparison request. The unchanged query builder uses the selected state. A request assertion would strengthen coverage during a later optimization-flow change.
3. `../task-reports/task-11-report.md:1`: The whitespace cleanup removed Markdown hard breaks from archived Worktree/Branch metadata lines. This is an archive presentation issue with no effect on product behavior or verification results; ordinary Markdown line breaks or a small metadata table can restore the formatting if the archive is edited again.

The baseline `ECONNREFUSED`, Node `DEP0190`, and large-chunk output are correctly disclosed as non-failing diagnostics in the final evidence. They are not branch defects and do not need a merge fix. The disclosed audit result (2 LOW production, 7 HIGH dev plus the same 2 LOW in full audit) matches the predecessor and adds no dependency change in Plan 100.

## Declined to judge

None. Unchanged code was inspected only where needed to check cross-task integration, including the comparison request builder and approval action path.

## Recommendations

Keep the two Task 6 assertions as focused test follow-ups. Retain the named final-gate logs and browser fixtures with the branch evidence.

## Assessment

**Ready to merge: Yes.** The implementation meets the revised plan and has no critical or important finding in this review. The remaining items are low-risk coverage and archive-format polish. This verdict covers review readiness only; merge, push, Linux verification, and live actions were not performed.
