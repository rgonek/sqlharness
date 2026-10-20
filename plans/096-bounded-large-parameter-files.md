# Plan 096: Make files a practical bounded path for large typed parameters

> **Executor:** use staged implementation, task review and whole-branch review. This is a plan, not authorization to implement, merge, push, run paid agent trials or access a live database.
>
> **Drift check first:** `git diff --stat 32adaa7..HEAD -- src/SqlHarness.Core/ParameterInputFileReader.cs src/SqlHarness.Core/ParameterSetFileReader.cs src/SqlHarness.Core/ParameterSetResolver.cs src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs src/SqlHarness.Mcp/McpInputReader.cs src/SqlHarness.Mcp/McpOperationMapper.cs`. Reconcile changed interfaces against the excerpts below; do not overwrite later features. Work in an isolated `.worktrees/plan-096` checkout.

## Status and authority

- TODO — plan only. Planned at `32adaa7`, 2026-10-20.
- Priority P2; effort M; implementation risk MED; category perf.
- Dependencies: 083 TVP/list implementation is merged. Coordinate CLI/parser edits with 097; no dependency on output-budget plans.
- **Spec:** the Fix contract in this file is authoritative for this change. Preserve existing safety, scope ownership, persistent-mutation classification and approval.
- Follow `AGENTS.md`, `plans/README.md`, `docs/agents/verification.md`; MCP changes also read `docs/mcp.md`.
- Optimize correct completed tasks, including discovery, requests, responses, recovery calls and available provider usage. UTF-8 bytes and bytes/4 estimates are not measured model tokens.

## Why this matters

A local parameter file should avoid putting large caller-owned data into a model prompt. Both parameter readers currently reject files above 64 KiB even though TVP/list materialization admits substantially larger inputs. Splitting a TVP into several SQL calls can change query semantics, so response batching is not a substitute for a usable file path.

## Current state and conventions

- `ParameterSetFileReader.cs:11`: `public const int MaximumBytes = 65_536;`. Its bounded read uses one extra byte and its `Parse` method revalidates the same limit.
- `ParameterInputFileReader.cs:17-18` checks stream length against that constant before reading fixed typed parameters.
- `ParameterSetResolver.cs:19-22` separately bounds non-scalar parameter count to 8, materialized rows/values to 100000, materialized bytes to 32 MiB and command parameters to 2100.
- CLI `Commands/SqlHarnessCommands.cs` reads fixed files around line 91 and set files around line 362. MCP uses `McpInputReader` inside explicitly allowed input roots. The set parser wraps typed values and delegates to `ParameterInputFileReader.Parse`; do not charge those internal copies as additional external input files.
- Read `docs/mcp/inputs.md` before changing admission. Preserve UTF-8/BOM/comment/trailing-comma compatibility, duplicate/unknown member validation and path/root/symlink policy.

## Fix contract

1. Introduce shared Core parameter-file ingress limits: **16 MiB per external file** and **32 MiB total external parameter-file bytes per logical call** (CLI fixed file plus all selected set files; existing MCP parameter-set files). Report exact byte units in capabilities. These are bounded raw ingress limits; the existing 32 MiB decoded/materialized limit is separate and unchanged. MCP currently has no fixed-parameter-file argument: do not add one in this plan.
2. The per-file 16 MiB matches existing SQL/plan file ingress scale, rather than allowing unlimited JSON. Do not make the limit a model-controlled switch or add a broad new configuration subsystem.
3. Charge each caller-supplied external file occurrence once, in input order, including repeated paths. Reject an over-limit total before SQL connection/setup/materialization. Do not reset the allowance per parameter set or Core retry. Internal parser wrappers do not consume the ingress allowance.
4. Read with a hard byte bound plus one sentinel byte; do not trust only the initial stream length. Preserve cancellation and detect a file growing beyond the allowance. Avoid preallocating 16 MiB for every tiny file.
5. Use one budget/accounting object shared by CLI and MCP mapping for a call. Existing public reader callers may use an overload with a fresh one-file budget; command paths with multiple files must pass the shared budget. Parser-only byte validation must not double-charge already admitted bytes.
6. Keep the 1 MiB MCP inline-argument cap, input-root enforcement, maximum file count, parser depth, all typed-value/schema validation, materialization bounds, persistent-mutation rejection and SQL parameter limits unchanged.
7. Preserve scalar parameter values, labels and caller paths as allowed by current readable-output/redaction policy; never reproduce credentials in new errors. Errors report which raw byte limit was exceeded and do not tell the agent to inline more data.
8. Amend the prior 64 KiB parameter-file contract explicitly in served descriptions, capabilities and docs. Do not rewrite old verification evidence as though it tested the larger bound.

## Scope

Source: create `src/SqlHarness.Core/ParameterFileIngressBudget.cs`; change `ParameterInputFileReader.cs`, `ParameterSetFileReader.cs`, `Capabilities.cs`, CLI `Commands/SqlHarnessCommands.cs`, MCP `McpLimits.cs`, `McpInputReader.cs`, `McpOperationMapper.cs` and served parameter-file descriptions in `Tools/McpToolCatalog.cs`.
Tests: `tests/SqlHarness.Tests/ParameterSetFileReaderTests.cs`; create `tests/SqlHarness.Tests/ParameterInputFileReaderTests.cs` if needed; `tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs`; `tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs`, `McpToolSchemaTests.cs`, `McpTokenBudgetTests.cs`, and a focused new `McpParameterFileBudgetTests.cs` for full-path accounting if needed.
Docs: `docs/mcp/inputs.md`, `docs/mcp/tools.md`, `README.md`. Do not change target selection, unrestricted path access, output budgets, case counts or database write policy.

### Task 1: Prove the old file cap and freeze new boundaries

- [ ] Add an offline typed TVP/list input fixture above 64 KiB but below 16 MiB; assert CLI fixed-file/set-file paths and the existing MCP set-file path admit it with equivalent typed values to inline parsing. Do not invent a new MCP fixed-file argument. Use fake session factories; no DB calls.
- [ ] Test exactly the new limit and limit+1, a growing stream, cancellation, invalid UTF-8/JSON, duplicate fields, and unauthorized MCP paths.
- [ ] Test combined files exceeding 32 MiB although each is valid; require rejection before a fake DB session opens. Assert fixed+sets share one allowance and nested parser wrappers are not charged twice.
- **Verify:** `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~ParameterSetFileReaderTests|FullyQualifiedName~ParameterInputFileReaderTests|FullyQualifiedName~MeasureParameterSetCommandTests"` and `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpInputReaderTests|FullyQualifiedName~McpParameterFileBudgetTests"` produce intended RED. Confirm at least one new accounting regression is selected, not merely existing passing tests.

### Task 2: Centralize admission and wire both command paths

- [ ] Implement the budget and bounded reader once, then wire all CLI/MCP parameter-file paths. Keep pure parse semantics distinct from accounting.
- [ ] Do not allocate unbounded file lists or preload all files just to discover the aggregate limit. Ensure exact admitted byte counts and deterministic first error.
- [ ] Test an admitted file whose decoded typed data later exceeds existing materialization limits; it must still fail before database execution.
- **Verify:** Task 1 tests GREEN; `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~ParameterSetResolverTests` GREEN; affected MCP project tests GREEN.

### Task 3: Publish the separate ingress and execution bounds

- [ ] Update all 64 KiB file claims on affected parameter surfaces, not unrelated SQL or frame limits. Check tool catalog still fits 32768 bytes.
- [ ] Record a >64 KiB successful offline file workflow with no data echoed into output, raw byte counts, and that larger raw file admission is not larger allowed SQL materialization.
- **Verify:** `rg -n "64 KiB|64KiB|65536|65_536" src/SqlHarness.Core/ParameterInputFileReader.cs src/SqlHarness.Core/ParameterSetFileReader.cs src/SqlHarness.Mcp/McpLimits.cs docs/mcp/inputs.md` reviewed for remaining legitimate unrelated constants (exit 1 is normal if none remain); run Common verification.

## Maintenance notes

Raw JSON bytes, decoded parameter memory and DB command parameter counts measure different resources. Keep separate limits and error names. Any future streaming materializer must preserve aggregate call admission and pre-execution failure, not silently authorize larger SQL payloads.

## Common verification and delivery

Before changing source in the isolated implementation checkout, record `$plan096Base = git rev-parse HEAD` and persist that exact SHA in evidence. On resume, reload the saved SHA rather than redefining it after commits. This implementation base can be newer than the planning drift-check base when predecessors have landed.

Run focused tests during iteration; retain the regression's meaningful RED and GREEN. Run affected suites once when ready, then scoped task review and independent whole-branch review. No live database is needed for acceptance.

Before the final gate, normalize only this implementation branch's changed C# files with `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan096Base -Fix`. Commit code, then run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan096Base` and retain its direct exit code and log. Run `just verify -LogDirectory D:/temp/sqlharness-plan096-verify`; expected exit 0 on the final product tree. Inspect failed stage logs before fixing or rerunning. Do not run Linux verification unless requested.

Record the tested revision/tree, commands, actual results, output bytes, call counts and evidence limits in `plans/096-verification-evidence.md`. Update this plan, `plans/README.md` and `plans/history.md` on completion. Use `fix(096): ...`, `feat(096): ...` and `docs(096): ...` commits as appropriate; integration/publication needs separate authorization.

## STOP conditions

Report a concrete design blocker if the live interfaces invalidate the Fix contract, a required safety/ownership invariant cannot be preserved, or implementation needs unrelated source changes. Ordinary drift, expected regression failures and already authorized task-local fixes are not reasons to restart the design or broaden scope.

## Done criteria

- [ ] All task-specific regressions below pass and each task has spec/quality review.
- [ ] Served schemas, descriptions and capabilities agree with runtime behavior; affected human docs are updated without duplicating interface contracts into skills.
- [ ] Direct branch formatting and final Windows `just verify` exit 0; final whole-branch review is approved.
- [ ] Evidence distinguishes deterministic fixture measurements from actual-agent or live-database results.
- [ ] Changes stay within the listed source/test surfaces plus this plan, its evidence, plan index/history and explicitly listed docs.
