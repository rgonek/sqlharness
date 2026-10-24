# Plan 104: Correct local-object IO aggregation across repetitions

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Core/BenchmarkRunner.cs src/SqlHarness.Core/Diagnostics.cs src/SqlHarness.Core/BenchmarkSummary.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs src/SqlHarness.Core/AgentOutputProjection.cs`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Correct local-object IO aggregation across repetitions.

**Architecture:** Aggregate positively identified anonymous table-variable counters within each run before computing cross-run distributions. Use an explicitly aggregate identity, consistent with the existing `table variables` journal concept, and retain raw per-run provenance where already stored. Never match unrelated #temp names heuristically or claim individual variable identity when the engine exposes only generated names. Preserve total batch reads, LOB accounting semantics, exact permanent-table keys, and 093/102 clipping guarantees. Existing saved summaries remain historical; do not silently reinterpret them as newly collected evidence.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: DONE on feature branch `plan-104`; merge prepared with main `9718b052` on 2026-10-24 in the isolated worktree. All three tasks and Luna xhigh reviews complete; Sol low whole-branch fix rereview approved. Windows feature gate passed on `f22137e8`; branch format checked 21 C# files and passed. Empirical rule accepted without a patch-version gate; Linux and implementation live UAT remain unverified. Combined integration gate/reviews and local main fast-forward are controller-owned and pending. See [feature evidence](104-verification-evidence.md) and [integration evidence](104-evidence/integration/implementation-report.md).
- Priority: **P1** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: M; change risk: MED.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 079 journal grouping and 093/102 identity-safe selection exist on main. Independent of 103; serialize shared projection edits.
- Source observations: [statement-metrics-and-local-object-noise.md](../docs/observations/statement-metrics-and-local-object-noise.md).

## Why this matters and current state

`BenchmarkRunner.cs:209-220` groups LogicalReadsByTable by raw key and computes distributions with `TryGetValue(table, out var reads) ? reads : 0L`. A generated variable name observed once among three repetitions therefore gets median zero. `JournalBenchmarkBuilder.cs:267-293` independently maps matching generated names to `table variables`. Diagnostics.TempTableKey handles session suffixes, not this cross-run variable identity.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Aggregate positively identified anonymous table-variable counters within each run before computing cross-run distributions. Use an explicitly aggregate identity, consistent with the existing `table variables` journal concept, and retain raw per-run provenance where already stored. Never match unrelated #temp names heuristically or claim individual variable identity when the engine exposes only generated names. Preserve total batch reads, LOB accounting semantics, exact permanent-table keys, and 093/102 clipping guarantees. Existing saved summaries remain historical; do not silently reinterpret them as newly collected evidence.

## Accepted experimental recognition rule (2026-10-24)

The user explicitly chose to proceed on the experimental evidence despite the absence of a formal cross-version IO naming guarantee. This decision supersedes the earlier STOP and the proposed exact-build allowlist. The observed engine build remains an evidence limit, not an imposed runtime version gate.

At the SQL Server STATISTICS IO producer, classify the decoded raw name **before** temporary-name normalization. An exact bare `#` plus eight hexadecimal characters is the recognized anonymous table-variable signature; an explicit-local suffix shape recognized by `TempTableKey` retains local-temp provenance and its normalized base name. All other forms and historical inputs lacking producer provenance remain Unknown. Never reclassify old normalized strings by pattern. Keep raw per-run evidence and use a typed/group identity or collision-safe encoding so a real object named `table variables` cannot merge with the aggregate. Sum anonymous-variable counters within each run, then compute distributions across runs. Preserve batch totals, LOB semantics, permanent keys, bounded output and existing failure semantics.

This is a characterized operational rule, not a Microsoft guarantee. Unexpected or unsupported raw forms retain their identity. Do not add an exact SQL Server patch-build gate or additional diagnostic queries to operator workloads.
## Files and responsibilities

- `src/SqlHarness.Core/BenchmarkRunner.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/Diagnostics.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/BenchmarkSummary.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/Artifacts.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/AgentOutputProjection.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `BenchmarkRunTests|BenchmarkSummaryTests|ArtifactReaderTests|AgentOutputTests|McpTokenBudgetTests`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/104-evidence/`, `plans/104-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Reproduce misleading medians

- [x] In BenchmarkRunTests/BenchmarkSummaryTests add repeat=2 and repeat=3 fixtures: a new #hex variable name per run with 100 reads must yield aggregate median 100, not 50/0; two generated variables per run must sum within the run first. Include absent real objects, explicit #temp tables, stable permanent names and long escaped keys.
- [x] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [x] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 2: Centralize explicit aggregation semantics

- [x] Extract/reuse the narrow name classification from journal normalization in a shared Core helper without introducing Core-to-Journal coupling. Apply normalization before BenchmarkRunner distributions and use equivalent semantics for new saved metrics. Account for a real name colliding with an aggregate label using a typed/group representation or explicit collision-safe encoding; never overwrite or merge a real identity silently.
- [x] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [x] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 3: Verify consumers and compatibility

- [x] Check single measure, named-set measure, compare and artifact metrics. Add fixtures proving unchanged totals and clipping identities, truthful grouping, legacy artifact readability and no lost later fitting entries. Document the aggregate meaning in affected contracts and run the focused suite, task reviews, final review and Windows gate.
- [x] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [x] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

## Verification and done criteria

Run `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "BenchmarkRunTests|BenchmarkSummaryTests|ArtifactReaderTests|AgentOutputTests|McpTokenBudgetTests"` and, for MCP tests, `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "BenchmarkRunTests|BenchmarkSummaryTests|ArtifactReaderTests|AgentOutputTests|McpTokenBudgetTests"`. Both must exit 0 with the intended tests actually discovered; a zero-test run is not evidence.

- [x] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [x] All task outcomes and relevant source paths are recorded; the final status distinguishes implementation, diagnosis, research and deferred live acceptance.
- [x] Run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef <recorded-base>` and retain its direct exit code/log. Run `just verify -LogDirectory D:/temp/sqlharness-plan104-final-verify`; both must exit 0 on the final product tree. Obtain independent whole-branch review after task reviews and resolve blocking findings before declaring implementation complete.
- [x] Linux remains unverified unless explicitly requested. Re-run completed checks only for changed behavior or a concrete unresolved risk.
- [x] Research/diagnosis evidence includes accept/defer/reject or cause/unknown outcomes; it need not invent a product change to be complete.
- [x] Update this plan and the index with tested revision, scope, review outcomes and remaining limitations. Do not mark deferred live UAT complete.

## STOP conditions

Do not globally merge every name starting with #. If generated names cannot be distinguished from explicit user names, return a documented ambiguity instead of claiming exact attribution; obtain a reviewed resolution before implementation proceeds.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.
