# MCP protocol upgrade final review fix report

Date: 2026-10-10
Worktree: `D:\Dev\sqlharness\.worktrees\mcp-protocol-upgrade`

## Changes

- `scripts/mcp-client-acceptance.ps1` now makes each client call `sqlharness_capabilities` exactly once and the target-free, journaled `sqlharness_gain` exactly once. It checks the `gain` operation joined to its session and compares persisted `client_name` and `client_version` with the `clientInfo` observed in that client's request frames.
- Each client gets a distinct temporary `SQLHARNESS_HOME` and synthetic closed profile. The tap explicitly passes that home to the SQLHarness server process, so client environment filtering cannot redirect journal writes. Captured frame/journal paths and contents are not printed.
- The MCP request-scope smoke asserts `capabilities.protocolVersion` equals each tested revision.
- The design spec and implementation plan now describe the actual acceptance checks and isolated homes.

`agent_kind`/`source` remains parked. If a first request arrives without `clientInfo`, the session can retain process-tree fallback classification after later requests provide identity. The design's required behavior is first non-null `client_name`/`client_version`, and both are now asserted against real-client calls. Changing fallback classification is independent of protocol negotiation and is not required to establish that behavior.

## Verification

Focused request-scope test:

```text
dotnet test ./tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter FullyQualifiedName~Request_scope_works_on_every_revision
Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3
```

Focused revision, client-identity and journal tests:

```text
dotnet test ./tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~McpRevisionSmokeTests|FullyQualifiedName~McpClientIdentityTests|FullyQualifiedName~McpJournalTests"
Passed! - Failed: 0, Passed: 20, Skipped: 0, Total: 20
```

Worktree-local publish:

```text
dotnet publish ./src/SqlHarness.Cli -c Release -p:PublishTrimmed=false -o ./artifacts/final-fix-local
Exit code: 0; SqlHarness.Cli published to artifacts/final-fix-local.
```

Real-client acceptance:

```text
pwsh ./scripts/mcp-client-acceptance.ps1 -Sqlharness ./artifacts/final-fix-local/sqlharness.exe
Exit code: 0.
Claude Code: exit 0; one capabilities call; negotiated and reported 2026-07-28; journal operation gain; persisted clientInfo claude-code / 2.1.296 matched.
Codex: exit 0; one capabilities call; negotiated and reported 2025-06-18; journal operation gain; persisted clientInfo codex-mcp-client / 0.162.1 matched.
All reported checks passed. The acceptance script suppressed artifact paths and frame contents.
```

Additional checks:

```text
PowerShell AST parse of scripts/mcp-client-acceptance.ps1: OK
Python AST parse of scripts/mcp-client-acceptance-tap.py: OK
git diff --check: OK
```

Two intermediate acceptance attempts failed and were corrected: PowerShell's reserved `$HOME` variable required renaming to `$clientHome`; Codex did not preserve the parent's journal-home environment for the spawned MCP server, so the tap now sets `SQLHARNESS_HOME` directly on that server process. The final real-client run above passed for both clients.

## Limits

The requested full Windows and Linux repository gates were not run. No database connection was made; `gain` is target-free and each run used a synthetic profile. Captured frames and the temporary journals remain in local temporary directories and were not included in output or this report.

## Self-review

Reviewed the full authored diff. The acceptance query is read-only and requires exactly one `gain` row per isolated journal; it cannot pass based only on received protocol frames. Capabilities remains exactly one call per client and is not treated as journal evidence. No application behavior or database operation was changed.
