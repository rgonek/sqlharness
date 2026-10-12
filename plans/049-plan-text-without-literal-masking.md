# Plan 049: MCP plan text returns the caller's plan as is, within size bounds

> Read current source before editing. Inspect drift since `cadde67` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition.

## Status

- Status: DONE (merged locally to `main` at `b0718d6`; not pushed)
- Priority: P2
- Effort: S
- Risk: LOW
- Depends on: plan 048 reverted (`cadde67`)
- Planned at: `cadde67`, 2026-10-11

## Why this matters

`sqlharness_plan` is offline. Its input is a showplan file or stdin the caller supplied, usually the `.sqlplan` written by the caller's own `measure`. Masking literals in that text hides nothing from the caller, who already holds the file. It costs a regex policy, a fail-closed timeout path, docs caveats and a growing list of plan shapes (plan 048 added six and was reverted for this reason).

The real secret boundary in SQLHarness is the resolved connection: passwords and tokens that come from environment variables or the Azure CLI and that the caller never sees. That boundary lives in Core `SecretRedactor` with known secrets and is unchanged here.

## Current state

- `src/SqlHarness.Mcp/McpOperationMapper.cs`, `McpPlanTextPolicy.Text`: with `includeText=true` runs `CredentialLiteralPattern` (masks a literal compared with a password-like column) and then `SecretRedactor.Redact(value, [])` (credential-word patterns, no known secrets).
- `includeText=false` drops statement text and predicates. This mode is a useful budget knob and stays.
- Length bounds come from `AgentOutputProjection` and the MCP wire budget.
- Docs: `docs/mcp.md` (`sqlharness_plan` row), `AGENTS.md` (MCP tool list) say text is kept "after credential-pattern filtering only (not complete sanitization)".

## Scope

In scope: `McpPlanTextPolicy` in `src/SqlHarness.Mcp/McpOperationMapper.cs`, its tests in `tests/SqlHarness.Mcp.Tests` (`McpMappingTests.cs`, `McpToolSchemaTests.cs`, `McpSecretRedactionTests.cs`), `docs/mcp.md`, `AGENTS.md`, the `includeText` schema description in `Tools/McpToolCatalog.cs`.

Out of scope: Core `SecretRedactor`, redaction of errors and journal text, the CLI `plan` command, artifact sections.

## Steps

1. Delete `CredentialLiteralPattern` and the `SecretRedactor.Redact(value, [])` pass from `McpPlanTextPolicy.Text`. With `includeText=true` the text passes through unchanged; with `false` it is `null`.
2. Delete the tests that pin literal masking in plan text (credential column, timeout fail-closed, bare password branch). Keep tests that pin `includeText=false` and the size bounds. Add one test: a plan whose predicate is `[Password]=N'x'` returns that text unchanged with `includeText=true`.
3. Update the `includeText` description, `docs/mcp.md` and the AGENTS.md MCP line to: "`includeText` (default `true`) returns bounded statement text and predicates; `false` returns metadata only."

## Verification

- `dotnet build SqlHarness.sln -warnaserror` — 0 warnings.
- `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName~McpMapping|FullyQualifiedName~McpToolSchema|FullyQualifiedName~McpSecretRedaction|FullyQualifiedName~McpTokenBudget"` — pass.
- Final `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`; `pwsh -NoProfile -File scripts/verify-linux.ps1` → `verify-linux: OK`.

## Done criteria

- [x] `rg -n "CredentialLiteralPattern" src tests` → no matches.
- [x] Core `SecretRedactor` and its tests unchanged.
- [x] Docs and tool schema describe bounded text and metadata-only mode.
- [x] Both gates pass; `plans/README.md` row updated.

## Implementation and verification evidence

- Feature commits: `932d4ad` (plan-text passthrough), `c12aeb4` (AgentWorkflow byte-budget observation), `58fab51` (EditorConfig final-newline formatting).
- Feature branch gates on `58fab51`: Windows `verify: OK` (Core 3722 passed; MCP 383 passed, 4 skipped); Linux `verify-linux: OK` (Core 3722 passed; MCP 384 passed, 3 skipped).
- Post-merge Windows `pwsh -NoProfile -File scripts/verify.ps1` on main `b0718d6`: exit 0, `verify: OK`; Core 3722 passed; MCP 383 passed, 4 skipped.
- Post-merge Linux `pwsh -NoProfile -File scripts/verify-linux.ps1` on main `b0718d6`: exit 0, `verify-linux: OK`; Core 3722 passed; MCP 384 passed, 3 skipped; run from WSL ext4. Logs: `.worktrees/plan-049/.superpowers/sdd/049-plan-text-without-literal-masking/main-windows.log` and `main-linux.log`.
- Task-level Luna xhigh review, whole-branch Sol low review, and the focused fixture and formatting reviews were clean.
- No live database checks were run. The merge is local and has not been pushed.
- The MCP tool-list line formerly mentioned in the planned `AGENTS.md` edit had already been removed; the current agent contract already says caller-supplied plan text remains readable, so `AGENTS.md` was left unchanged.

## STOP conditions

- A test shows plan text can carry a value the caller did not supply (for example a secret from the resolved target). Report the path; that value belongs to the Core known-secret redactor.
