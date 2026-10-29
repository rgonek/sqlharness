### Spec Compliance

- ❌ Issues found: Selecting A from a different conditions group can discard the selected version when B is unset (OptimizationSection.tsx:85,119). This violates independent group/version selection.
- ⚠️ Pairing, journal migration, and sensitive-data guarantees are outside this UI diff; full gate and browser acceptance remain Task 5. No backward-compatibility assessment was applied.

### Strengths

- Shared rows use group fingerprint plus version identity, scope union, per-group badges, and group-aware deltas (OptimizationSection.tsx:53-56; VersionTable.tsx:23-34).
- Cross-group UI suppresses equivalence and mixed verdicts, shows the approximate notice and explicit row variation (ComparisonPanel.tsx:80-100). Zero paired cases suppress IO totals, while SQL diff and plan remain reachable (ComparisonPanel.tsx:106-109; VersionPlan.tsx:30).
- Group B survives operation return navigation (sessionSearch.ts:12,29; router.tsx:30,70). Tests cover cross-group B, duplicate labels, stale B and scope union (OptimizationSection.test.tsx:209-269).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- OptimizationSection.tsx:85-87,118-119 — With no B selected, selecting A in another group writes the previous A group into groupB even though b is undefined. The stale-B effect then considers this invalid and clears a as well; the UI falls back to the new group's default A, which can differ from the user's selection. Only preserve groupB when a B actually exists, and ensure invalid-B recovery does not discard a valid explicit A. Add a test with no default B and a non-default A selected in another group.

#### Minor (Nice to Have)

- VersionTable.test.tsx:43-46 — The test named for a missing rows dash never asserts the dash in the null row. Assert its Rows cell specifically, and cover explicit variation separately to guard the missing-versus-varies contract.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** Core shared-table and approximate rendering paths are present, but a valid A selection can be silently replaced during cross-group navigation. Fix this before task approval.

**Checks and limits:** Read task diff and reported test evidence; did not rerun tests. Focused unchanged-code check: ComparisonPanel.tsx:25-43 confirms zero-pair action references select an available action case for scripts. No broader crawl or git rederivation.
