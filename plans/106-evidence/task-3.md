# Plan 106 Task 3 — verification evidence

Worktree: `C:\Users\rgone\.codex\worktrees\39fb\sqlharness`
Branch base: `2c0d9b3679f4cff3613ee529568e2573e4dd1d04`
Scope: MCP argument descriptions and capabilities policy, CLI `--max-cell-chars` help, MCP runtime documentation, and offline description/parity tests. No error-rendering behavior changed in Task 3.

## RED evidence

- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~Cell_limit_argument_describes_error_diagnostic_floor_in_both_modes"` failed as expected: 1 failed, 0 passed. The served `maxCellChars` schema still contained only `Cell clip 0..4096 characters; default 2048.` and omitted the error floor/path distinction.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~Query_help_explains_error_text_cell_limit"` failed as expected: 1 failed, 0 passed. Query help did not document the error text limit.

## GREEN evidence

- The MCP contract regression passed in fixed and request scope: 1/1. It verifies the compact argument range/default description and the detailed redaction, Message/Hint floor, response-byte cap and Location.Path policy in `sqlharness_capabilities`.
- The CLI query help regression passed: 1/1.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests"`: 64 passed, 0 failed, 0 skipped.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "McpTokenBudgetTests|AgentOutputTests"`: 93 passed, 0 failed, 0 skipped.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "McpTokenBudgetTests|AgentOutputTests"`: 51 passed, 0 failed, 0 skipped.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests.Operation_annotation_members_are_gated_by_the_startup_config" --logger "console;verbosity=detailed"`: 1 passed; fixed annotation-enabled tools/list 32,286/32,768 bytes; request-scope annotation-enabled tools/list 32,729/32,768 bytes.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~Cell_limit_argument_describes_error_diagnostic_floor_in_both_modes" --logger "console;verbosity=detailed"`: 1 passed; tools/list was 28,678 bytes fixed and 29,121 bytes request scope (32,768-byte limit).
- `git diff --check`: exit 0 on the final scoped product, plan, index and test diff.

## Budget investigation

The first complete per-argument wording increased the annotation-enabled fixed catalog to 33,516 bytes, above its 32,768-byte ceiling. A shorter argument description still left request scope at 33,199 bytes. The final contract split keeps `maxCellChars` range/default beside the argument and places the shared runtime error policy in the single capabilities description and `docs/mcp/runtime.md`. Final annotation-enabled sizes are 32,286 bytes fixed and 32,729 bytes request scope; no budget ceiling changed.

## Remaining gates

The controller owns the full Windows verification gate and final branch review. Linux, live database acceptance, publication, merge and push were not performed or claimed.

## Review fix: tiny response-budget policy

- Added assertions to the existing served-capabilities contract for preserving the error code and phase and reporting omitted content in `truncation` when the byte budget is tiny.
- RED: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~Cell_limit_argument_describes_error_diagnostic_floor_in_both_modes"` failed 1/1 because the policy lacked `preserve the error code and phase`.
- GREEN: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests"`: 64 passed, 0 failed, 0 skipped.
- Budget check: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests.Operation_annotation_members_are_gated_by_the_startup_config" --logger "console;verbosity=detailed"`: 1 passed; annotations-off tools/list 28,700 bytes, annotation-enabled fixed 32,308/32,768 bytes, request-scoped 32,751/32,768 bytes.
- The description remains below the unchanged tools/list ceiling in both annotated catalog modes. No runtime behavior or budget was changed.

## Branch formatter correction

- `just fix` exited 0 and reported `format: 11 changed C# file(s) (working-tree)` followed by `format: OK`. `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 6d543e509734a374caf882bc8c05ffe5ccaa98b2 -Fix` exited 0 and reported `format: 13 changed C# file(s) (branch)` followed by `format: OK`. Inspection with `git diff --ignore-space-at-eol` found only the capabilities policy and its two new assertions as non-whitespace C# changes; the remaining C# differences are formatter final-newline corrections. The controller owns the full gate and final review.
