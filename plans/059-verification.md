# Plan 059 verification record

## Scope

Plan 059 adds single-measured-execution reference output accounting, explicit operation and producer references for artifacts and journal rows, workflow output coverage and comparisons, and documentation for schema v7 and legacy accounting. Implementation commits include `5238abc`, `fcb5e94`, `eee5d95`, `2730227`, `92bfad0`, and documentation/integration fix `ccbb4d7`.

## Focused regression and diagnosis

- The recorded integration regression reproduced with `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~Capability_discovery_then_schema_uses_two_bounded_calls_without_database_access" --logger "console;verbosity=normal"`: observed 8724 UTF-8 bytes against the 8192-byte ceiling; the fixture's previous expected total was 8186.
- Root cause: Task 3 commit `2730227` put a verbose `limits.activityJournalStorage.outputCost` object in every capability response. This duplicated the full cost explanation in the bounded profile-discovery output, although capabilities discovery is outside workflow-cost coverage. Removing that redundant subtree restored the expected 8186-byte response without increasing the budget. The full semantics live in README, MCP documentation and the SQLHarness skill digest.
- Focused rerun covered the discovery/schema budget, capability contract and new `DocsSyncTests.Workflow_cost_docs_pin_provenance_measurements_legacy_and_dry_run_accounting`: 3 passed, 0 failed, 0 skipped.
- The first Windows gate found a stale `AGENTS.md` reference to the former README anchor `activity-journal-and-savings`; it now points to `activity-journal-and-workflow-output-cost`.
- The next Windows gate found `FINALNEWLINE` diagnostics on the Plan 059 changed C# files. `.editorconfig` sets `insert_final_newline = false`; the scoped recovery command was `$base = git merge-base main HEAD; $include = @(git diff --name-only "$base..HEAD" | Where-Object { $_ -match '\.cs$' }) + 'tests/SqlHarness.Tests/DocsSyncTests.cs'; dotnet format SqlHarness.sln --no-restore --include $include`. It removed only those EOF newline markers. No implementation lines changed in this formatting cleanup.

## Build and Windows — passed

- `dotnet build SqlHarness.sln -warnaserror` — exit 0, 0 warnings, 0 errors.
- `pwsh ./scripts/verify.ps1` — exit 0, final marker `verify: OK`.
- UI: 32 files passed, 144 tests passed.
- Core: 3,899 passed, 11 skipped (3,910 total).
- MCP: 403 passed, 4 skipped (407 total).
- Build: 0 warnings, 0 errors. Format check passed.

## Linux — passed

- `pwsh ./scripts/verify-linux.ps1` — exit 0, final marker `verify-linux: OK`. The WSL gate fetched and verified commit `ccbb4d7`.
- UI: 32 files passed, 144 tests passed; typecheck and lint passed.
- Core: 3,906 passed, 4 skipped (3,910 total).
- MCP: 404 passed, 3 skipped (407 total).
- Build: 0 warnings, 0 errors. Format check passed. The production UI build emitted its existing advisory about a minified chunk over 500 kB; the gate succeeded.

Scratch logs supported the checks but are not retained as durable evidence. The commands, counts, gate results, regression diagnosis and recovery recorded above are the retained verification record. No live database calls or push occurred.

## Final review correction — passed

The final Task 4 review found that the documentation assigned `raw_bytes` and `emitted_bytes` to v7 and left the matrix baseline aggregation unclear. README, MCP docs and the skill now state that v7 adds operation references and `dry_run`, reuses the byte columns added in v3 without changing existing values, and uses each producer's first measured result/messages per variant and parameter set (excluding warm-ups, later repeats and plan XML). The old statement that existing rows have null v7 byte values was removed. The same review also found a transient scratch-log retention claim; the durable record now retains commands and counts without promising the scratch logs will remain.

- RED: the strengthened `Workflow_cost_docs_pin_provenance_measurements_legacy_and_dry_run_accounting` test failed because the README did not state the exact v7 field list.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~DocsSyncTests|FullyQualifiedName~ContractsTests"` — 26 passed, 0 failed, 0 skipped.
- Format: `dotnet format SqlHarness.sln --no-restore --include tests/SqlHarness.Tests/DocsSyncTests.cs`, followed by `dotnet format SqlHarness.sln --no-restore --verify-no-changes --include tests/SqlHarness.Tests/DocsSyncTests.cs` — passed.

This correction changes documentation and assertions only; the previously recorded Windows/Linux runtime gates remain applicable.
