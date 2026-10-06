# Plan 022: SQLHarness builds and ships on .NET 10 (LTS) with a supported Npgsql before .NET 8/9 support ends on 2026-11-10

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- global.json Directory.Build.props Directory.Packages.props src/*/*.csproj tests/*/*.csproj src/SqlHarness.Core/Postgres/ .github/workflows/`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1 (time-boxed: support ends 2026-11-10)
- **Effort**: M
- **Risk**: MED
- **Depends on**: plans/016-restore-green-ci.md (a green gate is required to prove the migration)
- **Category**: migration
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

Every project targets `net8.0`, and `global.json` pins SDK `9.0.316`. Both .NET 8 (LTS) and .NET 9 (STS, extended to 24 months) reach end of support on **2026-11-10**, about five weeks after this plan was written. Release binaries are published `--self-contained` (`release.yml`), so each shipped `sqlharness` **embeds the .NET runtime**, including the TLS and networking stack that guards connections to production SQL Server and PostgreSQL. After end of support those binaries stop getting security fixes. .NET 10 is the current LTS (supported to November 2028).

Npgsql is pinned at `8.0.8`. Newer majors exist (`9.0.3`, `10.0.x` are in the local NuGet cache). The PostgreSQL TLS and endpoint-identity policy depends on Npgsql internals, so staying on an old major is a security-maintenance liability.

The two changes are separate phases with separate commits, so that a problem in one can be reverted on its own.

## Current state

- `global.json`:

  ```json
  { "sdk": { "version": "9.0.316", "rollForward": "latestPatch" } }
  ```
- Five projects each set `<TargetFramework>net8.0</TargetFramework>`: `src/SqlHarness.Cli/SqlHarness.Cli.csproj:9`, `src/SqlHarness.Core/SqlHarness.Core.csproj:4`, `src/SqlHarness.Mcp/SqlHarness.Mcp.csproj:4`, `tests/SqlHarness.Tests/SqlHarness.Tests.csproj:4`, `tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj:4`. The CLI and test projects also set `<RollForward>Major</RollForward>`.
- `Directory.Build.props`: `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors=true`, `LangVersion=latest`. Every new analyzer warning in SDK 10 is a build **error**.
- `Directory.Packages.props` (central package management) includes `Npgsql 8.0.8`, `Microsoft.Data.SqlClient 7.0.2`, `ModelContextProtocol 2.2.0`, `Microsoft.SqlServer.TransactSql.ScriptDom 180.37.3`, `SqlParserCS 0.6.5`, xunit `2.9.3`.
- Installed SDKs on the dev machine (from `dotnet --list-sdks`): 9.0.204, 9.0.205, 9.0.316, 9.0.318, **10.0.112, 10.0.204, 10.0.303**.
- `.github/workflows/ci.yml` and `release.yml` use `actions/setup-dotnet@v4` with `global-json-file: global.json`, so they follow `global.json` automatically.
- `tests/SqlHarness.Tests/ReleaseWorkflowTests.cs` pins parts of `release.yml` text. Read it before touching workflows (this plan should not need to).
- Npgsql usage: `src/SqlHarness.Core/Postgres/NpgsqlSessionFactory.cs`, `PostgresConnectionString.cs`, `PostgresEndpointIdentity.cs`, `PostgresParameters.cs`, `PostgresParameterReferenceValidator.cs`, plus `OperationFailureMapper.cs` (`Npgsql.NpgsqlException`) and `SqlHarnessModule.cs`.
- **Reflection on Npgsql internals**, `src/SqlHarness.Core/Postgres/PostgresEndpointIdentity.cs:164-190`:

  ```csharp
              var connector = typeof(NpgsqlConnection)
                  .GetProperty("Connector", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.GetValue(connection);
              ...
              var endpoint = connector.GetType()
                  .GetProperty("ConnectedEndPoint", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?.GetValue(connector);
              ...
              if (connector.GetType().GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?.GetValue(connector) is SslStream ssl &&
                  ssl.RemoteCertificate is { } remote)
              {
                  using var certificate = new X509Certificate2(remote);
  ```

  It fails **closed** (returns an empty observation, which makes identity checks fail) when a member is missing. A test (`SqlExecutionTests.cs` around lines 280-446) guards these member names. After a Npgsql bump this guard tells you whether the internals still exist. Also, `new X509Certificate2(X509Certificate)` is marked obsolete in newer .NET (SYSLIB0057), which becomes an error under `TreatWarningsAsErrors`.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| SDK in use | `dotnet --version` (repo root) | `10.0.x` after Step 1 |
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Tests | `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration"` | 0 failed (counts about 2811 / 212 + 4 skipped) |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |
| Vulnerable packages | `dotnet list SqlHarness.sln package --vulnerable --include-transitive` | "has no vulnerable packages" for all 5 projects |
| Publish smoke | `dotnet publish src/SqlHarness.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o <scratch dir>` then `<scratch dir>/sqlharness.exe capabilities --json` | exit 0, JSON output |

## Scope

**In scope**:
- `global.json`, `Directory.Build.props`, the five `*.csproj` (TFM lines only), `Directory.Packages.props` (the `Npgsql` line only)
- Code changes **required** to compile cleanly under SDK 10 / net10.0 (obsolete-API fixes, new analyzer findings), anywhere in `src/` and `tests/`, but only minimal mechanical fixes
- `src/SqlHarness.Core/Postgres/*` for Npgsql API changes
- `README.md` (requirements line)

**Out of scope**:
- Other package bumps (SqlClient, ScriptDom, Spectre, xunit v3, MCP SDK). Each is its own decision. ScriptDom parser selection is plan 018.
- Workflow changes (plans 028/032). `setup-dotnet` follows `global.json` with no edit.
- Behavior changes. If a new analyzer suggests a refactor, suppress it locally with a justification comment only when the fix would change behavior, and list every suppression in the commit message.

## Git workflow

- Branch `chore/plan-022-net10`. Commits: `build(022): target net10.0 with SDK 10`, `fix(022): <analyzer/obsolete fixes>`, `build(022): Npgsql 10`, `docs(022): requirements`. Do NOT push.

## Steps

### Step 1: Move the target framework into one place

Remove `<TargetFramework>` from all five csproj files and add `<TargetFramework>net10.0</TargetFramework>` to `Directory.Build.props`. Keep `<RollForward>Major</RollForward>` where it is.

Set `global.json` to the newest installed 10.0 feature band, keeping `"rollForward": "latestPatch"` (e.g. `"10.0.303"`). Pick the highest `10.0.*` from `dotnet --list-sdks`.

**Verify**: `dotnet --version` → `10.0.303` (or the chosen version). `dotnet restore SqlHarness.sln` → exit 0.

### Step 2: Build clean under warnings-as-errors

Run `dotnet build SqlHarness.sln -warnaserror`. For each error:
- Obsolete APIs (e.g. `SYSLIB0057` at `PostgresEndpointIdentity.cs` `new X509Certificate2(remote)`): replace with the recommended API. For the certificate, `X509CertificateLoader.LoadCertificate(remote.GetRawCertData())` or the cast `remote as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(remote.GetRawCertData())`. Keep `using` disposal semantics.
- New analyzer/IDE diagnostics: apply the minimal fix that preserves behavior.

**Verify**: `dotnet build SqlHarness.sln -warnaserror` → 0 warnings, 0 errors.

### Step 3: Full offline test suite on net10

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`. Pay attention to culture/date tests (`CanonicalResultsTests` pins `pl-PL` formatting) and the MCP stdio publish smoke (`McpStdioProcessTests.Published_single_file_server_passes_the_stdio_smoke_on_this_rid`), which runs a real `dotnet publish` for the current RID. Any failure here is a runtime behavior change: STOP and report the test and the diff. Do not adjust expectations.

### Step 4: Publish smoke

Run the publish command from the table for the local RID into a scratch directory outside the repo, run `capabilities --json` and `--version`, then delete the scratch directory.

**Verify**: both commands exit 0.

### Step 5: Npgsql to the current major

In `Directory.Packages.props` set `Npgsql` to the newest `10.0.x` available (`dotnet list SqlHarness.sln package --outdated` shows it). Build and test.

Specific checks:
1. The endpoint-identity reflection guard test (in `tests/SqlHarness.Tests/SqlExecutionTests.cs`, search `Connector` / `ConnectedEndPoint` / `_stream`) must pass. If any member was renamed, update the names in `NpgsqlEndpointObservation.Read` **and** the guard test together, and record the old and new names in the commit message. If the members no longer exist in any form, STOP: TLS identity would silently fail closed for every `verify-*` profile.
2. Type-mapping changes. Newer Npgsql majors may return `DateOnly`/`TimeOnly` for PG `date`/`time` where 8.x returned `DateTime`/`TimeSpan`. That would change canonical result hashes and make stored snapshots (`~/.sqlharness/snapshots`) diff as different after the upgrade. Check the Npgsql 9 and 10 release notes ("breaking changes") if available offline in the package's README, or search the package XML docs. If the default mapping changed, **do not** silently accept it. STOP and report, so the operator can choose between pinning the legacy mapping (an `AppContext` switch, if one exists) and documenting a one-time snapshot re-capture.
3. `PostgresParameters` timestamp mapping (`NpgsqlDbType.Timestamp` / `TimestampTz`): the parameter tests in `tests/SqlHarness.Tests/Postgres/` must pass unchanged.

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`. `dotnet list SqlHarness.sln package --vulnerable --include-transitive` → no vulnerable packages.

### Step 6: Live PostgreSQL check (only with explicit operator approval)

If the operator has the opt-in playground (`scripts/setup-local-postgres.ps1`, port 5433, database `pagila`) **and approves**, set `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING` per README and run `dotnet test tests/SqlHarness.Tests --filter "Category=PostgresIntegration"`. Never point it at a user profile. Without approval, record "live PG not run" in the index row.

### Step 7: Document

`README.md`: state the required SDK (from `global.json`) and that binaries are self-contained on .NET 10. Add one line to the maintenance notes: "re-release after .NET runtime security patches".

**Verify**: full gate passes; `git diff --name-only main` matches the scope.

## Test plan

- No new behavior, so no new tests. The existing suite (about 3,000 tests) is the proof.
- The Npgsql reflection guard and PG parameter tests are the critical ones for Step 5.

## Done criteria

- [ ] `grep -rn "net8.0" src tests Directory.Build.props` → no match
- [ ] `grep -n "net10.0" Directory.Build.props` → match
- [ ] `dotnet --version` at repo root → `10.0.*`
- [ ] `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`
- [ ] `grep -n 'Npgsql" Version="10' Directory.Packages.props` → match (or a STOP recorded in the index)
- [ ] No vulnerable packages reported
- [ ] `plans/README.md` row updated (state whether live PG ran)

## STOP conditions

- A test fails on net10 that passed on net8 (a runtime behavior change). Report it; do not edit expectations.
- Npgsql internals used by `NpgsqlEndpointObservation` are gone.
- Npgsql changed default result type mappings for `date`/`time`/`timestamp`.
- An analyzer fix would change behavior and cannot be suppressed with a clear justification.

## Maintenance notes

- After this lands, cut a release (plan 028 adds version stamping and CI gating first if possible). Users only get the runtime fix from a new binary.
- The next LTS-to-LTS move is about November 2027 for .NET 12. Add a calendar note to `plans/README.md`.
- Reviewers: scrutinize any change in `src/SqlHarness.Core/Postgres/PostgresEndpointIdentity.cs`. It is the TLS identity boundary.
