# Plan 105: Expose bounded statement IO, scans, grants and compilation evidence

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Core/StatementMetrics.cs src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/ArtifactReader.cs src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Expose bounded statement IO, scans, grants and compilation evidence.

**Architecture:** Extend existing artifact sections additively: bounded object IO and availability in `statements`; batch/run distributions and per-table scans in `metrics`. Store grant/compile fields at their real source granularity; do not duplicate a batch aggregate as a measured per-statement value. Reuse 103 identity/availability. Preserve logical vs LOB distinctions and distinguish unavailable from numeric zero. No raw XML exposure or automatic retrieval of every plan. Scan-count exposure is lower priority within this plan than statement reads and grant/compile evidence.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: IN PROGRESS — final review fixes implemented and focused regression suites green; independent final review and the fresh Windows gate remain pending; recorded 2026-10-24 ([final-fixes evidence](105-evidence/final-fixes.md)).
- Priority: **P2** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: L; change risk: MED.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 103 must land first for shared persistence, identity and availability semantics; 104 recommended first for correct table distributions.
- Source observations: [scan-counts-agent-path.md](../docs/observations/scan-counts-agent-path.md), [statement-metrics-and-local-object-noise.md](../docs/observations/statement-metrics-and-local-object-noise.md), [plan-memory-and-compilation-evidence.md](../docs/observations/plan-memory-and-compilation-evidence.md).

## Why this matters and current state

`StatementMetrics.cs` has StatementObjectCounters(LogicalReads, LobLogicalReads, Scans), absent from ArtifactStatementMetric. `Journal/PlanMetricsExtractor.cs:14-28` defines GrantRequestedKb, GrantGrantedKb, GrantMaxUsedKb, CompileTimeMs and CompileCpuMs. `Artifacts.cs:654` RunMetadata has LogicalReadsByTable but no scan/grant/compile metrics. Distilled PlanStatement has no grant/compile fields. Complete operator trees therefore do not supply these diagnostics.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Extend existing artifact sections additively: bounded object IO and availability in `statements`; batch/run distributions and per-table scans in `metrics`. Store grant/compile fields at their real source granularity; do not duplicate a batch aggregate as a measured per-statement value. Reuse 103 identity/availability. Preserve logical vs LOB distinctions and distinguish unavailable from numeric zero. No raw XML exposure or automatic retrieval of every plan. Scan-count exposure is lower priority within this plan than statement reads and grant/compile evidence.

## Files and responsibilities

- `src/SqlHarness.Core/StatementMetrics.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/Artifacts.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/ArtifactReader.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/AgentOutputProjection.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/105-evidence/`, `plans/105-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Define and test the persisted diagnostic shape

- [x] Use Showplan fixtures with present, absent and zero metrics; two statements with different grants; multiple repetitions/sets; UDF/TVF partial coverage; PostgreSQL unavailable SQL Server diagnostics. Freeze the expected units, source and aggregation of every field. Test extraction-to-new-artifact-to-offline-read without a live database.
- [x] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 2: Implement optional saved numeric evidence

- [ ] Reuse existing extractors without reading raw XML through the public artifact tool or relying on the journal. Keep parsing bounded; preserve repetitions and statement identity, explicitly report fallback batch scope. Extend Artifacts and ArtifactReader on 103's model; read older artifacts as unavailable. Use existing section paging and projection byte accounting for object lists; expose executable selectors in tool descriptions.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 3: Validate useful retrieval and cost

- [ ] Extend AgentOptimizationWorkflowTests with a memory/compile tradeoff and a multi-statement IO task. Assert exact retrieved evidence and bytes at min/default budgets, including continuations and no-node/item-fits behavior. Update docs/mcp/artifacts.md and affected schemas/capabilities; do not require these reads for every optimization. Run focused tests, independent task/final reviews and Windows gate.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

## Verification and done criteria

Run `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"` and, for MCP tests, `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "StatementMetricsTests|ArtifactWriterTests|ArtifactReaderTests|AgentOutputTests|AgentOptimizationWorkflowTests|McpTokenBudgetTests"`. Both must exit 0 with the intended tests actually discovered; a zero-test run is not evidence.

- [ ] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [ ] All task outcomes and relevant source paths are recorded; the final status distinguishes implementation, diagnosis, research and deferred live acceptance.
- [ ] Run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef <recorded-base>` and retain its direct exit code/log. Run `just verify -LogDirectory D:/temp/sqlharness-plan105-final-verify`; both must exit 0 on the final product tree. Obtain independent whole-branch review after task reviews and resolve blocking findings before declaring implementation complete.
- [ ] Linux remains unverified unless explicitly requested. Re-run completed checks only for changed behavior or a concrete unresolved risk.
- [ ] Research/diagnosis evidence includes accept/defer/reject or cause/unknown outcomes; it need not invent a product change to be complete.
- [ ] Update this plan and the index with tested revision, scope, review outcomes and remaining limitations. Do not mark deferred live UAT complete.

## STOP conditions

Do not sum nonadditive counters, treat ActualScans as universally equivalent to STATISTICS IO ScanCount, manufacture per-statement compile values, or label missing diagnostics zero. If source attribution is ambiguous, keep unavailable/partial with an explicit source.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.
