# Plan 033: SQL Server target identity accepts legitimate endpoints (FQDN, listeners, database name casing) without weakening wrong-target detection

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/SqlExecution.cs src/SqlHarness.Core/Targets/ tests/SqlHarness.Tests/SqlExecutionTests.cs tests/SqlHarness.Tests/Targets/ README.md AGENTS.md docs/example-targets.json`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED (this is a safety check; loosening it must not admit a wrong target)
- **Depends on**: plans/016-restore-green-ci.md
- **Category**: bug
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

After connecting, SQLHarness checks that the server and database it reached are the ones the profile resolved, and fails with exit 4 (`TargetMismatch`) otherwise. On SQL Server the check compares the profile's host text with `SERVERPROPERTY('ServerName')` (the machine NetBIOS name plus optional `\instance`). Legitimate configurations therefore always fail:

- An FQDN (`sql01.corp.local`) or IP profile versus `SQL01`.
- Availability-group listeners, Azure SQL failover-group listeners (`fog.database.windows.net`, which reports the physical primary's name), Azure DNS aliases and `*.privatelink.database.windows.net` hosts.
- A profile database `salesdb` versus `DB_NAME()` returning the stored casing `SalesDB`. The connection succeeds because SQL Server resolves the name, then the ordinal comparison fails.

Users hit exit 4 and are pushed toward `--unsafe-direct` or loopback tunnels. Loopback is a documented-by-code blind spot: only the database name is checked for `localhost`, `127.0.0.1`, `.` and `(local)`, so an SSH tunnel to production passes as long as the database name matches.

## Current state

`src/SqlHarness.Core/SqlExecution.cs:141-200`:

```csharp
    internal const string IdentitySql =
        "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) AS ServerName,\n" +
        "       DB_NAME() AS DatabaseName;";

    internal static bool TargetMatches(ResolvedTarget expected, string server, string database)
    {
        // SQL Server only. Postgres uses PostgresEndpointIdentity ...
        if (!string.Equals(expected.Database, database, StringComparison.Ordinal))
            return false;

        if (IsLoopbackEndpoint(expected.Server))
            return true;

        return string.Equals(
            NormalizeServer(expected.Server, expected.Engine),
            NormalizeServer(server, expected.Engine),
            StringComparison.OrdinalIgnoreCase);
    }
```

`NormalizeServer` strips `tcp:`, the port and the `.database.windows.net` suffix. `HostFromDataSource` keeps `host\instance`.

- The profile record is `src/SqlHarness.Core/Targets/TargetProfile.cs` (`Server`, `Database`, `Vars`, `Auth`, `SqlUser`, `PasswordEnvVar`, `TrustServerCertificate`, `Engine`, `SslMode`, `RootCertificate`). `ProfileStore.cs` (~`:62`) maps JSON to it. `TargetResolver.cs` produces `ResolvedTarget` and **rejects unknown/inapplicable fields** (e.g. Postgres fields on a SQL Server profile). Follow how `SslMode`/`RootCertificate` were added: grep those names across `src` and `tests` to find every touch point.
- Tests: `tests/SqlHarness.Tests/SqlExecutionTests.cs:97-185` (loopback, Azure, docker cases; `IsLoopbackEndpoint_classifies_data_sources`). `docs/example-targets.json` shows profile shape.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Identity/target tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlExecution|FullyQualifiedName~Target|FullyQualifiedName~Profile"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `SqlExecution.cs` (`TargetMatches`, `NormalizeServer`), `Targets/TargetProfile.cs`, `ProfileStore.cs`, `TargetResolver.cs` (one new optional SQL Server field), the corresponding tests, README/AGENTS profile docs and `docs/example-targets.json`.

**Out of scope**: PostgreSQL identity (separate policy, already designed); `--unsafe-direct` flags for the new field (profiles only, see Maintenance notes); MCP owner tuples (they use the resolved profile server text, which does not change).

## Git workflow

Branch `fix/plan-033-sqlserver-identity`. Commits per step. Do NOT push.

## Steps

### Step 1: Database name comparison ignores case

Change the database comparison to `StringComparison.OrdinalIgnoreCase`, with a comment: "// 033: the connection opened Initial Catalog by server-side name resolution, so DB_NAME() is that database; only its stored casing can differ." Tests: `salesdb` vs `SalesDB` matches; `salesdb` vs `otherdb` does not.

Check the mutation confirmation path is unaffected: `SqlSafety.cs` compares `--confirm-database` against `target.Database` (the profile value) with `Ordinal`. That stays as is, since the user confirms the name they configured.

**Verify**: identity tests pass.

### Step 2: FQDN matches its first label

In `NormalizeServer` (SQL Server only), split off the instance part (`\INST`). If the expected host is not an IP address and contains dots, compare **only its first DNS label** (case-insensitive) with the server's machine part, and compare instance parts exactly (case-insensitive; both absent, or both equal). Keep the existing `.database.windows.net` handling for Azure SQL Database (`server.database.windows.net` vs ServerName `server`).

Tests:
- `sql01.corp.local` vs `SQL01` → match.
- `sql01.corp.local\INST` vs `SQL01\INST` → match.
- `sql01.corp.local` vs `SQL02` → no match.
- `sql01.corp.local\A` vs `SQL01\B` → no match.
- `10.0.0.5` vs `SQL01` → no match. An IP proves nothing textual; Step 3 is the way to allow it.

**Verify**: identity tests pass.

### Step 3: Explicit `expectedServerName` for listeners and aliases

Add an optional SQL Server profile field `expectedServerName` (a single string). When it is present, the server check compares `SERVERPROPERTY('ServerName')` **only** against it, case-insensitively and exactly, instead of the derived host. It is allowed even for loopback endpoints, which closes the tunnel blind spot for users who opt in. Wire it through `TargetProfile` → `ProfileStore` → `TargetResolver` → `ResolvedTarget`, following the `RootCertificate` precedent. Reject it on `engine: postgres` profiles, like the other engine-specific fields.

Tests:
- Profile parsing accepts the field.
- A postgres profile with the field is rejected.
- With `expectedServerName: "SQLPRIMARY01"`, target `fog.database.windows.net` + ServerName `SQLPRIMARY01` → match, and ServerName `SQLOTHER` → mismatch.
- A loopback target with `expectedServerName: "PRODSQL"` and ServerName `DEVBOX` → **mismatch**.

**Verify**: identity/target/profile tests pass.

### Step 4: Docs

- README/AGENTS profile section and `docs/example-targets.json`: document `expectedServerName` with a listener example. Also add: "Loopback endpoints (`localhost`, `.`, tunnels) verify only the database unless `expectedServerName` is set."
- Exit-code 4 description: mention FQDN first-label matching.

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

About 12 new cases across `SqlExecutionTests.cs` and the target/profile tests, following `IsLoopbackEndpoint_classifies_data_sources` (theory style).

## Done criteria

- [ ] Identity/target tests and the full gate pass
- [ ] `grep -rn "expectedServerName" src/SqlHarness.Core/Targets docs/example-targets.json README.md AGENTS.md` → matches in each
- [ ] No existing identity test expectation was changed (only additions)
- [ ] `plans/README.md` row updated

## STOP conditions

- An existing test asserts that an FQDN profile **must** mismatch (an intentional strictness decision). Report it.
- `ResolvedTarget` is part of a persisted artifact owner tuple (`ArtifactOwner`) and adding a field changes owner matching (`grep -rn "ResolvedTarget" src/SqlHarness.Core/Artifact*`). Report it before changing the record shape.
- The profile loader's unknown-field rejection is order- or reflection-based in a way that makes adding a field risky.

## Maintenance notes

- A live check against a real AG listener or failover group is the only true proof. Add it to the live CI lane (plan 036) only if such a target exists.
- If `--unsafe-direct` users need the same option, add `--expected-server-name` later with the same semantics. It is deliberately not in this plan.
