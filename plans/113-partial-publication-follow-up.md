# Plan 113: Reconcile partial-matrix retention and publication-denial evidence

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Core/CompareMatrixRunner.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/ArtifactDirectoryPublisher.cs tests/SqlHarness.Tests/ArtifactDirectoryPublisherTests.cs tests/SqlHarness.Tests/CompareMatrixTests.cs`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Reconcile partial-matrix retention and publication-denial evidence.

**Architecture:** Close the historical duplicate defect explicitly and evaluate only residual recurrence/coverage. Verify that publication retry never reruns SQL or artifact generation and that completed matrix cells remain journaled on terminal failure. Existing bounded retry is an implemented mitigation, not proof antivirus/indexing caused the original denial. Do not add broader retries or retention pins.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: DIAGNOSIS COMPLETE (2026-10-24); Task 1/2/3 reviews and the Sol low whole-branch review approved on `8ed91395`.
- Priority: **P4** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: S; change risk: LOW.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 076 completed-run retention is already implemented. No new product change selected.
- Source observations: [partial-compare-journal-coverage.md](../docs/observations/partial-compare-journal-coverage.md).

## Why this matters and current state

CompletedRuns now flows from matrix failures into BenchmarkRuns. ArtifactDirectoryPublisher.PublishStaging already retries only Windows IOException HResult 0x80070005, up to three retries with 100ms delay, provided staging exists and destination does not. Existing WindowsFact tests assert a transient denial reuses staging once and persistent denial stops after four attempts. The original process holding the directory was never identified.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Close the historical duplicate defect explicitly and evaluate only residual recurrence/coverage. Verify that publication retry never reruns SQL or artifact generation and that completed matrix cells remain journaled on terminal failure. Existing bounded retry is an implemented mitigation, not proof antivirus/indexing caused the original denial. Do not add broader retries or retention pins.

## Files and responsibilities

- `src/SqlHarness.Core/CompareMatrixRunner.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/SqlHarnessModule.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/ArtifactDirectoryPublisher.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `tests/SqlHarness.Tests/ArtifactDirectoryPublisherTests.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `tests/SqlHarness.Tests/CompareMatrixTests.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `ArtifactDirectoryPublisherTests|CompareMatrixTests|JournalingBenchmarkTests`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/113-evidence/`, `plans/113-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Verify landed behavior and evidence

- [x] Inspect 076 implementation and the current publisher/test paths; record actual source revision, retry predicates and existing test coverage under plans/113-evidence/. Update historical status references only after verifying the claims. Preserve the original historical event rather than rewriting it as a confirmed cause.
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

### Task 2: Run focused offline confirmation if needed

- [x] Run the focused Windows suites. Existing Core fixtures cover the relevant retry and journal boundaries, so no tests were added. Record tests skipped on non-Windows separately. [Task 2 evidence](113-evidence/task-2-focused-offline-confirmation.md)
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved. See [Task 2 evidence](113-evidence/task-2-focused-offline-confirmation.md).
- [x] Save the task's evidence and inspect the diff. No production behavior changed. Durable evidence and the approved Task 2 reviews are archived under [Task 2 evidence](113-evidence/task-2-focused-offline-confirmation.md) and [review records](113-evidence/reviews/).

### Task 3: Decide whether residual diagnosis is justified

- [x] No later recurrence was found in the inspected observation and Plan 076 record; disposition: 'mitigation present; original cause unresolved; no further change'. The finding is bounded to those records and does not imply a global absence of recurrence. See [decision evidence](113-evidence/task-3-decision.md).
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

## Verification and done criteria

Run `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "ArtifactDirectoryPublisherTests|CompareMatrixTests|JournalingBenchmarkTests"`; the relevant Core fixtures must be discovered and pass. The matching MCP filter is recorded for discovery, but is **not applicable as a pass criterion** for these Core-owned filesystem-publication and completed-run journal behaviors: no matching MCP suite exists, and this contract accepts the Core offline fixtures as the evidence source. A zero-test MCP run is not success evidence. Any separate adjacent MCP projection tests are supporting envelope checks only. Record the resulting limitation: MCP integration of these outcomes is not verified by this plan's focused suites.

- [x] `git diff --check` exits 0; inspected the diff and resolved every new relative Markdown link (see [final verification](113-verification-evidence.md)).
- [x] All task outcomes and relevant source paths are recorded; status distinguishes diagnosis from implementation and deferred live acceptance.
- [x] Notes-only work: diff/link checks are the scoped check; no product gate is claimed.
- [x] Linux remains unverified; no completed executable check was rerun because behavior was unchanged.
- [x] Diagnosis evidence records the disposition and keeps the cause unknown; no product change was invented.
- [x] Updated this plan and the index with tested revision, scope, approved review state and limitations. Deferred live acceptance remains unverified.

## STOP conditions

No unbounded/DB retries, deleting operator artifacts, claiming root cause from successful retry, or reopening completed-cell retention without a new failing reproduction.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.
