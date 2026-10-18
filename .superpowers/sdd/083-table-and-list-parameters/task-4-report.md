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
