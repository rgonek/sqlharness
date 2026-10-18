# Plan 083 verification evidence

This durable record consolidates the approved task reports formerly stored in the local SDD workspace. Task reports are copied below in full so their test commands, outcomes, changes, and limits remain available after the scratch copies leave the Git index.

Plan: [083 — table-valued and list parameters](083-table-and-list-parameters.md). Feature base: `272d5dbb`; whole-branch review revision before this fix wave: `c94288c6fdfcbe4710924dd0da518494b44d0677`.

## Current final-fix wave

The whole-branch review identified two Important findings. Both product and evidence hygiene are addressed in this wave. The reproduction writer now redeclares and repopulates TVPs inside each generated setup command batch that binds them, then again before the measured batch after setup `GO` separators. Setup command preparation now recognizes table-valued parameters used as table sources (`FROM @Ids`) and includes their structured binding. The source setup SQL retains its existing command splitting and parameter-free temp-table prefix behavior. A fake-session test verifies the parameterized setup command reaches execution with `SqlDbType.Structured` and the resolved type name; safety coverage verifies the TVP is accepted only as a source while populating a local setup table.

The five plan-083 task report files were removed from the Git index with `git rm --cached`; their physical copies remain in the local SDD workspace. The controller merged `main` at `66f47c3` into the feature branch and verified product tree `6c843243a3fc6ae69c08916d7191a51c5e8ecc4e`. Integration commit `348ce019dd0d3c3a59e56c38652d7d423e42c39f` has the same tree. The final Windows gate passed on that tree. The scoped Sol re-review approved both original findings as addressed and found no new Critical or Important findings. The authorized local fast-forward completed at `55d23bafa60edbec1e0d21ce787863794828a253`; the feature has not been pushed. The operator explicitly waived Linux verification. Live SQL Server/SSMS UAT remains pending; no live database was used.

## SDD rulings preserved from progress.md

Ruling: Bundle Task 1b into Task 1; list dry-run wiring completes with Task 2 — dependencies require producer first — cost if wrong: adjust task boundaries and rerun review.
Ruling: Use ParameterKind enum internally with string JSON kinds, rather than conflicting string-plus-enum declarations — plan interface names enum — cost if wrong: public type adjustment.
Ruling: Matrix remains scalar; list-size cases use named parameter sets — explicit review amendment overrides older spec wording — cost if wrong: later matrix extension.
Ruling: No live DB UAT without exact operator authorization; report pending separately — task explicitly requires operator authorization — cost if wrong: acceptance remains unverified.
Ruling: Retain validated string Kind from approved Task 1, superseding the earlier enum preference — explicit model task text uses string and existing JSON transport is string-based; supported values are centrally validated — cost if wrong: future public enum migration and mapper changes.
Ruling: Use 10 vs 5000 TVP rows for large-case coverage and list lengths below 2100 for list version-identity regression — required 2100 command parameter ceiling makes a 5000-value Dapper list impossible — cost if wrong: narrower large-list acceptance example; larger lists remain explicitly rejected.
Ruling: Extend final TVP scope fix to SetupSqlExecution table-source parameter recognition — setup FROM @Tvp was not bound, so script-only declarations would leave accepted runtime setup broken — cost if wrong: shared setup regression; covered by focused command preparation/execution tests and final gate.
Ruling: Apply required just fix only to changed C# files after format gate failure despite general no-auto-formatting constraint — repository verification explicitly requires this correction on failure — cost if wrong: whitespace-only integration diff; inspect no semantic changes.

## Controller gate failure and corrective fix

The controller's `just verify` on `e15137a` failed after build and UI passed. Test totals were Core 4,392 passed / 2 failed / 11 skipped (4,405 total), and MCP 434 passed / 1 failed / 4 skipped (439 total). The failing cases were:

- `DryRunCommandTests.Setup_only_parameter_reference_is_allowed_when_the_batch_needs_no_parameter`
- `DryRunCommandTests.Measure_missing_parameter_is_rejected_and_setup_only_references_are_not_required`
- `McpToolSchemaTests.Fixed_postgres_catalog_carries_only_postgres_guidance`

Root causes: measure and compare dry-run callers included setup SQL in `MissingParameterCheck`, although its documented contract checks measured SQL only; shared parameter and parameter-set-file descriptions also included SQL Server-only guidance in PostgreSQL schemas. The dry-run callers now check measured SQL only. MCP descriptions are selected according to catalog engine, retaining SQL Server guidance in SQL Server schemas.

Corrective product commit: `35b8d4a5664b165f21cb94fa844c77a015376397 fix(083): preserve dry-run and engine-specific contracts`. The exact Core failures were reproduced RED (2 failed) before the product fix and passed GREEN (2 passed) afterward. The additional compare setup-only regression was also RED (1 failed) before and GREEN (1 passed) after. Serial covering results on this fix:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~DryRunCommandTests'
Passed 109, Failed 0, Skipped 0.

dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~McpToolSchemaTests.Fixed_postgres_catalog_carries_only_postgres_guidance|FullyQualifiedName~McpToolSchemaTests.Non_scalar_parameter_descriptions_name_read_only_paths_and_mutation_rejection|FullyQualifiedName~McpToolSchemaTests.Fixed_sql_server_catalog_carries_only_sql_server_guidance'
Passed 3, Failed 0, Skipped 0.

dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~McpToolSchemaTests'
Passed 51, Failed 0, Skipped 0.
```

`git diff --check` passed. The `e15137a` gate failure was corrected by `35b8d4a`; the final integrated gate and intermediate integration failures are recorded below. Linux verification was explicitly waived by the operator; live SQL Server/SSMS UAT remains pending.

## Integrated verification and intermediate gate outcomes

The first integrated gate on staged tree `5f7cd156` passed build, UI, and tests (Core 4,454 passed / 11 skipped; MCP 435 passed / 4 skipped) but failed the format stage. Per `docs/agents/verification.md`, `just fix` formatted 34 changed C# files. It changed 19 files: 18 final-newline corrections and one required blank line in `PlanGraphBuilderTests.cs`. The resulting diff was whitespace-only (`git diff --ignore-all-space --ignore-blank-lines --exit-code` passed); no product behavior changed.

The next integrated gate on tree `6c843243a3fc6ae69c08916d7191a51c5e8ecc4e` built successfully but its UI stage failed only because `SessionVersionDiff.test.tsx`'s `formatting expansion cannot bypass the combined diff size limit` exceeded its 20-second timeout during the 310.74-second full UI run. That run evaluated 214 modules 2,223 times (94.39 seconds of tracked import time). The same test file then passed 7/7 in isolation; an exact isolated run passed 1/1 (6 skipped), with no code or timeout changes. The previous integrated UI stage on the same semantic UI tree had also passed.

The final `just verify -LogDirectory D:/temp/sqlharness-plan083-348ce01` completed successfully: `verify: OK stages=5 14m56s`. It ran on product tree `6c843243a3fc6ae69c08916d7191a51c5e8ecc4e`; integration commit `348ce019dd0d3c3a59e56c38652d7d423e42c39f` preserves that exact tree. UI passed 301/301; Core passed 4,454 with 11 skipped; MCP passed 435 with 4 skipped; build and formatting passed; `ui-install` was skipped because the lockfile was unchanged. Non-empty logs are archived at `D:/temp/sqlharness-plan083-348ce01` (the format log is naturally empty; the console records the pass). Linux remains waived, and no live SQL Server/SSMS UAT was performed.

## Final scoped Sol review and local integration

The scoped final review covered the fixes from `c94288c..dbe35d5`, integration through `348ce01`, and durable evidence through `55d23ba`. It marked the TVP reproduction/setup-batch finding and durable-evidence hygiene finding **ADDRESSED**, found no new Critical or Important issues, and approved the authorized local fast-forward. It did not claim live SQL Server/SSMS UAT or Linux verification. Local `main` is now integrated at `55d23bafa60edbec1e0d21ce787863794828a253`; no push occurred. The remaining acceptance item is operator-authorized live UAT.

## Archived task reports

### Task 1 report

# Plan 083 Task 1 report

## Result

Implemented Task 1 and classifier amendment 1b on base `272d5dbb`. Table-valued and list inputs now have typed Core/MCP representations, bounded fixed-parameter `.sqljson` input via CLI `--params-json`, offline validation and dry-run reporting. Declared TVP names classify as read-only table sources for `SELECT ... FROM @Ids`; DML targets and `OUTPUT INTO` remain denied. Capabilities and MCP/CLI input descriptions document the current contract.

## RED / GREEN evidence

- RED: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter FullyQualifiedName~Parameter_mapping_preserves_table_and_list_sources_and_values` failed to compile because `SqlHarnessParameterInput` had no `Kind`, `Rows`, or `Values` members.
- GREEN: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~Parameter_mapping|FullyQualifiedName~Mutation_mapping_rejects_table'` — Passed 4, Failed 0.
- RED: classifier boundary test failed to compile with the expected missing `tableValuedParameterNames` argument.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~TVP_parameter` — Passed 3, Failed 0 (read source accepted; DML target and `OUTPUT INTO` denied).
- RED: dry-run notes test failed to compile because `SqlHarnessDryRunReport.Notes` did not exist.
- RED: CLI `--params-json` round-trip exited 2 because the option was not recognized.
- RED: typed `.sqljson` set test failed parsing with `invalid JSON at line 2, byte 3` before typed object entries were supported.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~Typed_non_scalar_inputs|FullyQualifiedName~List_inputs_obey|FullyQualifiedName~Scalar_inputs_obey|FullyQualifiedName~Table_inputs_reject|FullyQualifiedName~Non_scalar_input_cannot|FullyQualifiedName~Table_and_list_inputs_are_rejected|FullyQualifiedName~Fixed_parameter_file_parser|FullyQualifiedName~Query_params_json|FullyQualifiedName~Query_dry_run'` — Passed 29, Failed 0.
- GREEN: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~Parameter_mapping|FullyQualifiedName~Mutation_mapping_rejects_table|FullyQualifiedName~Tools_list_fits_32KiB|FullyQualifiedName~Served_tools_list_fits_32KiB'` — Passed 5, Failed 0.
- GREEN: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~Tools_list_fits_32KiB|FullyQualifiedName~Served_tools_list_fits_32KiB'` — Passed 1, Failed 0; serialized tool catalog remains within 32 KiB.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~Query_params_json_round_trips_typed_table_input_through_cli_dry_run|FullyQualifiedName~Query_dry_run_classifies_a_TVP_source|FullyQualifiedName~Fixed_parameter_file_parser_accepts_utf8_bom_and_rejects_duplicate_root_properties'` — Passed 3, Failed 0.
- `git diff --check` — passed.

One concurrent Core/MCP build pair contended on `SqlHarness.Cli/obj/Debug/net10.0/sqlharness.dll` and the MCP build exited with CS2012. Rerunning that MCP command serially passed; this was a build-output race, not a test or source failure.

## Changed files

- `src/SqlHarness.Core/Contracts.cs`, `SqlSafety.cs`, `ParameterSetResolver.cs`, new `ParameterInputFileReader.cs`, `ParameterSetFileReader.cs`, `SqlParameterReferences.cs`, `SqlHarnessModule.cs`, `Capabilities.cs`, `Dialect/ISqlDialect.cs`, `Dialect/SqlServerDialect.cs`, `CompareCellRunner.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`, `Tools/McpToolArguments.cs`, `Tools/McpToolCatalog.cs`, `Tools/McpRequestToolHandlers.cs`
- `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`
- `tests/SqlHarness.Tests/MeasureParameterSetValidationTests.cs`, `ParameterSetFileReaderTests.cs`, `SqlSafetyTests.cs`, `Cli/CapabilitiesCommandTests.cs`, `Cli/DryRunCommandTests.cs`; `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`
- `docs/mcp/inputs.md`, `plans/083-table-and-list-parameters.md`, `plans/README.md`

## Temporary boundary and concerns

Non-scalar execution is intentionally guarded until Task 2+ materializes and binds it. `ParameterSetResolver.RequireScalarExecution` provides the shared guard; it is used in query execution, measure fixed parameters, compare preparation (`CompareCellRunner`), typed measure parameter sets (`ParameterSetResolver.Resolve`), watch, and snapshot paths. Query dry-run is the current path that accepts non-scalar inputs for offline classification and notes. Replace these guards with materialization on every accepting path during subsequent tasks; they are not the final Plan 083 behavior.

No full `just verify` was run because the controller requested focused tests and will run the full gate after all plan tasks. No live database was used and no mutation approval was supplied.

## Review fix round 1

Addressed all three Important findings from `task-1-review.md`:

- Both fixed `--params-json` parsing and typed parameter-set parsing reject `valuesFrom` on a TVP and `rowsFrom` on a list before source SQL is collapsed to `SourceSql`. CLI and file-reader regressions cover both cross-kind forms.
- Empty inline lists now pass offline validation; they remain represented as `Kind = "list", Values = []` for Task 2's required typed-empty expansion.
- Core and MCP errors now exactly match the specified lowercase strings: `table-valued and list parameters are not supported by mutation` and `table-valued and list parameters are SQL Server only`.

Serial verification:

- `git diff --check` — passed.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~ParameterSetFileReaderTests` — Passed 73, Failed 0.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Query_params_json` — Passed 3, Failed 0.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~MeasureParameterSetValidationTests` — Passed 51, Failed 0.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter FullyQualifiedName~Parameter_mapping` — Passed 4, Failed 0.

## Review fix round 1 — final serial verification

Final runs after the empty-list parameter-count boundary was tightened:

- `git diff --check` — passed.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~ParameterSetFileReaderTests` — Passed 73, Failed 0. Covers fixed and set-file parser rejection for both mismatched source fields.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Query_params_json` — Passed 3, Failed 0. Covers CLI `--params-json` round-trip and both cross-kind source rejections.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~MeasureParameterSetValidationTests` — Passed 52, Failed 0. Covers valid empty inline lists, their typed-null final-parameter accounting, and exact Core mutation/PostgreSQL text.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter FullyQualifiedName~Parameter_mapping` — Passed 4, Failed 0. Covers both cross-kind MCP source rejections, empty-list mapping, and exact mutation text.

All four test commands ran sequentially to avoid build-output contention.


### Task 2 report

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


### Task 3 report

# Plan 083 Task 3 report

## Outcome

SQL Server table-valued and list inputs now materialize at the case boundary and flow through every accepting execution path: query, fixed measure, named measure parameter sets, compare, watch (including NDJSON), and snapshot. PostgreSQL rejects non-scalar inputs during validation before execution. Materialization happens once per case after connect and before setup; measure/compare retain the 081 ordering of connect, materialize, setup, capture session state, then measured runs. Compare reuses the same materialized TVP/list bindings for baseline and candidate.

TVP types resolve against SQL Server catalog metadata, including schema, ordered columns, SQL type, size, precision, scale, and nullability. Inline rows convert against the resolved shape; rows-from SQL must pass SQL classification before it is dispatched and its result metadata must match the declared shape exactly. TVPs bind through SqlClient `SqlDbType.Structured` with the resolved qualified `TypeName`, including empty TVPs. List placeholders expand to referenced values only. Parameter, row/value, and byte limits are enforced across each case, including the 2100 final command-parameter limit.

## Task 4 seam

`MaterializedParameters.Details` exposes one `MaterializedParameterInfo` per non-scalar input with `Name`, `Kind`, `DeclaredType`, `ResolvedType`, ordered `Columns`, `Count`, `SourceSql`, materialized `Rows`, and `Values`. `MaterializedParameters.Inputs` retains the original inputs and `Expand(sql)` provides the exact expansion used for execution. No unkeyed content hash is produced. Task 4 can re-record the resolved metadata and content using ActivityJournal-owned HMAC while preserving the original inputs for version identity.

## TDD and focused verification

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~TableParameterMaterializerTests` failed at compile time because `SqlServerTableTypes` and `TableParameterMaterializer` did not exist.
- GREEN: the materializer tests passed after implementation, covering catalog resolution, empty TVP binding, supported inline type conversions, shape mismatch, source result type mismatch, and streamed row limits.
- Latest covering run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~SqlHarnessQueryTests|FullyQualifiedName~SqlHarnessCompareTests|FullyQualifiedName~SqlHarnessMeasureTests|FullyQualifiedName~SqlHarnessWatchTests|FullyQualifiedName~SqlHarnessSnapshotTests|FullyQualifiedName~TableParameterMaterializerTests|FullyQualifiedName~MeasureParameterSetValidationTests|FullyQualifiedName~MeasureParameterSetExecutionTests'` — Passed 250, Failed 0, Skipped 0.
- Latest MCP surface check: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter 'FullyQualifiedName~Tools_list_fits_32KiB|FullyQualifiedName~Served_tools_list_fits_32KiB'` — Passed 1, Failed 0, Skipped 0.
- `git diff --check` — passed.

The focused tests include list execution on query/watch/snapshot, list execution on both compare sides, named parameter sets with different list lengths and a 5000-row TVP input, per-case materialization, and null `ValueHash` when a non-scalar input is present. The 5000-row case uses TVP; list cases remain under the SQL Server 2100 parameter limit.

## Files changed

- Added `src/SqlHarness.Core/Dialect/SqlServerTableTypes.cs` and `src/SqlHarness.Core/TableParameterMaterializer.cs`.
- Updated execution binding, parameter set resolution/validation, query/measure/compare/watch/snapshot paths, and measure artifact `ValueHash` nullability for non-scalar inputs.
- Updated MCP measure description and `docs/mcp/tools.md` for the absence of an unkeyed value hash on non-scalar sets.
- Added materializer tests and focused query/compare/measure/watch/snapshot and parameter-set coverage.

## Limits and concerns

No live SQL Server database was used, so catalog/source behavior against an actual server remains unverified. The requested full verification gate is intentionally left to the controller. The current scoped tests and in-memory SqlClient binding checks pass. No other known implementation blocker remains.

Initial implementation commit: `172a01b feat(083): resolve and bind table and list parameters`.

## Fix round 1/5

Addressed both Important findings from `task-3-review.md`:

- `rowsFrom` now compares each source column name with the catalog TVP column at the same ordinal, in addition to the existing type/size/precision/scale checks. It rejects a source NULL immediately with the source row number and target column name when the target column is non-nullable.
- Shared MCP parameter descriptions now name the supported read-only execution paths. Parameter-set-file descriptions identify read-only measure/compare support. Mutation's tool description and shared parameter field retain the explicit non-scalar rejection.
- Added regressions for same-typed source columns in the wrong order/name and NULL into a non-nullable catalog column; added a served-schema assertion for parameter and parameterSetFiles contract text.

TDD evidence:

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~TableParameterMaterializerTests` — Failed 2, Passed 6. The same-typed reordered source was accepted; the NULL test exposed that the later record conversion rejected it without source row/column context.
- GREEN materializer: same command — Passed 8, Failed 0, Skipped 0.
- GREEN MCP schema: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter FullyQualifiedName~Non_scalar_parameter_descriptions_name_read_only_paths_and_mutation_rejection` — Passed 1, Failed 0, Skipped 0.
- GREEN catalog budget: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter FullyQualifiedName~Served_tools_list_fits_32KiB_without_full_examples_or_runtime_cuts` — Passed 1, Failed 0, Skipped 0.

The NULL path is now explicitly checked while reading `rowsFrom` output, before records are constructed. No live DB or full verification gate was run, per controller instructions.

Fix-round commit message: `feat(083): validate rowsFrom column identity and nulls`.


### Task 4 report

# Task 4 report — conditions, versions, labels, and reproduction scripts

## Result

Implemented post-materialization journal conditions for table-valued and list parameters, keyed content fingerprints, readable case labels, and SQL Server reproduction scripts. Operation version identity continues to use the original unexpanded SQL text. No unkeyed content hash was added.

## TDD and verification evidence

Behavioral RED checks were run first:

- The conditions test filter failed to compile because `ConditionsModelBuilder.WithMaterializedParameters` and `ConditionsParameter.ContentHash` did not exist yet.
- The reproduction tests exposed the missing TVP declaration/rows/binding, missing `rowsFrom` insertion and re-query label, and unexpanded list SQL.
- The empty-list reproduction test exposed that a typed `DBNull` was incorrectly treated as a missing stored value; it was fixed to produce the typed empty-list predicate.

After implementation, the focused conditions, labels, reproduction, version-identity, compare-matrix, benchmark-journaling, and measure-set tests passed:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~ConditionsModelBuilderTests|FullyQualifiedName~CaseLabelTests|FullyQualifiedName~SqlServerReproScriptWriterTests|FullyQualifiedName~OperationJournalDescriberTests|FullyQualifiedName~JournalingBenchmarkTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~MeasureParameterSetExecutionTests'
Passed!  - Failed: 0, Passed: 157, Skipped: 0, Total: 157
```

The named-set integration test uses a fake session/materializer and a real `ActivityJournal`. It verifies resolved `dbo.IdList READONLY`, per-case row counts and ordered columns, distinct nonempty HMAC content hashes, no stored raw rows/source text when sensitivity storage is disabled, and that conditions are recorded before warmup/measured execution:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Named_set_conditions_record_resolved_type_and_rows_from_content_before_measurement
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

The same 157-test covering command was rerun after the integration test and callback changes and passed as shown above. `git diff --check` completed without whitespace errors.

## Changes

- `ConditionsModelBuilder` enriches per-case and fixed conditions from materializer details, records resolved types/counts/ordered columns, and holds transient content bytes for sealing. Conditions model version is now 2.
- `ActivityJournal` computes keyed HMAC content hashes with its journal salt before building fingerprints. Its output-size limiter now removes oversized values, rows, lists, or source SQL while retaining omission evidence and non-sensitive metadata. `storeSensitive=false` removes raw scalar values, rows, lists, and source text while preserving counts, types, columns, and HMACs.
- Compare, matrix, and measure flows re-record enriched conditions immediately after materialization and before setup or measured work. Matrix and named-set paths accumulate already materialized cases so subsequent cases do not erase earlier case content.
- Labels use the required TVP and short/long list forms. Sensitive-off labels show counts without raw values.
- SQL Server reproduction writes TVP declarations and stored rows (at most 1,000 rows per `INSERT`), emits `rowsFrom` as a re-query with the data-change label, binds TVPs as `READONLY`, and expands list parameters, including typed empty lists.
- A regression verifies legal list sizes do not change the version identity derived from unexpanded operation SQL.

## Files changed

Core: `CaseLabel.cs`, `CompareCellRunner.cs`, `CompareMatrixRunner.cs`, `Journal/ActivityJournal.cs`, `Journal/ConditionsModel.cs`, `Journal/ConditionsModelBuilder.cs`, `MeasureParameterSets.cs`, `Repro/SqlServerLiteral.cs`, `Repro/SqlServerReproScriptWriter.cs`, `SqlHarnessModule.cs`.

Tests: `CaseLabelTests.cs`, `Journal/ConditionsModelBuilderTests.cs`, `Journal/OperationJournalDescriberTests.cs`, `MeasureParameterSetExecutionTests.cs`, `Repro/SqlServerReproScriptWriterTests.cs`.

## Interfaces and boundaries

The implementation consumes Task 3's `MaterializedParameterInfo` details (`Name`, `Kind`, `DeclaredType`, `ResolvedType`, ordered `Columns`, `Count`, `SourceSql`, `Rows`, and `Values`) and records them through `IActivityJournal.RecordConditions`. It preserves original parameter inputs and unexpanded SQL for operation identity. No live database, full `just verify`, or controller-owned final gate was run.

## Concerns

No unresolved Task 4 concern identified. SQL Server script behavior is covered by unit tests; no live SQL Server execution was performed. The existing conditions size cap remains authoritative and oversized content is represented by omission metadata.

## Fix round 1 — review findings

Addressed all three Important findings from `task-4-review.md`:

- Missing stored list values remain distinguishable from an explicitly stored empty list and mark reproduction `ValuesMissing` / `Incomplete — values not stored`.
- Generated list parameters resolve their source by exact base name or a numeric suffix, preferring the longest matching source name. This preserves types for overlapping names such as `@Id` and `@Ids`.
- Conditions size omission rebuilds all case labels after removing fields from fixed or varying parameters. A fixed list's label now falls back to its count when values are omitted, allowing the serialized conditions to remain within the existing limit.

The three new regressions were run red first:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~Omitted_list_values_are_not_reproduced_as_a_complete_empty_list|FullyQualifiedName~Overlapping_list_names_keep_each_expanded_parameters_source_type|FullyQualifiedName~Oversized_fixed_list_values_are_omitted_and_case_label_is_rebuilt_within_conditions_bound'
Failed: 3, Passed: 0, Skipped: 0, Total: 3
```

After the fixes, the focused regressions passed:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~Omitted_list_values_are_not_reproduced_as_a_complete_empty_list|FullyQualifiedName~Overlapping_list_names_keep_each_expanded_parameters_source_type|FullyQualifiedName~Oversized_fixed_list_values_are_omitted_and_case_label_is_rebuilt_within_conditions_bound'
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3
```

The serial covering group, including those regressions, passed:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~ConditionsModelBuilderTests|FullyQualifiedName~CaseLabelTests|FullyQualifiedName~SqlServerReproScriptWriterTests|FullyQualifiedName~OperationJournalDescriberTests|FullyQualifiedName~JournalingBenchmarkTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~MeasureParameterSetExecutionTests'
Passed!  - Failed: 0, Passed: 160, Skipped: 0, Total: 160
```

`git diff --check` passed. No live database or full verification gate was run; those remain controller-owned.


### Task 5 report

# Plan 083 Task 5 report

## Status

Documentation Task 5 is implemented on the `plan-083` feature branch. Tasks 1–4 were reported implemented and approved by their task owners. This report does not mark Plan 083 complete: controller whole-branch review and final `just verify` are pending, and live SQL Server UAT was not authorized or performed.

## Documentation changes

- README parameter and safety sections explain SQL Server TVP/list inputs, exact source choices, read-only source classification, per-case materialization order, Dapper and empty-list behavior, limits, mutation rejection, offline dry-run limits, journal sensitivity, keyed content fingerprints, reproduction scripts, and unexpanded version identity.
- `docs/mcp.md` points to the MCP input contract and README reference and describes the accepted read-only SQL Server path and mutation boundary.
- The shared MCP parameter description now states that each non-scalar kind requires exactly one inline or query source and that source SQL passes read-only classification. Capabilities already expose all four configured limits, so no capability shape change was needed.
- `skills/sqlharness/SKILL.md` now directs users to pass application TVPs/lists as parameters and check the schema/capabilities for kinds and limits.
- The TVP substitution observation is marked addressed by Plan 083 with the remaining review, gate, and live UAT limits stated.
- The plan and queue now show documentation implemented while controller review, final gate, and UAT remain pending.

## Scoped checks

- `git diff --check`: passed.
- Relative Markdown link targets: passed for all changed Markdown files. README parameter anchor and MCP input guide anchor resolve; installed skill frontmatter is valid.
- Existing docs-sync examples were not changed; no new examples were added.
- No application tests or full verification gate run; neither was authorized for this documentation task.

## Remaining evidence

Controller review, final `just verify` on the reviewed product tree, and operator-authorized UAT (500-row `rowsFrom`, empty TVP, list matrix, and SSMS reproduction scripts) remain pending. No database behavior or integration into `main` is claimed.

