# Plan 109: Measure and reduce artifact-review discovery cost

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows src/SqlHarness.Core/AgentBenchmarkOutput.cs src/SqlHarness.Core/ArtifactReader.cs src/SqlHarness.Core/AgentOutputProjection.cs skills/sqlharness/SKILL.md`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Measure and reduce artifact-review discovery cost.

**Architecture:** First deliver a reproducible synthetic task study. Compare (a) existing interfaces with accurate selectors/budgets, (b) compact per-statement CPU/elapsed discovery without operators, (c) precise artifact metrics pointers for named-set measurements, and (d) relevant-result-set selection for assertion queries. Rank findings by total emitted bytes and equivalent evidence completeness, not call count alone. Evidence reuse must be keyed by immutable artifact/variant/set/statement/repetition; no automatic claim that a changed query has already been reviewed.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: RESEARCH COMPLETE on `f6044c8`; final Windows gate evidence is coordinator-owned and pending. Offline/synthetic study only; live acceptance remains unverified.
- Priority: **P3** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: M; change risk: LOW.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 077/095/097 are context; freeze current baseline before edits. Coordinate 103/105 evidence schemas, no requirement for paid-agent acceptance.
- Source observations: [artifact-review-workflow-cost.md](../docs/observations/artifact-review-workflow-cost.md), [statement-metrics-and-local-object-noise.md](../docs/observations/statement-metrics-and-local-object-noise.md), [sql-optimization-response-consilium.md](../docs/observations/sql-optimization-response-consilium.md).

## Why this matters and current state

The retained investigation counted 112 operations, including 50 artifact reads, but did not measure the avoidable split. AgentMeasureSetItem lacks per-table reads; ArtifactNamedMetrics has them. ArtifactStatementMetric always includes TopOperators, even when a task only needs CPU/elapsed across sets. Plan 095 is DONE and offers page budgets; page control alone does not add lightweight summary semantics.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

First deliver a reproducible synthetic task study. Compare (a) existing interfaces with accurate selectors/budgets, (b) compact per-statement CPU/elapsed discovery without operators, (c) precise artifact metrics pointers for named-set measurements, (d) relevant-result-set selection for assertion queries, and (e) identifying setup-in-batch statements separately from the measured subject. Rank findings by total emitted bytes and equivalent evidence completeness, not call count alone. Evidence reuse must be keyed by immutable artifact/variant/set/statement/repetition; no automatic claim that a changed query has already been reviewed. Setup exclusion must preserve the application input/compilation form and may not subtract overlapping work without attributable counters; compare existing setup SQL and TVP binding before proposing another benchmark mode.

## Files and responsibilities

- `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/AgentBenchmarkOutput.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/ArtifactReader.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/AgentOutputProjection.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `skills/sqlharness/SKILL.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `AgentOptimizationWorkflowTests|McpTokenBudgetTests`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/109-evidence/`, `plans/109-verification-evidence.md` if needed, this plan and `plans/README.md`.

These are scope boundaries, not an instruction to edit every listed file. Research/diagnostic plans may inspect source but only modify plans/evidence and narrowly specified offline tests; a production change requires a follow-up implementation plan.

## Global constraints

- Read AGENTS.md, plans/README.md and docs/agents/verification.md before execution. Before changing startup, targets or file inputs, read docs/mcp.md and the relevant sub-contract.
- Work in an isolated checkout for product implementation; preserve unrelated dirty work. Record git status, worktree list and the actual branch base.
- Protect resolved credentials. Keep SQL, parameters, plans, results and target names readable; bound output rather than masking them.
- Persistent writes use mutation with operator approval; an agent never approves its own mutation. This plan needs no live database.
- Preserve owner/target locks, bounded parsing/materialization, cancellation, total response ceilings and readable exact evidence identities. No blanket limit increases.
- Missing/unavailable evidence is not measured zero. Historical observations do not prove loaded-binary behavior or complete session cost.
- No edits to the agent-session report. Workflow findings here concern tool use and evidence retrieval.

## Review focus

1. Older artifacts and absent engine-specific fields must remain readable and explicitly unavailable.
2. Multiple variants, statements, parameter sets and repetitions must not exchange identities.
3. Minimum byte budgets, long escaped/multibyte text and continuations must preserve truthful clipping.
4. Errors and negative results must retain their meaning; missing output is not a successful empty check.
5. A notes-only or offline synthetic result must not be presented as live acceptance, deployment or measured model-token savings.

For implementation, map applicable review-focus items to assertions in the owning task. For research, record each as an assessed constraint, including nonapplicability, in the decision evidence.

### Task 1: Freeze equivalent-evidence tasks

- [ ] Build offline real-SDK fixtures for a four-statement/four-set batch, repeated saved-plan review across candidate revisions, named-set per-table attribution, and multi-result assertions located after a large first result. Record calls, UTF-8 output bytes, clipping/continuations followed, exact cases/statements reviewed and byte/4 token estimates separately from actual tokenizer counts.
- [ ] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [ ] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

### Task 2: Evaluate candidate improvements without committing an API

- [ ] Use test-only renderers/fixtures or recorded deterministic alternatives; do not change production defaults during the study. Establish the best existing request sequence first. Treat orchestration clipping, a too-small row cap, an unwritten file, unused parameters and invalid SQL as agent/orchestration errors. Negative candidate measurements and empty valid checks remain useful evidence.
- [ ] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [ ] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

### Task 3: Write the decision and any implementation handoff

- [x] Save the baseline, candidate outputs and runnable commands under plans/109-evidence/. Record accept/defer/reject with measured completeness and bytes. Accepted candidates have separate numbered implementation plans pinning schema and compatibility. No query/compare/artifact tool is removed.
- [x] Record inspected sources, commands, expected and observed outcomes; preserve unresolved limits and causes explicitly.
- [x] Save the decision and reproduction evidence, inspect the diff, and make no production behavior changes.

## Verification and done criteria

Run `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "AgentOptimizationWorkflowTests|McpTokenBudgetTests"` and, for MCP tests, `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "AgentOptimizationWorkflowTests|McpTokenBudgetTests"`. Both must exit 0 with the intended tests actually discovered; a zero-test run is not evidence.

- [x] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [x] All task outcomes and relevant source paths are recorded; the final status distinguishes implementation, diagnosis, research and deferred live acceptance.
- [x] No executable code/tests changed in Task 3. Scoped checks cover the notes/evidence diff and links; the separate coordinator-run Windows gate is not claimed by this task.
- [x] Linux remains unverified. No completed product checks were rerun.
- [x] Research evidence records accept/defer/reject outcomes and the remaining unknowns.
- [x] Update this plan and the index with tested revision, scope, review outcomes and remaining limitations. Deferred live acceptance remains unverified.

## STOP conditions

Do not compare historical real output bytes with new synthetic output as savings. No paid-agent trials, live journal exports, workload SQL, final-report design or production skill edits are authorized by this study.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.

