# Plan 108: Default the unique measure-plan variant and clarify invalid selections

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Core/ArtifactReader.cs src/SqlHarness.Mcp/McpOperationMapper.cs src/SqlHarness.Mcp/Tools/McpToolCatalog.cs src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs tests/SqlHarness.Tests/ArtifactReaderTests.cs`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Default the unique measure-plan variant and clarify invalid selections.

**Architecture:** Only omitted/empty mapped variant defaults to query for measure and measure-set. Compare still requires explicit baseline or candidate. Explicit invalid/nonempty values are never silently repaired. Emit artifact-kind-specific accepted values on rejection. Retain current exit classification unless a separate compatibility-reviewed error-classification change is explicitly selected; do not reopen Plan 077 error policy here.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: IMPLEMENTED on `plan-108`; task reviews approved; Windows gate and whole-branch review pending. See [evidence](108-verification-evidence.md).
- Priority: **P3** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: S; change risk: LOW.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 065/095 artifact reader behavior exists on main; coordinate 103/105 shared reader edits.
- Source observations: [artifact-plan-variant-default.md](../docs/observations/artifact-plan-variant-default.md).

## Why this matters and current state

ArtifactReader.cs:467-472 accepts only query for measure/measure-set and baseline/candidate for compare, then emits `The artifact plan variant is invalid.` McpOperationMapper maps omitted variant to string.Empty. Schema marks variant optional. No accepted value is named in the error.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Only omitted/empty mapped variant defaults to query for measure and measure-set. Compare still requires explicit baseline or candidate. Explicit invalid/nonempty values are never silently repaired. Emit artifact-kind-specific accepted values on rejection. Retain current exit classification unless a separate compatibility-reviewed error-classification change is explicitly selected; do not reopen Plan 077 error policy here.

## Files and responsibilities

- `src/SqlHarness.Core/ArtifactReader.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Mcp/McpOperationMapper.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `ArtifactReaderTests|ArtifactCommandTests|McpTokenBudgetTests`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/108-evidence/`, `plans/108-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Test artifact-kind selection

- [ ] Add fixtures for measure and measure-set without variant, explicit query, compare without variant, invalid casing/nonempty variant, and selectors on non-plan sections. Assert the default has identical identity/repetition/statement/set semantics to explicit query.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 2: Normalize after manifest validation

- [ ] Resolve the unique variant in ArtifactReader after artifact kind/owner validation so CLI and MCP share semantics; keep ambiguity rejection for compare. Update both served tool descriptions and docs/mcp/artifacts.md. Do not default variant before knowing artifact kind.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 3: Verify bounded errors and compatibility

- [ ] Run focused tests covering scope mismatch, absent saved plans, invalid selectors and minimum budgets. Add an offline MCP request omitting variant; assert success only for measure kinds. Obtain review and Windows gate evidence.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

## Verification and done criteria

Run `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "ArtifactReaderTests|ArtifactCommandTests|McpTokenBudgetTests"` and, for MCP tests, `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "ArtifactReaderTests|ArtifactCommandTests|McpTokenBudgetTests"`. Both must exit 0 with the intended tests actually discovered; a zero-test run is not evidence.

- [ ] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [ ] All task outcomes and relevant source paths are recorded; the final status distinguishes implementation, diagnosis, research and deferred live acceptance.
- [ ] Run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef <recorded-base>` and retain its direct exit code/log. Run `just verify -LogDirectory D:/temp/sqlharness-plan108-final-verify`; both must exit 0 on the final product tree. Obtain independent whole-branch review after task reviews and resolve blocking findings before declaring implementation complete.
- [ ] Linux remains unverified unless explicitly requested. Re-run completed checks only for changed behavior or a concrete unresolved risk.
- [ ] Research/diagnosis evidence includes accept/defer/reject or cause/unknown outcomes; it need not invent a product change to be complete.
- [ ] Update this plan and the index with tested revision, scope, review outcomes and remaining limitations. Do not mark deferred live UAT complete.

## STOP conditions

Do not default a compare side, loosen owner checks, change available repetitions, or use an error-code change as an incidental cleanup.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.

