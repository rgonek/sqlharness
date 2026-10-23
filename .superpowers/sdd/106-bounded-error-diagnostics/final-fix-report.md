# Plan 106 final fix report

## Scope

Resolved whole-branch review Important #1 in the CLI and MCP error byte-budget fallbacks. Optional, redacted `Location.Path` values now shrink or are omitted when necessary to preserve the original error code and phase at the minimum 4096-byte budget. The omission count unions cell clipping and byte fallback per field. Normal path handling still caps Location.Path at the requested cell limit.

Resolved the catalog description Minor by restoring `connectionTimeoutSeconds` while preserving the documented floor, caps, requested path limit, redaction order and tiny-budget behavior. The same description is checked in fixed and request catalogs; catalog tests cover annotation-off/on tools/list under 32768 bytes.

## RED and GREEN

The new minimum-budget CLI/MCP regressions use `maxCellChars=4096`, long ASCII paths, and multibyte paths containing JSON-escaped backslashes. Before the fix, MCP returned `output_budget_too_small` instead of `sql_execution_failed`; CLI dropped the location object and did not preserve the required identity. After the fix:

- CLI `AgentOutputTests`: 95 passed, 0 failed.
- MCP `McpTokenBudgetTests` and `McpToolSchemaTests`: 117 passed, 0 failed.
- Schema tests verified the restored key and all retained policy wording in fixed/request catalogs, annotation off/on, with the 32768-byte ceiling.
- `git diff --check`: exit 0.

Exact final test commands:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentOutputTests"
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~McpTokenBudgetTests|FullyQualifiedName~McpToolSchemaTests"
```

## Branch formatting

The first direct command reported `FINALNEWLINE` in six changed C# files and exited 1:

```powershell
pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 6d543e509734a374caf882bc8c05ffe5ccaa98b2
```

`just fix` exited 0 (`format: OK`). The direct branch command was rerun after the fix and exited 0 (`format: OK`, 13 changed C# files checked). Both focused test commands above were rerun after formatting and passed.

## Status and limits

The controller owns final Windows `just verify` and independent scoped review on the committed head; neither was run in this fix wave. Linux, live database behavior, loaded MCP binary identity and deployment are unverified. The untracked controller draft `plans/106-verification-evidence.md` was not changed.
