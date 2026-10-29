# Plan 123 whole-branch review

Base: `f54979563d7aa7575b71c7e682f1417931f7902d`. Reviewed head: `5f7bdce0`. Reviewer: Sol low. Read-only source review; this report is the sole requested write.

## Scope and evidence

Reviewed the supplied complete commit/stat/diff package in passes, the plan as its own specification, AGENTS.md, global constraints, progress rulings, final source and targeted surrounding consumers, archived task reviews and verification evidence. The constructor-location ruling is implemented in the corrected plan; no SqlHarnessModule edit is needed. No subagents, product/index/HEAD changes, live database access, installation, merge, push or publication.

Accepted existing final Windows evidence on product `d3d3c068`: UI 446 passed, Core 4792 passed / 11 skipped, MCP 535 passed / 4 skipped, build passed, gate exit 0; separate branch formatting passed for 29 C# files. `5f7bdce0` contains documentation/evidence only. Synthetic browser evidence passed with zero page errors; this establishes fixture UI behavior, not live journal acceptance. No tests or gate were rerun. Relative file links in the plan and verification document resolved during this review.

The earlier gate failure came from tests discovered inside the incomplete dependency directory. Its intact move outside the UI root and successful retry are disclosed; nothing supports a deletion or pristine-output claim. Linux and live behavior remain unverified.

## Strengths

- Row counts follow actual canonical result capture through both dialect producers as journal-only evidence. Repetition list equality and result-set-count equality remain distinct; absent capture stays nullable. Schema 26 adds only the two nullable columns, and the migration test retains an existing metric without populating historical evidence.
- Varying case identity is sealed with the existing journal HMAC salt and includes the same measurement payload except fixed parameters. The variation marker is bounded, keyed by case and variant, and excluded from fingerprint inputs. No artifact or runs.jsonl backfill was introduced.
- Cross-group snapshots select the latest observation of each hash and pair only unique, nonempty varying fingerprints. Missing metrics do not destroy retained case/plan references. Metric helpers consume paired selections, including the additional IO-counter consumer's mapping; zero pairs suppress totals while SQL and plan actions remain available.
- Exact comparison code retains its existing verdict/equivalence path, with a dedicated guard. Approximate results use an explicit basis, null operation/verdict, unavailable case outcomes and no equivalence-path call. Version-table deltas are suppressed across groups.
- Group plus version identity reaches row keys, radio selection, query parameters, SQL-diff remounting, and operation-return URLs. Existing focused tests cover duplicated labels, nondefault A preservation, invalid B recovery, endpoint 404 behavior and no-pair plan access.

## Issues

### Critical

None found.

### Important — fix before merge

1. **Cross-group comparison context drops the identities of the conditions being compared.** `src/SqlHarness.Dashboard/ui/src/components/optimization/ComparisonPanel.tsx:104` renders only the generic warning; `src/SqlHarness.Dashboard/ui/src/lib/optimizationMarkdown.ts:54` and `:67` export only version labels plus that warning. Neither consumes `groupLabelA` / `groupLabelB` or group identities, although the backend supplies them and the plan's Deferred section explicitly says the banner shows both group labels. When the same SQL version exists in two conditions groups, Copy summary produces `v0 → v0` with row/metric differences but no identification of which conditions belong to either side. The table's hover titles are unavailable in the copied evidence. Render explicit A/B conditions context alongside the warning and include the corresponding labels and unambiguous group identity in the copied summary. Cover same-version cross-group output with distinct conditions in focused UI/Markdown tests.

### Minor

No additional demonstrated product defect. The deferred observations below remain explicitly triaged rather than silently discarded.

## Deferred-minor dispositions

| Observation | Disposition and reason |
| --- | --- |
| ActivityJournalBenchmarkTests.cs:26 multi-case / multi-variant marker and size-cap coverage | Accept as nonblocking coverage improvement. The writer maps MatrixCell/index and variant names, enforces the byte cap, and the reader only treats an explicit marker as variation; absent evidence displays a dash. Add this focused regression fixture when extending marker behavior. |
| ConditionsModel.cs:65 synthesized record equality | Close as report wording correction, not a product defect. ConditionsCase equality includes the additive field; preserved invariants are fingerprint values and CanonicalResult equality. No production equality dependency was identified. Do not claim all condition-record equality is unchanged. |
| OptimizationSummaryBuilderTests.cs:249 latest-measurement row-count test | Accept as nonblocking coverage improvement. Both row count and variation are derived from metrics selected from `latest.Compare` / `latest.Variant`; there is no earlier-observation fallback. A two-observation fixture would independently protect this boundary. |
| DashboardServerTests.cs:636 adversarial subject/database/scope fixture | Accept as nonblocking hardening. The shared handler resolves a single selected subject/database, validates each group's scope/version and filters evidence by subject/database, both fingerprints and normalized scope; comparer identity checks add defense. No cross-context evidence leak found. |
| OptimizationSection.tsx:87 and :93 exhaustive-deps warnings | Accept as nonblocking maintainability debt; no concrete stale-selection defect established. The invalid-B effect is triggered by card identity, requested B group/version and validity; the 404 effect is triggered by the active query's error, whose query key contains selection and context. Effects execute a current-render closure when triggered. Existing tests exercise both recovery paths and A preservation. A future refactor should stabilize the recovery inputs/callback before making dependencies exhaustive; blindly adding render-created functions would retrigger navigation/refetch effects. The warnings remain real and are not a lint-clean claim. |
| Node DEP0190 | Retain as an unresolved diagnostic, nonblocking for this feature. The gate reports shell child-process argument concatenation, but no stack/origin is established and this branch introduces no process-launch change. The warning alone does not establish an exploitable product defect. Attribute with tracing in a separate diagnostic change if pursued; do not assert its origin. |
| Task 4 explicit dash assertion | Closed: VersionTable.test.tsx:47 checks the missing row's Rows cell; a separate test checks explicit `varies`. |

## Recommendations

Apply the single Important finding in the combined fix wave. Keep the accepted coverage and diagnostic observations visible in the completion ledger. Refresh plan/index review statuses only after the fix and its verification/review are complete; their pending text accurately describes the reviewed head.

## Declined to judge

None. No considered in-scope behavior was set aside. Evidence limits above distinguish unexecuted environments from source-review conclusions.

## Assessment

**Ready to merge? With fixes.**

The implementation is coherent across capture, persistence, comparison, routes and selection, and the existing Windows and synthetic-browser evidence meets the declared verification boundary. One conditions-identity/context omission remains in the banner and exported comparison evidence; fix it before final acceptance. No other blocking defect was found.
