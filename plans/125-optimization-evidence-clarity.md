# Optimization evidence clarity implementation plan

> **For agentic workers:** Use superpowers:executing-plans, or
> superpowers:subagent-driven-development only if explicitly selected by the operator.
> Execute sequentially; checkboxes track completion.

**Goal:** Make failed attempts, available IO and mixed performance outcomes clear without duplicated metrics.
**Architecture:** Preserve recorded evidence and policy semantics. Extend the existing journal-to-dashboard
projection, expose metric-level availability, and simplify the existing comparison components.
**Tech stack:** C#/.NET, SQLite, ASP.NET Core, React/TypeScript, xUnit, Vitest.
**Spec:** The approved direction is recorded in [Design contract](#design-contract) below.
**Status:** IMPLEMENTED AND VERIFIED on `b189a999`; Task 6 and final whole-branch reviews pending. See [verification evidence](125-evidence/verification.md).
**Baseline:** `8d8d959f`, inspected 2026-10-29; working tree initially clean.
**Related work:** Builds on 104/105 statement evidence and 121/122/123 dashboard work.
Read those contracts when changing their invariants; do not duplicate their collectors or version-label machinery.

## Global constraints

- Read AGENTS.md, plans/README.md and docs/agents/verification.md before execution; reconcile HEAD drift.
- This plan authorizes no implementation, database access, replay, installation, restart, commit or push.
- Use anonymous synthetic fixtures; do not copy local operation payloads, SQL, targets or identifiers into the repository.
- No SQL execution changes, statement splitting, new database queries or policy-threshold changes.
- Do not invent missing measurements, convert unknown values to zero, or treat estimates as observed counters.
- Keep caller SQL and evidence readable under existing storage policy; protect credentials and bound output.
- Preserve unrelated work and existing session, agent-session and operation comparison routes.
- Keep public interface details in the dashboard DTO/types; README receives only necessary human guidance.

## Design contract

### 1. Attempts and diagnostics

Failed/cancelled/rejected/running/abandoned compare attempts remain in operation and compare history.
They do not create optimization versions, influence defaults or supply successful comparison evidence.
Use one eligibility predicate across summary/comparison paths: a completed successful attempt with usable
recorded measurement evidence (and the appropriate variant identity). Do not confuse a successful compare
execution whose regression policy failed with a failed operation: the former remains eligible.
Retained partial measurements from interrupted operations stay inspectable in operation detail.
Do not delete/rewrite historical data or change frozen version labels to close numbering gaps.

Show the recorded failure message or a readable cancellation reason prominently in operation detail,
without requiring an exit-code interpretation or opening the status popover. Reuse existing error
description logic and popover; distinguish client cancellation, timeout, rejection and unknown failure.
When no error message was stored, say so without manufacturing a SQL exception.

### 2. Honest per-statement evidence

Evaluate availability independently for each statement, side and metric. A partial batch must not hide
another statement's individually confirmed evidence. Carry existing evidence eligibility/provenance through
the reader; do not simply remove the batch-level check from StatementCounter.
Keep whole-batch verdict fallback until the existing complete-attribution requirements are satisfied.
Showing a confirmed subset never upgrades the comparison to a changed-statements verdict.

Text, identity, objects and plan links remain usable without IO. Show runtime counters only when recorded
and eligible; estimates remain identified in Plan. Missing CPU/elapsed/grants do not imply missing IO.
For multiple selected cases, aggregate a metric only when every contributing case is eligible; report
coverage/reason otherwise. Preserve the existing single-case restriction for runtime metrics whose
cross-case aggregation is not meaningful. Missing branches count as zero only with existing proof that
the statement was not executed; an unmatched/omitted statement is not such proof.

When no statement has trustworthy IO on either side for the selection, replace the empty numeric grid
with an explanation and a Whole batch action. Keep statement/plan navigation accessible. With mixed
availability, show confirmed numbers and concise unavailable reasons; never fill an all-empty column.
Provide reasons such as ambiguous attribution, omitted evidence, unsupported metric or missing historical
measurement. Local copy explains: "Some IO cannot be attributed to individual statements. IO assessment
uses the whole batch." Use existing English UI conventions.

### 3. Whole-batch IO

Project all already stored table IO fields through JournalReader, CompareTableIoEvidence and TableRow:
data logical reads, scans, LOB logical, physical, read-ahead, LOB physical and LOB read-ahead.
Preserve current logical-read total semantics (data + LOB); distinguish the separate LOB column.
Zeros remain zeros, nulls remain unavailable. Aggregate per-case stored representatives using the existing
sum/mean selection without summing repetitions or silently dropping missing contributors.
Do not invent per-statement physical/read-ahead evidence from whole-batch counters.
Keep optional IO columns; offer/show only supported available columns for the current view.
Fix WholeBatch.Label so absent shared-statement totals never produce "reads unavailable" when batch totals exist.

### 4. One place for elapsed and CPU

The version table is the sole summary location for elapsed and CPU. Replace the median-of-case-medians
summary with the same selected-case sum/mean of per-case medians used by comparison.
For all cases, label the default "Elapsed (sum of case medians)" and "CPU (sum of case medians)";
for mean mode use "mean of case medians", and for one case use "median". Scans is a visible column
beside reads. Tooltips define ↑ as improvement, not a rise in the measured value.
Comparison case selection and aggregate controls drive these version-table metrics; keep baseline
selection and scope/group identity intact. A row from different conditions uses only matched case identities
for the active pair, states coverage, and never receives an exact-comparison verdict. Other rows retain
their own group metrics with a visible different-conditions indication; do not silently apply foreign case IDs.
When no pair is active, use all cases and sum. Missing contributors produce unavailable summaries with coverage.
Case-detail rows retain per-case elapsed/CPU: these are drill-down evidence, not a second aggregate summary.

### 5. Independent outcome dimensions

Replace the ambiguous Outcome string with three independent dimensions: result equivalence, IO outcome,
and recorded policy outcome. Example compact presentation: "Results 4/4", "IO ↑3 · =1",
"Policy ✓3 · ⚠1". Count better/same/worse/unavailable for IO; pass/fail/unavailable for policy.
Show nonzero categories and include complete counts in accessible names/tooltips. No color-only meaning.
Baseline rows show equivalence/measurement coverage rather than invented improvement against themselves.
Keep existing IO thresholds; do not recompute policy from IO outcomes. Read per-case recorded policy
reasons and elapsed/CPU/reads evidence from stored comparison results.

Clicking IO or Policy opens one shared case-details table, sorted with policy failures first, then IO
regressions, then stable original case order. Include case label, result equivalence, reads before/after
and delta, elapsed before/after and delta, CPU before/after and delta, policy verdict and recorded reasons.
A case with lower reads and regressed elapsed must visibly occupy both categories correctly.
Historical missing policy is unavailable, never pass. For independently measured or approximate pairs,
show IO deltas when justified but no borrowed direct-comparison policy verdict; explain unavailability.
Keyboard activation and focus return work; long case labels wrap without expanding the page horizontally.

### 6. Remove the evidence drawer

Remove "Comparison evidence and scripts". Move existing executed/local-variable script copy actions into
SQL diff for each side, preserve selected-case binding, and keep plan download/navigation in Plan.
Keep operation provenance next to the comparison heading, including separate source operations when needed.
Show the verdict basis once near IO controls; explain partial attribution locally instead of repeating
"per-statement attribution incomplete" below version rows. Keep SQL/plan actions available with no IO.

## Review focus

1. Failed operation versus successful execution with policy fail; retained history and stable version labels (Task 1).
2. One confirmed statement inside a partial batch, omitted/colliding identities and asymmetric sides (Task 2).
3. Zero versus missing historical IO and sum/mean across cases without repetition multiplication (Task 3).
4. Lower reads with regressed time, absent policy and approximate pairs (Task 4).
5. Case/aggregate changes, cross-condition row coverage and moved script bindings/accessibility (Task 5).

## Execution tasks

Execute Tasks 1 -> 2 -> 3 -> 4 -> 5 -> 6. Product changes require failing focused tests first.
Use the existing test classes and helpers; add fixture builders only when their callers share real setup.

### Task 1: Eligible evidence and visible failure reasons

**Files:** `src/SqlHarness.Dashboard/JournalReader.cs`,
`Optimization/OptimizationSummaryBuilder.cs`, `Optimization/OptimizationComparer.cs`,
`ui/src/pages/OperationPage.tsx`, `ui/src/components/StatusBadge.tsx` and existing error-description helper.
**Tests:** `tests/SqlHarness.Tests/Dashboard/{JournalReaderTests,OptimizationSummaryBuilderTests,OptimizationComparerTests}.cs`,
`ui/src/pages/OperationPage.test.tsx`, `ui/src/components/StatusBadge.test.tsx`.
**Interface:** One shared internal eligibility predicate consumed by summary and comparison paths;
recorded operation status stays authoritative. Reuse the current failure-description contract.

- [x] Test every ineligible status with SQL hashes/conditions present; none creates summary versions or pair evidence.
- [x] Test successful compare execution with a failed policy remains eligible, frozen labels do not renumber,
  and interrupted retained metrics/history remain inspectable.
- [x] Test visible client-cancel/timeout/unknown explanations and plain-text rendering of stored error messages.
- [x] Run focused tests, confirm intended failures, implement eligibility and visible diagnostics, rerun to PASS.

### Task 2: Per-statement availability without weakening verdicts

**Files:** `JournalReader.cs`, `Optimization/{OptimizationModels,OptimizationComparer}.cs`,
`ui/src/api/types.ts`, `ui/src/components/optimization/{IoComparison,StatementRows}.tsx`.
**Tests:** `JournalReaderTests.cs`, `OptimizationComparerTests.cs`, `IoComparison.test.tsx`, `StatementRows.test.tsx`.
**Interface:** Add a typed availability/provenance result for each displayed metric/side at the DTO boundary,
derived from existing stored eligibility and identity. Keep numeric nulls and existing fields compatible.

- [x] Test partial batch with one individually eligible statement and one ambiguous statement; show only confirmed counters.
- [x] Test asymmetric sides, repeated statement occurrences, ambiguous object identities, omitted statements,
  absent evidence versus proved nonexecution, and all-case partial coverage.
- [x] Test no-IO empty state, Whole batch navigation, usable plan links, and mixed availability presentation.
- [x] Confirm failures, implement the projection and per-metric checks, rerun focused tests to PASS.
- [x] Assert changed-statements verdict stays unavailable for the partial batch even when some rows now show numbers.

### Task 3: Preserve stored table IO end-to-end

**Files:** `JournalReader.cs`, `Optimization/{OptimizationModels,OptimizationComparer}.cs`,
`ui/src/api/types.ts`, `ui/src/components/optimization/IoComparison.tsx`.
**Tests:** `JournalReaderTests.cs`, `OptimizationComparerTests.cs`, `IoComparison.test.tsx`.
**Interface:** Extend existing nullable table-IO fields through reader, DTO and UI; no schema migration expected.

- [x] Seed distinct synthetic nonzero values for each stored IO field plus zero and null contributors.
  Assert reader and comparison retain each field in sum and mean modes; repetitions are not multiplied.
- [x] Test label with batch totals but unknown shared totals, totals including LOB, and legacy null fields.
- [x] Confirm failures, implement full projection/aggregation and available-column controls, rerun to PASS.
- [x] Verify PostgreSQL unsupported table counters stay unavailable rather than fabricated SQL Server fields.

### Task 4: Separate IO, results and policy with case drill-down

**Files:** `JournalReader.cs`, `Optimization/{OptimizationModels,OptimizationSummaryBuilder,OptimizationComparer}.cs`,
`ui/src/api/types.ts`, `ui/src/components/optimization/{VersionTable,ComparisonPanel}.tsx`;
create `CaseOutcomeDetails.tsx` beside these components if needed to own the shared details table.
**Tests:** existing reader/summary/comparer tests, `VersionTable.test.tsx`, `ComparisonPanel.test.tsx`,
new `CaseOutcomeDetails.test.tsx` if extracted.
**Interface:** Structured outcome counts plus per-case verdict/reason/metric evidence from the exact direct
pair; consume the same case records for badges and detail table. Preserve compatibility of existing DTO fields.

- [x] Use synthetic four-case fixture: three lower-read cases, one equal; one lower-read case has elapsed
  10 -> 30 ms and recorded elapsed-regressed fail. Assert IO ↑3 · =1 and Policy ✓3 · ⚠1 independently.
- [x] Test unavailable policy, differing results, unsupported IO, all-unavailable, zero baseline percentage,
  reversed A/B and approximate pairs; reversed policy must not be presented as a newly evaluated verdict.
- [x] Test deterministic failure-first sort, exact recorded reasons, accessible labels, keyboard opening/closing
  and a long case label. Retain all metrics in details even when IO improves.
- [x] Confirm failures, implement structured projection and shared drill-down, rerun to PASS.

### Task 5: One summary and consolidated actions

**Files:** `Optimization/{OptimizationSummaryBuilder,OptimizationComparer,OptimizationModels}.cs`,
`ui/src/api/{types,queries}.ts`, `ui/src/components/optimization/{OptimizationSection,VersionTable,ComparisonPanel,IoComparison,VersionPlan}.tsx`,
`ui/src/lib/optimizationMarkdown.ts` where exported summaries consume changed semantics.
**Tests:** `OptimizationSummaryBuilderTests.cs`, `OptimizationComparerTests.cs`, matching component tests,
`ui/src/lib/optimizationMarkdown.test.ts` when affected.
**Interface:** Reuse the existing case/aggregate selection as the source for visible summary metrics.
Add optional summary projection fields where needed; never relabel old median-of-medians as a sum.

- [x] Test medians [12, 28] -> sum 40/mean 20, single-case selection -> 12, missing contributor -> unavailable;
  CPU and elapsed use identical selection semantics. Assert only one aggregate elapsed/CPU location exists.
- [x] Test shared version table across conditions, matched-case coverage, no active pair, and selection changes.
- [x] Test visible Scans column and independent Results/IO/Policy columns with usable narrow-screen overflow.
- [x] Test executed/local-variable script actions in SQL diff retain correct side/case; Plan retains downloads;
  provenance links remain in the heading; removed drawer/repeated fallback text does not render.
- [x] Confirm failures, implement layout and aggregation, rerun focused tests to PASS.

### Task 6: Integrated verification and documentation

- [x] Run focused backend tests with `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
  using the repository SDK/environment and established build recipes.
- [x] Run `npm run check` in `src/SqlHarness.Dashboard/ui`; inspect failing-stage output before changes.
- [x] Browser-verify a synthetic seeded local dashboard: failed attempt, mixed policy/IO, partial statement
  evidence, no statement IO, zero/missing table counters, case/aggregate switching, approximate comparison,
  script copy actions, plan navigation and keyboard drill-down. Use the browser-verification workflow
  applicable to SQLHarness; do not start a real database workload. Record actual checks and limitations.
- [x] Run `just verify` on the final product tree as required by docs/agents/verification.md.
  Report revision and actual results; installation/live acceptance remain separate.
- [x] Check README/dashboard guidance and affected interface descriptions; update only changed human-facing
  meanings and DTO contracts. Do not duplicate interface details into installed skills.
- [x] Update this plan and plans/README.md with actual status/evidence when implementation lands.

## Planning self-review

All eight original proposals are covered: attempt eligibility/diagnostics (Task 1), trustworthy per-statement
display (Task 2), missing whole IO/label (Task 3), mixed outcomes (Task 4), single time summary/scans/action
placement/local attribution explanation (Task 5), and verification (Task 6). No collector certainty is promised:
unavailable per-statement data remains explicitly unavailable. Public examples are synthetic.
