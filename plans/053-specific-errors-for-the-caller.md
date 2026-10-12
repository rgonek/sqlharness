# Plan 053: Errors name what went wrong when the reader already holds the data

> Read current source before editing. Inspect drift since `bab7618` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition. Line numbers come from the 2026-10-11 rule review; confirm them.

## Status

- Status: DONE — locally merged to main at `5a1eb2a`; see [053 verification](053-verification.md)
- Priority: P2
- Effort: M
- Risk: LOW
- Depends on: —
- Planned at: `bab7618`, 2026-10-11

## Why this matters

Several messages hide values from the reader who supplied them or who wrote the configuration. That is the same mistake plans 025/043 removed from redaction: it protects nothing and makes failures undiagnosable. Credentials stay protected by exact known-secret replacement (`SecretRedactor` with the resolved password and token), which this plan keeps.

## Baseline when planned

1. **Parameter errors** — `SqlHarnessSafetyException.Diagnostic` / `PreservedTokens` (`SqlSafety.cs:14-58`) and `SecretRedactor.RedactPreserving` with private-use placeholders (`SecretRedactor.cs:11-12, 60-95`) keep a parameter's name and type but never its value, although the caller typed the value.
2. **Parameter-set files** — `ParameterSetFileReader` (`:13, 40-44`) returns one opaque "The parameter set file is invalid." for every failure and drops the path. It also rejects a UTF-8 BOM, comments and trailing commas (`:18-22, 119-120`); Windows PowerShell 5 `-Encoding UTF8` writes a BOM, while SQL input already strips one (`McpInputReader.cs:246`).
3. **Startup and profile errors** — `McpHost.cs:107-112` reports "invalid MCP startup configuration." for everything; `McpScope.cs` (≈ `:8-11, 110, 141, 224`) is similarly generic; `TargetResolver.cs:65-67` says "Missing required profile vars." without names, and `:120-123` swallows the auth-spec reason. These go to the operator's terminal or stderr; configuration holds env-var *names*, not secrets.
4. **MCP stderr logger** — `McpHost.cs:227-250` writes only category, level and event id; message and exception text are dropped, so transport failures cannot be diagnosed. SQL and arguments are already in the client transcript.
5. **Journal error text** — `ActivityJournal.cs:94-95, 143` store a failed operation's message only with `journal.storeSensitive: true`, although the message is already credential-redacted and the same text sits in the agent's transcript.

## Scope

In scope: the files above, their tests, README (Safety contract, Measure parameter sets, Activity journal), docs/mcp.md (logging paragraph), `ContractsTests` pins on "BOM", "comments", "trailing commas".

Out of scope: exact known-secret redaction; SQL text and plan storage in the journal (stay behind `storeSensitive`); MCP stdout protocol purity.

## Steps

1. Parameter errors: remove `RedactPreserving`, the placeholder mechanism and the `Diagnostic`/`PreservedTokens` split. Messages may quote the parameter name, type and the offending value; they still pass through `SecretRedactor.Redact` with known secrets.
2. Parameter-set files: accept a UTF-8 BOM; enable `JsonCommentHandling.Skip` and `AllowTrailingCommas`. Keep the 64 KiB limit, the `name`/`parameters`-only shape, unknown-key rejection and the label rule. Each failure names the path and the reason (size, JSON position, unknown key, invalid entry with its parameter name).
3. Startup/profile errors: name the profile, the missing variables, the rejected input root and the auth-spec reason.
4. MCP stderr: render message and exception through `SecretRedactor.Redact` with the resolved known secrets. Stdout stays protocol-only.
5. Journal: store the redacted error message by default; keep SQL text and plans behind `storeSensitive`. Update the dashboard copy that says the message needs the flag.
6. Docs and pins: update README/mcp.md wording; replace the BOM/comments/trailing-commas pins with the new accepted forms.

## Verification

- Windows `pwsh ./scripts/verify.ps1` at `5a1eb2a`: exit 0, `verify: OK`, UI 148, Core 3,829, MCP 393 passed / 4 skipped, 0 warnings/errors, format passed, uppercase `FAILED` 0.
- Linux `pwsh ./scripts/verify-linux.ps1` at `5a1eb2a`: exit 0, `verify-linux: OK`, UI 148, Core 3,829, MCP 394 passed / 3 skipped, 0 warnings/errors, format passed, uppercase `FAILED` 0.
- The [verification record](053-verification.md) contains commit history, reviews, rulings, and limits. No live database proof was run; no push was performed.

## Done criteria

- [x] A test per item shows the specific message; known secrets remain redacted.
- [x] A BOM-prefixed, commented parameter-set file with a trailing comma loads.
- [x] `rg "RedactPreserving|PreservedTokens" src` → no matches.
- [x] Both gates pass; `plans/README.md` row updated.

## STOP conditions

- A message path can carry a credential that is not in the known-secret list (for example a token from a source the redactor is not given). Add it to the known secrets (plan 054) before un-hiding that path.
