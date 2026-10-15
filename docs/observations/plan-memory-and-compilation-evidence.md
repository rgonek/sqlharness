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
