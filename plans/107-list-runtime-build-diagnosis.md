# Plan 107: Diagnose list-parameter runtime and source divergence

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Core/CompareCellRunner.cs src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/ListParameterExpander.cs src/SqlHarness.Mcp/McpOperationMapper.cs tests/SqlHarness.Tests/CompareTests.cs tests/SqlHarness.Tests/ListParameterExpanderTests.cs tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Diagnose list-parameter runtime and source divergence.

**Architecture:** Deliver an offline reproduction matrix and a cause classification: stale/mismatched build, current-source uncovered path, client payload difference, or unresolved. Do not assume a passing Core test identifies a running server. No install, restart, live SQL, validation bypass, or replacement parameter strategy is part of diagnosis. If a current-source defect is proven, write a narrow follow-up implementation plan with failing coverage rather than silently expanding this diagnostic scope.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: DIAGNOSIS COMPLETE — known-source compare failures confirmed; follow-up implementation and historical deployment attribution remain open. Task 1, Task 2, Task 2 re-review, Task 3, and whole-branch reviews are archived in the [review archive](107-evidence/reviews/README.md). Reviews are approved; the two whole-branch wording Minors are corrected in the diagnosis, approved by the final scoped Sol low re-review. Windows `just verify` passed on `19035a07f16f04e77977ab5f1f15ecc01090ca21`; results and nonempty stage logs are in [the Task 3 diagnosis](107-evidence/task-3-diagnosis.md) and [gate evidence](107-evidence/windows-gate-20261023-task3/).
- Local integration: merged into `main` at `ca0f601a`; combined Windows gate and Sol low integration review approved ([integration evidence](107-evidence/integration-main.md)). No push.
- Priority: **P2** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: M; change risk: LOW.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 083/097 implementations on main; no new-plan dependency.
- Source observations: [list-parameter-runtime-divergence.md](../docs/observations/list-parameter-runtime-divergence.md), [table-valued-parameter-substitution.md](../docs/observations/table-valued-parameter-substitution.md).

## Why this matters and current state

CompareCellRunner.cs:75-84 expands setup/baseline/candidate then calls DeduplicateClassificationListParameters before CombineClassificationParameters. CompareTests.Compare_dry_run_deduplicates_a_list_shared_by_multiple_SQL_texts tests shared lists, but not the entire observed named-set MCP path. Installed dry runs rejected bare IN @Ids and reported a phantom @Ids1 collision for IN (@Ids); loaded build correspondence was not established.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Deliver an offline reproduction matrix and a cause classification: stale/mismatched build, current-source uncovered path, client payload difference, or unresolved. Do not assume a passing Core test identifies a running server. No install, restart, live SQL, validation bypass, or replacement parameter strategy is part of diagnosis. If a current-source defect is proven, write a narrow follow-up implementation plan with failing coverage rather than silently expanding this diagnostic scope.

## Files and responsibilities

- `src/SqlHarness.Core/CompareCellRunner.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/SqlSafety.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/ListParameterExpander.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Mcp/McpOperationMapper.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `tests/SqlHarness.Tests/CompareTests.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `tests/SqlHarness.Tests/ListParameterExpanderTests.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `CompareTests|ListParameterExpanderTests|McpParameterBatchTests`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/107-evidence/`, `plans/107-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Establish evidence and build identity

- [x] Record HEAD, candidate executable path/hash/version/buildId and any already available process-start evidence. Do not dump configs or credentials. A PATH executable is not proof of the process loaded by an existing client; label unresolved identity honestly. Reuse retained synthetic request shapes; no historical workload replay.
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

### Task 2: Compare exact offline request paths

- [x] Run known-source isolated test-host MCP dry runs for identical baseline/candidate SQL, one named set with one int list value, no scalar parameters, using both IN @Ids and IN (@Ids). Add singleton/multi-value/empty-list cases, multiple sets, setup reuse and a genuine caller-supplied @Ids1 collision which must remain rejected. Compare Core and real-SDK MCP paths, with connection attempts asserted zero.
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

### Task 3: Publish a bounded diagnosis

- [x] Save requests without private targets, outcomes, source/build identity confidence and focused-test logs under plans/107-evidence/. Known-source failures are present; source correction is assigned to the narrow implementation follow-up, and historical deployment attribution remains assigned as an unresolved check. No tests or production behavior changed in Task 3.
- [x] Record the inspected source, command or fixture, expected outcome and observed outcome; keep uncertain causes explicitly unresolved.
- [x] Save the task's evidence and inspect the diff. Do not change production behavior as an incidental fix.

## Verification and done criteria

Run `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "CompareTests|ListParameterExpanderTests|McpParameterBatchTests"` and, for MCP tests, `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "CompareTests|ListParameterExpanderTests|McpParameterBatchTests"`. Both must exit 0 with the intended tests actually discovered; a zero-test run is not evidence.

- [x] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [x] All task outcomes and relevant source paths are recorded; the final status distinguishes implementation, diagnosis, research and deferred live acceptance.
- [x] No executable code/tests changed in Task 3; scoped documentation checks were used. Earlier focused suite evidence is recorded and is not presented as proof of a fixed product behavior.
- [x] Linux remains unverified. No completed check was rerun without a changed behavior or concrete unresolved risk.
- [x] Diagnosis evidence includes confirmed rejection outcomes and explicitly unknown source/deployment causes.
- [x] Updated this plan and the index with tested source, scope, review-focus outcomes and remaining limitations. Historical deployment attribution remains deferred.

## STOP conditions

Do not restart an active MCP server or replace binaries to complete identity evidence. Missing access to loaded-build identity is a documented unknown, not permission to guess; a failed dry run must never trigger a live execution.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.
