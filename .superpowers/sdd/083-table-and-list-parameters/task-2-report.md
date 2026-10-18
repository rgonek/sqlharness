# Plan 083 Task 2 report

## Result

Implemented ScriptDom-token-based Dapper list expansion and representative offline classification. Execution still uses Task 1's scalar-only guards; full list materialization and command execution remain Task 3 work.

## RED / GREEN evidence

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~ListParameterExpanderTests` failed to compile because `ListParameterExpander` and `ListValues` did not exist.
- GREEN: the same command passed 6/6 after implementation.
- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Query_dry_run_classifies_list_using_one_typed_representative` returned `Safety` for `IN @Ids`.
- GREEN: the same command passed 1/1 after query dry-run classification used the typed representative `@Ids1`.
- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Compare_dry_run_classifies_lists_after_representative_expansion` returned `Safety` for the unexpanded list SQL.
- GREEN: the same command passed 1/1 after compare preparation classified representative-expanded setup, baseline, and candidate SQL.
- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Measure_dry_run_classifies_lists_after_representative_expansion` returned `Safety` for the unexpanded list SQL.
- GREEN: the same command passed 1/1 after measure preparation classified representative-expanded setup and query SQL.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~Watch_classifies_list_after_representative_expansion|FullyQualifiedName~Snapshot_classifies_list_after_representative_expansion'` passed 2/2. Both reached the existing pre-connection execution guard after offline classification.
- GREEN: final focused command:
  `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~SqlHarnessCompareTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~SqlHarnessMeasureTests|FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~SnapshotTests|FullyQualifiedName~SnapshotScopeTests|FullyQualifiedName~SqlHarnessQueryTests|FullyQualifiedName~ListParameterExpanderTests'`
  — Passed 333, Failed 0, Skipped 0.
- `git diff --check` passed. No `just verify` or live database was run, per task scope.

## Interface and behavior

- Added `internal sealed record ListValues(string Name, string Type, IReadOnlyList<string?> Values)`, `ExpandedSql(string Text, IReadOnlyList<SqlHarnessParameter> Parameters)`, and `ListParameterExpander.Expand(string sql, IReadOnlyList<ListValues> lists)` in `src/SqlHarness.Core/ListParameterExpander.cs`.
- Expansion reads ScriptDom lexer tokens and edits only exact variable tokens. It handles `IN @Ids`, `IN (@Ids)`, and the lexer-combined `IN@Ids` spelling; repeated occurrences reuse one generated parameter set. Literals and comments are left intact. Prefix names such as `@IdsOther` are not changed when expanding `@Ids`.
- Non-empty list values become typed `@Name1`, `@Name2`, … parameters. Empty lists become `IN (SELECT @Name WHERE 1 = 0)` with a typed null parameter. The expander rejects over 2,100 generated parameters and generated-name collisions.
- `SqlParameterInputs.ExpandListsForClassification` selects one typed representative: the first inline value; an empty list remains empty; a `valuesFrom` list uses one typed null representative because its row count is unknown offline. The existing `DryRunNotes` reports unknown list length for `valuesFrom` and unverified TVP shape.
- Query, compare (including compare matrix), measure, watch, watch NDJSON, and snapshot classify representative-expanded SQL. Compare and measure setup SQL uses the same representative expansion. `rowsFrom`/`valuesFrom` source SQL receives the same read-only classification and 1 MiB bound on each accepting path. Parameter reference and setup validation use the expanded text and representative bindings. Scalar bindings that collide with a generated list parameter fail safely.
- `CompareOperationPreparer` keeps scalar execution bindings in `FixedParameters` and exposes `ClassificationParameters` plus `ClassificationSetupSql`, `ClassificationBaselineSql`, and `ClassificationCandidateSql` for offline validation. `PreparedCompareFamily.HasNonScalarParameters` indicates where representative list parameters must be added during variant checks.

## Task 3 handoff

Replace representative preparation with full expansion after `rowsFrom`/`valuesFrom` materialization, before setup and measured runs. The replacement points are `SqlParameterInputs.ExpandListsForClassification` and its query, compare, measure, watch, watch NDJSON, and snapshot callers in `SqlHarnessModule`; compare classification fields in `PreparedCompareFamily` are offline-only. Task 3 must pass the fully expanded SQL and full typed list parameters to each actual execution path, preserve the original unexpanded SQL for operation/version identity, and keep the command-wide 2,100 count across scalar plus expanded list parameters. Task 1 scalar-only guards remain on actual query, compare/matrix, measure, watch/NDJSON, and snapshot execution paths until that materialization and binding is wired.

## Changed files

- `src/SqlHarness.Core/ListParameterExpander.cs` (new)
- `src/SqlHarness.Core/SqlSafety.cs`
- `src/SqlHarness.Core/CompareCellRunner.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `tests/SqlHarness.Tests/ListParameterExpanderTests.cs` (new)
- `tests/SqlHarness.Tests/QueryTests.cs`
- `tests/SqlHarness.Tests/CompareTests.cs`
- `tests/SqlHarness.Tests/MeasureTests.cs`
- `tests/SqlHarness.Tests/WatchTests.cs`
- `tests/SqlHarness.Tests/SnapshotTests.cs`
- `plans/083-table-and-list-parameters.md`
- `plans/README.md`

## Concerns

The representative `valuesFrom` value is typed SQL NULL; it proves offline parse, safety classification, and reference binding but does not establish the materialized list's actual types or length. Full materialization, list execution, and live SQL Server behavior remain unverified until Task 3 and the authorized integration gates.

## Review fix round 1

Addressed both Important findings from `task-2-review.md`: compare representative bindings now union setup, baseline, and candidate expansion outputs; measure representative bindings union setup and query outputs. Shared list names are deduplicated, and conflicting representative values for the same generated name fail closed.

Expansion now returns bindings only for list tokens present in that SQL text. This makes each expansion's result reflect its text and requires the compare/measure union above. Repeated references within one text still share one binding set.

### RED / GREEN evidence

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~Does_not_emit_bindings_for_lists_not_referenced_by_this_SQL_text|FullyQualifiedName~Compare_dry_run_includes_a_list_used_only|FullyQualifiedName~Compare_dry_run_deduplicates_a_list_shared|FullyQualifiedName~Measure_dry_run_includes_a_list_used_only|FullyQualifiedName~Measure_dry_run_deduplicates_a_list_shared'` — Failed 1, Passed 5. The new expander contract failed because `SELECT 1` still returned `@Ids1` and `@Ids2`; the operation regressions passed on the previous implementation because it emitted every declared list for every SQL text.
- GREEN: the same command after the change — Passed 6, Failed 0. Covers the per-text binding contract, candidate-only compare list, setup-only compare list, setup-only measure list, and one binding for list use across multiple SQL texts.
- Final focused command:
  `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~SqlHarnessCompareTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~SqlHarnessMeasureTests|FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~SnapshotTests|FullyQualifiedName~SnapshotScopeTests|FullyQualifiedName~SqlHarnessQueryTests|FullyQualifiedName~ListParameterExpanderTests'`
  — Passed 339, Failed 0, Skipped 0.
- `git diff --check` passed. No full gate or live database was run.

### Changed files in this fix round

- `src/SqlHarness.Core/ListParameterExpander.cs`
- `src/SqlHarness.Core/CompareCellRunner.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Core/SqlSafety.cs`
- `tests/SqlHarness.Tests/ListParameterExpanderTests.cs`
- `tests/SqlHarness.Tests/CompareTests.cs`
- `tests/SqlHarness.Tests/MeasureTests.cs`
