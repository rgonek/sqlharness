# Observation: predicates shortened before artifact projection

Recorded 2026-10-31 against repository HEAD `e4e4ecf4`.
Status: collecting observations, not approved for implementation.
System label: unmapped.

## Observed workflow

A current-source optimization investigation used query, compare and saved-artifact reads.
Five artifact calls succeeded: four selected plan reads and one metrics read. Selected plan
hierarchies were available without outstanding continuations, but some predicate strings ended
mid-expression. Raising maxCellChars to 4096 did not recover their remaining text. The final local
evidence note acknowledged abbreviated predicates; the retrospective clarified that complete
operator coverage did not establish complete predicate-text review.

These are distinct completeness dimensions: operator hierarchy, text retained when the artifact
was produced, and text emitted under the response budget. No failure or safety rejection occurred.
Negative candidate performance was useful investigation evidence, not retrieval friction.

## Verified behavior and interpretation limits

- [PlanDistiller.cs](../../src/SqlHarness.Core/PlanDistiller.cs), inspected at lines 58 and 198-208,
  limits predicates to 200 characters before serialization.
- [PlanNode](../../src/SqlHarness.Core/PlanDistiller.cs), inspected at lines 19-43, stores the predicate
  but has no original-length or predicate-truncated field.
- [PlanDistillerTests.cs](../../tests/SqlHarness.Tests/PlanDistillerTests.cs), inspected at lines
  271-287, contains an existing synthetic test asserting the 200-character retained string.
  The test was inspected, not executed during this retrospective.
- [ArtifactReader.cs](../../src/SqlHarness.Core/ArtifactReader.cs), inspected at lines 889-899,
  reads saved distilled nodes; includeText retains or removes their already-stored predicates.
  It cannot reconstruct text discarded during distillation.
- The [artifact contract](../mcp/artifacts.md#artifacts) says that saved distilled JSON is read,
  rather than raw XML regenerated. Its response-budget clipping accounting therefore does not
  establish completeness of the producer's original predicate.

Classification: UX/contract gap, not a demonstrated response-budget or pagination defect.
Confidence is high for the current source and the observed symptom. The exact running producer
revision was not established; local HEAD alone does not identify that build. No fresh synthetic
execution or live SQL was needed to preserve this finding.

## Existing coverage and questions

[Plan 065](../../plans/065-artifact-plan-pages-and-clipping.md) and
[plan 095](../../plans/095-budget-aware-artifact-pages.md) cover source paging and output clipping.
They do not by themselves restore discarded producer text. The
[telemetry-plan observation](query-store-plan-cell-clipping.md) concerns query-result cell clipping,
a different boundary. [Plan 109](../../plans/109-artifact-review-workflow-study.md) owns broader
equivalent-evidence review cost; no duplicate plan or skill amendment is selected.

For later synthesis: should a saved node disclose original predicate length or explicit shortening,
and what completeness can an agent honestly infer when all node pages have been read?
These are hypotheses, not a selected implementation.

The cheapest useful next check is an offline synthetic long predicate through distillation,
artifact persistence and MCP retrieval, with ample response budget. Assert separately whether
the hierarchy is complete, whether producer text was shortened, and whether projection clipped
anything. No default-limit increase or raw-plan API is authorized.

Related report entry: [current-source aggregation with mixed candidate outcomes](../superpowers/specs/2026-10-07-agent-session-report-draft.md#2026-10-31---current-source-aggregation-with-mixed-candidate-outcomes).
