# Plan 110 Task 1 — timeout source reconciliation

Date: 2026-10-23 · Worktree: `plan-110` · initial review HEAD: `6d543e50` · correction base: `2ea01369` · original base: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`

| Layer | Source / fixture | Expected from source | Observed / limits |
|---|---|---|---|
| SQL connection | `SqlHarnessConfig.cs:55-61`; `SqlHarnessModule.cs:91-99`; `SqlExecution.cs:251-255,297-303`; `McpHost.cs:106-113`; `McpOperationMapper.cs:171,192`; `docs/mcp/startup.md:82` | `connection.timeoutSeconds` defaults to 30, range 1..300. Host loads it at startup; capabilities reports the loaded value. | Applies to the SQL connection and driver-managed authentication timeout. Azure CLI token acquisition has a separate existing budget. This is the connection-stage boundary and remains distinct from a SQL command timeout after connection. |
| SQL command | `SqlHarnessConfig.cs:64-70,187-188`; `McpOperationMapper.cs:750-757`; `SqlExecution.cs:574-579`; fixture `McpMappingTests.cs:576-628` | `execution.defaultTimeoutSeconds` defaults to 30, range 1..300. Optional per-call `timeout` overrides it. | Mapper supplies configured default when omitted; provider receives it as `SqlCommand.CommandTimeout`. Fixtures assert configured 47 is used when omitted, explicit override, invalid bounds, and all timeout tools. Not run here. |
| Server call budget | `McpLimits.cs:62-92`; `McpToolCatalog.cs:555-610`; fixtures `McpCancellationTests.cs:325-380`, `McpCallRegistryTests.cs:94-126` | Process maximum defaults to 900 s, range 1..86400; optional `maxOperationSeconds` can lower it. | Each call gets a linked request/shutdown token and `CancelAfter`; cancellation maps to stable cancelled result. Call values cannot extend process maximum. Fixtures inspected, not run. |
| External client wait | `docs/mcp/runtime.md` cancellation contract; `McpToolCatalog.cs:582-584`; existing observation below | No client timeout setting is established by SQLHarness sources inspected. | If client stops waiting without a server response, server completion is unknown. If an MCP cancellation reaches SQLHarness, the linked request token propagates it. Client-specific setting, error wording, and reload behavior remain unknown. |

## Config lifecycle

`McpHost.RunAsync` loads config at startup (`McpHost.cs:106-113`), passes it to the process context, and catalog creation passes the process's execution default into handlers (`McpToolCatalog.cs:748-751`). Capabilities reports that process value in both scopes (`McpOperationMapper.cs:161-193`). Saving config alone therefore does not change an existing server; starting a new process reads it. The client must connect to that process to ask its capabilities. Automatic restart/reconnect behavior is client-specific and unverified. Existing runtime text says restart, reconnect, then query capabilities; this matches the server lifecycle, but reconnect alone does not reload the same process.

## Prior observation and unresolved evidence

Inspected `docs/observations/mcp-timeout-layers.md` read-only. It records a SQL timeout envelope and a later client-side wait timeout with no SQLHarness envelope; server completion for the latter was unknown. Its 2026-10-23 historical phase follow-up retains no raw export, operation/connection identity, timestamp, or workload timing. It does not establish causality, installed-binary behavior, client settings, or reload behavior. No evidence was treated as zero; no agent-session report was edited.

## Commands and outcomes

- `git worktree list`: active isolated checkout `D:/Dev/sqlharness/.worktrees/plan-110`, branch `plan-110`.
- `git merge-base HEAD 677b2f93b66c06d3dc0bda8475c7570ef29738d9`: returned the requested base.
- `git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- docs/mcp/runtime.md docs/mcp/startup.md README.md`: empty.
- Targeted source/fixture reads confirmed the anchors above. No tests, live DB calls, client experiments, or product edits.

Conclusion: the connection timeout is a separate connection-stage setting; the three troubleshooting cases remain SQL command timeout, server operation-budget cancellation, and external client wait expiry. A server envelope distinguishes SQL command failure from server call cancellation. A client-side timeout without a SQLHarness response leaves completion unknown. No retry, policy change, blanket timeout increase, or client-specific setting claim is supported.
