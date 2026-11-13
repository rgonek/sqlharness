# Plan 126 Task 2 comparison review

Reviewed 2026-11-11. Product commit: `5a09e529` against its parent. Scope:
Plan 125 frontend Tasks 1-5, Plan 126 Task 2 and the approved
`docs/superpowers/specs/2026-11-10-dashboard-quality-design.md`.
Current sibling Activity, Monaco, Usage, preference and CSS edits were excluded,
as were separately owned backend collision/frozen-header-label corrections.
This reviewer changed only this report. No suites, full gate, live database,
installation, merge or publication were run.

## Spec: changes required

### P2: Active version metrics can come from a different observation than the displayed pair

Location: `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx:31-36`
and `:43-52`.

`metricsFor` always reduces `version.metricCases`. Those cases describe the latest
observation of each SQL hash: `OptimizationSummaryBuilder.cs:169-179` selects the
last measurement for each version. The active Results/IO/Policy dimensions instead
come from `comparison`, whose direct-pair path selects the latest operation connecting
the requested A/B hashes (`OptimizationComparer.cs:40-57`). A later observation of A
or B with another partner therefore makes the numbers and the outcome badges describe
different runs, without saying so.

Concrete scenario, with one uniquely identified case and unchanged conditions:

1. Record successful A -> B operation #10: reads 100 -> 50, elapsed 10 -> 20 ms,
   recorded elapsed-regression policy failure.
2. Later record successful A -> C operation #11, with A reads 20 and elapsed 5 ms.
3. Select A -> B in session Optimization.

The version table displays A reads 20, B reads 50 and `vs A +150%`, but the candidate
IO badge says improvement from #10, its detail dialog says 100 -> 50, and provenance
points to #10. The sole elapsed summary similarly displays 5 -> 20 ms, while the
exact pair's retained evidence is 10 -> 20 ms. This persists after loading finishes;
it is independent of colliding case identities.

Expected: the active pair's summary and deltas must use the same selected-case,
sum/mean and source-operation evidence as its IO/Policy details and provenance.
Other rows may retain their own latest measurements with explicit context. Preserve
complete coverage, null semantics and cross-condition matching when supplying the
active summaries; do not silently mix source operations.

Contract: Plan 125 sections 4-5 require the version summary to use the same selected
case aggregation as comparison and consume exact pair evidence for outcomes. The
approved design requires correct active A/B updates and trustworthy provenance,
without conflicting aggregate summaries.

Suggested regression: two successful direct operations A -> B then A -> C, with
different A metrics; select A -> B, switch case and sum/mean, and assert the table,
`vs A`, case details and provenance remain aligned with the selected pair.

### P2: Case changes expose previous-case outcome counts as current table evidence

Location: `src/SqlHarness.Dashboard/ui/src/components/optimization/OptimizationSection.tsx:116-117`
and `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx:43-46`.

The comparison hooks intentionally retain previous data while a new selection loads
(`api/queries.ts:214-222`; the agent-session hook does the same). `ComparisonPanel`
receives `isPlaceholderData` and hides stale evidence, but `VersionTable` receives
only `data`. Its active-pair check compares A/B/groups and ignores selected case,
aggregate and placeholder status. Changing the case leaves that check true.

Concrete scenario:

1. Load A -> B for all four cases: Results 4/4, IO three better/one same, Policy
   three pass/one fail.
2. Select a single case and delay its comparison response.
3. The table immediately recalculates that one case's reads/elapsed/CPU and labels
   the timing as a median, while still showing all four cases' Results/IO/Policy.
   Its IO/Policy buttons remain enabled and open the old four-case detail table.
   The panel below correctly hides the same placeholder evidence.

Expected: mark the affected table evidence as updating and prevent opening stale
case details until the requested comparison is available, or consistently retain
and explicitly label the previous selection. A/B/group equality alone cannot prove
that comparison data belongs to the active case/aggregate selection. Apply the same
protection to cross-condition matching keys, which are also read from comparison
data during placeholder transitions.

Contract: the approved design requires correct case/aggregate updates, local loading
feedback and clear stale-data handling; unavailable/partial evidence must not be
presented as current confirmed outcomes.

Suggested regression: defer the single-case response after an all-cases selection;
assert that old counts/details are not exposed as current, then settle the response
and verify the selected counts and metrics together. Cover both session and
agent-session consumers of the shared table path.

## Standards

No additional material standards/security finding in the owned diff. Failure
messages render as escaped text and use existing error descriptions. Independent
Results/IO/Policy dimensions, directed-policy unavailability, case-detail sorting,
nullable table counters and partial-statement whole-batch fallback are represented
without adding execution or mutation approval paths. Scripts use side-specific raw
case mappings rather than borrowing the other side's case ID.

## Evidence and limits

- Read the owned committed product/test diff and relevant existing query, summary,
  comparer, script-action and statement-evidence paths. Findings above are source
  review with concrete synthetic scenarios; this reviewer did not claim browser
  reproduction or execute a new suite.
- Implementer evidence reports a frozen focused run of 11 files / 116 tests passing.
  That evidence was inspected, not rerun or promoted to final full verification.
- Known sibling Results-card truncation, Monaco label feedback, and separately owned
  backend collision/frozen-label findings are not counted as new Task 2 findings.
- Final integrated/browser acceptance, whole-branch review and Windows gate remain
  coordinator work. Linux and live acceptance remain unverified.

Summary: **Spec: 2 material findings (P2); Standards: 0 material findings. Task 2
approval withheld pending fixes and focused regression evidence.**
