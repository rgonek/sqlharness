# Plan 050: Connected endpoint identity is reported, not used to block (SQL Server and PostgreSQL)

> Read current source before editing. Inspect drift since `bab7618` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition. Supersedes plan 033. Extended on 2026-10-11 to PostgreSQL after the rule review.

## Status

- Status: Implementation complete; focused verification passed; Windows/Linux full gates pending controller.
- Priority: P2
- Effort: M
- Risk: MED (removes exit 4 from the contract)
- Depends on: —
- Planned at: `cadde67`; extended at `bab7618`, 2026-10-11

## Why this matters

The previous implementation compared the endpoint text with an identity observed after connecting. That could not catch a wrong profile or a wrong `--var`: the connection already went to the resolved host. Endpoint authentication belongs to transport validation by the driver; the connected server identity is useful report context.

**SQL Server** (`SqlExecution.TargetMatches`): now compares only the requested and connected database names. The report still includes `SERVERPROPERTY('ServerName')` for FQDNs, IPs, AG listeners, Azure failover groups, DNS aliases and privatelink hosts.

**PostgreSQL** (`PostgresEndpointIdentity.Matches`): now compares only the requested and connected database names. The report still includes `inet_server_addr()` and `transportPolicy`; Npgsql performs TLS validation according to the configured mode, including hostname verification for `verify-full`.

The database comparison (`DB_NAME()` / `current_database()` against the requested name) remains an exact ordinal guard, and a mismatch is the only connected-target condition that returns exit 4.

## Scope

In scope: `src/SqlHarness.Core/SqlExecution.cs`, `src/SqlHarness.Core/Postgres/PostgresEndpointIdentity.cs`, `NpgsqlSessionFactory.cs` (identity observation), their tests, README/AGENTS/docs exit-code text, `docs/superpowers/specs/2026-09-26-postgres-transport-policy.md` (identity section), `plans/033-sqlserver-target-identity.md` status.

Out of scope: profile schema, `--confirm-database` (stays exact and ordinal), the PostgreSQL legacy SSL mapping (separate operator decision recorded in plan 031's history).

## Steps

1. SQL Server: `TargetMatches` keeps only the ordinal database check. Keep reading `SERVERPROPERTY('ServerName')` into the identity report so outputs show where the session landed.
2. PostgreSQL: keep the `current_database()` check. Remove the host/address/certificate-name comparison and the reflection into Npgsql internals. Report `inet_server_addr()` (and the transport mode) in the identity report.
3. Delete helpers that become unused (`IsLoopbackEndpoint`, `NormalizeServer`, PG resolver plumbing) after confirming no other callers with `rg`.
4. Exit 4 now means only "the connected database differs from the resolved one". Update README, AGENTS exit-code line, docs/mcp.md and the transport-policy spec: server identity comes from TLS validation (`verify-full` for PostgreSQL, `trustServerCertificate: false` for SQL Server).
5. Tests: FQDN/listener/Azure alias with a different `ServerName` matches; PostgreSQL `require` with a host name connects (fake observation); a different database still yields exit 4.

## Verification

- `dotnet build SqlHarness.sln -warnaserror` — 0 warnings.
- `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlExecution|FullyQualifiedName~Target|FullyQualifiedName~PostgresEndpoint|FullyQualifiedName~PostgresTransport"` — pass.
- Final `scripts/verify.ps1` → `verify: OK`; `scripts/verify-linux.ps1` → `verify-linux: OK`.

## Done criteria

- [x] No path returns exit 4 for a server-name or address difference alone, on either engine.
- [x] No reflection into Npgsql private members remains (`rg "BindingFlags.NonPublic" src/SqlHarness.Core/Postgres` → none for identity).
- [x] Identity reports still show the connected server.
- [x] Plan 033 is REJECTED in the index. Windows/Linux full gates remain pending controller.

## STOP conditions

- An MCP artifact owner tuple or snapshot namespace uses the connected server identity rather than the resolved profile server. Report before changing matching.
