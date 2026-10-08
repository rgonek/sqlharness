# Plan 038 W8 implementation report

## Outcome

W8 is implemented in commit `4bb4d44` (`Explain SQL setup variable scope rejections`). Existing safety reason and exit codes are unchanged. SQL Server table-position variables without an earlier inline `DECLARE @t TABLE (...)` remain denied, with the brief's generic detail appended. AST-based batch-scope validation now rejects setup-declared variables referenced by baseline, candidate, query, or measured batches unless that batch declares its own variable or the caller supplies a supported typed input. Compare, measure, parameter-set measure, and offline validation perform the check before connecting.

## TDD evidence

Added tests first for the unproven named table type diagnostic and setup-only scalar references. The initial focused run exited `1` with the expected failures:

- The named table type was denied as `NonTemporaryWrite`, but the actual message was only `NonTemporaryWrite.`
- Offline setup-only reference validation reported `missing_parameters` instead of a generic setup-scope diagnostic.
- Typed parameter and independently declared variant-local cases passed before implementation.

After implementation, the focused Core tests passed: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~T3_Compare_setup_explains_unproven_named_table_variable_without_echoing_sql_names|FullyQualifiedName~Setup_only_scalar_reference_is_rejected_offline_without_echoing_names_or_values|FullyQualifiedName~Setup_only_scalar_reference_is_allowed_when_variant_binds_typed_parameter|FullyQualifiedName~Variant_local_declaration_shadows_setup_only_name"` → 4 passed, 0 failed.

Compare and MCP regression tests prove the setup-only error happens before a connection opens, preserves the generic detail, and does not emit SQL, the declared variable/type name, or the sentinel values. Focused MCP tests passed 2/2. The relevant existing Core suites passed 445/445 and the MCP mapping suite passed 69/69.

## Changes

- Added the exact generic `SqlSafetyDecision.Detail` for denied unproven table-position variables without including SQL or supplied names/values.
- Added an AST visitor that compares variables declared in setup against references and declarations in each benchmark batch. Typed input names count as bound inputs; variant declarations are resolved per batch, including `GO` boundaries.
- Applied the check in compare, measure, parameter-set measure, and `sqlharness validate` paths before authentication/connection.
- Documented setup-variable lifetime and named table-type boundaries in `README.md` and `docs/mcp.md`.
- Added Core, CLI validation, compare, and MCP regressions. No safety allowlist, denied construct, reason code, or exit code was broadened or reordered.

## Verification

Windows: `pwsh ./scripts/verify.ps1` exited `0` and printed `verify: OK`. The final run passed UI checks (25 files / 103 tests), built with 0 warnings and 0 errors, passed Core 3,238/3,238 and MCP 257 passed / 4 skipped, and passed the format stage. The first run reached format and reported only `FINALNEWLINE` differences caused by newly added EOF newlines; those were restored to match the repository convention before the successful rerun. `dotnet format SqlHarness.sln --verify-no-changes` also exited `0`.

Linux: `pwsh ./scripts/verify-linux.ps1` could not pass its initial `sync` stage because WSL interpreted this Windows worktree's absolute `.git` pointer as a nested `D:/...` path. Per the established Plan 038 fallback, the committed branch was fetched into the existing clean ext4 clone at `/home/rgone/src/sqlharness-plan-038-linux`; no worktree Git metadata was changed. The clone was at commit `4bb4d44`. The same stages and commands then exited `0`: Node `v24.18.0` matched `.nvmrc`; UI 25 files / 103 tests; restore passed; build had 0 warnings and 0 errors; Core passed 3,238/3,238; MCP passed 258 with 3 skipped; `dotnet format SqlHarness.sln --no-restore --verify-no-changes` passed. The staged run ended with `verify-linux clone: OK`.

`git diff --check` passed. No live database, credentials, or SQL execution were used.

## Self-review and concerns

- The only new diagnostic content is constant text. Existing classifiers continue to reject undeclared, scalar-as-table, use-before-declare, cross-batch, and unsupported named-type table variables. Existing persistent-write diagnostics remain intact.
- The setup-variable validator is SQL Server AST-only and is not run for PostgreSQL. It examines each variant batch independently and exempts caller-supplied parameter names.
- The Windows `verify-linux.ps1` wrapper itself cannot source this worktree under WSL because of its absolute Windows Git pointer. All Linux gate stages passed against the committed code in the existing ext4 clone. The worktree pointer remains unchanged.
- No additional concerns found in the reviewed diff.
