# Plan 122 whole-branch review

Reviewed base `b7a820f5` through product HEAD `b30a11e1` on `plan-122-backend`. Scope: all six tasks, backend and UI, including integration with the rebased main UI. Review was read-only except this report; no tests rerun, no live target, installation, merge or push. Production changes were inspected in bounded passes, with focused surrounding callers, requirements, test additions and task review/fix evidence.

## Strengths

- The session contract is explicit and additive. Session INSERT writes version 1, conflict updates omit it, migrations leave historical values NULL, and new projections/retention require exact version 1. The raw-plan regression tests check that GET does not derive missing scalars or change stored bytes/data_version.
- Statement evidence follows the existing median-total-logical-reads run, retaining repetition and exact plan references. Incomplete attribution, ambiguous object identity, multiple cases and mixed old/new sides preserve unavailable values rather than inventing totals or deltas. Existing whole-batch counters remain available.
- Mutation history uses a digest-bound, one-use preparation token whose hash is retained. Link consumption is transactional; rejected mismatches and replay do not acquire provenance. Broker deduplication invokes only the canonical request callback, and journal failures do not change execution or approval decisions. Raw token serialization is restricted to the internal HTTP submission path.
- Grant/spill scalars come from a statement's current QueryPlan, without duplicating XML or adding SQL. Unknown warnings and unmarked UDF possibilities are treated conservatively; known supported spill positives survive other uncertainty.
- UI additions preserve null/zero/false semantics, label units and selected-run provenance, and keep the All-cases explanation visible without expanded statement rows. Unsupported optional counters disappear in statement view, and benchmark Rows were removed from both metrics and expanded Details while query Rows remain.

## Issues

### Critical

None identified.

### Important

None identified.

### Minor

1. `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs:19`: the concurrent upsert test uses a fresh key; migration/resume checks cover an old key sequentially. An explicit concurrent old-key resume assertion would pin the central no-upgrade invariant against future changes. The inspected conflict clause omits the marker, so this is a nonblocking coverage improvement, not an observed defect.
2. `src/SqlHarness.Dashboard/ui/src/components/ActivityOperations.test.tsx:19`: false truncation flags are supplied, but the test does not assert absence of the truncation indicator. Add that assertion to make the presentation contract explicit. The component currently requires both a preview and a true flag, so no product defect was found.
3. `src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.tsx:26`: the measured-after-only grant/spill rendering path shares the metric component with comparison rendering, but the added grant/spill UI assertions exercise comparison mode. Add a measured-mode test proving it uses only after values and does not show a before arrow/value. The inspected measured branch is correct.
4. `src/SqlHarness.Core/StatementMetrics.cs:491`: the final conservative ScriptDom negative-spill check was added after the offline timing probe. Its incremental overhead and actual SQLite storage growth remain unmeasured. Preserve that limitation in closeout; a bounded repeatable offline measurement is a useful follow-up. This is not evidence of a performance regression.

## Spec alignment and integration

All six selected tasks are represented in the final implementation. The correlation transport changes are broader than the original file list, but necessary to establish exact request-to-operation history; they leave digest construction, classification and operator policy unchanged. Narrowing spill false to AST-proven literal-only SELECTs is a justified conservative supported-material boundary: most real queries without a supported positive remain unknown. This reduces usefulness and must stay disclosed.

No historical backfill, extra target SQL, credential disclosure, raw-token response field, independent approval mechanism or components/ui changes were identified in the reviewed range. Production dashboard startup already upgrades/checks journal schema before serving read endpoints; the added scalar columns do not require GET writes. The LF/CRLF warning is a tooling observation, not a demonstrated behavior defect; no line-ending-only patch is requested.

## Verification evidence boundary

Task reports and independent task rereviews record focused passing checks, including corrected provenance, raw-plan non-derivation, omission cap and warning/UDF cases. I inspected relevant tests and reports but did not rerun suites. The controller's Windows `just verify` and browser evidence were still in progress when this report was written; this report does not claim their success or a full verification pass. Final plan/spec/index status updates are controller-owned closeout work and were already in progress during review.

## Declined to judge

- Live SQL Server/PostgreSQL provider acceptance and real workload behavior: explicitly outside authorized execution; fixtures do not establish live acceptance.
- Linux behavior: no Linux gate authorized or run in this review.
- Exact incremental parser cost and SQLite page growth: the existing probe is noisy, predates the final AST check and uses a JSON size proxy; retained as Minor evidence limitation above.
- Other dashboard UX, historical reconstruction, SQL search and fuller spill interpretation: explicitly deferred by the selected specification; no such expansion is needed for this implementation.
- Publication/install/restart/merge/push readiness in an integrated or deployed tree: this review covers the feature branch only and those actions are not authorized.

## Assessment

**Whole-branch review: APPROVED. No Critical or Important finding.**

**Ready to merge?** Code review is favorable, subject to the final Windows gate, requested browser evidence and accurate plan/index closeout. No merge is authorized by this verdict. The remaining observations are nonblocking coverage/measurement follow-ups with their limits made explicit.

## Focused integration supplement: retained-memory copy

The controller supplied browser evidence after the initial report. I inspected `plans/122-evidence/visual/statement-evidence-1280.png` and `src/SqlHarness.Dashboard/ui/src/components/optimization/IoDetails.tsx`.

**Important — contradictory retention guidance (`IoDetails.tsx:23`).** The unconditional sentence “Statement memory is not retained in the journal.” directly contradicts the requested/granted/max-used statement grants now shown above it and retained by Task 5. The screenshot demonstrates both claims on one screen. This can cause an operator to misinterpret whether the new evidence is persisted or merely transient. Replace the obsolete sentence with accurate batch-versus-statement provenance, or remove it while retaining the batch aggregate label. Do not imply availability for old/ineligible sessions or imply that batch aggregates equal selected-run statement grants. Add a focused rendering assertion so the obsolete claim cannot return. This is an integration omission in an unchanged caller, not a new request for UX expansion.

The earlier three coverage Minors remain valid nonblocking improvements: concurrent old-key resume, false-preview truncation-marker absence, and measured-after-only grant/spill rendering. Closing them in the same bounded fix wave is appropriate. The final AST/SQLite measurement caveat remains accepted and disclosed; no performance claim follows from these fixes.

**Updated verdict: WITH FIXES** for the contradictory retention guidance above. Initial no-Important assessment is superseded by this concrete browser/caller evidence. The controller reports ten browser states passing, but the full Windows gate has not passed: the default-fork UI test stage is still being diagnosed. No suite was rerun for this supplement. After the focused fix, review its diff and evidence on the final product revision before restoring approval.
