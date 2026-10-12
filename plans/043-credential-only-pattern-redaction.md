# Plan 043: Limit redaction patterns to credentials while preserving target diagnostics

> Read current source before editing. Inspect drift since `729d88f` in the scope below and adapt implementation details within this authorized intent. A changed file alone is not a STOP condition. Escalate an incompatible contract or missing required proof. This file plans future implementation; writing it runs no SQL.

## Status

- Status: DONE — fast-forwarded to main `ab0e137` on 2026-10-11; Windows and Linux gates green; evidence in plans/README.md. Unterminated quoted secret-field values fixed in `ddf6285` (masked to end of line; both gates green).
- Priority: P2
- Effort: S
- Risk: MED
- Depends on: 025 (completed)
- Planned at: `729d88f`, 2026-10-11

## Why this matters

Plan 025 stopped registering caller SQL and parameter values as secrets, but its unchanged connection regex still masks server, database and user identifiers. Those identifiers already appear in target reports. This plan completes the credential-only policy approved for planning on 2026-10-11.

## Current state

- src/SqlHarness.Core/SecretRedactor.cs:101 includes password, pwd, user id, uid, data source, server, initial catalog and database in ConnectionSecretPattern.
- OperationSecrets.AddCredentials registers the resolved SQL password; keep this behavior, including short credentials.
- TokenLikePattern masks any word containing access-token; a descriptive label is not evidence of a token value.
- tests/SqlHarness.Tests/SecretRedactorTests.cs and CredentialRedactionTests.cs are the existing regression fixtures. Plan 025 is delivered history, not reopened implementation.

## Scope

src/SqlHarness.Core/SecretRedactor.cs; credential collection only if a demonstrated missed credential path needs it; tests/SqlHarness.Tests/SecretRedactorTests.cs, CredentialRedactionTests.cs; tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs; affected command error pins; README.md; docs/mcp.md; AGENTS.md; plans/025-redaction-consistency.md only a follow-up reference.

No dashboard API, user configuration edits, live production queries, commit, merge, installation or push is authorized by this plan. Match existing xUnit and fake-provider conventions; use synthetic fixtures, not operational SQL/data. Persistent-mutation controls, target locking, timeout and byte limits retain their separate purpose.

## Steps and acceptance

1. Keep exact registered credentials and single-pass replacement. Narrow connection patterns to password/pwd values and explicit authentication-secret fields; retain token/JWT value protection.
2. Preserve server/database/user values, environment-variable names, file paths, SQL identifiers and ordinary diagnostic labels. Replace the broad access-token word heuristic with recognition of credential values in explicit key/value contexts; do not classify a field name or explanatory word alone as a credential.
3. Use synthetic fixtures to prove complete connection-string errors preserve target/user fields while removing quoted/unquoted passwords and tokens. Cover overlapping/one-character passwords, replacement-marker integrity and benign access-token documentation labels.
4. Update expected diagnostics that deliberately change. Do not weaken content-free MCP stderr logging or journal storage opt-in. Audit other regex callers for assumptions before adjusting pins.
5. Align docs with actual fields. A caller can supply arbitrary sensitive SQL or result data; this policy protects credentials, not a guarantee that every sensitive value is absent.

After each logical implementation change, run the relevant subset of the focused test command below. Build first when needed. All selected tests must pass; a skipped required provider scenario is not proof.

## Verification

- Build: `dotnet build SqlHarness.sln -warnaserror` — exit 0, no warnings/errors.
- Focused: `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName~SecretRedactor|FullyQualifiedName~CredentialRedaction|FullyQualifiedName~McpSecretRedaction"` — exit 0, at least one selected test, no failed selected tests; confirm the intended regression tests actually ran.
- Final Windows gate: `pwsh -NoProfile -File scripts/verify.ps1` — exit 0 and literal `verify: OK`.
- Final Linux gate: `pwsh -NoProfile -File scripts/verify-linux.ps1` — exit 0 and successful completion of its full gate on ext4.
- `git diff --check` — exit 0.

Run one final gate on each required platform after the final relevant code change. Repeat or broaden verification only for a remaining failure or risk. Record literal results and skipped live checks separately.

## Done criteria

- [ ] Target/user/path diagnostics remain readable.
- [ ] Password, token and JWT regression fixtures retain protection.
- [ ] Unlabelled registered credentials of any length remain scrubbed.
- [ ] Broad noncredential pattern masking is removed without changing stderr policy.
- [ ] Focused and required platform gates pass, and plans/README.md records evidence.
- [ ] Source, CLI/MCP descriptions and documentation agree on the final behavior.

## Stop conditions and maintenance

Stop if a removed pattern was the only protection for an actual credential; add narrowly defined protection before removing it. Do not expand the list back to SQL/parameter literals.

Future changes must preserve the specific boundary tested here rather than adding generic hiding or syntax restrictions. Update the plan if implementation evidence changes its premise; historical tests are evidence to examine, not authority to preserve obsolete policy.
