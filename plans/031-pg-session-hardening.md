# Plan 031: PostgreSQL sessions refuse to run classified text under non-standard string lexing, and never offer the password to an unauthenticated server in a recoverable form

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/Postgres/NpgsqlSessionFactory.cs src/SqlHarness.Core/Postgres/PostgresConnectionString.cs src/SqlHarness.Core/Postgres/PostgresTransportPolicy.cs tests/SqlHarness.Tests/Postgres/ docs/superpowers/specs/2026-09-26-postgres-transport-policy.md AGENTS.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2 (security hardening)
- **Effort**: S–M
- **Risk**: MED (Step 2 can stop md5-only servers working on unauthenticated transports)
- **Depends on**: plans/019-pg-classifier-holes.md (denies `set_config`-via-string, which would otherwise flip the setting mid-session); plans/022 if Npgsql is upgraded (check the API on the version in use)
- **Category**: security
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

1. **Parser and server must agree on where strings end.** The classifier parses PostgreSQL text with SqlParserCS's `PostgreSqlDialect`, which assumes `standard_conforming_strings = on` (backslash is not an escape in `'...'`). The classified text is then sent unchanged. If a role or database is configured with `standard_conforming_strings = off`, the server treats `\'` inside a plain string as an escaped quote, so text the classifier saw as one harmless statement can be a different statement sequence on the server. The setting is reported by the server on every connection (ParameterStatus), so checking it costs no round trip.
2. **Credential exposure before identity is proven.** The endpoint identity check runs **after** `OpenAsync` has authenticated. For `require`/`disable`/legacy transports (no certificate verification), an active man-in-the-middle or a wrong server can ask for **cleartext or MD5** password authentication and receive a password it can use or crack before SQLHarness detects the mismatch. SCRAM-SHA-256 never reveals a reusable password to the peer. With `verify-full`/`verify-ca` the server is authenticated first, so the risk does not apply there.

The documented transport policy (`docs/superpowers/specs/2026-09-26-postgres-transport-policy.md`) already states that `require` does not authenticate the server. This plan closes the credential consequence and records it there.

## Current state

- `src/SqlHarness.Core/Postgres/PostgresConnectionString.cs:11-45` builds the `NpgsqlConnectionStringBuilder` (`Host`, `Port`, `Database`, `Username`, `Password`, `Timeout`, `SslMode`, optional `RootCertificate`). The mode comes from `PostgresTransportPolicy.EffectiveMode(target)`, mapped by `ToNpgsqlSslMode` to `VerifyFull`/`VerifyCA`/`Require`/`Disable`.
- `src/SqlHarness.Core/Postgres/NpgsqlSessionFactory.cs`:
  - `ConnectAsync` (`~:23-45`): build → `ConnectNpgsqlAsync` → identity query → `PostgresEndpointIdentity.Matches` → mismatch throws `SqlTargetMismatchException`.
  - `ConnectNpgsqlAsync` (`~:62-80`) opens the `NpgsqlConnection`.
  - `PostgresSessionScope.BindClassifiedCommand` (`~:85-93`) is the single place classified text is bound:

    ```csharp
        // Query and CompareSetup both run already-classified text.
        // No transaction wrapper, role change, privilege change, or search_path edit.
        internal static void BindClassifiedCommand(NpgsqlCommand command, SqlExecutionCommand execution)
        {
            command.CommandText = execution.Sql;
            ...
  ```
- `src/SqlHarness.Core/Postgres/PostgresDocument.cs:14-26`: `new SqlQueryParser().Parse(sql.AsSpan(), new PostgreSqlDialect())`.
- Tests: `tests/SqlHarness.Tests/Postgres/PostgresConnectionStringTests.cs` (builder assertions) and the transport policy tests (find them with `grep -rln "PostgresTransportPolicy" tests`).
- Exit codes: `SqlHarnessSafetyException` → 2; `NpgsqlException` in the authentication phase → 3 (`OperationFailureMapper.cs`).

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| PG tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Postgres"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `PostgresConnectionString.cs`, `NpgsqlSessionFactory.cs` (`BindClassifiedCommand` / `NpgsqlSession`), Postgres tests, the transport policy spec (one section), `AGENTS.md` (one sentence in the PostgreSQL engine notes).

**Out of scope**: new profile fields (no opt-out knob in this plan; see STOP), the SQL Server path, and the `--unsafe-direct` `--password-env-var` naming policy (considered and not pursued).

## Git workflow

Branch `fix/plan-031-pg-session`. Commits per step. Do NOT push.

## Steps

### Step 1: Fail closed unless `standard_conforming_strings` is `on`

1. Write an offline parser test proving the assumption: `PostgresDocument.TryParse("SELECT 'a\\'; SELECT 1 --'", out var s)` (C# literal; the SQL text is `SELECT 'a\'; SELECT 1 --'`). Under standard strings, this is **two** statements (the first string is `a\`). Assert `s.Count == 2`. If the parser yields 1, the parser assumes the opposite. STOP and report, because the check below would then need to be inverted.
2. In `NpgsqlSession` (or in `BindClassifiedCommand`, which receives an `NpgsqlCommand`; use `command.Connection`), before executing any classified command, read `connection.PostgresParameters.TryGetValue("standard_conforming_strings", out var v)`. If it is missing or not `on` (case-insensitive), throw `SqlHarnessSafetyException("PostgreSQL standard_conforming_strings must be on; SQLHarness classifies SQL under standard string rules.")`.
3. Make the check testable without a server: extract `internal static void EnsureStandardStrings(IReadOnlyDictionary<string, string> parameters)` and unit-test it with `on` (passes), `off` (throws), and missing (throws).

**Verify**: PG tests pass.

### Step 2: SCRAM-only authentication on unauthenticated transports

1. Check whether the referenced Npgsql exposes a "require auth" setting. `grep -rn "RequireAuth" ~/.nuget/packages/npgsql/<version>/lib/net8.0/Npgsql.xml` (Windows: `%USERPROFILE%\.nuget\packages\npgsql\...`), or try `builder.RequireAuth = "ScramSHA256";` in a scratch build. If no such setting exists in the version in use, **STOP** and report. Do not implement a hand-rolled auth check.
2. In `PostgresConnectionString.Build`, when the effective mode is `Require` or `Disable` (which includes the legacy mappings), set the builder to allow only SCRAM-SHA-256 (with SCRAM-SHA-256-PLUS when TLS is used, if the setting supports a list). For `VerifyFull`/`VerifyCa`, leave the default.
3. Tests in `PostgresConnectionStringTests.cs`: the connection string for `require` and legacy-`trustServerCertificate:true` targets contains the SCRAM-only setting; for `verify-full` it does not. Mirror the file's existing env-var setup, using a **unique** env var name per test (`$"SQLHARNESS_TEST_{Guid.NewGuid():N}"`) to avoid the known cross-test env race.

**Verify**: PG tests pass.

### Step 3: Policy and contract text

- Transport policy spec: add a section (in the file's language, Polish) covering both rules: SCRAM only for `require`/`disable`/legacy, and fail-closed on `standard_conforming_strings`, with the reasons above.
- `AGENTS.md`, PostgreSQL engine notes: one sentence: "On `require`/`disable`/legacy transports only SCRAM-SHA-256 authentication is accepted; sessions with `standard_conforming_strings` off are refused (exit 2)."

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

### Step 4 (only with operator approval): playground check

On the opt-in playground (`scripts/setup-local-postgres.ps1`, `postgres:16`, which uses SCRAM by default), run `dotnet test tests/SqlHarness.Tests --filter "Category=PostgresIntegration"` with `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING` set. Record the result in the index row, or "not run".

## Test plan

1 parser-assumption test, 3 `EnsureStandardStrings` tests, and 2–3 connection-string tests.

## Done criteria

- [ ] PG tests and the full gate pass
- [ ] `grep -n "standard_conforming_strings" src/SqlHarness.Core/Postgres` → match
- [ ] Transport policy spec and AGENTS.md updated
- [ ] `plans/README.md` row updated (state whether Step 4 ran)

## STOP conditions

- The parser-assumption test (Step 1.1) shows the parser does **not** assume standard strings.
- Npgsql has no require-auth setting in the version in use (Step 2.1).
- The operator reports md5-only servers reached over `require`. Then a profile opt-out (e.g. `"allowMd5": true`) is needed, which changes the profile schema: a separate decision.

## Maintenance notes

- If the parser is upgraded or replaced (see plan 019 maintenance notes), re-run the Step 1.1 assumption test first.
- Channel binding (SCRAM-PLUS) strengthens `require` further against relays. Make it mandatory when Npgsql and the target servers support it.
