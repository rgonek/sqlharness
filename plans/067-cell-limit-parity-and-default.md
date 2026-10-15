# Plan 067: Per-call cell limit on MCP, shared 2048-character default

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat ed44649..HEAD -- src/SqlHarness.Mcp/McpLimits.cs src/SqlHarness.Mcp/McpResultAdapter.cs src/SqlHarness.Mcp/Tools/McpToolCatalog.cs src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs src/SqlHarness.Mcp/Tools/McpToolHandlers.cs src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Core/Capabilities.cs src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs src/SqlHarness.Cli/Commands/ArtifactCommand.cs src/SqlHarness.Cli/Commands/PlanCommand.cs src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Cli/Infrastructure/OutputContext.cs README.md docs/mcp.md`. Plans 064 (descriptions/capabilities) and 065 (projection clipping) touch neighbouring code; rebase on whichever lands first. Changed files alone are not a STOP condition.

**Goal:** An MCP caller can request the same cell limit as a CLI caller (0..4096 characters), and both surfaces default to 2048 instead of 512.

**Architecture:** One shared constant pair in Core (`AgentOutputLimits.DefaultCellCharacters = 2048`, `MaximumCellCharacters = 4096`) replaces the scattered literals. MCP tools that return projected output gain an optional `maxCellChars` argument that flows into the existing `McpResultBudget.Resolve(..., callMaximumCellCharacters, ...)`. The process ceiling becomes the 4096 contract maximum, so a call can raise as well as lower the limit, like `maxResultBytes`. The byte budget and detail-limit ladder stay the protection against one huge cell.

**Tech Stack:** .NET 10, C#, xUnit, Spectre.Console.Cli, ModelContextProtocol SDK.

**Spec:** [Agent output contract](../docs/superpowers/specs/2026-09-26-agent-output-contract.md) (cell clipping) and [MCP adapter design](../docs/superpowers/specs/2026-09-26-mcp-adapter-design.md):72, which planned an operator-adjustable cell limit up to 4096 that was never exposed. `docs/mcp.md:160` currently states the opposite and changes with this plan.

## Status

- DONE on feature branch `feat/plan-067-cell-limit`, at `3388917`. Windows `just verify` passed. A later branch-scoped Windows format check passed after whitespace-only changes (`pwsh -NoProfile ./scripts/format-changed.ps1 -Mode branch -BaseRef cf0e7c4`, 16 C# files in scope); tests were not rerun for formatting. The operator explicitly waived Linux after its test stage ran for about 15 minutes without completing; Linux tests and formatting remain unverified. No integration, installation or publication performed.
- Planning base: `ed44649`, 2026-10-14.
- Priority: P2. Effort: S–M. Risk: LOW–MEDIUM (tool schema change; larger default output).
- Owner: this plan.

## Evidence

- `McpResultBudget.Resolve` already accepts `callMaximumCellCharacters` (`src/SqlHarness.Mcp/McpResultAdapter.cs:43-58`), but no tool passes it and the ceiling is `McpLimits.DefaultMaximumCellCharacters` (512, `McpLimits.cs:46`), so `Math.Min` could only lower it.
- The CLI already takes `--max-cell-chars` 0..4096 on `query`/`measure`/`compare`/`watch`/`snapshot` (`SqlHarnessCommands.cs:39`), `artifact` (`ArtifactCommand.cs:61`) and `plan` (`PlanCommand.cs:24`).
- Observed sessions: 2026-10-12 (one obstructively clipped query cell) and 2026-10-14 (one catalog cell clipped near three times the limit, one extra `query` call). Both are in the [report observation log](../docs/superpowers/specs/2026-10-07-agent-session-report-draft.md).

## Design decisions

1. **Parity.** Every MCP tool whose CLI counterpart has `--max-cell-chars` gets `maxCellChars` (`query`, `measure`, `compare`, `watch`, `snapshot`, `plan`, `artifact`). `capabilities` does not.
2. **Default 2048.** Covers the observed clipped cells, and four such cells still use only a small part of the 65536-byte default budget. 4096 as a default was rejected: plan pages and wide catalog rows would reach the detail-limit ladder sooner. The default stays a constant, not a config setting.
3. **Raise and lower per call.** The process ceiling is the 4096 contract maximum. No `mcp serve --max-cell-chars` option: per-call control already covers the need, and an operator cap adds a second rule to explain.
4. **Validation like CLI.** Out-of-range values are rejected with the same message on both surfaces (`Cell limit must be 0..4096 characters.`), not clamped silently.
5. **Coordinate with 065.** If 065 lands first, its plan-page tests pin node counts at the old default; re-run them at 2048 and update expectations only where the byte-aware projection legitimately returns fewer nodes per page.

## Global Constraints

- Envelope field `truncation.maxCellChars` keeps reporting the applied value.
- The tools/list catalog stays within its 32768-byte budget (`McpTokenBudgetTests`).
- CLI/MCP parity (plan 060): a surface difference needs an explicit documented reason.

## Review Focus

1. `maxCellChars: 0` returns no cell content but keeps schemas and clip metadata (existing CLI behavior).
2. A value above 4096 or below 0 is a validation error on both surfaces, not a silent clamp.
3. Watch NDJSON progress and snapshot diff outputs apply the requested limit too.
4. An artifact `plan` page at 2048 still advances its cursor correctly (no stalled page when one node's text is long).
5. Catalog size after adding seven parameter descriptions.

---

### Task 1: Shared limits in Core and CLI defaults

**Files:**
- Modify: `src/SqlHarness.Core/AgentOutputProjection.cs:50`, `src/SqlHarness.Core/Capabilities.cs:114`
- Create: `src/SqlHarness.Core/AgentOutputLimits.cs`
- Modify: `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs:39`, `ArtifactCommand.cs:61`, `PlanCommand.cs:24`, `Renderer.cs:107`, `src/SqlHarness.Cli/Infrastructure/OutputContext.cs:20`
- Test: `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs`

**Interfaces:**
- Produces: `public static class AgentOutputLimits { public const int DefaultCellCharacters = 2048; public const int MaximumCellCharacters = 4096; }`

- [ ] **Step 1: Failing tests**

```csharp
    [Fact]
    public void Default_agent_cell_limit_is_2048()
    {
        Assert.Equal(2048, new AgentOutputOptions().MaximumCellCharacters);
        Assert.Equal(AgentOutputLimits.DefaultCellCharacters, new AgentOutputOptions().MaximumCellCharacters);
    }

    [Fact]
    public void Query_cell_of_1500_characters_is_not_clipped_by_default()
    {
        // build a one-row query report with a 1500-char string cell using this file's existing report helpers
        var output = RenderAgentQuery(new string('x', 1500));
        Assert.DoesNotContain("clippedCells", output);
    }
```

(`RenderAgentQuery`: use the helper this file already uses to render a query report through `--output agent`; if none exists, build it from the existing query-report fixture in the same file.)

- [ ] **Step 2: Run** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~AgentOutputTests"` — new tests FAIL.
- [ ] **Step 3: Implement** `AgentOutputLimits.cs`; set `AgentOutputOptions(int MaximumBytes = 65_536, int MaximumCellCharacters = AgentOutputLimits.DefaultCellCharacters)`; replace the literal `512` defaults in the CLI files and `Renderer.cs:107` fallback with the constant, and `4096` with `AgentOutputLimits.MaximumCellCharacters`; `Capabilities.cs:114` → `new { min = 0, max = AgentOutputLimits.MaximumCellCharacters, defaultValue = AgentOutputLimits.DefaultCellCharacters }`. Do **not** change `StatementMetrics.MaximumTextCharacters` (journal text bound, unrelated).
- [ ] **Step 4: Run** the full `tests/SqlHarness.Tests` project. Update golden outputs whose only change is the default cell limit (`maxCellChars` 512 → 2048 in truncation objects, longer retained cells); inspect each diff, do not regenerate blindly.
- [ ] **Step 5: Commit** `git commit -m "Agent output: shared cell-limit constants, default 2048"`

### Task 2: MCP `maxCellChars`

**Files:**
- Modify: `src/SqlHarness.Mcp/McpLimits.cs:46-49`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (`RunDbAsync` at `:351-360`, every `ThrowIfUnknown` list, `plan`/`artifact` paths), `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs`, `src/SqlHarness.Mcp/Tools/McpToolHandlers.cs`
- Test: `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs`, `McpToolSchemaTests.cs`, `McpTokenBudgetTests.cs`

**Interfaces:**
- Consumes: `AgentOutputLimits` from Task 1; existing `McpResultBudget.Resolve(int processCeilingBytes, int? callMaximumBytes, int? callMaximumCellCharacters, int processMaximumCellCharacters)`.
- Produces: optional tool argument `maxCellChars` (integer, 0..4096) with description `"Cell clip 0..4096 characters; default 2048."`.

- [ ] **Step 1: Failing tests** in `McpOutputTests.cs`, using the file's existing server/fixture helpers:

```csharp
    [Theory]
    [InlineData(null, 2048)]
    [InlineData(4096, 4096)]
    [InlineData(100, 100)]
    public async Task Query_applies_requested_cell_limit(int? requested, int expectedApplied)
    {
        var result = await CallQueryReturningOneCellAsync(new string('x', 5000), maxCellChars: requested);
        Assert.Equal(expectedApplied, ReadTruncation(result).GetProperty("maxCellChars").GetInt32());
        Assert.Equal(expectedApplied, ReadFirstCell(result).Length);
    }

    [Fact]
    public async Task Cell_limit_above_contract_maximum_is_rejected()
    {
        var result = await CallQueryReturningOneCellAsync("x", maxCellChars: 4097);
        Assert.True(result.IsError);
        Assert.Contains("Cell limit must be 0..4096 characters.", ReadErrorMessage(result));
    }
```

Add a schema test asserting `maxCellChars` exists on the seven tools and not on `sqlharness_capabilities`.

- [ ] **Step 2: Run** `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpToolSchemaTests"` — FAIL.
- [ ] **Step 3: Implement**
  - `McpLimits`: `DefaultMaximumCellCharacters = AgentOutputLimits.DefaultCellCharacters`, `MaxCellCharactersLimit = AgentOutputLimits.MaximumCellCharacters`.
  - `RunDbAsync` (and the target-free `plan`/`capabilities`-less paths): add `int? maxCellChars`, call `McpResultBudget.Resolve(scope.MaxResultBytes, maxResultBytes, maxCellChars, McpLimits.MaxCellCharactersLimit)`. When `maxCellChars` is null, `Resolve` must use the default, not the ceiling: change its fallback line to `callMaximumCellCharacters ?? McpLimits.DefaultMaximumCellCharacters` and keep `Math.Min` against the ceiling.
  - Add `"maxCellChars"` to each relevant `ThrowIfUnknown` list and the parameter to both handler classes with the description above.
- [ ] **Step 4: Run** the whole `tests/SqlHarness.Mcp.Tests` project, including `McpTokenBudgetTests` (catalog ≤ 32768 bytes). If the catalog exceeds budget, shorten the description; do not drop the argument.
- [ ] **Step 5: Commit** `git commit -m "MCP: per-call maxCellChars with CLI parity"`

### Task 3: Documentation and gates

- [x] MCP runtime/tools docs describe the 2048 default, `maxCellChars` 0..4096, and applied `truncation.maxCellChars`; the former rejection-note wording is absent from the reorganized docs.
- [x] `README.md` and the agent-output contract spec describe the shared 2048 default and MCP `maxCellChars`; removed the obsolete accepted-follow-up note.
- [x] `rg -n "512" skills` found no matches, so no skill changes were needed.
- [x] Windows gate: `just verify -LogDirectory D:/temp/sqlharness-plan067-verify` passed at `3388917`; Core/CLI 3513 passed, 11 skipped; MCP 381 passed, 4 skipped. All six stages completed in 7m04s; format was skipped because no C# files changed.
- [x] Linux gate: `just verify-linux -LogDirectory D:/temp/sqlharness-plan067-linux` synchronized `3388917` and passed sync, SDK, Node, UI install, UI check, restore, and build. The Core/CLI test stage ran for about 15 minutes with an active testhost but produced no result or `test.log`. The operator waived Linux; the gate was interrupted through its own process session. Linux tests and format are not verified.

### Completion evidence

- Implemented on feature branch `feat/plan-067-cell-limit`; no integration, installation or publication performed.
- Windows gate passed on `3388917`: Core/CLI 3513 passed, 11 skipped; MCP 381 passed, 4 skipped; 0 failed.
- Linux gate reached build on `3388917`; test stage was waived and interrupted by operator direction after about 15 minutes without a result. No Linux test or format result is claimed.
- Task 3 report: [gate results, test counts and documentation checks](../.superpowers/sdd/067-cell-limit-parity-and-default/task-3-report.md).

## Acceptance

- Same request (`maxCellChars` / `--max-cell-chars` N) yields the same applied limit on CLI and MCP for 0, 2048 and 4096; 4097 is rejected on both.
- Default applied limit is 2048 on both surfaces; capabilities report it.
- Catalog budget test passes.

## STOP conditions

- Plan 065 or 064 restructured the projection or budget resolution so that the per-call limit no longer flows through `McpResultBudget.Resolve`: re-plan Task 2 against the new seam.
- A client-facing contract test pins 512 as a protocol guarantee (not merely a default): record it and ask the operator.
