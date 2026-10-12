# Plan 048: Close credential-column literal gaps in MCP plan text

> **REVERTED 2026-10-11** — implemented and merged (`a74b485`), then reverted in `cadde67`. The offline plan tool reads a showplan the caller supplied, so masking literals in it hides nothing from the caller. Follow-up: [049](049-plan-text-without-literal-masking.md) removes the remaining 042 rule.

> Read current source before editing. Inspect drift since `3f425ef` in the scope below and adapt implementation details within this authorized intent. A changed file alone is not a STOP condition. Escalate an incompatible contract or missing required proof. This file plans future implementation; writing it runs no SQL.

## Status

- Status: TODO
- Priority: P2
- Effort: S
- Risk: MED
- Depends on: 042 (DONE, merged `3f425ef`)
- Planned at: `3f425ef`, 2026-10-11

## Why this matters

Plan 042 made the offline MCP `sqlharness_plan` tool return bounded statement text and predicates by default (`includeText=true`). It masks a string literal compared with a credential-named column through an MCP-local rule, `CredentialLiteralPattern` in `McpPlanTextPolicy`. That rule only matches the literal directly after `<column> <op>`. The final review of 042 and its re-review found real plan shapes where the same secret is still echoed in `predicate` or `sql`. The SQL Server implicit conversion case is the most likely of these: a `varchar` password column compared with an `N'...'` literal. These gaps were parked to keep 042 to one fix wave. This plan closes them without widening the rule into generic literal hiding.

## Current state

- `src/SqlHarness.Mcp/McpOperationMapper.cs`, `McpPlanTextPolicy`:
  - `CredentialLiteralPattern` masks `N'..'` / `'..'` after a credential key (password, pwd, access_token, client_secret, account_key, shared_access_key and their variants).
  - The key may be bracketed, quoted or qualified, and the operator is `=`, `<>`, `!=` or `LIKE`.
  - Text between matches goes through `SecretRedactor.Redact(value, [])` (plan 043 patterns; the shared Core redactor is unchanged).
  - The regex has a 1 s timeout. If it times out, the tool fails closed as a generic, content-free failure. This is not tested.
- Unmasked shapes, verified by review analysis rather than by tests:
  1. Wrapped column on SQL Server: `CONVERT_IMPLICIT(nvarchar(4000),[db].[dbo].[Users].[Password],0)=N'x'`, plus `CONVERT(...)` and `CAST(... AS ...)` wrappers.
  2. Wrapped column on PostgreSQL: `((password)::text = 'x'::text)` and `(password::text = 'x'::text)`.
  3. PostgreSQL operator spelling: EXPLAIN prints `LIKE` as `~~`, `ILIKE` as `~~*`, and `NOT LIKE` as `!~~`. The rule only knows `LIKE`. Confirm this against a fixture before relying on it.
  4. Lists:
     - statement text `Password IN (N'a', N'b')`;
     - PostgreSQL `(password = ANY ('{a,b}'::text[]))`.
     - SQL Server Showplan usually expands `IN` into `=` OR-chains. Verify this with a fixture rather than assuming it.
  5. Reversed operand order: `N'x'=[Password]`.
  6. Credential key inside a longer column name: `[PasswordHash]`, `password_hash`, `[PwdSalt]`, `UserPassword`. Today only `[OldPassword]`-style prefixes match, by accident, because there is no left word boundary.
- docs/mcp.md (`sqlharness_plan` row) and AGENTS.md (MCP tool list) describe the 042 boundary. They must change with the rule.

## Scope

- Source: `src/SqlHarness.Mcp/McpOperationMapper.cs` (`McpPlanTextPolicy` only).
- Tests: `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`, `McpToolSchemaTests.cs` (served tests), and `McpSecretRedactionTests.cs` if useful.
- Docs: `docs/mcp.md`, AGENTS.md.

Out of scope:
- the shared Core `SecretRedactor` (CLI, journal and errors);
- the CLI `plan` command;
- `PlanNode.Warnings` text;
- artifact sections.

No dashboard API, user configuration edits, live queries, merge, installation or push is authorized by this plan. Use synthetic fixtures only.

## Steps and acceptance

1. **Add RED tests first, one per gap.** Write unit cases on `McpPlanTextPolicy` and at least one served test per engine, through real MCP binding in both modes. Each case asserts:
   - the secret is absent from the whole wire result;
   - a neighbouring non-credential literal, such as `[Status]=N'ACTIVE'`, survives.

   Base the PostgreSQL shapes (gaps 2–4) on real EXPLAIN text. If you cannot confirm an exact spelling, record that and keep the case as a regex unit test only.
2. **Extend the rule narrowly** so that it masks only literals compared with an expression that references a credential-named column. Specifically:
   - Allow the column reference inside known wrappers on either side of the operator: `CONVERT_IMPLICIT(...)`, `CONVERT(...)`, `CAST(...)`, a parenthesised column, and a trailing Postgres `::type`.
   - Allow the reversed order, literal before column.
   - Allow the operators `~~`, `~~*`, `!~~` and `!~~*`.
   - Mask every element of an `IN (...)` list or `ANY ('{...}'...)` array literal that is compared with such a column.

   Keep the literal's quotes and `N` prefix, and the `::type` suffix, around `[REDACTED]`. Do not mask literals compared with other columns, and do not mask parameters (`@p`, `$1`).
3. **Settle the column-name boundary.** Mask when the last identifier part contains a credential key as a whole word part, at a camel-case, `_` or `-` boundary. Examples: `PasswordHash`, `password_hash`, `PwdSalt`, `UserPassword`.
   - This deliberately accepts that literals next to columns such as `PasswordHint` or `LastPasswordChange` are masked too. Losing a non-secret literal is the accepted cost; leaking a secret is not.
   - Write this decision into the rule's comment and into the docs.
   - Make sure `Passwordless` and other unrelated words do not become a problem. Mask them too, or exclude them explicitly, and pin whichever you choose with a test.
4. **Prove timeout fails closed.** Add a test showing that a pathological input, or an injected short timeout, produces a content-free failure result and never unfiltered text. If the rule's shape makes catastrophic backtracking impossible, use `RegexOptions.NonBacktracking` when the constructs allow it, and test the bound instead.
5. **Update docs/mcp.md and AGENTS.md** to the new boundary in one or two sentences. Keep the statement that this is not complete sanitization. Unlabelled secrets remain undetected: free text without a key, and columns not named like credentials.

After each logical change, run the focused tests below.

## Verification

- Build: `dotnet build SqlHarness.sln -warnaserror` — exit 0, no warnings or errors.
- Focused: `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName~McpMapping|FullyQualifiedName~McpToolSchema|FullyQualifiedName~McpSecretRedaction|FullyQualifiedName~McpTokenBudget"` — exit 0, and confirm the new cases ran.
- Final Windows gate: `pwsh -NoProfile -File scripts/verify.ps1` — exit 0 and the literal `verify: OK`.
- Final Linux gate: `pwsh -NoProfile -File scripts/verify-linux.ps1` — exit 0 and `verify-linux: OK`.
- `git diff --check` — exit 0.

## Done criteria

- [ ] Gaps 1–6 each have a test that failed before the change and passes after it, in both MCP modes where served.
- [ ] Non-credential literals and parameters remain visible; no generic literal hiding was added.
- [ ] The timeout or bound behaviour is proven fail-closed.
- [ ] Shared Core `SecretRedactor` is unchanged.
- [ ] Docs agree with the rule. Both gates pass, and plans/README.md records the evidence.

## Stop conditions and maintenance

Stop if a fix requires parsing SQL into an AST, changing the shared Core redactor, or masking all literals. Report the remaining shape instead. Keep the rule a bounded, testable regex policy local to MCP. Future shapes are added the same way: a RED fixture first, then a narrow extension.
