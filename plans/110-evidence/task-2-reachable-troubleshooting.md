# Plan 110 Task 2 — reachable timeout troubleshooting

Date: 2026-10-23 · Worktree: `plan-110`

## Scope and outcome

Added a compact timeout guide to `docs/mcp/runtime.md` and linked it from the README execution-timeout setup paragraph. It distinguishes the SQL command timeout, SQLHarness operation-budget cancellation, a client timeout without a server response, and SQL connection establishment. The examples use synthetic data and preserve the runtime's error field names and values; provider timeout message text is explicitly variable.

## Evidence inspected

| Source | Expected contract | Observed |
|---|---|---|
| `src/SqlHarness.Core/SqlHarnessError.cs`, `src/SqlHarness.Core/Contracts.cs`, `src/SqlHarness.Mcp/McpResultAdapter.cs` | MCP v3 failure envelope has `schemaVersion`, `status`, `result`, and an `error` object; null optional error fields are omitted. SQL execution failures use code `sql_execution_failed`; the error phase is `sql`. | Adapter serializes the error's `code`, `phase`, `message`; SQL execution failures have `result: null`. Example uses a placeholder provider message because the actual message varies. |
| `src/SqlHarness.Mcp/McpCallOutcome.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs:325-380` | Process/per-call operation budget cancellation yields the stable `cancelled` error, phase `execution`, with message `The operation was cancelled.` | Source and fixture agree; no live operation was run. |
| `src/SqlHarness.Mcp/McpOperationMapper.cs:750-757`, `src/SqlHarness.Core/SqlExecution.cs:574-579`, `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs:576-628` | SQL command timeout is 1..300 seconds; omitted value uses configured process default. | Mapper applies fallback and passes timeout to provider command. Fixture covers omitted default, explicit override, bounds, and timeout-bearing tools; fixture not run for this documentation task. |
| `src/SqlHarness.Mcp/McpLimits.cs:62-92`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:555-610` | Process operation maximum defaults to 900 seconds; per-call `maxOperationSeconds` can lower it, not raise it. | Linked cancellation token is scheduled against effective budget and reaches the call. |
| `src/SqlHarness.Mcp/McpHost.cs:106-113`, `src/SqlHarness.Mcp/McpOperationMapper.cs:161-193`, `docs/mcp/startup.md:82`, `README.md:541-543` | Connection establishment has separate `connection.timeoutSeconds` (1..300, default 30); startup-loaded values appear through capabilities. | Existing startup contract identifies SQL and driver-managed authentication timeout and separate Azure CLI token budget. Runtime guide says save, restart process, reconnect, then query capabilities; automatic client reload remains unknown. |
| `plans/110-evidence/task-1-source-reconciliation.md`, `docs/observations/mcp-timeout-layers.md` | An external client timeout without a SQLHarness response must not be assigned a server outcome or a client-specific setting/reload claim. | Prior observation has no SQLHarness envelope for that client error and leaves server completion unknown; source reconciliation marks client timeout/reload behavior unverified. Runtime guide retains `UNKNOWN` and attributes no causal explanation. |

## Checks

Commands and results are recorded in [Plan 110 verification evidence](../110-verification-evidence.md). The earlier local report pointer was ephemeral and is replaced by this durable record. No product code, client configuration, live database, retry, or installation was changed or exercised.
