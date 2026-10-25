# Plan 112: Evaluate write-statement and existing-index evidence

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/PlanDistiller.cs plans/072-mutation-risk-tiers.md plans/073-mutation-approval-broker.md plans/063-missing-index-overlap.md docs/mcp/tools.md`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Evaluate write-statement and existing-index evidence.

**Architecture:** Deliver three separately ranked decisions: nonexecuting DML-plan evidence; actual write benchmarking on a disposable/restorable target; read-only existing-index overlap and expanded-list family discovery. Treat rollback-only execution as write activity requiring mutation policy, never as a read-only escape. Compare lock/log/trigger/identity/sequence/external side effects and cleanup/outcome-unknown risks. No index drop, mutation execution or safety-tier relaxation is part of this plan.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: TODO — research; recorded 2026-10-23.
- Priority: **P4** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: L; change risk: HIGH.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 072/073 mutation policy, 063 missing-index overlap exist; coordinate 111 for stored plans.
- Source observations: [write-statement-and-index-maintenance-evidence.md](../docs/observations/write-statement-and-index-maintenance-evidence.md), [table-valued-parameter-substitution.md](../docs/observations/table-valued-parameter-substitution.md).

## Why this matters and current state

Read-only query/measure/compare reject persistent DML by design. Mutation broker approves execution, not a write-benchmark contract. Plan 063 owns missing-index overlap, not redundant existing indexes. Expanded IN lists can fragment Query Store identities; a clustered key plus a similarly keyed nonclustered index is not proof the latter is redundant.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Deliver three separately ranked decisions: nonexecuting DML-plan evidence; actual write benchmarking on a disposable/restorable target; read-only existing-index overlap and expanded-list family discovery. Treat rollback-only execution as write activity requiring mutation policy, never as a read-only escape. Compare lock/log/trigger/identity/sequence/external side effects and cleanup/outcome-unknown risks. No index drop, mutation execution or safety-tier relaxation is part of this plan.

## Files and responsibilities

- `src/SqlHarness.Core/SqlSafety.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/PlanDistiller.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `plans/072-mutation-risk-tiers.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `plans/073-mutation-approval-broker.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `plans/063-missing-index-overlap.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `docs/mcp/tools.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `none for notes-only study; existing safety fixtures only for offline classification`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/112-evidence/`, `plans/112-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Assess evidence demand and existing coverage

- [ ] Count only non-report observation cases whose selected bottleneck was DML, recording sample size and recurrence limits. Reconcile 063 inventory overlap and 083 TVP support. Describe expanded-list grouping as a candidate heuristic with source/query-shape confirmation, not a canonical text normalization guarantee.
- [ ] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [ ] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

### Task 2: Evaluate the three capability options

- [ ] For estimated-only evidence examine compile-time permissions and side effects, source availability, plan capture constraints and exact no-execution guarantee. For actual benchmarking require an isolated target strategy, explicit operator policy and known cleanup limits. For index inventory compare uniqueness, INCLUDE/filter/order/partition/compression and read usage before any redundancy label; no inferred drop recommendation from key prefix alone.
- [ ] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [ ] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

### Task 3: Deliver independent go/no-go decisions

- [ ] Save a risk/evidence matrix under plans/112-evidence/ and write separate numbered implementation/design plans only for justified accepted directions. Define a reproducible synthetic workload and authorized environment requirements for future UAT without running it. Completion is a reviewed decision record, including low-value/rejected options and why.
- [ ] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [ ] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

## Verification and done criteria

Documentation/research checks only unless executable fixtures change.

- [ ] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [ ] All task outcomes and relevant source paths are recorded; the final status distinguishes implementation, diagnosis, research and deferred live acceptance.
- [ ] If executable code/tests change, use focused suites and the required Windows implementation gate before claiming those changes verified. For notes-only work, diff/link/example checks are the complete scoped check; no product gate is claimed.
- [ ] Linux remains unverified unless explicitly requested. Re-run completed checks only for changed behavior or a concrete unresolved risk.
- [ ] Research/diagnosis evidence includes accept/defer/reject or cause/unknown outcomes; it need not invent a product change to be complete.
- [ ] Update this plan and the index with tested revision, scope, review outcomes and remaining limitations. Do not mark deferred live UAT complete.

## STOP conditions

Do not execute DML/DDL, obtain self-approval, introduce rollback-only read tiers, infer write-cost share from aggregate Query Store data, or declare an index redundant from leading keys alone.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.
