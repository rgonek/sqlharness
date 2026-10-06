# Plan 036: An opt-in CI lane runs the integration tests against throwaway PostgreSQL and SQL Server containers

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- .github/workflows/ tests/SqlHarness.Tests/Integration/`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2 (direction: unblocks live proof for many plans)
- **Effort**: M
- **Risk**: MED (CI time; SQL Server container weight)
- **Depends on**: plans/016-restore-green-ci.md; plans/028 if actions must be SHA-pinned
- **Category**: direction / tests
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

Every proof row in `plans/README.md` for plans 001–015 records "Brak live DB" (no live DB). Nothing in CI ever runs SQL against a real server: not STATISTICS capture, not target identity, not the claim that statements the classifier allows are harmless, and not the TLS policy. The roadmap's rule allows live tests "only on explicitly authorized single-use databases". Ephemeral CI service containers fit that rule exactly. Pending items that need this lane: plan 013's median-truncation proof, plan 014's per-version column shapes, plans 017/019 (server binding of the bypass shapes), 021 (localized messages), 022 (Npgsql upgrade) and 024 (UDT result values).

## Current state

- Integration tests live in `tests/SqlHarness.Tests/Integration/`:
  - `SqlServerIntegrationFactAttribute.cs`: skips unless `SQLHARNESS_INTEGRATION_CONNECTION_STRING` is set. Trait `Category=SqlServerIntegration`.
  - `PostgresIntegrationFactAttribute.cs`: `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING`. Trait `Category=PostgresIntegration`.
  - `PostgresTlsProofTests.cs`: `SQLHARNESS_PG_TLS_PROOF` (needs certificates; **not** part of this lane).
  - `BenchmarkSessionIntegrationTests.cs` (2 SQL Server tests) builds a profile with auth **`"integrated"`** (`BenchmarkSessionIntegrationTests.cs` ~22-30):

    ```csharp
            ["integration"] = new(
                builder.DataSource,
                builder.InitialCatalog,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "integrated"),
    ```

    Windows integrated auth does not work against a Linux `mssql/server` container with `sa`. The test must choose `sql` auth (user plus password env var) when the connection string carries `User ID`.
  - `PostgresSessionIntegrationTests.cs` already maps the connection string's password into an env var (~`:26-49`). Use it as the pattern.
- The MCP project has opt-in live stdio tests (`McpStdioLiveTests`, `SQLHARNESS_MCP_LIVE_SQLSERVER_JSON` / `..._POSTGRES_JSON`). They are optional for this lane.
- The playground scripts (`scripts/setup-local-postgres.ps1`, `scripts/setup-local-adventureworks.ps1`) are for local use. CI uses service containers instead.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Local PG run (playground running) | `$env:SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING="Host=localhost;Port=5433;Database=pagila;Username=postgres;Password=<from your env>"; dotnet test tests/SqlHarness.Tests --filter "Category=PostgresIntegration"` | tests run (not skipped) and pass |
| Full offline gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

Never paste a real password into a file. Use the env var the playground script uses (`SQLHARNESS_PG_PLAYGROUND_PASSWORD`).

## Scope

**In scope**: `.github/workflows/live.yml` (create), `tests/SqlHarness.Tests/Integration/BenchmarkSessionIntegrationTests.cs` (auth selection only), and a README "Development" note.

**Out of scope**: TLS proof (needs certificates), user profiles or `~/.sqlharness/targets.json` (never read or written), making the lane a required check (operator decision), and new integration tests beyond what already exists (each other plan adds its own).

## Git workflow

Branch `ci/plan-036-live-lane`. Commits per step. Do NOT push.

## Steps

### Step 1: SQL Server integration tests work with SQL auth

In `BenchmarkSessionIntegrationTests.cs`, when `builder.UserID` is non-empty, set a process env var with a **unique** name (`$"SQLHARNESS_IT_{Guid.NewGuid():N}"`) to `builder.Password`, and build the profile as `auth: "sql"` with `SqlUser = builder.UserID`, `PasswordEnvVar = <that name>` and `TrustServerCertificate = builder.TrustServerCertificate`. Restore or clear the env var in `finally`. Otherwise keep `"integrated"`. Follow the shape of `PostgresSessionIntegrationTests.cs:26-49`.

**Verify**: offline gate passes (the tests still skip without the variable).

### Step 2: `live.yml`

Create `.github/workflows/live.yml`:
- `on: workflow_dispatch` plus `schedule: - cron: "0 3 * * 1"` (weekly). Not on push.
- `permissions: contents: read`, `timeout-minutes: 45`, `concurrency` group `live`.
- Job `postgres` on ubuntu-latest with `strategy.matrix.pg: ["16", "17"]` and `services.postgres: image: postgres:${{ matrix.pg }}`, `env: POSTGRES_PASSWORD: ${{ secrets.LIVE_PG_PASSWORD || 'ci-ephemeral-only' }}`, ports `5432:5432`, and health `--health-cmd "pg_isready -U postgres"`. A throwaway password literal for an ephemeral container is acceptable **only** because the container is unreachable from outside the runner. Prefer a repository secret if the operator provides one. Steps: checkout, setup-dotnet (`global-json-file`), then `dotnet test tests/SqlHarness.Tests --filter "Category=PostgresIntegration" --logger trx` with `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING: Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=<same value>`. Upload trx.
- Job `sqlserver` on ubuntu-latest with `services.mssql: image: mcr.microsoft.com/mssql/server:2022-latest`, `ACCEPT_EULA: Y`, `MSSQL_SA_PASSWORD` (same approach; must meet complexity rules), ports `1433:1433`, and a health check using `/opt/mssql-tools18/bin/sqlcmd -C -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT 1"`. Before the tests, create a database with a step that runs the same `sqlcmd` inside the container (`docker exec`) with `CREATE DATABASE sqlharness_it`. Test env: `SQLHARNESS_INTEGRATION_CONNECTION_STRING: Server=localhost,1433;Database=sqlharness_it;User ID=sa;Password=<value>;TrustServerCertificate=true` and `--filter "Category=SqlServerIntegration"`.
- Assert that tests actually **ran**: after `dotnet test`, fail the job if the trx shows 0 executed for the category (a small PowerShell/bash check counting `outcome="Passed"` entries). A lane that skips everything must not look green.

**Verify**: if `actionlint` is available, no errors. The real proof is the first `workflow_dispatch` run triggered by the operator after push. Record "pending" in the index.

### Step 3: Document

README "Development": how to trigger the lane (`gh workflow run live.yml`), what it covers, and that it never touches user profiles.

## Test plan

No new tests. Step 1 makes the existing SQL Server tests runnable on Linux. The job-level "tests actually ran" check guards against silent skips.

## Done criteria

- [ ] `.github/workflows/live.yml` exists with both jobs and the ran-check
- [ ] Offline gate passes
- [ ] `plans/README.md` row updated (first live run: pending operator trigger)

## STOP conditions

- The SQL Server integration tests require Windows-only features beyond auth (e.g. specific sample databases). Report what they need.
- A test writes to or reads `~/.sqlharness/targets.json`. That is forbidden for this lane; report it.

## Maintenance notes

- Each plan that adds live evidence (017, 019, 021, 022, 024, 014) should add its integration test under the same traits so this lane picks it up.
- Add `postgres:18` to the matrix when plan 037 extends the supported versions.
