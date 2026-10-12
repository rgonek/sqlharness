# Plan 054: Redaction is exact known-secret replacement; MCP input guards match a same-user threat model

> Read current source before editing. Inspect drift since `bab7618` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition. Line numbers come from the 2026-10-11 rule review; confirm them.

## Status

- Status: DONE (locally merged to main; not pushed)
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
4. **Duplicate JSON key guard** (`src/SqlHarness.Mcp/McpDuplicateJsonFieldGuardInput.cs`, added in `75d43aa`). Keep it: the pinned `ModelContextProtocol.Core` 2.2.0 source (repository commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`) has two readers with different duplicate-`id` behavior. `JsonRpcMessage.Converter.Read` assigns every exact top-level `id` occurrence, so the last wins; `StreamServerTransport.TryRecoverRequestId` returns immediately at the first exact `id`. In `{"jsonrpc":"2.0","id":41,"id":42,"jsonrpc":"1.0","method":"tools/call","params":{}}`, the converter assigns ID 42 then throws on the later invalid version; the transport's parse-error recovery instead returns ID 41. The guard rejects repeated properties before either SDK reader and drops an ambiguous ID. `McpProtocolTests.Duplicate_json_rpc_id_is_rejected_as_ambiguous_before_sdk_parsing` pins the SDK converter's last-value behavior and confirms the guard emits a content-free replacement with `id: null`.
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

- [x] Azure token is a known secret on every redaction path.
- [x] Each removed guard has its rationale recorded in the commit message; item 4 records the `75d43aa` finding either way.
- [x] README Secrets row says: "Known credentials (the profile's password and the Azure token) are replaced in every output. Other text, including SQL quoted in server errors, is shown as is."
- [x] Both gates pass; `plans/README.md` row updated.

## STOP conditions

- A credential reaches output that is not a known secret (for example a connection string the caller did not supply). Keep a narrow pattern for that source and document it.

## Completion evidence — 2026-10-12

Implemented in order with one commit per item: `0ec4e4e`, `e0b7cc0`, `4025483`, `68d8da2`, `ee5e64e`. Final review fixes: `3a5f1f9`; final-newline normalization: `187fcbb`.

- Windows at `187fcbb`: `pwsh ./scripts/verify.ps1` -> `verify: OK`; Core 3783 passed, MCP 381 passed / 4 skipped; build 0 warnings/errors.
- Linux at `187fcbb`: `pwsh ./scripts/verify-linux.ps1` -> `verify-linux: OK` on WSL ext4; Core 3783 passed, MCP 382 passed / 3 skipped.
- Task reviews: Luna xhigh; whole-branch review and scoped final re-review: Sol low. No production Critical/Important findings remain.
- The first Windows run exposed six stale heuristic-redaction tests and an observed capabilities byte-count fixture; corrected without increasing the 8192-byte budget. A later format-only gate failure was corrected, then both complete gates passed.
- No live database calls, merge, or push. The existing duplicate JSON guard remains for the concrete SDK differential documented above.

### Controller decisions

- Ruling: The five numbered Items were treated as tasks and equivalent briefs generated directly because the extraction helper requires absent Task headings. Cost if wrong: task grouping rework only.
- Ruling: Defer direct malformed-frame first-ID recovery test coverage after the single final fix wave. The pinned SDK differential was independently verified and the guard remains; valid duplicate-ID rejection and session-recovery tests pass. Cost if wrong: SDK fallback behavior could drift without that focused regression; duplicate keys are still rejected before SDK parsing.
## Post-merge verification — 2026-10-12

- Local merge to `main`: `6ff68d3` (parents `27d2a9a` Plan 053 and `f17c1ed` Plan 054). Conflicts resolved preserving both plans; capabilities output is 7525 UTF-8 bytes within unchanged 8192-byte budget.
- README journal-contract omission corrected in `f5aef96`. Focused integration review and scoped re-review approved.
- At `f5aef96`: Windows `pwsh ./scripts/verify.ps1` -> `verify: OK`; Core 3787 passed, MCP 389 passed / 4 skipped.
- At `f5aef96`: WSL ext4 `pwsh ./scripts/verify-linux.ps1` -> `verify-linux: OK`; Core 3787 passed, MCP 390 passed / 3 skipped.
- Local integration only; no push and no live database execution.
