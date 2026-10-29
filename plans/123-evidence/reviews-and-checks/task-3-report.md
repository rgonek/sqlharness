# Task 3 report

Commit: 219eb575 Add approximate cross-group optimization comparison.

Implemented cross-group approximate comparison in OptimizationComparer and OptimizationModels, using the latest snapshot for each selected hash and group, pairing only unique nonempty VaryingFingerprint values. Ambiguous duplicate keys do not pair; missing per-case metrics remain null. Metrics and statement aggregation use only paired cases, no equivalence path or verdict is invoked, and both operation snapshot references are retained even with no pairs. CaseRows exposes per-side labels, rows and explicit RowsAVaries/RowsBVaries, including same-group comparisons. Group identity and labels are included for cross-group comparisons. Both session and agent-session endpoints now share selection/validation, accept groupB, and reject missing group or version. TypeScript request/response interfaces include groupB and new fields.

Tests: OptimizationComparerTests covers 4 of 5 pairing and aggregation, absence of verdict, missing keys, no-pair snapshot references, null rows, duplicate keys, explicit variation. DashboardServerTests and AgentSessionsTests cover cross-group and invalid group/version selectors; existing tests guard same-group behavior.

RED: `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~DashboardServerTests|FullyQualifiedName~DashboardContractTests' -p:SkipDashboardUi=true` failed compilation as expected (CS0117: CompareAcrossGroups and ApproximateAcrossConditions absent).

GREEN: `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~DashboardServerTests|FullyQualifiedName~DashboardContractTests|FullyQualifiedName~Agent_optimization_is_provisional_while_an_earlier_exact_call_is_unmatched' -p:SkipDashboardUi=true` passed 106, failed 0, skipped 0. Earlier expanded run passed 105; subsequent added case exposed nondeterministic group ordering in the test, fixed by locating versions using hashes. One interrupted earlier expanded test run caused temporary DLL lock retries. `git diff --check` passed. `npm run typecheck` failed because node_modules is absent and tsc is unavailable; full Windows gate belongs to Task 5.

Self-review: verified identity scoping by subject/database/fingerprint/scope and independent latest hash snapshots; no cross-group direct operation pairing; no inferred variation. Empty key and duplicate keys cannot pair. Snapshot actions preserve full case references when no pairs. Same-group comparison calculations, verdicts and labels are unchanged, with only additive CaseRows. Residual concern for final review: synthetic absent metric entries carry null values to preserve pairing by case identity, and group coverage counts each case without a unique key separately; full UI integration and Windows verification remain outstanding. No merge, push, live DB access, install or publication.

## Review fix round 1

Commit: 6cac83b3. Full no-pair action case references now come from journaled cases independently of metric rows, including both complete case ID lists and case fingerprint mappings. Added a no-pair test with missing metrics on both sides, an explicit same-group snapshot guard for basis, equivalence, outcome, operation, reads and policy verdict, and a JSON contract test for approximate basis, group identities/labels, case row labels, nullable counts and explicit variation.

RED: `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~DashboardContractTests' -p:SkipDashboardUi=true` failed 2, passed 55, total 57. The no-pair regression failed because both action case lists were empty. The contract test's attempt to use the existing flat TypeScript field matcher also failed because its regex counts nested fields; the contract test now verifies serialized JSON fields directly.

GREEN: same exact command passed 57, failed 0, skipped 0. `git diff --check` passed. No full gate run.

Task 4 interface note: no-pair comparisons retain action case references even when metrics are absent; VersionPlan currently resolves a plan only by a selected case, so the UI must explicitly handle Cases empty and selectedCase all to make these retained plans accessible. This fix does not change UI behavior.
