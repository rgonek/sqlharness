# Plan 106: Keep actionable errors independent of small result-cell limits

> **For agentic workers:** Use superpowers:executing-plans or, when assigned, superpowers:subagent-driven-development. Follow tasks in order and honor STOP conditions. This queue entry is planning authorization only; merge, push, installation, paid-agent runs and live database access are separate.
>
> **Drift check:** Run `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Mcp/McpResultAdapter.cs src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Core/AgentOutputLimits.cs src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`. Reconcile changed paths against Current state before continuing; do not assume source HEAD identifies a running MCP binary.

**Goal:** Keep actionable errors independent of small result-cell limits.

**Architecture:** Give redacted error Message/Hint a floor equal to AgentOutputLimits.DefaultCellCharacters, using max(requested cell limit, default cell limit), bounded by existing maximum and the final response budget. Keep Location.Path identity behavior unchanged unless a separate demonstrated defect requires it. Redact before clipping. Do not add a new public knob, increase total response ceilings, or return unbounded exception chains. Under a tiny total budget preserve error code/phase and truthful truncation even when the full diagnosis cannot fit.

**Tech stack:** C#/.NET 10, System.Text.Json, xUnit and offline MCP SDK fixtures where relevant; PowerShell on Windows.

**Spec:** This plan's Contract and task assertions are the bounded design specification. Source observations are historical input, not an implemented contract.

## Status and priority

- Status: IN PROGRESS — Tasks 1–2 complete and reviewed; Task 3 contract and final review fixes are implemented. Focused suites and branch formatting pass on the final-fix commit; independent scoped review and controller-owned Windows gate remain pending ([final-fix evidence](106-evidence/final-fix.md)); recorded 2026-10-23.
- Priority: **P2** (P1 correctness/evidence interpretation, P2 useful diagnostics, P3 ergonomics or cost research, P4 exploratory/residual work).
- Effort: M; change risk: MED.
- Planned at: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Dependencies: 067 error/cell parity and 093/094/095 budget behavior exist on main; no new-plan dependency.
- Source observations: [error-message-cell-clipping.md](../docs/observations/error-message-cell-clipping.md).

## Why this matters and current state

`McpResultAdapter.cs:373-384` clips error Message, Hint and Location.Path with maximumCellCharacters. The same caller limit is used for result cells; CLI Renderer has parity. The observed low cell cap removed the actionable error tail. No historical full-message recovery was verified.

The observation establishes the described source/workflow boundary, not recurrence across all installations or measured savings. Implementation and runtime identity must remain distinct. Report-related observations and final-report format are excluded.

## Contract

Give redacted error Message/Hint a floor equal to AgentOutputLimits.DefaultCellCharacters, using max(requested cell limit, default cell limit), bounded by existing maximum and the final response budget. Keep Location.Path identity behavior unchanged unless a separate demonstrated defect requires it. Redact before clipping. Do not add a new public knob, increase total response ceilings, or return unbounded exception chains. Under a tiny total budget preserve error code/phase and truthful truncation even when the full diagnosis cannot fit.

## Files and responsibilities

- `src/SqlHarness.Mcp/McpResultAdapter.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Cli/Commands/Renderer.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Core/AgentOutputLimits.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — inspect and change only the behavior named in the tasks; follow its current record, serializer and validation conventions.
- Tests: extend existing matching suites identified by `McpTokenBudgetTests|AgentOutputTests`; use current fixture and assertion style instead of a new framework.
- Documentation: affected tool descriptions/schemas, capabilities when runtime policy changes, and relevant `docs/mcp/` pages; README contains only human guidance.
- Evidence: `plans/106-evidence/`, `plans/106-verification-evidence.md` if needed, this plan and `plans/README.md`.

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

### Task 1: Pin failure behavior

- [ ] Add synthetic connect/auth/SQL error tests with maxCellChars 0, 200, default and maximum, with actionable tails and credential sentinels spanning clipping boundaries. Assert result cells still obey the caller limit, secrets never appear, and error field floors remain within the total byte budget.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 2: Implement one shared error clipping policy

- [ ] Put the narrow policy beside existing Core output limits and use it in MCP and CLI renderer. Preserve independent envelope versions and error codes. Cover Hint=null, long/multibyte text, all-error oversized responses and minimum budgets. Do not change authentication retry policy.
- [ ] Add a behavior-level regression that fails before the change, run the focused suite, implement the narrow change, then confirm GREEN.
- [ ] Save the task's evidence and inspect the diff. Obtain task review before moving to the next independently reviewable change.

### Task 3: Update and verify the contract

- [x] Update schema descriptions and docs/mcp/runtime.md to state cell vs error limits accurately; verify CLI/MCP description parity and focused output suites.
- [x] Add a contract-description regression, observe RED before changing descriptions, and confirm GREEN.
- [x] Save task evidence/report and inspect the scoped diff.
- [ ] Obtain task review; controller owns direct branch formatting, full Windows gate and final review, then archive the exact tested revision.

## Verification and done criteria

Run `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "McpTokenBudgetTests|AgentOutputTests"` and, for MCP tests, `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "McpTokenBudgetTests|AgentOutputTests"`. Both must exit 0 with the intended tests actually discovered; a zero-test run is not evidence.

- [ ] `git diff --check` exits 0; inspect the diff and resolve every new relative Markdown link.
- [ ] All task outcomes and relevant source paths are recorded; the final status distinguishes implementation, diagnosis, research and deferred live acceptance.
- [ ] Run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef <recorded-base>` and retain its direct exit code/log. Run `just verify -LogDirectory D:/temp/sqlharness-plan106-final-verify`; both must exit 0 on the final product tree. Obtain independent whole-branch review after task reviews and resolve blocking findings before declaring implementation complete.
- [ ] Linux remains unverified unless explicitly requested. Re-run completed checks only for changed behavior or a concrete unresolved risk.
- [ ] Research/diagnosis evidence includes accept/defer/reject or cause/unknown outcomes; it need not invent a product change to be complete.
- [ ] Update this plan and the index with tested revision, scope, review outcomes and remaining limitations. Do not mark deferred live UAT complete.

## STOP conditions

If the floor defeats existing minimum-envelope guarantees, redesign reservation/omission behavior before raising any limit. No live authentication attempts or retries are needed.

Also stop and report a concrete mismatch if the central source assumption is false, required work changes a safety boundary, or two reasonable fix attempts leave the same verification failure. Reconcile ordinary line-number drift; do not follow stale excerpts blindly.

## Maintenance and delivery

Keep one authoritative home for each interface meaning: schemas/descriptions for arguments, capabilities for runtime policy, skill for user workflow. Changes to artifact models must retain legacy readers and bounded selectors. Coordinate shared ArtifactReader/projection edits with the other observation plans rather than running overlapping changes concurrently. Record rejected alternatives so the next audit does not propose the same work without new evidence.

A completed plan is reviewable local work, not permission to merge, push, install or execute against an operator target.

