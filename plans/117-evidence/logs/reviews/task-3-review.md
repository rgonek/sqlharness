### Spec Compliance

- ❌ Issues found: The decision accepts a future map as complete, but the dashboard row names only an “existing dashboard compare model/history/presentation surface” and defers finding the owning TSX component until implementation is selected (`plans/117-evidence/decision.md:47`). The done criteria require a precise future file/test/contract map, and the binding scope calls for the existing dashboard compare presentation and tests. Name the current component/model and test file, or record this part as an explicit unresolved no-go with reopening criteria.
- ⚠️ Cannot verify from diff: Production capture allocations, heap use, and serializer size remain unmeasured. This is an acceptable study boundary and is clearly stated as such (`plans/117-evidence/decision.md:6,33`).

### Strengths

- The decision separates schema, ordered-row, unordered-row, and raw-value diagnostics, and keeps diagnostic availability independent from authoritative verdicts and budgets (`plans/117-evidence/decision.md:5-17`).
- The deterministic examples cover empty result sets, an equivalent verdict with partial diagnostics, item omissions, legacy/off availability, and both cross-side and same-side pair provenance (`plans/117-evidence/decision.md:21-29`).
- The future core/artifact/MCP test map includes concrete proposed test names and the requested focused command, while the report and archived log consistently label the work synthetic and offline (`plans/117-evidence/decision.md:42-55`; `task-3-report.md:3-5,25-31`; `plans/117-evidence/logs/task-3-verification.log:5-15`).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `plans/117-evidence/decision.md:47`: The dashboard entry is not a precise file/test map; it postpones identifying the component and existing presentation tests. This leaves the presentation work unbounded for the next implementation plan and misses the done criterion. Add the concrete current paths and proposed test surface, or explicitly mark the dashboard part unresolved and provide reopening criteria.

#### Minor (Nice to Have)

- `plans/117-equivalence-localization-study.md:10`; `plans/README.md:34`; `.superpowers/sdd/117-equivalence-localization-study/task-3-brief.md:3-5`: The study status and index row say RESEARCH COMPLETE, but the task/done checkboxes remain open and the completed row remains in the selected-studies queue. Reconcile the checklist and move the completed row to the history location required by the index convention.
- `plans/117-evidence/decision.md:5,27`: “Every comparison mode” can include `off` under the constraints, while the same decision says `eq-off-018` is unavailable. Clarify that localization applies to modes with an active comparison and that `off` is unavailable.

### Assessment

**Spec compliance:** Needs fixes.

**Code quality:** Approved.

**Overall task quality:** Needs fixes.

**Reasoning:** The offline decision is careful about provenance, caps, verdict isolation, and evidence limits. The dashboard map does not yet meet the required precision; the remaining findings are small status and wording inconsistencies.
