# Observation: regression verdict and adoption judgment

Recorded 2026-10-29. Inspected repository HEAD `8d8d959f` (initial retrospective inspection:
`455adea0`). Status: collecting observations, not approved for implementation.
System label: unmapped.

## Observed workflow

A current-source optimization compared a full multi-statement read batch across four parameter
sets, with five repetitions per variant. The request used `compareResults: multiset` and
`compareNullability: true`. All cases reported equal results and stable variant/set results.
Some cases improved elapsed time and logical reads; another reduced reads while increasing elapsed
time. The successful comparison correctly returned `regression.verdict: fail` with reason
`elapsed-regressed` for that case. This was a negative performance outcome, not an execution error.

The agent recommended against adoption too categorically because of that latency verdict. The user
pointed out that the absolute increase was modest. The agent then corrected the recommendation to
a trade-off requiring representative workload and acceptability evidence. No application or database
change followed. Real workload measurements and private source identities are intentionally omitted.

## Classification and verified boundary

Classification: agent slip, not a demonstrated SQLHarness defect. Confidence is high in the visible
comparison outcome, delivered recommendation and subsequent correction. Whether a permanent tool
affordance would prevent recurrence is unknown.

[RegressionPolicy.cs](../../src/SqlHarness.Core/RegressionPolicy.cs), inspected at lines 91-165,
derives the verdict from elapsed samples after evidence gates and reports reads/CPU separately.
[The MCP contract](../mcp/tools.md) states that a comparison pass means the configured elapsed
regression condition was not met, rather than proving an improvement. The running response supplied
per-case baseline/candidate metrics and reasons sufficient to discuss the compromise; the agent did
not lack the underlying numbers. Repository HEAD is not proof of the installed producer build.

## Coverage, uncertainty and later synthesis

The investigation made 12 MCP calls in order: capabilities, query, compare, two selected count-plan
reads, two other statement-plan reads, metrics, two baseline statement-plan reads, and two count-plan
reads for the regressing case. All returned success, with no observed SQLHarness errors, omitted
comparison cases or unconsumed plan continuations. The nine artifact reads comprised eight plan
reads and one metrics read. Some were necessary for domain review; the avoidable fraction is unknown.

Output bytes, actual model tokens, inputs/catalog discovery overhead, source/orchestration cost and
the retro's source-read cost were not measured. No hypothetical unread artifacts are counted as token
savings. Ordinary successful calls did not trigger a diagnostic sweep; existing journal phase records
were not inspected, so no connection, authentication or transport cause is asserted. No live SQL was
rerun for the retrospective.

Existing coverage includes [plans 051](../../plans/051-compare-regression-verdict.md) and
[066](../../plans/066-regression-resolution-floor-and-cost-thresholds.md) for regression semantics,
and [the workflow-cost observation](artifact-review-workflow-cost.md) / [plan 109](../../plans/109-artifact-review-workflow-study.md)
for evidence retrieval cost. This observation does not reopen those implementations or select new
policy thresholds, API fields, skill prose or a plan. The outer orchestration clipped some source
discovery output; that is agent workflow cost, not a SQLHarness output defect.

Question for synthesis: how should an agent preserve the distinction between a latency-policy result
and an adoption judgment that weighs absolute/relative changes, IO, workload mix and user tolerance?
The cheapest useful next check is an offline synthetic response-review task with opposing elapsed/IO
signals, evaluating whether the interpretation states both signals and the missing acceptance context.
This is a research question, not an implementation checklist or a claim of savings.

See the corresponding [report observation](../superpowers/specs/2026-10-07-agent-session-report-draft.md#2026-10-29---multi-statement-count-candidate-with-a-latency-io-compromise).
