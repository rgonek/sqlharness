# Observation: artifact review cost and agent-created follow-up work

Recorded 2026-10-23 against repository HEAD `fdc161d1`.
Status: collecting observations, not approved for implementation.

## Observed workflow and cost coverage

A prolonged optimization investigation issued repeated whole-query comparisons and saved-plan reads.
A bounded read-only journal review identified 112 retained investigation operations: 50 artifact
reads, 48 comparisons, 2 measurements and 12 queries. Recorded emitted output was approximately
0.69 MiB, with approximately 181 thousand byte-derived estimated tokens. Artifact reads contributed
approximately 42 percent of the recorded output bytes. These are tool-output counters, not actual
model tokenization, business-workload measurements or complete session cost.

Inputs, catalog/capabilities discovery, source reads, orchestration, final prose and repeated private
report content were not included. A pre-journal file-read rejection was also outside that count.
Retro diagnostic and synthetic validation calls were counted separately in the
[report observation](../superpowers/specs/2026-10-07-agent-session-report-draft.md#2026-10-23---multi-scope-read-batch-candidate-and-changing-evidence-coverage).
No hypothetical unread artifacts are counted as counterfactual model consumption; no assumption is
made that every representation of an MCP response reached the model.

## Friction and classification

- The 50 artifact calls included repeated full hierarchy reviews as variants evolved. Some review
  was necessary; the necessary/avoidable split was not measured. Broader output was also clipped
  by the agent's outer orchestration budget in at least two artifact-producing calls. This is an
  agent/output-budget workflow issue, not proof of a SQLHarness pagination defect.
- One verification query explicitly requested too few presentation rows across multiple result sets.
  The final assertion rows were marked omitted. The agent reran the same verification with a larger
  row cap and read the assertions. This is an agent slip plus a possible query-result selection UX
  question; missing rows were disclosed, not silently interpreted as zero differences.
- One unused-parameter validation rejection was corrected by removing the unused argument. This was
  useful validation of the submitted batch, not a defect. One input-file rejection followed a file
  the agent had not successfully written. Path/write and shell-length mistakes belong to the agent
  and local orchestration, not SQLHarness.
- Negative candidate results, empty valid checks and broader-case regressions advanced the
  investigation. They are not friction merely because no universally faster query was adopted.
- Full operator-tree retrieval did not establish complete memory-grant or compilation review. See
  the existing [evidence coverage observation](plan-memory-and-compilation-evidence.md).

Confidence is high in retained operation/output counts and the visible row-cap rerun. The amount
of avoidable cost, best interface and whole-workflow token effect remain unknown.

## Existing coverage and questions for synthesis

[Plan 077](../../plans/077-agent-sql-optimization-evidence.md) owns optimization evidence and workflow
cost evaluation. [Plan 095](../../plans/095-budget-aware-artifact-pages.md) owns bounded source pages
and response budgets. [ArtifactReader.cs](../../src/SqlHarness.Core/ArtifactReader.cs) and
[AgentOutputProjection.cs](../../src/SqlHarness.Core/AgentOutputProjection.cs) are the relevant
retrieval/projection paths. Existing source capabilities do not prove optimal agent use or a deployed
fix, and fewer calls alone would not prove lower token cost.

The cheapest useful next check is an offline synthetic multi-result assertion and a fixed saved-plan
review task, measuring requests plus emitted bytes at equivalent evidence completeness. Determine
which repeated reads were required before proposing a new surface. Keep query, compare and artifact
interfaces where they provide execution, repetition/equivalence and offline persisted evidence,
respectively. Do not remove useful commands merely to reduce command count.

Potential directions such as selecting the relevant result set or reusing reviewed evidence are
questions, not an implementation checklist. No default-limit increase, API change, new safeguard or
skill edit is selected by this record.

## Follow-up 2026-10-29 - selectors and repeated statement detail

Inspected repository HEAD `9c9d20b4`. Status: collecting observations, not approved for implementation.
The earlier observation remains unchanged. The system label is unmapped.

A current-source optimization investigation made 14 MCP calls: one capabilities discovery, four
queries, two comparisons and seven artifact requests. The bounded read-only journal review found
12 successful operations: four queries, two comparisons and six artifact reads. Capabilities and
the rejected artifact arguments were outside that retained set.

One artifact request used `section: statements` with plan selectors and returned `safety_rejected`
in validation: `Plan selectors are only valid for the plan section.` Removing the selectors allowed
the read. Classification: agent slip, not a demonstrated safety or pagination defect.
[ArtifactReader.cs](../../src/SqlHarness.Core/ArtifactReader.cs), inspected at line 414, implements
this restriction; the variant description in
[McpToolCatalog.cs](../../src/SqlHarness.Mcp/Tools/McpToolCatalog.cs) identifies it as a plan selector.

The corrected statements read and its advancing continuation exposed repeated operator and diagnostic
details after a metrics read had already supplied the decision-relevant grant and compile information.
The first statements response disclosed omitted records and a continuation; the agent followed it.
Outer orchestration also clipped displayed output. That clipping is a separate agent/output-budget
issue, not evidence that SQLHarness lost saved records. Complete statement retrieval was possible;
its necessity for the decision and the avoidable fraction of this cost were not measured.

The 12 retained operations emitted 76,522 bytes, with 19,135 byte-derived estimated tokens. Artifact
reads emitted 61,345 bytes, approximately 80 percent of that output. These are tool-output counters,
not actual model tokenization, business-workload measurements or complete session cost. Input,
discovery, source reads, orchestration and final prose are outside coverage; the rejected response
is also outside these byte totals. Do not assume every MCP representation reached model context.
Two read-only journal invocations belonged to the retrospective, separately from investigation calls;
their output/token cost and the retrospective's source-discovery cost were not measured.

Confidence is high in the observed validation result, pagination and retained counters. A possible
UX gap is the amount of repeated evidence an agent requests when compact metrics already answer
its question; the best interface or safeguard remains unresolved. Negative candidate performance
was useful evidence and is not classified as friction.

Existing coverage: [plan 109](../../plans/109-artifact-review-workflow-study.md) is recorded as research
complete and locally integrated, with live acceptance unverified. It already studies accurate
selectors, compact discovery and equivalent-evidence cost. Local source status does not identify
the running build. No duplicate plan or additional skill prose is selected here.

Question for later synthesis: which additional statement reads change the decision after metrics
and a complete selected plan have been inspected? The cheapest useful check is an offline synthetic
saved-artifact task comparing emitted bytes and evidence completeness with accurate existing
selectors. This is a research question, not an approved implementation or a claim of token savings.
See the [report observation](../superpowers/specs/2026-10-07-agent-session-report-draft.md#2026-10-29---current-source-ranking-candidate-with-an-io-memory-trade-off).
