# Plan 106 final fix evidence

Date: 2026-10-23
Branch: `plan-106`
Base: `6d543e509734a374caf882bc8c05ffe5ccaa98b2`
Starting revision: `d78eac9d876adb49b57ab2ec75a25fc1c2385afc`

## Change

At the minimum 4096-byte response budget and `maxCellChars=4096`, both CLI and MCP fallback searches now include the already-redacted optional `Location.Path`. The byte fallback shortens the path when useful and removes it in the final minimum envelope if needed. Existing requested cell clipping remains in force on the normal path. Truncation counts use a union per field, so a path clipped by both cell and byte limits is counted once; error code and phase remain intact.

Capabilities again names `connectionTimeoutSeconds`. The compact description retains redaction order, the 2048-character diagnostic floor and 4096-character cap, response-byte clipping, requested-limit path behavior, and the tiny-budget identity/omission policy. Fixed/request catalog schema tests cover annotations off and on, including the 32768-byte tools/list ceiling.

## Regression evidence

Added CLI and MCP cases using `maxCellChars=4096` and a 4096-byte budget. Each exercises a 4096-character ASCII path and a path containing multibyte characters plus JSON-escaped backslashes. Both assert the original `sql_execution_failed` code and `sql` phase, a valid bounded response, a shortened or omitted path, and exactly one omitted field.

Before the implementation fix, the MCP cases failed with `output_budget_too_small` in place of `sql_execution_failed`. CLI likewise failed to preserve identity and omitted the location object. After the fix and branch formatting, results were:

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentOutputTests"` — exit 0; 95 passed, 0 failed.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~McpTokenBudgetTests|FullyQualifiedName~McpToolSchemaTests"` — exit 0; 117 passed, 0 failed.
- `git diff --check` — exit 0.
- `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 6d543e509734a374caf882bc8c05ffe5ccaa98b2` — exit 0; final output `format: OK` (13 changed C# files checked).

The first direct branch-format invocation reported `FINALNEWLINE` in six changed C# files. `just fix` exited 0 with `format: OK`; the direct branch-format command was rerun afterward and passed. The focused suites above were rerun after that formatter fix.

## Remaining verification

This is scoped implementation evidence, not final plan completion. Independent scoped review and the controller-owned Windows `just verify` remain pending on the committed head. Linux, live database behavior, loaded MCP binary identity, and deployment remain unverified and were outside this fix wave. The controller's untracked `plans/106-verification-evidence.md` draft was left untouched.
