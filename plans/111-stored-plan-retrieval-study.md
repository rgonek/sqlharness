# Plan 111: Evaluate a bounded catalog-plan retrieval path

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Core/AgentOutputLimits.cs src/SqlHarness.Core/PlanDistiller.cs src/SqlHarness.Core/SqlExecution.cs src/SqlHarness.Mcp/McpOperationMapper.cs docs/mcp/inputs.md docs/mcp/artifacts.md`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Evaluate a bounded catalog-plan retrieval path.

**Architecture:** Determine whether a new retrieval path is warranted when measurement cannot replace a stored plan (DML, absent parameters, historical plan diagnosis). Compare existing catalog SQL/server-side XML projection guidance, controlled export to an input-root file, and a new bounded internal capture-and-distill path. Keep source attribution explicit: stored/estimated plans are not current measured runtime evidence. Do not raise cell limits or introduce arbitrary file/SQL execution access.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: RESEARCH COMPLETE — Tasks 1–3 and Sol low whole-branch review approved; final scoped re-review pending. Implementation deferred pending recurrence. No feature or deployment was authorized.
- Priority: **P4** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: M; change risk: MED.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: Coordinate 112 write-evidence study; no hard predecessor. Existing input-root and read-only contracts remain binding.
- Source observations: [query-store-plan-cell-clipping.md](../docs/observations/query-store-plan-cell-clipping.md), [write-statement-and-index-maintenance-evidence.md](../docs/observations/write-statement-and-index-maintenance-evidence.md).

## Why this matters and current state

AgentOutputLimits.MaximumCellCharacters is 4096. The observed Query Store plan had about 23k characters, so query-cell output could not carry it intact. Plan accepts caller content or an allowed-root file; it has no current query-cell-to-distillation transfer. A later measured plan replaced the stored plan in that one session.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Determine whether a new retrieval path is warranted when measurement cannot replace a stored plan (DML, absent parameters, historical plan diagnosis). Compare existing catalog SQL/server-side XML projection guidance, controlled export to an input-root file, and a new bounded internal capture-and-distill path. Keep source attribution explicit: stored/estimated plans are not current measured runtime evidence. Do not raise cell limits or introduce arbitrary file/SQL execution access.

## Files and responsibilities

- `src/SqlHarness.Core/AgentOutputLimits.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/PlanDistiller.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/SqlExecution.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Mcp/McpOperationMapper.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `docs/mcp/inputs.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `docs/mcp/artifacts.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `none for notes-only study; reuse plan/distiller fixtures if adding executable offline checks`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/111-evidence/`, `plans/111-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Define useful cases and alternatives

- [x] Create three synthetic task descriptions: measurable SELECT, unavailable-parameter historical SELECT, and DML stored plan. For each enumerate required evidence and whether existing measure/plan can supply it. Inspect the existing source safety, distillation and input-root checks; document trust and size boundaries. ([Task 1 evidence](111-evidence/task-1-useful-cases.md))
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. No production behavior changed. Task report: `.superpowers/sdd/111-stored-plan-retrieval-study/task-1-report.md` (local execution record).

### Task 2: Evaluate bounded transfer designs

- [x] Use static plan fixtures larger than 4096 characters and malformed/oversized XML. Compare selected server-side fields versus internal complete-document distillation without model round-trip. Specify target locking, catalog selector validation, parsing/document caps, cancellation and no raw XML response. Assess whether any existing tool can express this without adding a new one. ([Task 2 evidence](111-evidence/task-2-bounded-transfer.md))
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix. Task report: `.superpowers/sdd/111-stored-plan-retrieval-study/task-2-report.md` (local execution record).

### Task 3: Record go/no-go and handoff

- [x] Save findings and fixture commands under plans/111-evidence/. Decision: defer implementation for insufficient recurrence. Offline feasibility is established for the SQL Server Showplan example, but no repeated demand, real response cost, model-token savings, or cross-engine design is established. No separate executor-ready implementation plan is warranted yet. Keep the existing caller-supplied/rooted-file path as the available workflow; deployment/live permissions remain separate.
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved. ([Task 3 decision evidence](111-evidence/task-3-go-no-go.md))
- [x] Save the task's evidence and inspect the diff. No production behavior changed. Task report: `.superpowers/sdd/111-stored-plan-retrieval-study/task-3-report.md` (local execution record).

## Verification and done criteria

Documentation/research checks only unless executable fixtures change.

- [x] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [x] All task outcomes and relevant source paths are recorded; the final status distinguishes research, deferred implementation and unverified live acceptance.
- [x] No executable code/tests changed. For notes-only work, diff/link/example checks are the complete scoped check; no product gate is claimed.
- [x] Linux remains unverified. Existing checks were not rerun because no behavior changed.
- [x] Research evidence includes a defer decision and explicit unknown outcomes; no product change was invented.
- [x] Updated this plan and the index with the assessed revision, scope, task and whole-branch review outcomes, and remaining limitations. The Sol low whole-branch review is approved; final scoped re-review remains pending and deferred live acceptance is not complete.

## STOP conditions

No live Query Store reads, new export files outside allowed roots, raw XML public endpoint, arbitrary catalog-plan execution, or default cell-limit increase.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.
