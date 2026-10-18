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
