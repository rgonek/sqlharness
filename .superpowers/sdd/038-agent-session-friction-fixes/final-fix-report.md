# Plan 038 final fix report

Branch: `fix/plan-038-friction`
Implementation commit: `5c79db1524113dfbb86b8913f0ac5ede88a2b375` (`Fix Plan 038 agent session friction findings`)

## Findings fixed

1. **Statement metric ownership:** operator extraction now attributes a showplan element only to its nearest `RelOp`. The nested-plan regression verifies the root keeps 2 rows/1 execution and the child keeps 7 rows/4 executions and its table/index identity.
2. **Setup split local scope:** before splitting parameterized setup, the parser collects scalar and table-variable declarations throughout the prefix AST and checks references throughout the parameterized suffix. A cross-boundary local dependency rejects offline with a generic actionable message. SQL ordering, parameter interpolation, and safety classification are unchanged. README and MCP docs describe this boundary.
3. **Request-mode artifact schema:** the request artifact tool now advertises `statements` and `matrix-cells`; its schema test covers these sections and the optional cursor.
4. **Usable matrix paging:** completed matrix cells receive an index stored in the first cell artifact directory. The compare summary exposes only the safe artifact id and a continuation offset. `matrix-cells` returns up to 32 safe cell ids per page; each page read checks the same manifest scope owner. Callers then read each cell's normal safe artifact sections. The index contains no matrix values, SQL, or filesystem paths. The 100-cell test adapts real MCP results against the 16 KiB serialized budget, follows every cursor, retrieves every omitted cell's metrics, and confirms a foreign scope is rejected.

The projection also removes matrix parameter values from agent-visible summaries/references. Statement artifact output remains SQL-free. CLI help and artifact section discovery include `statements` and `matrix-cells`; the plan index remains **IN PROGRESS** because the Linux full gate did not finish green.

## TDD evidence

- **Statement ownership RED:** reverting the nearest-`RelOp` predicate made the new test report root rows `9` rather than expected `2`. **GREEN:** focused `ArtifactReaderTests` passed with separate root/child counters and object identity.
- **Setup split RED:** bypassing the split guard made the new test observe no exception for a prefix local referenced by the parameterized suffix. **GREEN:** the guard returns the offline shape rejection; the focused test passed.
- **Artifact paging RED:** the new reader test initially failed because `matrix-cells` was not an accepted section. **GREEN:** a 40-cell index returns a first page of 32 with cursor 32, followed by 8 cells and no continuation.
- **Request schema RED:** schema assertion failed before `statements`/`matrix-cells` were added to request-mode `AllowedValues`. **GREEN:** request and fixed schemas now advertise the available sections.
- **Matrix recovery GREEN:** MCP projection and page traversal recovered all omitted cells from a 100-cell matrix while each serialized result stayed within 16,384 bytes; foreign owner access returned the constant scope error.

## Verification

Focused final-code tests:

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~SetupSqlExecutionTests.Local_variable_declared_before_split_and_used_after_split_is_rejected|FullyQualifiedName~ArtifactReaderTests.Statement_metrics_keep_nested_relop_counters_and_objects_with_their_owner|FullyQualifiedName~ArtifactReaderTests.Matrix_cell_index_pages_return_safe_ids_and_a_consumable_cursor"` — **3 passed, 0 failed**.
- MCP focused coverage for matrix retrieval, request schema, and statement artifact handling — **3 passed, 0 failed**.
- `dotnet format SqlHarness.sln --no-restore --verify-no-changes` on final Windows working tree — **passed** after restoring the repository's `.editorconfig` no-final-newline convention.
- `git diff --check` — **passed**.

Windows `pwsh ./scripts/verify.ps1` evidence:

- A full run passed UI checks (25 files / 103 tests), restore, build (0 warnings / 0 errors), MCP (257 passed / 4 skipped), Core (3242 passed), and format before the final nested-declaration traversal refinement.
- On final code, a full run passed UI (25 files / 103 tests), restore, build (0 warnings / 0 errors), MCP (257 passed / 4 skipped), and Core (3242 passed), then stopped in format because `SetupSqlExecution.cs` had one final newline contrary to `.editorconfig` (`insert_final_newline = false`). I corrected the file and reran full format verification successfully. Per the parent instruction, I did not start another full Windows gate.
- An earlier final-code Windows full-gate attempt had one MCP stdio initialization timeout at its 30-second cancellation budget. The exact test passed alone (1/1 in 10 seconds) and passed in the subsequent full test stage.

Linux ext4 `pwsh ./scripts/verify-linux.ps1` ran from a regular clone at the committed SHA (not the worktree pointer):

- UI passed (25 files / 103 tests); restore passed; build passed (0 warnings / 0 errors); Core passed (3242 / 3242).
- The MCP stage reported **256 passed, 2 failed, 3 skipped** and stopped the full gate at `test`. One captured failure was `McpStdioProcessTests.Inprocess_host_returns_zero_on_precancelled_token_without_stdout_bytes` (expected exit 0, got 1). The test passed alone (1/1); all three `Inprocess_*` boundary tests also passed together (3/3). A separate MCP-only Linux run passed **258 / 258, 3 skipped**. The original combined-stage output delivered to this run was truncated before the second failing test's detail was retained; I do not infer its name.
- Linux `dotnet format SqlHarness.sln --no-restore --verify-no-changes` on the ext4 gate clone completed without diagnostics.

The complete Linux gate therefore remains **not green** despite the isolated MCP reruns passing. No live SQL Server/PostgreSQL operations or database mutations were run. No gate was counted as passing based only on the isolated suite.

## Scope decisions and concerns

- W2 uses the explicitly allowed offline rejection path for scalar or table locals that cross the parameterized setup boundary. It does not reorder statements, interpolate values, recommend literals, broaden table-variable support, or weaken safety.
- Matrix continuation is an offset into the owner-held index, not an emitted list position without a reader. Page size is fixed at 32 and output projection can further trim a response under the caller's actual budget.
- W3's approved redaction threshold and behavior were preserved. W5 statement output continues to omit SQL text.
- Remaining concern: the Linux full test stage is flaky under the combined solution run. The captured pre-cancel test failure and the full MCP-only rerun establish that the test passes independently, but the complete Linux gate still failed and its second failing test name was unavailable in the truncated runner output. Plan 038 must remain in progress until a green full Linux gate is recorded.
