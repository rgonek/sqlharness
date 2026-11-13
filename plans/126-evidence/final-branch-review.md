# Plan 126 whole-branch review

Reviewed 2026-11-11. Base `a274b0ecca09bd447c875849799194aea724bcd7`; head `794a1e0e1073e45790d097e65d8300837b980366`; worktree `C:/Users/rgone/MultiCliProfiles/codex/account-b/worktrees/8723/sqlharness`.

**Code approval: changes required.** Two P2 findings and one P3 finding below. This is a source/spec review, not final browser/native acceptance or a verification-gate result. No builds, tests, workload, mutation approval, product/index/HEAD changes or subagents were used. This report is outside the repository.

## Spec and behavior findings

### P2: A normalized stale case leaves the active version table updating forever

Location: `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx:31-35`. Related flow: `OptimizationSection.tsx:67-82,118-120`, `ComparisonPanel.tsx:70-74`, and `OptimizationComparer.cs:92-94,209`.

The new current-evidence check requires `comparison.selectedCase === sessionSearch.case`; any mismatch is treated as an ongoing update even when `pending` is false. The backend deliberately resolves an unknown case to `all`. No consumer reconciles that successful normalization back into route state. A/B selection preserves the old case, so ordinary pair changes can trigger this without manually editing a URL. In particular, a same-condition comparison uses measurement-fingerprint keys, while a cross-condition comparison pairs varying-fingerprint keys.

Scenario: select a single case in A/B, then select B from another conditions group where that key is absent. The request completes successfully with `selectedCase: "all"`. The active table nevertheless remains in `Updating comparison…`, with its numbers and outcome actions hidden indefinitely. The panel still prefers the invalid search case, displays its raw key, and cannot map it to reproduction scripts. A bookmarked stale case produces the same result.

Expected: distinguish a genuinely outstanding response from a settled server-normalized selection. Reconcile the case for the exact settled request (without accepting placeholder evidence), or reset invalid case state when the pair changes. Keep unrelated route/return state intact, and make table metrics, selector, scripts and response refer to the same resolved case.

Regression expectation: cover session and agent-session routes with a valid single case, a pair/conditions change that invalidates it, a delayed response, then a successful normalized `all` response. Assert no old evidence while pending; after settlement, no endless updating state, correct all-case aggregates/counts, a valid selector and correctly bound scripts. Also cover a stale bookmarked case.

Contract: Plan 126 review focus 3-4 and approved design workflow/navigation/feedback criteria require stale state to recover and selections/actions to remain correct. Existing pending tests cover a valid selected case that the response echoes; they do not cover server normalization.

### P2: PostgreSQL buffers bypass the pending-evidence boundary

Location: `src/SqlHarness.Dashboard/ui/src/components/optimization/ComparisonPanel.tsx:125`.

The newly moved `optimizationMetricRows(comparison)` paragraphs render directly inside the IO tab, outside `ComparisonPending`. After filtering CPU, this is the PostgreSQL Buffers evidence. The comparison query intentionally retains previous data during A/B, case and aggregate requests (`api/queries.ts:138,217,229`). Although IO rows, memory, provenance and outcome details are hidden during these transitions, this paragraph remains visible and accessible with the previous selection's counts.

Scenario: open the IO tab of a PostgreSQL comparison, select one case after viewing all cases, and delay the new response. The selector changes to the selected case while the buffer paragraph still reports the old all-case totals. A/B and sum/mean changes produce the same stale numeric evidence. This is a new exposure: the removed evidence drawer wrapped these metric rows in `ComparisonPending`.

Expected: place the buffer evidence and selection-dependent IO messages behind the same pending boundary as the rest of the comparison. Keep a local updating indication available, and expose the requested evidence only after it settles.

Regression expectation: render a PostgreSQL comparison with distinct buffer counts in the IO tab, transition to a pending case or A/B selection, and assert old buffer text is absent from both visible and accessible output; then settle the new counts. A delayed-response integration check should cover at least one shared consumer.

Contract: Plan 126 review focus 3 and the approved trust/feedback criteria prohibit old comparison evidence from appearing current.

### P3: Baseline IO coverage counts ambiguous groups as single cases

Location: `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx:77`.

For the active pair, baseline coverage uses `caseOutcomes.filter(...).length` and `caseOutcomes.length`. The backend now intentionally groups colliding identities into an unavailable row with `caseCount > 1`, and `OptimizationOutcomes.Count` correctly weights that row. The UI coverage ignores the weight.

Scenario: two ambiguous cases and one uniquely measured case produce three retained cases. Results/Policy counts correctly include all three, but baseline IO displays `IO 1/2 measured` instead of `IO 1/3 measured`; selecting only the two-case ambiguous group displays `0/1` instead of `0/2`. This understates the missing evidence and makes adjacent dimensions disagree. It does not itself fabricate numeric metrics or a policy pass.

Expected: use `caseCount ?? 1` for both available and total coverage counts. Add a focused regression with one measured row plus an ambiguous row of weight two, including the selected-ambiguous-only state.

## Standards and security

No additional material documented-standard or security finding. Reviewed changes preserve existing execution/mutation authorization boundaries; manual read retries do not supply approval. Error messages remain escaped text. Storage preferences are bounded and validated with failure-tolerant reads/writes. SQL/diff limits, original side payloads, exact action-side mappings and unavailable mappings remain represented. No speculative refactoring or smell-only finding is requested.

## Coverage and prior corrections inspected

Read the approved design, Plans 125/126, plan index, verification contract, SDD ledger, three task reviews, backend correction report and relevant current product/test paths. Inspected the whole-branch source changes rather than inferring correctness from fix-commit ancestry. Raw test logs were not consumed indiscriminately.

- Eligibility is shared by optimization summaries and comparisons; retained failed attempt details remain inspectable. Session/history/detail version labels now use eligibility and the frozen mapping from a shared snapshot.
- Direct and independent snapshots preserve missing case contributors and reject duplicate metric/fingerprint representatives. Selected ambiguous script mappings are unavailable; all-case raw action identities remain retained.
- IO and directed recorded policy outcomes remain separate; lower reads can coexist with elapsed-policy failure. Reversed/independent/approximate pairs do not receive a newly invented recorded policy verdict.
- Whole-table IO projection retains the added nullable counters; missing contributors stay unavailable. Partial confirmed statements can expose eligible individual metrics without promoting the full batch to an exact changed-statements verdict.
- Active A/B metrics now come from the same comparison operation as outcome counts rather than latest unrelated observations. Valid pending case selections close/hide old outcome dialogs; findings above identify remaining normalization and PostgreSQL gaps.
- Activity canonicalizes initial bare history entries, bounds live expansion by recent changes, and separates filter/scroll memory. Sessions/Usage/Profiles preferences, native SQL frame sizing, side-specific editor labels, manual-copy feedback, skip navigation, reduced motion and responsive layouts were source-reviewed.
- Existing focused tests provide relevant regression coverage, but were only read. Their reported passes are not a new run on this reviewed head.

## Declined to judge

- Whether all 14 categories actually meet 9.0: that requires the frozen actual acceptance matrix. The available provisional browser report explicitly reports 59/65 and outstanding evidence on an earlier tree; it is not a final pass for this head.
- Native NVDA acceptance: the coordinator reports SecureDesktop/operator unlock pending. Build success, automated AX output and source inspection cannot replace that exercise.
- Final rendered screenshots, both-theme text/non-text contrast, native 200% zoom, end-to-end keyboard/focus behavior and frozen large-fixture performance: coordinator owns the final current-tree browser/native runs. Source review does not certify these outcomes.
- Final Release build and Windows `just verify`: coordinator-owned and not rerun by this reviewer. Linux, installed/live deployment and real database behavior remain outside this review.

Summary: **Spec/behavior: 2 P2 + 1 P3; Standards/security: 0 additional material findings. Code approval withheld for correction; final acceptance remains separately pending even after code fixes.**
