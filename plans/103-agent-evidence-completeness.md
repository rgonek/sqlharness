# Plan 103: Expose evidence completeness and hidden work to agents

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Core/StatementMetrics.cs src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/ArtifactReader.cs src/SqlHarness.Core/AgentBenchmarkOutput.cs src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Mcp/McpResultAdapter.cs src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Expose evidence completeness and hidden work to agents.

**Architecture:** Persist a bounded, versioned statement-evidence projection with exact variant/set/cell/statement/repetition identity. Expose attribution status (`complete`, `partial`, `unavailable`) and bounded hidden-work kinds on artifact statement reads. Add a compact partial-evidence indication to measure/compare, scoped to the affected case, with an executable artifact reference when saved evidence exists. Status describes collected-counter coverage, never universal diagnostic sufficiency. Missing old-artifact evidence is unavailable, never complete or zero. Hidden function-internal work is a limitation even if observed object counters reconcile.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: IMPLEMENTED on `plan-103`; staged/final review and Windows verification in progress. See [evidence](103-verification-evidence.md).
- Priority: **P1** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: M; change risk: MED.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 082 implementation is on main; coordinate 077. No new-plan hard predecessor.
- Source observations: [statement-metrics-and-local-object-noise.md](../docs/observations/statement-metrics-and-local-object-noise.md).

## Why this matters and current state

`StatementMetrics.cs:29-65` already stores Objects, HiddenWork, CountersComplete and IdentityComplete. `JournalBenchmarkBuilder.cs` reconciles instruction attribution. `ArtifactReader.cs:102-112` exposes only statement identity, CPU/elapsed, DOP and TopOperators. These are deliberately separate projections; simply serializing StatementMetrics would expose additional fields without a controlled contract.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Persist a bounded, versioned statement-evidence projection with exact variant/set/cell/statement/repetition identity. Expose attribution status (`complete`, `partial`, `unavailable`) and bounded hidden-work kinds on artifact statement reads. Add a compact partial-evidence indication to measure/compare, scoped to the affected case, with an executable artifact reference when saved evidence exists. Status describes collected-counter coverage, never universal diagnostic sufficiency. Missing old-artifact evidence is unavailable, never complete or zero. Hidden function-internal work is a limitation even if observed object counters reconcile.

## Files and responsibilities

- `src/SqlHarness.Core/StatementMetrics.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/Artifacts.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/ArtifactReader.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/AgentBenchmarkOutput.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/AgentOutputProjection.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Mcp/McpResultAdapter.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `StatementMetricsTests|ArtifactReaderTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/103-evidence/`, `plans/103-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Capture current evidence loss

- [ ] Add synthetic scalar-UDF and multi-statement-TVF fixtures following StatementMetricsTests and Journal/JournalBenchmarkBuilderTests. Assert the extractor/journal evidence and currently absent artifact/agent fields separately. Include a multi-set comparison with only one affected statement and a PostgreSQL/legacy artifact with unavailable SQL Server attribution.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 2: Persist and serve controlled evidence

- [ ] Extend Artifacts.cs persistence and ArtifactReader.cs models instead of opening the journal/dashboard API to the agent. Reuse the reconciled attribution semantics without rerunning SQL or coupling artifact reads to journal retention. Page evidence with the existing source/byte budgets. Test old manifests, incomplete identity, long function names, continuation, and two statements sharing operator node IDs.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 3: Expose compact limitations and verify the workflow

- [ ] Update AgentBenchmarkOutput, projection, MCP descriptions/runtime capabilities only where semantics require it, and docs/mcp/artifacts.md. Add real-SDK offline workflow coverage where the agent can retrieve the affected statement evidence at minimum/default budgets; clipped detail must not change partial into complete. Run focused tests, staged task review and final branch review, then the Windows gate.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

## Verification and done criteria

Run `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "StatementMetricsTests|ArtifactReaderTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"` and, for MCP tests, `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "StatementMetricsTests|ArtifactReaderTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"`. Both must exit 0 with the intended tests actually discovered; a zero-test run is not evidence.

- [ ] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [ ] All task outcomes and relevant source paths are recorded; the final status distinguishes implementation, diagnosis, research and deferred live acceptance.
- [ ] Run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef <recorded-base>` and retain its direct exit code/log. Run `just verify -LogDirectory D:/temp/sqlharness-plan103-final-verify`; both must exit 0 on the final product tree. Obtain independent whole-branch review after task reviews and resolve blocking findings before declaring implementation complete.
- [ ] Linux remains unverified unless explicitly requested. Re-run completed checks only for changed behavior or a concrete unresolved risk.
- [ ] Research/diagnosis evidence includes accept/defer/reject or cause/unknown outcomes; it need not invent a product change to be complete.
- [ ] Update this plan and the index with tested revision, scope, review outcomes and remaining limitations. Do not mark deferred live UAT complete.

## STOP conditions

Do not infer hidden-work absence from a missing field, promise complete SQL cost coverage, or require journal access for offline artifacts. If artifact storage cannot retain the reconciled identity, resolve the storage design before changing output.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.

