# Plan 039: Tell the agent what to do on a BUSY rejection

> **Executor instructions**: One work item. Run its acceptance check and confirm the expected result. If anything in
> "STOP conditions" occurs, stop and report — do not improvise. When done, update the status row for this plan in
> `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat ece2f30..HEAD -- src/SqlHarness.Mcp/McpExecutionGate.cs src/SqlHarness.Mcp/Tools/McpToolCatalog.cs docs/mcp.md`
> If any in-scope file changed since this plan was written, compare the "Current state" excerpts against the live
> code before proceeding; on a mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW (message and hint only; the gate contract stays the same)
- **Depends on**: —
- **Category**: agent guardrail
- **Planned at**: commit `ece2f30`, 2026-10-08

## Source session

A Claude Code session checked a System2 data deployment in five isolated database scopes through MCP request-scope mode. Three
times the agent sent database calls in parallel; seven calls were rejected with `busy`. The local operator guidance
already says to run database operations sequentially, so more skill text is unlikely to help; the rejection itself
should carry the instruction.

## W1 — BUSY result carries a hint naming the running tool

**Evidence.** Each rejection returned `error: { code: "busy", phase: "execution", message: "Another database
operation is already running.", hint: null }`. The agent retried only after noticing the pattern.

**Current state.** `McpExecutionGate.BusyResult` (`src/SqlHarness.Mcp/McpExecutionGate.cs:91-98`) builds a
`SqlHarnessError(BusyCode, "execution", BusyMessage)` with no hint. `TryEnterDb()` (`:81`) does not record which
tool holds the slot; `McpToolCatalog.cs:398-399` calls it and returns `BusyResult(command, budget)`.
`docs/mcp.md:130` documents "no queue, no retry hint".

**Change.** Keep the immediate rejection, code, exit code and message. Record the holding tool name when the slot is
entered (cleared on exit) and set `hint` to a static template: `One database operation runs per process. Wait for
the running <tool> call to return, then send the next call.` Tool names are fixed identifiers, so no user data or
scope enters the hint. Update `docs/mcp.md:130` and `:134` to describe the hint instead of "no retry hint".

**Acceptance.** Test in `tests/SqlHarness.Mcp.Tests`: while one database tool holds the gate, a second database call
returns `busy` with exit 2, the unchanged message, and a hint naming the first tool; after the first returns, the
next call executes. Safe local tools still run in parallel. MCP schema/lifecycle tests stay green.

## Out of scope

- Queueing or waiting for the slot: the gate stays non-blocking by design.
- Operator guidance: the sequential-operation rule is already documented locally.

## STOP conditions

- The "Current state" excerpts no longer match the code.
- Recording the holder needs shared state beyond the gate (for example across processes).
