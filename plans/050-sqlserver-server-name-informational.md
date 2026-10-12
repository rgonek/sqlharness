# Plan 050: Connected endpoint identity is reported, not used to block (SQL Server and PostgreSQL)

> Read current source before editing. Inspect drift since `bab7618` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition. Supersedes plan 033. Extended on 2026-10-11 to PostgreSQL after the rule review.

## Status

- Status: TODO
- Priority: P2
- Effort: M
- Risk: MED (removes exit 4 from the contract)
- Depends on: —
- Planned at: `cadde67`; extended at `bab7618`, 2026-10-11

## Why this matters

Both engines compare the endpoint they just connected to with what the server reports. That cannot catch a wrong profile or a wrong `--var`: the connection already went to the wrong host. Server identity is authenticated by TLS certificate validation, which the driver performs before any of this runs.

**SQL Server** (`SqlExecution.TargetMatches`): compares host text with `SERVERPROPERTY('ServerName')`. It fails for FQDNs, IPs, AG listeners, Azure failover groups, DNS aliases and privatelink hosts, pushing users toward `--unsafe-direct`.

**PostgreSQL** (`PostgresEndpointIdentity.Matches`):
- with `require`/`disable` (including legacy `trustServerCertificate: false` → `Require`), a DNS host name always returns false, so every remote legacy profile using a host name gets exit 4; an IP literal is compared with the address just connected to (a tautology);
- with `verify-full`/`verify-ca`, Npgsql has already authenticated the certificate; SQLHarness re-checks it through reflection into Npgsql private members (`Connector`, `ConnectedEndPoint`, `_stream`) and re-resolves DNS. An Npgsql upgrade that renames those members makes `ConnectedAddress` null and fails every remote connection; round-robin DNS, private endpoints and proxies fail too.

The database comparison (`DB_NAME()` / `current_database()` against the requested name) is near-tautological — the connection string already selects it — but it is cheap and catches a server that maps names unexpectedly. Keep it.

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

- [ ] No path returns exit 4 for a server-name or address difference alone, on either engine.
- [ ] No reflection into Npgsql private members remains (`rg "BindingFlags.NonPublic" src/SqlHarness.Core/Postgres` → none for identity).
- [ ] Identity reports still show the connected server.
- [ ] Plan 033 REJECTED in the index; both gates pass.

## STOP conditions

- An MCP artifact owner tuple or snapshot namespace uses the connected server identity rather than the resolved profile server. Report before changing matching.
