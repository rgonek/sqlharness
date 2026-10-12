# Plan 054: Redaction is exact known-secret replacement; MCP input guards match a same-user threat model

> Read current source before editing. Inspect drift since `bab7618` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition. Line numbers come from the 2026-10-11 rule review; confirm them.

## Status

- Status: TODO
- Priority: P2
- Effort: M
- Risk: MED (removes defensive code; each removal needs its rationale checked)
- Depends on: 049 (plan-text masking removed), ideally after 053
- Planned at: `bab7618`, 2026-10-11

## Why this matters

The secret boundary is the resolved credential: the password read from the profile's environment variable and the Azure access token. Everything else these guards defend against is either data the caller already has or an action the same OS user can already take. Each guard costs code, tests and doc caveats.

## Items

1. **Known secrets include the Azure token.** `SecretRedactor` receives only the environment password (`SecretRedactor.cs:124-128`). Add the access token obtained from `az` to the known-secret list on every path that redacts. This is a real gap and comes first.
2. **Drop credential field patterns** (`PasswordPattern`, `SecretFieldPattern`, the unterminated-quote rule; `SecretRedactor.cs:54-56, 100-118`). SqlClient and Npgsql do not echo passwords; these patterns only mask the caller's own SQL quoted in a server error (`WHERE password = '…'`). Keep the exact known-secret replacement and the JWT pattern only if a test shows a JWT can reach a message without being a known secret; otherwise drop it too.
3. **MCP input roots** (`McpInputReader.cs:171-210, 264-302, 361-374`): keep absolute-path + root membership (scoping), UNC rejection (avoids SMB authentication on a prompt-injected path) and size caps. Drop the reparse-chain walk, link-target resolution, alternate-data-stream rejection and the before/after file snapshot: their adversary is the same OS user, who can read those files directly.
4. **Duplicate JSON key guard** (`src/SqlHarness.Mcp/McpDuplicateJsonFieldGuardInput.cs`, added in `75d43aa`). Frames come from the MCP client's serializer, not from the model. First check `git show 75d43aa` and the request-scope design for a concrete differential (two readers seeing different values for one key). If none exists, remove the stream wrapper and its tests. If one exists, record it in this plan and keep the guard.
5. **Azure CLI argument validator** (`AzureCli.cs:215-234`): its only input is the constant `account get-access-token --resource …`. Remove the metacharacter validator. Keep `cmd.exe` from the System directory, the neutral working directory and `NoDefaultCurrentDirectoryInExePath` (they stop a planted `az.cmd` in a repository).

## Scope

In scope: `SecretRedactor.cs` and its callers' known-secret lists, `McpInputReader.cs`, `McpDuplicateJsonFieldGuardInput.cs` and its host wiring, `AzureCli.cs`, their tests, README Safety contract (Secrets row), docs/mcp.md (input roots, frames).

Out of scope: journal storage policy, error wording (plan 053), MCP budgets and busy gate.

## Steps

Implement items in the order above, one commit each, each with its tests adjusted (delete tests that pin removed behaviour; add a test for item 1 where a token appears in an error and is replaced).

## Verification

- `dotnet build SqlHarness.sln -warnaserror` — 0 warnings.
- Focused: `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName~SecretRedact|FullyQualifiedName~CredentialRedaction|FullyQualifiedName~McpInputReader|FullyQualifiedName~McpStartup|FullyQualifiedName~McpProtocol|FullyQualifiedName~AzureCli"` — pass.
- Final `scripts/verify.ps1` → `verify: OK`; `scripts/verify-linux.ps1` → `verify-linux: OK`.

## Done criteria

- [ ] Azure token is a known secret on every redaction path.
- [ ] Each removed guard has its rationale recorded in the commit message; item 4 records the `75d43aa` finding either way.
- [ ] README Secrets row says: "Known credentials (the profile's password and the Azure token) are replaced in every output. Other text, including SQL quoted in server errors, is shown as is."
- [ ] Both gates pass; `plans/README.md` row updated.

## STOP conditions

- A credential reaches output that is not a known secret (for example a connection string the caller did not supply). Keep a narrow pattern for that source and document it.
