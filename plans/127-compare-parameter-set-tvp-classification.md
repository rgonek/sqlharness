# Plan 127: Classify compare SQL with table-valued parameters supplied by parameter sets

> **Executor:** use staged implementation, task review and whole-branch review. This is a plan, not authorization to implement, merge, push, install or access a live database.
>
> **Drift check first:** `git diff --stat 85d1c95e..HEAD -- src/SqlHarness.Core/CompareCellRunner.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/ParameterSetResolver.cs src/SqlHarness.Core/SqlSafety.cs tests`. Reconcile changed interfaces against the excerpts below; do not overwrite later features. Work in an isolated `.worktrees/plan-127` checkout.

## Status and authority

- PENDING. Planned at `85d1c95e`, 2026-11-12. Not approved for implementation.
- Priority P1; effort S; implementation risk LOW; category defect.
- Dependencies: Plan 083 table and list parameters (integrated). No other plan touches the compare fixed-classification path.
- **Spec:** [Plan 083](083-table-and-list-parameters.md) is authoritative for TVP semantics. Its decision 2, "Cases with different lists/TVPs use parameter sets", is the contract this plan restores for `compare`. Preserve existing safety, scope ownership and per-case classification.
- Follow `AGENTS.md`, `plans/README.md`, `docs/agents/verification.md`; MCP changes also read `docs/mcp.md`.

## Why this matters

`compare` rejects a TVP referenced in table position when the TVP is supplied through `parameterSets` or `parameterSetFiles`, the exact form Plan 083 prescribes for comparing different TVP inputs. The same SQL passes when the TVP is a fixed parameter, and `measure` accepts the same parameter sets. An agent then rewrites the application statement into an inline table variable filled inside the measured batch, which changes the measured form.

Synthetic reproduction against the installed server, offline (`dryRun: true`):

| Operation | TVP supplied by | Result |
|---|---|---|
| compare `SELECT r.Id FROM @Ids r` vs `SELECT DISTINCT r.Id FROM @Ids r` | `parameterSets` (`kind: tvp`, `type: dbo.IdList`, `rows: [["1"]]`) | `safety_rejected`: "SQL safety rejection for baseline: UnsupportedStatement. Table-position variables require an earlier inline TABLE declaration…" |
| same compare | fixed `parameters` | `validation: passed` |
| measure `SELECT r.Id FROM @Ids r` | `parameterSets` | `validation: passed` |

## Current state and conventions

- `CompareCellRunner.PrepareFixed` (`CompareCellRunner.cs:42-117`) computes `tableValuedNames` from fixed inputs only (`:73`) and adds parameter-set inputs only when they are lists (`classificationListInputs`, `:76-85`). It then classifies setup, baseline and candidate with those names (`:106-117`) and throws through `EnsureSafe`.
- The caller builds `classificationListInputs` from `operation.ParameterSets`, filtered to `ParameterKind.List` (`SqlHarnessModule.cs:606-623`).
- The per-case loop that follows (`SqlHarnessModule.cs:636-656`) classifies each case with `caseTableNames` from `ParameterSetResolver.ValidateInputs(caseInputs, …)`. It is correct but never reached.
- `ParameterSetResolver.ValidateInputs` returns TVP names normalized with a leading `@` (`ParameterSetResolver.cs:85`); `SafetyInspectionVisitor` normalizes again (`SqlSafety.cs:1496-1498`).
- Measure builds its classification inputs from the sets (`SqlHarnessModule.cs:904`), which is why it passes.
- Check how `parameterSetFiles` reach `operation.ParameterSets` before PrepareFixed; the fix must cover both forms.

## Fix contract

1. The fixed compare classification admits a table-position reference to a TVP declared by any parameter set, exactly as it admits a fixed TVP. Pass the set TVP inputs to `PrepareFixed` alongside the set list inputs and add their names to `tableValuedNames`.
2. The per-case classification stays authoritative and unchanged: each case is still classified with its own inputs, so a name declared by no set or fixed input remains rejected.
3. Existing rules stay unchanged: matching set shapes, fixed-name collisions, source-SQL read-only classification of `rowsFrom`, materialization limits, mutation rejection of non-scalar inputs, and the rejection of locals declared with a named user-defined type.
4. No change to measure, query, watch or snapshot beyond regression coverage proving they still accept their current forms.

## Review Focus

- A set TVP named with and without a leading `@` must both be admitted.
- A table-position variable declared by no set and no fixed input must still be rejected with the existing detail.
- A TVP referenced from `setup` and supplied by a set must classify as it does with a fixed TVP.
- `parameterSetFiles` carrying a TVP must behave like inline `parameterSets`.
- A call mixing a set list and a set TVP must admit both.

## Scope

Source: `src/SqlHarness.Core/CompareCellRunner.cs`, `src/SqlHarness.Core/SqlHarnessModule.cs`.
Tests: `tests/SqlHarness.Tests/Cli/DryRunCommandTests.cs`; new `tests/SqlHarness.Mcp.Tests/McpCompareParameterSetTvpTests.cs`, following `McpDryRunMissingParameterTests.cs`.
Docs: none expected; update `docs/mcp/inputs.md` only if it states the limitation.

### Task 1: Pin the defect and the boundaries

- [ ] Add MCP dry-run tests using a synthetic `dbo.IdList` TVP with inline rows:
  - `Compare_dry_run_accepts_a_set_TVP_in_table_position`: the reproduction above returns `validation: passed` with baseline and candidate `read-only`;
  - `Compare_dry_run_accepts_a_set_TVP_named_with_at_sign`;
  - `Compare_dry_run_accepts_a_set_TVP_referenced_by_setup`;
  - `Compare_dry_run_accepts_a_set_list_and_a_set_TVP_together`;
  - `Compare_dry_run_rejects_an_undeclared_table_position_variable`: SQL referencing `@Other` while the sets declare only `@Ids` is still rejected with "Table-position variables require an earlier inline TABLE declaration";
  - `Compare_dry_run_still_accepts_a_fixed_TVP` and `Measure_dry_run_still_accepts_a_set_TVP`.
- [ ] Add a CLI test in `DryRunCommandTests.cs`, `Compare_dry_run_accepts_a_TVP_supplied_by_a_parameter_set_file`, using a `.sqljson` set file.
- **Verify:** `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpCompareParameterSetTvpTests"` and `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~DryRunCommandTests"`. The accept tests for set TVPs are RED; the reject, fixed and measure tests are GREEN.

### Task 2: Pass set TVP names to the fixed classification

- [ ] Generalize the `PrepareFixed` parameter `classificationListInputs` to `classificationSetInputs` (`IReadOnlyList<SqlHarnessParameterInput>?`) carrying set inputs of kind List and Tvp, deduplicated by normalized name. Keep the list handling unchanged. Union the set TVP names into `tableValuedNames`.
- [ ] In `SqlHarnessModule.cs:606-610`, build that collection from the sets including TVP inputs, for both inline sets and set files.
- **Verify:** Task 1 commands GREEN; then Common verification from `docs/agents/verification.md`.

## STOP conditions

- The drift check shows `PrepareFixed` or the per-case loop restructured: re-derive the fix against the new code before editing.
- Admitting set TVP names would let a name declared by no case pass the per-case classification: stop and report.
- Any change to safety rules outside table-position TVP admission.
