# Plan 055 verification record

## Scope and implementation history

Plan 055 implementation is on `feat/plan-055-agent-output` in commit range `52bdc23..f5ac90c` (base `52bdc23286efd277fedde7c577a1301ea77a571b`). The final Windows gate ran at `3d99ab6`; the only later source changes were the portable test-fixture corrections in `f5ac90c`, which were then exercised by focused Windows and Linux tests. The final Linux gate ran at `f5ac90c`.

Key implementation commits:

| Commit | Change |
|---|---|
| `46cc420` | Shared detail ladder and per-table read ordering |
| `0a500f2` | Text-only MCP results and byte-budget behavior |
| `7350e47..c3745ed` | Compact query projection, schema version 2, and resolved database field |
| `8fd8daf` | Matrix case inventory before optional detail |
| `d514999` | Statements section cursor paging |
| `397d288` | Task 6 documentation and CLI output default alignment |
| `c9447b2` | Migrate matrix assertions and update measured workflow byte fixture |
| `3d99ab6` | Remove final-newline formatting findings |
| `f5ac90c` | Make benchmark artifact and long-root fixtures portable across Windows/Linux |

## Reviews and recorded rulings

The task-level reviews and required fix-round rereviews approved the implementation; the final portability fix rereview was clean. The final whole-branch review is still pending. The user authorized local merge after completion, but the branch has not yet been merged. No push was performed or authorized.

Execution allocation recorded in the SDD ledger: implementation by gpt-6-luna (high), task reviews by gpt-6-luna (xhigh), and whole-branch review assigned to gpt-6-sol (low). The latter remains outstanding.

Contract rulings retained during implementation:

- The CLI `--output agent` default is 65,536 bytes, matching the global per-call default and capability digest. The 262,144-byte `--max-result-bytes` default is an MCP process ceiling only.
- Call budgets below the minimum remain rejected; budgets above the process ceiling clamp to it.
- Grep checks for removed APIs use exact symbol names. `DefaultCallToolResultBudgetBytes` intentionally contains the old name as a substring and is the replacement API.
- Matrix details are indexed compact comparison objects, with matrix-shared fields represented once. Existing legacy matrix indexes remain readable, with missing metrics/equivalence explicitly unavailable.
- Statements and matrix inventory paging are in scope. Saved-plan operators and other plan artifact pagination remain in Plan 058.
- Existing input-root and redaction changes from the drifted branch were preserved.

## Verification

### Windows — passed

Command: `pwsh ./scripts/verify.ps1` on `3d99ab6`.

- Exit code: `0`; required marker: `verify: OK`.
- Dashboard UI: 148 passed.
- Core: 3,796 passed, 0 skipped.
- MCP: 400 passed, 4 skipped, 404 total.
- Build: 0 warnings, 0 errors.
- Format check: passed.

Four MCP skips were the configured live SQL Server/PostgreSQL stdio checks and Linux/macOS published-server smoke checks. This gate does not provide live database proof.

The two portability fixtures changed in `f5ac90c` were separately checked on Windows:

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Matrix_projection_keeps_input_order_and_omits_full_run_and_operator_arrays"` — 1 passed, 0 failed, 0 skipped.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Capabilities_omit_an_input_root_that_cannot_fit_any_page_and_still_terminate"` — 1 passed, 0 failed, 0 skipped.

### Linux — passed

Command: `pwsh ./scripts/verify-linux.ps1` on the WSL ext4 gate checkout at `f5ac90c`.

- Exit code: `0`; required marker: `verify-linux: OK`.
- Dashboard UI: 148 passed.
- Core: 3,796 passed, 0 skipped.
- MCP: 401 passed, 3 skipped, 404 total.
- Build: 0 warnings, 0 errors.
- Format check: passed.

The portability fixtures were each run as focused tests on the Linux gate clone after syncing `f5ac90c`:

- `wsl -d Ubuntu -- bash -lc 'export PATH="$HOME/.node/current/bin:$HOME/.dotnet:$PATH"; cd /home/rgone/src/sqlharness-gate; dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Matrix_projection_keeps_input_order_and_omits_full_run_and_operator_arrays"'` — 1 passed, 0 failed, 0 skipped.
- `wsl -d Ubuntu -- bash -lc 'export PATH="$HOME/.node/current/bin:$HOME/.dotnet:$PATH"; cd /home/rgone/src/sqlharness-gate; dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Capabilities_omit_an_input_root_that_cannot_fit_any_page_and_still_terminate"'` — 1 passed, 0 failed, 0 skipped.

The three MCP skips are configured environment-gated tests. No live database or macOS publish proof is claimed.

## Remaining integration steps

The implementation and platform gates are complete. Complete the assigned whole-branch review, then perform the already-authorized local merge. This record describes the feature branch and does not claim that review or merge has happened.
