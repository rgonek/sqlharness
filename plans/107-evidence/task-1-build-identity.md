# Plan 107 Task 1: Source and build identity evidence

Captured 2026-10-23 in `D:/Dev/sqlharness/.worktrees/plan-107`. This is read-only identity evidence; Task 1 did not replay or execute the retained requests.

## Source identity

- Planned source baseline from Plan 107: `677b2f93b66c06d3dc0bda8475c7570ef29738d9`.
- Actual Task 1 worktree HEAD before evidence edits: `3c698588a32372ce82f83ad3e79fe15e9fac2e87` (`docs(107): capture assigned diagnostic plan`). Worktree branch: `plan-107`; status was clean.
- Controller drift check, repeated verbatim against the plan's seven scoped files:

  ```text
  git diff --stat 677b2f93b66c06d3dc0bda8475c7570ef29738d9..HEAD -- src/SqlHarness.Core/CompareCellRunner.cs src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/ListParameterExpander.cs src/SqlHarness.Mcp/McpOperationMapper.cs tests/SqlHarness.Tests/CompareTests.cs tests/SqlHarness.Tests/ListParameterExpanderTests.cs tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs
  ```

  Result: no output; no scoped-file drift since the planned base.
- The historical runtime/source observation was recorded against `fdc161d1` (see `docs/observations/list-parameter-runtime-divergence.md`). It is a distinct snapshot and does not identify the binary that handled those requests.
- Current source inspection: `CompareCellRunner.cs` calls `DeduplicateClassificationListParameters` across setup, baseline and candidate expansions before combining classification parameters. Existing coverage names are `ListParameterExpanderTests.Expands_a_list_in_both_supported_IN_forms_with_typed_parameters` and `CompareTests.Compare_dry_run_deduplicates_a_list_shared_by_multiple_SQL_texts`. These do not by themselves prove the full named-set MCP runtime path.

## Candidate executable and observed process evidence

Read-only metadata for the PATH candidate:

```text
Get-Command sqlharness | Select-Object Name,Source,CommandType
Name: sqlharness.exe
Source: C:\Users\rgone\.local\bin\sqlharness.exe
CommandType: Application

Get-FileHash -Algorithm SHA256 C:\Users\rgone\.local\bin\sqlharness.exe
SHA256: 51F566567C76E4D1696AC2DE6CD1AE58B69E338D58E7838894A5A9E3C8283FCC

FileVersion: 0.0.0.0
ProductVersion: 0.0.0-dev+1b8bd70b4020ffdb217d50ee8c5a3b7a17e36162
Length: 142596855 bytes
LastWriteTimeUtc: 2026-10-17 22:32:13Z
sqlharness.exe --version: 0.0.0
```

The executable's embedded version identifies candidate build commit `1b8bd70b4020ffdb217d50ee8c5a3b7a17e36162`, dated 2026-10-18. Comparing that commit with Task 1 source HEAD reports changes across the seven scoped files (1,504 insertions, 88 deletions). The candidate executable therefore advertises a source revision that predates substantial changes in the inspected source; this is source/build mismatch evidence, not proof that it caused the retained symptom.

Process inventory was limited to process ID, image path, start time and filtered module paths; command lines and configuration were not queried. At capture, 15 `sqlharness` processes were visible. Every process image path was `C:\Users\rgone\.local\bin\sqlharness.exe`; start times ranged from 2026-10-23 07:31:44 to 17:18:20 local time. Module inspection showed the apphost path and Windows system modules, with no separate `SqlHarness.*` module. The current on-disk candidate path and process image paths match. The process evidence does not establish the hash/content actually mapped when each process started, and the apphost module listing does not reveal its embedded managed assembly identity. Loaded build identity remains unresolved.

## Retained synthetic request shape and outcome

The historical observation describes two offline compare dry runs. Both used the same typed integer list and named parameter sets on baseline/candidate; neither supplied a scalar parameter and neither connected to a database. SQL forms:

```sql
SELECT 1 AS Value WHERE 1 IN @Ids;
SELECT 1 AS Value WHERE 1 IN (@Ids);
```

Expected according to the inspected source contract and focused source tests: both supported `IN` forms expand typed lists; shared generated list parameters are deduplicated before classification validation. Observed in the retained dry-run record: the bare form returned `safety_rejected` / `ParseError`; the parenthesized form reported a collision involving generated `@Ids1`. No historical workload was replayed in Task 1, and this evidence does not establish the runtime cause or a successful end-to-end result.

## Worktree context

- Assigned worktree `D:/Dev/sqlharness/.worktrees/plan-107`: branch `plan-107`, HEAD `3c698588a32372ce82f83ad3e79fe15e9fac2e87`, clean before this task's evidence edits.
- Main worktree `D:/Dev/sqlharness`: HEAD `6d543e509734a374caf882bc8c05ffe5ccaa98b2`. Its pre-existing dirty state includes modified `README.md` and `plans/README.md`, plus untracked plans 103–108 and 110–113. These were inspected only by status and left untouched.
- `git worktree list --porcelain` confirmed the assigned and main worktree paths and their HEADs; other worktree contents were not inspected.
