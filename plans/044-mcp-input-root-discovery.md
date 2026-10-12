# Plan 044: Show the configured file-input boundary to the MCP caller

> Read current source before editing. Inspect drift since `729d88f` in the scope below and adapt implementation details within this authorized intent. A changed file alone is not a STOP condition. Escalate an incompatible contract or missing required proof. This file plans future implementation; writing it runs no SQL.

## Status

- Status: DONE (8a088fb, 2026-10-11)
- Priority: P2
- Effort: S
- Risk: LOW
- Depends on: none
- Planned at: `729d88f`, 2026-10-11

## Why this matters

The file allowlist is useful, but hiding its directories forces agents to read client configuration or guess a usable path. Capabilities and the error should describe the same boundary the server actually enforces. This plan follows completed 038/W4; it does not reopen that completed task.

## Current state

- McpOperationMapper.LocalDiagnostics reports inputRootCount and existence flags, never paths.
- McpInputReader.OutsideRootsException reports only the number of roots.
- McpScope.InputRoots and McpProcessContext.InputRoots already contain normalized frozen directories.
- Request-mode capabilities already disclose allowlisted profiles; its old 'never profile lists' description is stale.

## Scope

src/SqlHarness.Mcp/McpOperationMapper.cs; McpInputReader.cs; Tools/McpToolCatalog.cs; Tools/McpRequestToolHandlers.cs; schema/projection declarations directly needed by capabilities; tests/SqlHarness.Mcp.Tests/McpMappingTests.cs, McpInputReaderTests.cs, McpToolSchemaTests.cs, McpTokenBudgetTests.cs; docs/mcp.md; README.md; plans/038-agent-session-friction-fixes.md only a follow-up reference.

No dashboard API, user configuration edits, live production queries, commit, merge, installation or push is authorized by this plan. Match existing xUnit and fake-provider conventions; use synthetic fixtures, not operational SQL/data. Persistent-mutation controls, target locking, timeout and byte limits retain their separate purpose.

## Steps and acceptance

1. Add inputRoots, an array of normalized configured directory strings, to MCP capabilities for both modes. Empty means file inputs disabled. Do not disclose unrelated filesystem paths, profiles outside the allowlist, environment values or dashboard token files.
2. Return a bounded root inventory with omitted-root count if necessary. Ensure callers can obtain a complete inventory using bounded paging when the configured inventory cannot fit; do not claim a truncated list is complete. Reuse capability arguments includeDiagnostics plus a root cursor/page limit if needed, rather than creating another tool. Base catalog/schema tests on the actual tool list after the active model-prices plan removes gain; do not restore a historical tool count.
3. Outside-root errors point to capabilities and include actionable configured-root information when it fits. Relative/malformed/unreadable input errors name the reason and the relevant caller-supplied path after credential filtering. Keep stable error codes.
4. Test frozen-root discovery, disabled inputs, multiple roots, long paths and wire budgets. Ensure the disclosed roots are exactly the enforced roots. Traversal, UNC/ADS, outside-root junctions and changes during read keep their rejection.
5. Replace contradictory 'never paths/profile lists' tool descriptions and docs. Do not auto-read client config, auto-create directories or let roots/list widen the allowlist.

After each logical implementation change, run the relevant subset of the focused test command below. Build first when needed. All selected tests must pass; a skipped required provider scenario is not proof.

## Verification

- Build: `dotnet build SqlHarness.sln -warnaserror` — exit 0, no warnings/errors.
- Focused: `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName~McpMapping|FullyQualifiedName~McpInputReader|FullyQualifiedName~McpToolSchema|FullyQualifiedName~McpTokenBudget"` — exit 0, at least one selected test, no failed selected tests; confirm the intended regression tests actually ran.
- Final Windows gate: `pwsh -NoProfile -File scripts/verify.ps1` — exit 0 and literal `verify: OK`.
- Final Linux gate: `pwsh -NoProfile -File scripts/verify-linux.ps1` — exit 0 and successful completion of its full gate on ext4.
- `git diff --check` — exit 0.

Run one final gate on each required platform after the final relevant code change. Repeat or broaden verification only for a remaining failure or risk. Record literal results and skipped live checks separately.

## Done criteria

- [x] Both capability modes disclose their actual configured input roots.
- [x] Empty and oversized root inventories have explicit semantics.
- [x] A caller can discover a permitted path using MCP without reading client config.
- [x] Existing path containment and wire-budget tests remain green.
- [x] Focused and required platform gates pass, and plans/README.md records evidence.
- [x] Source, CLI/MCP descriptions and documentation agree on the final behavior.

## Stop conditions and maintenance

Stop if discovery changes what files the process may read. Paths are diagnostics, but credentials embedded in a malformed value still require filtering.

Future changes must preserve the specific boundary tested here rather than adding generic hiding or syntax restrictions. Update the plan if implementation evidence changes its premise; historical tests are evidence to examine, not authority to preserve obsolete policy.
