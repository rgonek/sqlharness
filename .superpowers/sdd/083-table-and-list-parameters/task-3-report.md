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
