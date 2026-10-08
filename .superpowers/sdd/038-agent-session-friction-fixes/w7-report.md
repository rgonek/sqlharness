# W7 implementation report

## Outcome

Allowed read-only SQL Server XML methods `nodes`, `value`, `query`, and `exist` on XML variables and alias-qualified columns. The multi-part `nodes()` table-source form is treated as an XML method only when its leading identifier resolves to a table alias or table name already encountered in the same SELECT. Ordinary three-part object names remain `CrossDatabaseReference`; persistent writes remain denied.

## TDD evidence

- RED command: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Query_allows_read_only_XML_methods_on_columns_and_variables`
- RED output: failed 5 / passed 3 / total 8. The failures were `CrossDatabaseReference` for alias-column `.nodes()` and `UnsupportedStatement` for XML variable calls/declarations.
- GREEN focused command: same command after implementation.
- GREEN output: passed 8 / failed 0 / total 8.
- Focused safety suite: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~SqlSafetyTests` — passed 308 / failed 0.

## Verification

- First `pwsh ./scripts/verify.ps1`: UI passed (103), build passed (0 warnings, 0 errors), MCP passed (255, 4 skipped), core passed (3231); format failed on final-newline diagnostics in the two edited files. Applied `dotnet format SqlHarness.sln --include src/SqlHarness.Core/SqlSafety.cs tests/SqlHarness.Tests/SqlSafetyTests.cs`.
- Second `pwsh ./scripts/verify.ps1`: exit 0, literal `verify: OK`. UI passed (103), build passed (0 warnings, 0 errors), MCP passed (255, 4 skipped), core passed (3231), format passed.
- `git diff --check`: passed; only Git line-ending normalization warnings were printed.
- `pwsh ./scripts/verify-linux.ps1` initially stopped at `sync` with exit 128 because WSL could not resolve the Windows worktree `.git/worktrees` pointer. Created an ordinary clone on the WSL ext4 filesystem at `~/src/sqlharness-plan-038-linux` and checked out `d070bbb`.
- The same Linux stages on that ext4 clone passed UI install, UI check (25 files / 103 tests), restore, and build (0 warnings / 0 errors). The first full test run had MCP 255 passed / 1 failed / 3 skipped and core 3231 passed. `Inprocess_host_returns_zero_on_immediate_eof_without_stdout_bytes` was the only failure (expected exit 0, got 1); it passed in isolation and on the full rerun.
- Full Linux test rerun: MCP 256 passed / 0 failed / 3 skipped; core 3231 passed / 0 failed. `dotnet format SqlHarness.sln --no-restore --verify-no-changes` passed. Together with the successful earlier UI, restore, and build stages, this completes the Linux gate on the ext4 clone.

## Review follow-up: query-block alias isolation

- RED command: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~XML_nodes_resolution_does_not_reuse_aliases_from_other_query_blocks`
- RED output: failed 1 / passed 1; the UNION-branch `otherdb.dbo.nodes(...)` shape was incorrectly allowed because `otherdb` came from the first branch. The CTE collision case was already denied.
- Fix: moved alias collection from `SelectStatement` to each `QuerySpecification`, isolating CTE and UNION query blocks while retaining in-block aliases for valid XML method resolution. The schema-qualified `nodes` function stays denied as `CrossDatabaseReference` when no same-block alias proves the XML shape.
- GREEN: the collision tests passed 2/2; `SqlSafetyTests` passed 310/310.
- Final Windows gate after the fix: `pwsh ./scripts/verify.ps1` exited 0 with `verify: OK`; UI 103, MCP 255 passed / 4 skipped, core 3233, build 0 warnings/errors, format passed.
- Final Linux gate after the fix: `pwsh ./scripts/verify-linux.ps1` again could not read the linked worktree pointer, so the same stages ran in `~/src/sqlharness-plan-038-linux` on WSL ext4 at commit `817c1ff`: `npm ci --prefix src/SqlHarness.Dashboard/ui`, `npm run check --prefix src/SqlHarness.Dashboard/ui`, `dotnet restore SqlHarness.sln`, `dotnet build SqlHarness.sln --no-restore -warnaserror`, `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration" --logger "console;verbosity=normal"`, and `dotnet format SqlHarness.sln --no-restore --verify-no-changes`. The stage script exited 0 with `verify-linux: OK`; UI 25 files / 103 tests, build 0 warnings/errors, MCP 256 passed / 3 skipped, core 3233 passed, and format passed.
## Self-review

- Added tests for all four XML method names across variables/columns, including `.nodes()` in `CROSS APPLY` and a variable table source.
- Added boundary checks proving a real three-part database table reference still returns `CrossDatabaseReference` and an XML method in an `UPDATE` does not authorize a persistent write.
- XML locals are added to the scalar declaration classification because ScriptDom represents `xml` declarations as `XmlDataTypeReference`; this does not grant a write path.
- No SQL was executed against a database, and no safety reason or exit code was added.

## Concerns

- `verify-linux.ps1` could not sync the linked worktree into WSL because the Windows worktree `.git` pointer is not a WSL-readable path; the same gate stages were run in a normal ext4 clone instead. The first full Linux test run had the intermittent MCP EOF failure described above; the isolated test and full rerun passed. Linux UI tests also print existing non-fatal chart-container warnings and Vite reports the existing large-bundle warning.
- This is offline syntax classification only; column types and database object existence are not established. Successful classification does not prove runtime object validity or domain semantics.
