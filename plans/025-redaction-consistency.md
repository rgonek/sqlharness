# Plan 025: Every operation redacts the same secrets (including the resolved password), binary values register correctly, and short values stop corrupting words

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/SecretRedactor.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/SecretRedactorTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED (redaction output is pinned by many tests)
- **Depends on**: plans/016-restore-green-ci.md
- **Category**: security / bug
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

SQLHarness treats SQL text, parameter values, profile vars and credentials as locally sensitive and redacts them from errors and server messages (`SecretRedactor`). Three defects:

1. **Inconsistent secret sets.** `ping`, `counts`, `space`, `qstop` and `indexes` start from `CollectTargetSecrets(target)` (var values, SQL user, password env-var **name**). `query`, `measure`, `compare`, `watch` and `snapshot` start from only the SQL and parameters. So a var value echoed in a server error is redacted by one command family and not the other.
2. **The password value itself is never registered.** Protection relies only on the `password=` regex. A driver or Azure message that echoes the raw password without a `Password=` prefix is not redacted.
3. **Binary and short values.** Query, measure-fixed, watch and snapshot register bound values with `Convert.ToString(parameter.Value)`, which for `byte[]` is the literal `"System.Byte[]"`. The module's own `AddTypedSecret` comment says this is wrong, and the matrix/param-set paths use `AddTypedSecret` correctly. Meanwhile, every non-empty value is replaced **as a plain substring everywhere**, so `--var env=dev` turns "device" into "[REDACTED]ice", and `--param id:int=1` turns "Scan count 10" into "Scan count [REDACTED]0". Those redacted messages also feed the canonical result accumulator.

## Current state

- `src/SqlHarness.Core/SecretRedactor.cs:41-52`:

  ```csharp
      public static string Redact(string value, IReadOnlyList<string> knownSecrets)
      {
          var safe = value;
          foreach (var secret in knownSecrets
              .Where(secret => !string.IsNullOrEmpty(secret))
              .Distinct(StringComparer.Ordinal)
              .OrderByDescending(secret => secret.Length)
              .ThenBy(secret => secret, StringComparer.Ordinal))
              safe = safe.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
          safe = AccessTokenPattern().Replace(safe, "$1[REDACTED]$3");
          safe = ConnectionSecretPattern().Replace(safe, "$1[REDACTED]");
          safe = JwtPattern().Replace(safe, "[REDACTED]");
          safe = TokenLikePattern().Replace(safe, "[REDACTED]");
          return safe;
      }
  ```

  `RedactPreserving` (`:54-80`) protects parameter **names and types** with private-use placeholders. Its comment: "Digits would let a value of "0" eat the placeholder". Keep that mechanism.
- `src/SqlHarness.Core/SqlHarnessModule.cs`:
  - `:1462-1476` `CollectTargetSecrets(SqlTargetRequest target)`: var values, `SqlUser`, `PasswordEnvVar` (the name).
  - Used at `:1081` (ping), `:1126` (counts), `:1208` (space), `:1268` (qstop), `:1342` (indexes) only.
  - Query (`:189-215`):

    ```csharp
            var knownSecrets = new List<string> { query.Sql };
            knownSecrets.AddRange(query.Parameters.Where(value => !string.IsNullOrEmpty(value)));
            SqlParameterSecrets.AddValues(knownSecrets, query.TypedParameters);
            ...
                foreach (var parameter in parameters)
                {
                    if (parameter.Value is not DBNull)
                        knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                }
    ```

    The same `Convert.ToString` loop appears in measure (`:611-615`) and, per `grep -n "Convert.ToString(parameter.Value" src/SqlHarness.Core/SqlHarnessModule.cs`, in watch/snapshot. Count the sites with that grep before editing.
  - `:522-535` `AddTypedSecret(List<string> knownSecrets, SqlHarnessParameter parameter)`: the correct helper (Base64 for `byte[]`).
  - `:549-558` `LongestFirst(...)` is redundant with `Redact`'s own ordering but harmless. Leave it.
- Credentials: `src/SqlHarness.Core/Auth/AuthSpec.cs:68` reads `Environment.GetEnvironmentVariable(PasswordEnvVar!)` into `builder.Password`. The PostgreSQL equivalent is in `Postgres/PostgresConnectionString.cs:26`. Read both.
- Tests: `tests/SqlHarness.Tests/SecretRedactorTests.cs` (4 tests), plus many tests asserting `[REDACTED]` in outputs across `tests/SqlHarness.Tests` and `tests/SqlHarness.Mcp.Tests` (`grep -rln "\[REDACTED\]" tests`).

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Redaction tests | `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName~Redact|FullyQualifiedName~Secret"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `src/SqlHarness.Core/SecretRedactor.cs`, `src/SqlHarness.Core/SqlHarnessModule.cs` (secret collection only), `tests/SqlHarness.Tests/SecretRedactorTests.cs`, and any existing test whose pinned output changes **only** because of Step 3's boundary rule (each change listed in the commit).

**Out of scope**: restructuring `SqlHarnessModule` (a separate refactor), MCP logging, and new redaction regexes.

## Git workflow

Branch `fix/plan-025-redaction`. One commit per step. Do NOT push.

## Steps

### Step 1: One secret-collection entry point that includes the credential

Introduce a small internal type in `SecretRedactor.cs` (or a new `KnownSecrets.cs` beside it) that separates two classes:

```csharp
internal sealed class KnownSecrets
{
    // Credentials: always replaced as raw substrings, at any length.
    internal void AddCredential(string? value) { ... }
    // Inputs (SQL text, parameter values, var values, user names): see Step 3.
    internal void AddInput(string? value) { ... }
    internal void AddTypedParameter(SqlHarnessParameter parameter) { ... } // moves AddTypedSecret logic here
    internal static KnownSecrets ForTarget(SqlTargetRequest target) { ... } // replaces CollectTargetSecrets
}
```

`ForTarget` adds var values and `SqlUser` as inputs, the env-var **name** as an input (current behavior), and the env-var **value** (`Environment.GetEnvironmentVariable(target.PasswordEnvVar)`, when set) as a credential. The value only lives in process memory, which the contract allows.

`SecretRedactor.Redact(string, KnownSecrets)` becomes the primary overload. Keep `Redact(string, IReadOnlyList<string>)` and `Redact(Exception, IReadOnlyList<string>)` as thin adapters that treat every list entry as an **input**, so callers you do not migrate keep compiling.

Tests (`SecretRedactorTests.cs`):
- A credential value `p@ss-W0rd!` echoed bare in a message is redacted.
- A credential of length 1 is still redacted as a substring (credentials have no minimum).

**Verify**: redaction tests pass.

### Step 2: Use it in every operation

In `SqlHarnessModule.cs`, start **every** operation's secret set from `KnownSecrets.ForTarget(operation.Target)` (query, measure, compare, watch, watch-NDJSON, snapshot, ping, counts, space, qstop, indexes, schema). Then add the SQL text and parameters as today, but replace each `Convert.ToString(parameter.Value, …)` loop with `AddTypedParameter`.

**Verify**: `grep -n "Convert.ToString(parameter.Value" src/SqlHarness.Core/SqlHarnessModule.cs` → no match. Redaction tests and the full gate pass. Add one test per command family, using the existing fake-module pattern in `tests/SqlHarness.Tests/QueryTests.cs` or `CommandTests.cs`. Have a fake session throw an error containing a var value, and assert it is redacted for `query` (newly covered) as it already is for `counts`.

### Step 3: Boundary-aware matching for short inputs

In `Redact`, for **input** entries shorter than 6 characters, replace only occurrences that are not adjacent to a letter, digit or `_` on either side. Use a regex built with `Regex.Escape(value)` and lookarounds `(?<![\p{L}\p{Nd}_])` … `(?![\p{L}\p{Nd}_])`, `RegexOptions.CultureInvariant`. Do not compile it per call; cache by value or build once per `Redact` call. Inputs of 6 or more characters and all credentials keep raw substring replacement.

Tests:
- Input `dev` with message `device dev` → `device [REDACTED]`.
- Input `1` with message `Scan count 10, logical reads 1` → `Scan count 10, logical reads [REDACTED]`.
- Input `acme-prod` (≥6) inside `xacme-prodx` → still redacted.
- The existing placeholder test for value `"0"` in `RedactPreserving` (see `SqlParameterParserTests.cs:318`) still passes.

Then run the full gate. Pinned outputs elsewhere may now show **less** redaction for short values inside words. For each failing test, confirm the change is only a short value no longer redacted **inside a word**, and update the expectation. Any other kind of change is a STOP.

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

About 8 new tests (Steps 1–3) plus the per-family redaction test. Pattern: `SecretRedactorTests.cs`.

## Done criteria

- [ ] `grep -n "CollectTargetSecrets" src` → no match (replaced by `KnownSecrets.ForTarget`)
- [ ] `grep -n "Convert.ToString(parameter.Value" src/SqlHarness.Core/SqlHarnessModule.cs` → no match
- [ ] Full gate passes; every changed existing expectation is listed in the Step 3 commit message
- [ ] `plans/README.md` row updated

## STOP conditions

- A test shows a **credential** or a value of 6 or more characters becoming less redacted.
- MCP redaction tests (`McpSecretRedactionTests`, `McpStderrLeakRegressionTests`) change behavior. Report them; they are a security boundary owned by plan 001.
- The env-var password value cannot be read in some operation because the target is resolved later than the secret set is built. Move the `ForTarget` call to right after `TargetResolver.Resolve`, and report if that is not possible.

## Maintenance notes

- New commands must start from `KnownSecrets.ForTarget`. Reviewers should reject any new `new List<string> { ... }` secret set in `SqlHarnessModule`.
- The 6-character threshold is a trade-off: values shorter than 6 are still redacted as whole tokens, but not inside longer words. If a future requirement treats short values as highly sensitive, switch them to "redact all" per operation, not globally.
