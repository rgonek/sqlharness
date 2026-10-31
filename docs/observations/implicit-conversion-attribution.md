# Observation: locating an implicit-conversion warning from compare output

Recorded 2026-10-31 against repository HEAD `e4e4ecf4`.
Status: collecting observations, not approved for implementation. System label: unmapped.

## Observed workflow

A current-source optimization ran one whole-query matrix comparison with `compareResults: multiset`,
then read one saved case through `section: statements` and through `section: plan` for each variant,
with `includeText: true`. All calls succeeded. A later comparison of the final SQL also succeeded.

Each comparison case returned one case-level entry, `warnings: ["ImplicitConversion"]`. The projected
case carried no variant field for the warning and no list of affected operators. The two plan
responses returned operator nodes and predicates but showed no implicit-conversion marker on any node.
The statements page that was read did not show one either; its continuation for the remaining
records was not followed. The `operators` and `metrics` sections were not read.

The agent then told the user that the warning occurred in both variants. Nothing it had read
supported that. This was an unlabeled inference, corrected in the retrospective. The source of the
conversion was never identified, and the warning did not affect the adoption decision.

## Classification and verified boundary

Provisional classification: ux gap. Confidence is high in the visible responses and low in the cause.

[BenchmarkRunner.cs](../../src/SqlHarness.Core/BenchmarkRunner.cs), inspected at lines 225-231, adds
the warning when any collected operator has `HasImplicitConversion`.
[AgentOutputProjection.cs](../../src/SqlHarness.Core/AgentOutputProjection.cs), inspected at lines
872-878, selects flagged operators for a set-level list, and
[BenchmarkSummary.cs](../../src/SqlHarness.Core/BenchmarkSummary.cs) holds noteworthy-operator evidence
with per-side flags. Whether those paths apply to this response shape was not traced. Repository HEAD
is not proof of the installed producer build.

## Hypotheses and missing evidence

- The per-operator location may already be available through the unread `operators` section. If so,
  this is a discoverability question and possibly an agent slip, not missing evidence.
- The flag may be set from plan content that the plan section does not project per node.
- The flagged operator may belong to another statement of the batch. Each variant's second statement
  was read; the setting lookup statement was read only through its statements-page summary.

## Coverage and questions for later synthesis

Existing coverage: [statement metrics and local-object noise](statement-metrics-and-local-object-noise.md)
mentions an implicit-conversion warning in another context, but not its attribution.
[Plan 071](../../plans/071-dashboard-compare-review.md), recorded as done, has a dashboard test fixture
counting this warning per variant. The dashboard was not opened in this session, and that fixture does
not show what the agent-facing response exposes. A text search found no open plan on this question;
the other matches were not reviewed beyond their matching lines.
See the [report observation](../superpowers/specs/2026-10-07-agent-session-report-draft.md#2026-10-31---current-source-grouped-aggregate-applied-branch-for-a-missing-setting).

Question: starting from a case-level warning, what is the smallest read that identifies the variant,
statement and operator concerned? The cheapest useful check is offline: save a synthetic comparison
in which only one variant contains an implicit conversion, then read each artifact section and
record which one identifies it. No API change, skill prose or plan is selected by this record.
