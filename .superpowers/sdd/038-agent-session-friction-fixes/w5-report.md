# W5 implementation report

## Outcome

Implemented Plan 038 W5 in `fix/plan-038-friction`. Benchmark artifacts expose a new scope-checked MCP `statements` section. Successful benchmark journal writes also persist `operation_statements` rows. Both paths omit statement SQL text, even when `journal.storeSensitive` is enabled; hashes and bounded plan diagnostics remain available.

The projection groups measured plans by variant, parameter set, matrix cell index, and statement ordinal. CPU and elapsed values use medians across measured runs, DOP uses the maximum observed value, and operator details come from the first available plan. Each statement has a SHA-256 hash and up to ten operators ordered by actual rows then executions. Artifacts cap the section at 128 statements and report `omittedStatements`; the shared agent projection further bounds rows and operator details.

The journal schema is now version 5. `operation_statements` stores variant/set/cell identity, statement ordinal and hash, CPU/elapsed/DOP, and bounded operator JSON. It stores no statement text. PostgreSQL JSON plans return an empty statement section because they do not expose SQL Server `StmtSimple`/`QueryTimeStats` fields.

## TDD evidence

- RED: `ArtifactReaderTests.Statements_section_returns_per_statement_metrics_without_sql_text` failed with `Unknown artifact section 'statements'`.
- RED: `ActivityJournalBenchmarkTests.Benchmark_record_persists_per_statement_metrics_without_statement_text` failed with SQLite `no such table: operation_statements`.
- GREEN: both regressions passed after implementation. The artifact test verifies two statements, CPU sum 30 ms, elapsed sum 40 ms, hashes, DOP, estimates, actual rows/executions, operator identity, and no SQL text. The journal test verifies two persisted rows and no statement text in operator JSON.
- MCP dispatch regression passed for an owned artifact's `statements` section; the agent projection regression verified statement/operator truncation and clipping with omission counts.

Focused final run: 6 passed, 0 failed. MCP handler test: 1 passed, 0 failed. Earlier targeted core run: 3 passed, 0 failed.

## Verification

- Windows `pwsh ./scripts/verify.ps1`: exit 0, final output `verify: OK`. UI: 25 files / 103 tests passed. Build: 0 warnings, 0 errors. Core: 3,221 passed, 0 failed. MCP: 255 passed, 4 skipped, 0 failed. Format stage passed.
- An earlier Windows gate run found three expected integration updates: hard-coded journal version 4, the capability section list, and the byte-budget observation. Updated those expectations and reran the full gate successfully.
- WSL `pwsh ./scripts/verify-linux.ps1`: exit 1 at `sync` (git exit 128) because WSL cannot resolve the Windows worktree `.git` pointer (`fatal: not a git repository: .../.git/worktrees/plan-038`). Per instruction, ran the equivalent stages from a normal clone on WSL ext4 at `/home/rgone/src/sqlharness-gate-plan038`, commit `49890cd`.
- Normal-clone Linux UI check passed (25 files / 103 tests); restore passed; build passed with 0 warnings and 0 errors; format `dotnet format SqlHarness.sln --no-restore --verify-no-changes` passed.
- First Linux full test run: core 3,221 passed; MCP 255 passed, 3 skipped, 1 failure. Existing test `Inprocess_host_returns_zero_on_immediate_eof_without_stdout_bytes` expected exit 0 and got 1. The isolated test rerun passed. A second full Linux test run passed: core 3,221 passed; MCP 256 passed, 3 skipped, 0 failed. The original script sync limitation remains, while every Linux gate stage passed from the normal clone.
- `git diff --check`: passed; Git printed only line-ending conversion warnings.

## Self-review and concerns

The MCP path still validates manifest ownership before reading any section. The statements file has a fixed child name and the same 16 MiB read bound as report files. The extractor prohibits DTD/entity resolution, clips operator strings, saturates counter sums, treats malformed/missing plan details as unavailable, and never returns SQL text. Journal inserts share the existing benchmark transaction and cascade with operation deletion. No live database, profile, or secret was used.

No unresolved implementation concern. The provided Linux script remains incompatible with this Windows worktree pointer; the normal-clone Linux stages passed.
