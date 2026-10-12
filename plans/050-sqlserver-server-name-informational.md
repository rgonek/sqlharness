# Plan 050: SQL Server reports the connected server name instead of blocking on it

> Read current source before editing. Inspect drift since `cadde67` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition. Supersedes plan 033.

## Status

- Status: TODO
- Priority: P2
- Effort: S
- Risk: LOW
- Depends on: —
- Planned at: `cadde67`, 2026-10-11

## Why this matters

After connecting, `SqlExecution.TargetMatches` compares the host SQLHarness just connected to with `SERVERPROPERTY('ServerName')`. That comparison cannot catch a wrong profile or a wrong `--var`: the connection already went to the wrong host, and the check compares against that same host. It only fires when DNS names and NetBIOS names differ, which is the normal case for FQDNs, IPs, availability-group listeners, Azure failover groups, DNS aliases and privatelink hosts. Those users hit exit 4 and are pushed toward `--unsafe-direct`.

Server identity is authenticated by TLS certificate validation (`TrustServerCertificate=false`), not by name text. The PostgreSQL policy already states this ("DNS by itself is not authentication"). Plan 033 would add a profile field to work around a check that protects nothing; this plan removes the blocking comparison instead.

## Current state

- `src/SqlHarness.Core/SqlExecution.cs`: `TargetMatches` checks the database ordinally, returns true for loopback, otherwise compares normalized host text. `SqlConnector.ConnectAsync` throws `SqlTargetMismatchException` (exit 4) on mismatch and records `SqlHarnessTargetIdentityReport(target.Server, target.Database, identity.Server, identity.Database, mode)`.
- `IsLoopbackEndpoint`, `NormalizeServer`, `HostFromDataSource` exist only for this comparison (check other callers with `rg`).
- Tests: `tests/SqlHarness.Tests/SqlExecutionTests.cs` (`TargetMatches`, loopback and Azure cases).
- PostgreSQL uses `PostgresEndpointIdentity` and is out of scope.

## Scope

In scope: `SqlExecution.cs`, `SqlExecutionTests.cs`, README/AGENTS exit-code 4 and target-identity text, `plans/033-sqlserver-target-identity.md` status line.

Out of scope: PostgreSQL identity, profile schema, mutation `--confirm-database` (stays exact and ordinal).

## Steps

1. `TargetMatches` keeps only the ordinal database comparison. `DB_NAME()` stays authoritative: a case-sensitive server can hold `SalesDB` and `salesdb`.
2. Keep reading `SERVERPROPERTY('ServerName')` and keep it in the identity report, so outputs show where the session actually landed. Confirm CLI text and `--json` both show the connected server name.
3. Delete helpers that become unused. Update tests: an FQDN, listener or Azure alias with a different `ServerName` now matches; a different database still yields exit 4.
4. Docs: exit 4 means "the connected database differs from the resolved one". One sentence where profiles are described: "Server identity comes from TLS certificate validation; keep `trustServerCertificate: false` for remote servers."

## Verification

- `dotnet build SqlHarness.sln -warnaserror` — 0 warnings.
- `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlExecution|FullyQualifiedName~Target"` — pass.
- Final `scripts/verify.ps1` → `verify: OK`; `scripts/verify-linux.ps1` → `verify-linux: OK`.

## Done criteria

- [ ] No SQL Server path returns exit 4 for a server-name difference alone.
- [ ] Identity report still carries the connected server name.
- [ ] Plan 033 marked REJECTED in `plans/README.md`, pointing here.
- [ ] Both gates pass.

## STOP conditions

- An MCP artifact owner tuple or snapshot namespace uses the connected `ServerName` rather than the resolved profile server. Report it before changing matching.
