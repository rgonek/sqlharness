# Plan 025: Redact credentials, not caller-supplied input

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 6d04cc2..HEAD -- src/SqlHarness.Core/SecretRedactor.cs src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/CompareCellRunner.cs src/SqlHarness.Core/MeasureParameterSets.cs src/SqlHarness.Core/SqlParameterMatrix.cs src/SqlHarness.Core/Auth/AuthSpec.cs src/SqlHarness.Core/Postgres/PostgresConnectionString.cs tests/SqlHarness.Tests/SecretRedactorTests.cs tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs README.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED (redaction output is pinned by many tests; this deliberately reverses a policy from plans 012/038)
- **Depends on**: —
- **Category**: security / bug
- **Planned at**: commit `6d04cc2`, 2026-10-10 (rewritten; first written at `5280f78`, 2026-10-03)

## Decision (user, 2026-10-10)

Error redaction protects only values that the reader of the output **cannot already know or obtain**:

- **Redacted**: the SQL password value read from the profile's password environment variable (any length, raw
  substring), plus the existing pattern-based scrubbing in `SecretRedactor` (access tokens, JWTs,
  `password=`/`server=`/… connection-string fragments, `access-token`-like words).
- **Not redacted**: values the caller supplied or can read back from the server: SQL text (query, setup,
  baseline, candidate), parameter values (`--param`, typed, matrix, `--param-set`), `--var` values, the SQL user
  name, and the password environment-variable **name**.

Rationale, recorded so it is not re-litigated:

1. Parameter values and SQL text are in the caller's own invocation (and an agent's transcript). Redacting
   them hides nothing. It only corrupts diagnostics: `C` turned `ClientID` into `[REDACTED]lientID`, and `1`
   turned `Line 1` into `Line [REDACTED]`.
2. `--var` values and the SQL user are not secret from the agent even in fixed-mode MCP. Every report carries
   `Target` (`RequestedServer`, `RequestedDatabase`, `ActualServer`, `ActualDatabase`;
   `src/SqlHarness.Core/Artifacts.cs:8`), `ping` returns `Server`, `Database` and `Login`
   (`src/SqlHarness.Core/Contracts.cs:347`), and the agent can run `SELECT DB_NAME()`. In the primary
   deployment, the database name itself encodes the profile variables.
3. The password is the only operator-held value the agent cannot reach, and today it is **not** registered at
   all. Only the `password=` regex protects it, while `README.md:270` claims passwords are "redacted regardless
   of length".

This supersedes plan 038 W3's four-character parameter threshold. W3's single-pass replacement (no re-scanning
of `[REDACTED]` markers) stays. It also supersedes the 012-era MCP test expectation that SQL, scope and
parameter sentinels are absent from a request-scope error (`McpSecretRedactionTests.cs:69`). Stderr logging
(plan 001) is unaffected: stderr never carries content at all, independent of redaction.

## Current state

- `src/SqlHarness.Core/SecretRedactor.cs`: `Redact(string, IReadOnlyList<string>)` replaces all non-empty known
  secrets in one longest-first `NonBacktracking` regex pass, then applies four `[GeneratedRegex]` patterns.
  `Redact(Exception, …)` joins the exception chain. `RedactPreserving` protects parameter names and types with
  private-use placeholders for `SqlHarnessSafetyException.IsParameterValue`. **Keep all of this unchanged.**
- `src/SqlHarness.Core/SqlSafety.cs:60-115` `SqlParameterSecrets`: registers parameter, typed, and matrix
  values of length ≥ `MinimumRedactionLength = 4`.
- `src/SqlHarness.Core/SqlHarnessModule.cs` builds a fresh `List<string> knownSecrets` per operation:
  - query `:189`, compare `:293`, compare-matrix `:388`, measure `:572`, schema `:799`, watch `:835`,
    watch-NDJSON `:910`, snapshot `:1019`: SQL text, then `SqlParameterSecrets.*`, then
    `Convert.ToString(parameter.Value)` loops over bound parameters (`:214`, `:315`, `:623`, `:865`, `:940`,
    `:1044`; for `byte[]` this registers the literal `"System.Byte[]"`) and `AddTypedSecret` (`:530-543`).
    Schema registers `schema.Filter` / `schema.Object`.
  - ping `:1093`, counts `:1133`, space `:1210`, qstop `:1265`, indexes `:1334`:
    `CollectTargetSecrets(target)` (`:1450-1464`) = var values + `SqlUser` + `PasswordEnvVar` from the
    **request**. For profile targets the request carries no `PasswordEnvVar`; the name lives in the resolved
    `AuthSpec`. So even the name is missed for profiles, and the value is never registered anywhere.
  - `LongestFirst` (`:561`) duplicates `Redact`'s own ordering.
- Other registration sites: `CompareCellRunner.cs:75` (`SqlParameterSecrets.AddValues`, and its own
  `TargetResolver.Resolve` at `:55`), `MeasureParameterSets.cs`, and `SqlParameterMatrix.cs`. List them with
  `grep -rn "SqlParameterSecrets\|AddTypedSecret\|CollectTargetSecrets" src`.
- Password reads: `src/SqlHarness.Core/Auth/AuthSpec.cs:68` (SQL Server, `AuthStrategy.Sql`) and
  `src/SqlHarness.Core/Postgres/PostgresConnectionString.cs:26` (via `target.Auth.PasswordEnvVar`). Both read
  `Environment.GetEnvironmentVariable` of the resolved `AuthSpec.PasswordEnvVar`.
- Consumers of the list (unchanged signatures): `QueryResultCollector` (redacts SQL `PRINT`/info messages before
  they enter the canonical result accumulator), `SnapshotRunner`, `WatchRunner`, `CompareCellRunner`,
  `ArtifactStoragePreflight`, `McpResultAdapter` (MCP callers mostly pass no list).
- Docs: `README.md:270` (Secrets row) states the 4-character policy and the false password claim.
- Tests that assert `[REDACTED]` (`grep -rln "\[REDACTED\]" tests --include=*.cs`): `SecretRedactorTests`,
  `SqlParameterParserTests`, `McpSecretRedactionTests`, `ArtifactStoragePreflightTests`, `Cli/CommandTests`,
  `IndexesTests`, `MeasureParameterSetExecutionTests`, `PingTests`, `QueryResultCollectorTests`,
  `QueryStoreTopTests`, `QueryTests`, `SpaceTests`.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Redaction tests | `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName~Redact\|FullyQualifiedName~Secret"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |
| Linux gate | `pwsh -NoProfile -File scripts/verify-linux.ps1` | green |

## Scope

**In scope**: `SecretRedactor.cs` (only if a helper is needed; its matching logic stays), `SqlSafety.cs`
(`SqlParameterSecrets`), `SqlHarnessModule.cs` (secret collection only), `CompareCellRunner.cs`,
`MeasureParameterSets.cs`, `SqlParameterMatrix.cs` (registration calls only), `Auth/AuthSpec.cs` (one read-only
accessor), `README.md` / `AGENTS.md` / `docs/mcp.md` wording about redaction, and the tests listed above.

**Out of scope**: the redaction regex patterns, `RedactPreserving`, stderr/MCP logging (plan 001), journal
storage rules, and restructuring `SqlHarnessModule`.

## Git workflow

Branch `fix/plan-025-redaction`. One commit per step. Do NOT push.

## Steps

### Step 1: Register the password value after target resolution

Add to `AuthSpec` a read-only accessor, e.g. `internal string? PasswordForRedaction()`, that returns
`Environment.GetEnvironmentVariable(PasswordEnvVar)` when `Strategy == AuthStrategy.Sql` and the variable is set,
otherwise `null`. Do not cache the value; it stays in process memory as today.

Add one helper in `SqlHarnessModule` (or a small `OperationSecrets` static beside `SecretRedactor`):

```csharp
internal static void AddCredentials(List<string> knownSecrets, ResolvedTarget target)
{
    if (target.Auth.PasswordForRedaction() is { Length: > 0 } password)
        knownSecrets.Add(password);
}
```

Call it **immediately after every** `TargetResolver.Resolve` in an executing path: each operation in
`SqlHarnessModule.cs` (`grep -n "TargetResolver.Resolve" src/SqlHarness.Core/SqlHarnessModule.cs`) and
`CompareCellRunner.cs:55` (matrix cells resolve their own target and receive the module's list). Do not touch
`SqlValidation.cs:118` (offline, never connects) or the MCP scope resolvers (`McpScope.cs:137`,
`McpProcessContext.cs:156`), which do not redact operation output. Postgres uses the same `AuthSpec`, so no
Postgres-specific code is needed. Verify that by test.

Tests (new, in `SecretRedactorTests.cs` or the per-command test files, using each file's existing fake
session/profile pattern):
- With a profile whose `passwordEnvVar` names a test-scoped variable set to `p@ss-W0rd!`, a fake session error
  that echoes the bare password (no `Password=` prefix) is redacted for `query`, `measure`, `compare`,
  `compare --matrix`, `watch`, `snapshot`, `schema`, `ping`, `counts`, `space`, `qstop`, `indexes`. One test per
  command family is enough if the families share a code path. Otherwise one per operation.
- A one-character password is still redacted (credentials have no minimum length).
- A Postgres-engine profile redacts its password the same way.
- `integrated` / `azure-cli` / `ad-default` profiles register nothing extra.

Restore the environment variable in each test (`try/finally`); never use a real credential.

**Verify**: build, redaction tests, and the new tests pass.

### Step 2: Stop registering caller-supplied values

Remove from every registration site: SQL text, `SqlParameterSecrets.*` calls, the
`Convert.ToString(parameter.Value…)` loops, `AddTypedSecret`, matrix display values, schema filter/object,
and `CollectTargetSecrets` (var values, `SqlUser`, env-var name). After this step, each operation's list
contains only what Step 1 adds. Delete the dead code: `SqlParameterSecrets` (including
`MinimumRedactionLength`), `AddTypedSecret`, `CollectTargetSecrets`, and `LongestFirst` (`Redact` already
orders longest first). Keep passing the list to the existing consumers. Do not change their signatures.

Keep `RedactPreserving` and `SqlHarnessSafetyException.PreservedTokens`: they still guard parameter names
against a password substring collision. Note in the commit that they now protect only that edge.

**Verify**:
- `grep -rn "SqlParameterSecrets\|AddTypedSecret\|CollectTargetSecrets\|MinimumRedactionLength" src` → no match.
- `grep -n "Convert.ToString(parameter.Value" src/SqlHarness.Core/SqlHarnessModule.cs` → no match.
- Build passes with `-warnaserror`.

### Step 3: Update pinned tests

Run the full test suite and classify every failure:

- **Expected flip**: a test asserted that a caller-supplied value (SQL text, parameter, typed/matrix/param-set
  value, var value, SQL user, env-var **name**) is absent or replaced by `[REDACTED]`. Rewrite it to assert
  that the value is now **present verbatim** in the output, and, where the test has a target, that a password
  sentinel set through the profile's env var is absent. Rename the test to match the new contract. Example:
  `McpSecretRedactionTests.Request_scope_core_error_redacts_SQL_scope_and_typed_parameter_sentinels` becomes a
  test that the SQL/var/parameter sentinels are visible while a password sentinel is not.
- **Delete**: tests whose only subject is the removed threshold or registration helpers
  (`SecretRedactorTests.Parameter_values_shorter_than_four_characters_are_not_registered_for_redaction`,
  helper tests in `SqlParameterParserTests` that call `SqlParameterSecrets`).
- **Keep unchanged**: pure `SecretRedactor.Redact` tests that pass an explicit list
  (`Redact_removes_overlapping_values_longest_first`, `Redact_does_not_rescan_replacement_markers_for_later_secrets`,
  `Short_known_passwords_are_still_redacted`), all regex-pattern tests, `ArtifactStoragePreflightTests` path
  redaction, and every stderr test (`McpStderrLeakRegressionTests`, startup/transport log tests).

Add regression tests for the original corruption:
- `query` with `--param code:nvarchar=C` and a fake error `Invalid column name 'ClientID'. Line 1` returns that
  text unchanged.
- `--var env=dev` with a fake error containing `device` returns `device` unchanged.

List every flipped or deleted test in the commit message, one line each with the reason.

**Verify**: full gate `verify: OK`.

### Step 4: Documentation

- `README.md:270` Secrets row: replace the redaction sentences with the policy from "Decision": the SQL password
  from the profile's environment variable, tokens, JWTs, and connection-string fragments are redacted from
  errors and server messages. SQL text, parameter values, profile variables, and the SQL user are not redacted
  because the caller supplied them or can read them from the target identity. Keep the first sentence
  ("Tokens and passwords remain only in process memory…").
- `AGENTS.md`: check the Safety contract and PostgreSQL notes for any sentence implying parameter or var
  redaction (`grep -n -i "redact" AGENTS.md`); align it. The Postgres line "Error text must not contain the
  password; a missing-password error may still name the variable" stays true.
- `docs/mcp.md`: same check. Add one sentence in the agent section noting that error text may quote the
  caller's SQL and parameter values.
- `plans/038-agent-session-friction-fixes.md` W3 "As built": append "Threshold superseded by plan 025
  (2026-10-10): caller-supplied values are no longer redacted at all."
- If the `capabilities` output or the docs sync test (plan 029) pins redaction wording, update it in the same
  commit.

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`, then
`pwsh -NoProfile -File scripts/verify-linux.ps1` → green.

## Known one-time effect

`QueryResultCollector` redacts SQL messages before adding them to the canonical result accumulator. A named
snapshot captured **before** this change whose SQL messages contained a registered value (≥ 4 characters)
stored `[REDACTED]` there. `snapshot --diff` against it after the change may report a message difference once.
Confirm whether canonical messages participate in snapshot comparison (`CanonicalResults.cs:409` and its
callers). If they do, add one line to the README snapshot paragraph: re-capture with `--force` after upgrading.
Compare equivalence is unaffected because both variants run under the same policy.

## Done criteria

- [x] Password value registered after every executing `TargetResolver.Resolve`, covered by tests for SQL
      Server and Postgres profiles
- [x] `grep -rn "SqlParameterSecrets\|AddTypedSecret\|CollectTargetSecrets" src` → no match
- [x] `ClientID` / `Line 1` / `device` regression tests pass
- [x] README/AGENTS/docs/mcp.md describe the credential-only policy; plan 038 W3 cross-referenced
- [ ] Windows and Linux gates green; flipped/deleted tests listed in the Step 3 commit
- [x] `plans/README.md` row updated

## STOP conditions

- Any test about the **password**, tokens, JWTs, connection-string fragments, or stderr content changes
  behavior.
- The password cannot be registered before the first statement runs in some operation (for example, a code path
  opens a connection inside a helper before the module sees the `ResolvedTarget`). Report the path. Do not read
  the env var a second time inside the connection code.
- A failing test asserts redaction of something that is **not** in the "Not redacted" list and not a credential
  (for example, result-cell data or a file path). Report it instead of flipping it.

## Maintenance notes

- New operations must call the credential helper right after resolving their target. Reviewers should reject
  any new registration of caller-supplied input.
- If a future auth strategy introduces another operator-held secret (a client secret, a certificate password),
  register it in the same helper.
- If SQLHarness ever serves a reader who did **not** supply the inputs (for example, a shared report surface),
  revisit this decision for that surface only. Do not re-add global input redaction.
