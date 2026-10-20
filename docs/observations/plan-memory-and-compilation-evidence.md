# Observation: memory grants and compilation evidence across surfaces

Status: collecting observations, not approved for implementation. Recorded 2026-10-15
against repository HEAD `e709937`. No implementation plan is requested.

## Observed workflow

An optimization investigation used two successful matrix comparisons and three successful
`artifact` reads of saved `plan` sections. The selected small plans fit in single responses,
without truncation or continuation. Comparisons exposed execution CPU, elapsed time and reads;
plan reads exposed operator hierarchy, estimates, actual rows and access paths. The final
report explicitly left memory grants and compilation timing unreviewed. This is a reporting
limit, not evidence that the engine did not collect those diagnostics.

## Verified source and remaining uncertainty

- `src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs:14-28` models requested, granted and
  maximum-used memory, compilation time and compilation CPU. Its extractor reads these
  fields from raw plans for the journal. Collection support does not prove values were
  present in this investigation's journal, which was not inspected for this observation.
- `src/SqlHarness.Core/ArtifactReader.cs:45-100` defines safe metrics, plan and statement
  sections without those fields. `src/SqlHarness.Core/PlanDistiller.cs:11-31` likewise
  defines saved distilled statements and nodes without grant or compilation metrics.
- The [artifact contract](../mcp/artifacts.md#artifacts) exposes saved distilled plans,
  not raw XML. A successful full node read therefore does not establish coverage of all
  statement-level diagnostics. Missing fields are different from measured zero values.
- [Plan 058](../../plans/058-benchmark-plan-evidence.md) and
  [plan 065](../../plans/065-artifact-plan-pages-and-clipping.md) are recorded as DONE.
  They own earlier evidence retrieval and pagination work; neither establishes that these
  additional metrics are available through the artifact contract. The installed binary's
  exact correspondence to HEAD was not established.

Classification: evidence-surface UX gap, not a demonstrated contract violation. Confidence
is high in the inspected source-model difference; actual metric availability for the
reviewed operations and the best future surface remain unverified.

## Questions for later synthesis

Do other investigations need these diagnostics to complete their evidence review? Should
an existing bounded artifact section carry them, should another existing surface serve
them, or is clearer guidance sufficient? How should variant, statement, parameter-set and
repetition identity relate to journal aggregates? Older artifacts and unavailable metrics
must remain distinguishable from zero; an observation does not settle the storage/API design.

The cheapest next checks are synthetic Showplan fixtures with present and absent grant and
compilation attributes, traced through extraction, persistence and safe readback, followed
by an older-artifact case. No live database or raw-plan exposure is required for those checks.
Output bytes, token costs and potential savings were not measured. This record authorizes
no implementation, installation or publication.

## Follow-up 2026-10-15

Recorded against HEAD `4f1e5a1` from a separate optimization session; the original entry above is
unchanged. In that session the agent told the operator that a candidate removed hash operators
"sized for" a large estimate, implying a smaller memory grant. That claim came from estimated rows in
saved plan nodes, not from grant values. During the retro, a bounded read-only check of the local
journal found requested, granted and max-used memory and compile time recorded for every metric row of
those operations (batch level), confirming a direction consistent with the claim. The values were
reachable only through the journal or dashboard, not through any `artifact` section the agent had used.
This supports the question above: whether a bounded artifact section should expose these fields. Batch-level
values also mix fill statements with the statement under test. Statement-level grants were not checked.
See also [statement metrics](statement-metrics-and-local-object-noise.md).

## Follow-up 2026-10-20

Recorded against repository HEAD `32adaa78`. Status: collecting observations, not approved for
implementation. Earlier observations above remain unchanged.

A separate current-source optimization investigation rejected an existence-check candidate. Three
successful compare operations covered nine parameter cases; two artifact reads retrieved complete
plan trees for both variants of one case only. Neither response required a continuation. The final
report used that pair to explain changed work order. A later retro clarification explicitly limited
that interpretation to the selected case and stated that grants and compilation remained unreviewed.
The other case plans were not read. The [report observation](../superpowers/specs/2026-10-07-agent-session-report-draft.md#2026-10-20--current-source-existence-check-candidate-rejected)
retains report provenance, workflow counts and the separate median-range clarification.

Classification: agent slip in evidence-review completeness and reporting coverage, not a demonstrated
new tool defect. Confidence is high in what the transcript shows was reviewed. Successful complete
node retrieval establishes coverage of the selected trees, not complete performance evidence or the
absence of grant, spill or compilation concerns. The earlier surface-gap observation is relevant
coverage, but actual availability of grant and compilation fields for these operations was not checked;
this follow-up does not establish that the installed tool could not supply them.

Two bounded read-only journal queries during the retro inspected emission counters and aggregate
operation phases, not memory-grant or compilation metrics. Connection work dominated the initial
successful target check, and the available connection event recorded success. The cause remains
unknown; these records do not distinguish authentication, network and server effects. No live benchmark
was repeated. Exact installed-build correspondence to the inspected HEAD was not established.

The cheapest useful next check is offline: inspect saved or journal-backed grant and compilation
availability for the existing operations and enumerate which cases and statements remain unreviewed.
Missing fields must remain distinct from zero. For later synthesis, ask whether explicit review coverage
alongside a rejection decision is sufficient or whether an existing evidence surface needs improvement.
This entry chooses neither a safeguard nor an API change and authorizes no plan or implementation.
