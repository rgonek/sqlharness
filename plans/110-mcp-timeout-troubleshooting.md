# Plan 110: Document SQL, server and client timeout boundaries

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- docs/mcp/runtime.md docs/mcp/startup.md README.md`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Document SQL, server and client timeout boundaries.

**Architecture:** Add concise troubleshooting with three synthetic cases: SQL command expiry with a server envelope; server operation-budget cancellation; external client giving up without a SQLHarness result. Explain that the last case leaves server completion unknown. Distinguish saving configuration from restart/reconnect and verified capabilities. Preserve the 1..300 command policy and existing stop/retry rules; SQLHarness does not control a client's call ceiling.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: Documentation implemented; Task 1–3 reviews and Sol low whole-branch review approved; final scoped re-review pending after the metadata correction.
- Priority: **P3** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: S; change risk: LOW.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 068 phase diagnostics and 092 default SQL timeout setting on main.
- Source observations: [mcp-timeout-layers.md](../docs/observations/mcp-timeout-layers.md).

## Why this matters and current state

McpToolCatalog.cs has separate operation cancellation budgets; SQL command timeout is applied by SqlExecution. docs/mcp/runtime.md already documents execution-default snapshots and restart/reconnect. The observation distinguishes a SQLHarness error envelope from a client's `timed out awaiting tools/call after 300s`; later success below the old limit does not prove a timeout-setting fix.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Add concise troubleshooting with three synthetic cases: SQL command expiry with a server envelope; server operation-budget cancellation; external client giving up without a SQLHarness result. Explain that the last case leaves server completion unknown. Distinguish saving configuration from restart/reconnect and verified capabilities. Preserve the 1..300 command policy and existing stop/retry rules; SQLHarness does not control a client's call ceiling.

## Files and responsibilities

- `docs/mcp/runtime.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `docs/mcp/startup.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `README.md` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `none (documentation-only)`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/110-evidence/`, `plans/110-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Reconcile current contracts

- [x] Read docs/mcp.md, runtime/startup, McpToolCatalog cancellation paths and SqlExecution timeout assignment. List which values are configurable and when loaded; do not publish a client-specific setting or reload assertion without verification from that client's authoritative documentation.
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

### Task 2: Write reachable troubleshooting

- [x] Put the server/runtime distinction in docs/mcp/runtime.md and link from the existing human-facing timeout/setup guidance. Keep interface details authoritative in schemas/capabilities rather than duplicating large tables. Include exact observed error-shape examples using synthetic identifiers only.
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

### Task 3: Verify documentation and boundaries

- [x] Check every relative link and example against served contracts; inspect git diff --check and the final diff. No product tests/full gate are needed if only prose changes. Record which client reload behaviors were verified and which remain unknown; collect another client case only if accessible without new account/setup changes.
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

## Verification and done criteria

Documentation/research checks only unless executable fixtures change.

- [x] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [x] All task outcomes and relevant source paths are recorded; the final status distinguishes implementation, diagnosis, research and deferred live acceptance.
- [x] If executable code/tests change, use focused suites and the required Windows implementation gate before claiming those changes verified. For notes-only work, diff/link/example checks are the complete scoped check; no product gate is claimed.
- [x] Linux verification was not run; it is not required for this documentation-only scope. No Linux pass is claimed. Re-run completed checks only for changed behavior or a concrete unresolved risk.
- [x] Research/diagnosis evidence includes accept/defer/reject or cause/unknown outcomes; it need not invent a product change to be complete.
- [x] Update this plan and the index with checked revision, scope, review outcomes and remaining limitations. Do not mark deferred live acceptance complete.

## Review outcomes

- Task 1: approved after the connection-timeout/load-timing correction at `63a3bc57`.
- Task 2: approved at `176b7412` with no findings.
- Task 3: Luna xhigh spec and quality approved at `2bc9b472`; its Minor about the Linux checkbox is corrected above.
- Whole branch: Sol low approved at `2bc9b472`, with no Critical or Important findings; the Linux-status Minor is corrected above.
- Final scoped re-review of this metadata correction: pending. No approval is claimed yet.

Client-specific timeout/reload behavior, installed-binary and live-database outcomes, and executable regression behavior remain unverified because those runs were outside this documentation-only scope.

## STOP conditions

Do not raise defaults/ceilings, implement automatic DB retries, diagnose authentication/network causes from execution aggregates, or attribute recovery to a saved setting alone.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.
