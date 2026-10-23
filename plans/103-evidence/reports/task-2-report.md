# Task 2 — Persist and serve controlled statement evidence

## Result

Implemented a versioned, bounded statement-evidence projection in `statements.json` and served it through the existing offline `ArtifactReader` statement section. The artifact write does not rerun SQL or require journal/dashboard retention. The projection reuses `JournalStatementMetricsExtractor`, `StatementReconciler`, and `StatementAttributionClassifier` for each saved measured run, preserving one row per variant, parameter set, matrix cell, repetition, and statement ordinal.

The statement section carries `EvidenceVersion = 1`, `EvidenceAvailability` (`available` / `unavailable`), `CoverageBasis = "collected-statement-counters"`, and a coverage description that explicitly says the attribution does not establish complete query cost. Each row carries `CounterAttribution` (`complete` / `partial` / `unavailable`), `StatementIdentityComplete`, and its repetition plus optional plan document/index/hash identity. Legacy statement files read with unavailable attribution and no inferred run identity. A PostgreSQL artifact with no SQL Server statement attribution rows reads as unavailable with an empty row list.

Hidden-work projection admits only `scalar-udf` and `multi-statement-tvf`, caps names at 512 UTF-8 bytes on a Unicode scalar boundary, and includes `NameTruncated`. Corresponding row limitations remain present even when counter attribution is complete. Existing statement paging and continuation fields remain in use. The statement evidence row does not add per-object IO diagnostics; those remain for Plan 105.

## Task 3 interface

- Read `ArtifactStatementsSection` via `ArtifactReader.ReadSection(..., "statements", cursor: ..., pageSize: ...)` or the existing artifact statement MCP section.
- Use section `ArtifactId` as the artifact reference. Identify a statement within it by the complete tuple `(Variant, ParameterSet, MatrixCell, StatementOrdinal, Repetition)`. Optional `PlanDocumentOrdinal`, `PlanStatementIndex`, and `PlanHash` provide plan identity details.
- Use row `CounterAttribution` only as collected statement-counter coverage. `CoverageBasis` and `CoverageDescription` describe that boundary. Use row `Limitations` for per-case caveats; scalar UDF and multi-statement TVF rows retain their limitation even when attribution is complete.
- `ArtifactHiddenWork` exposes `Kind`, `Name`, `NameTruncated`, `Executions`, and `SharePercent`. The persisted row also retains bounded `TopOperators` for the existing interface.
- Section `Continuation` and `Cursor` continue to describe source paging. Page limits remain governed by the existing reader (`MaxPageSourceItems`, default 128 statement rows).
- Missing/legacy evidence is represented as `unavailable`; no zero counter or complete status is inferred. Task 3 may keep statement response and MCP descriptions in its own scope.

## RED / GREEN evidence

RED command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -m:1 -p:SkipDashboardUi=true --filter "FullyQualifiedName~Statements_section_preserves_repetition_identity_attribution_and_hidden_work|FullyQualifiedName~Legacy_statement_rows_and_postgres_artifacts_report_unavailable_attribution|FullyQualifiedName~Statements_section_marks_incomplete_identity_partial_and_keeps_node_collisions_separate" --logger "console;verbosity=normal"
```

The three new behavior tests compiled and failed because the current artifact projection did not return `evidenceVersion`, `counterAttribution`, `evidenceAvailability`, repetition identity, or statement-identity completeness.

Final focused GREEN command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -m:1 -p:SkipDashboardUi=true --filter "FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~StatementMetricsTests|FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~AgentOutputTests" --logger "console;verbosity=minimal"
```

Result: passed, 207/207 tests, 0 skipped. This includes legacy and PostgreSQL unavailable cases, incomplete identity, two statements sharing operator node IDs, long multi-byte function names with truthful truncation, continuation, Task 1 UDF/TVF and matrix fixtures, and output projection compatibility.

Formatter evidence: `pwsh ./scripts/format-changed.ps1 -Mode working-tree` initially reported whitespace/final-newline differences. `pwsh ./scripts/format-changed.ps1 -Mode working-tree -Fix` exited 0 (`format: OK`), then the direct working-tree format check exited 0 (`format: OK`). The 207-test run preceded this whitespace-only formatting step; tests were not repeated afterward. The full `just verify` gate was intentionally left to the controller.

## Files changed

- `src/SqlHarness.Core/Artifacts.cs` — no change; the existing artifact writer persists the `ArtifactStatementProjector` result to `statements.json`.
- `src/SqlHarness.Core/StatementMetrics.cs` — per-run reconciliation and versioned row projection; bounded hidden-work mapping; existing paired statement hashes and operator summaries retained.
- `src/SqlHarness.Core/ArtifactReader.cs` — statement evidence model and conservative legacy normalization; preserves existing page/cursor behavior.
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs` — writer/reader, legacy, PostgreSQL, incomplete identity, collisions, long-name clipping, and current-version continuation coverage.
- `tests/SqlHarness.Tests/StatementMetricsTests.cs` — changed the prior repetition-aggregation assertion to verify per-repetition node paths.

## Scope and remaining checks

No live database, journal endpoint, dashboard API, MCP schema/docs, measure/compare compact response, full gate, merge, or push was used. The whole-branch gate and task review remain controller-owned. No product limitation is known beyond bounded evidence omission already reported by `OmittedStatements` and the coverage/hidden-work qualifications above.

## Review fix — hidden-work list omission metadata

Review identified that `.Take(16)` bounded the visible hidden-work list without telling callers how many known entries were dropped. Added nullable row field `HiddenWorkOmitted`: versioned artifacts write the exact omitted count (including zero), while legacy artifacts keep the field null. The row's `Limitations` are now computed from all recognized hidden-work entries before applying the visible-list cap, so an omitted TVF row still contributes its limitation. Name-level `NameTruncated` remains unchanged.

Regression: one saved statement plan with 18 actual multi-statement TVF operators round-trips 16 visible hidden-work rows, `hiddenWorkOmitted = 2`, and the TVF limitation. RED command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -m:1 -p:SkipDashboardUi=true --filter "FullyQualifiedName~Statements_section_reports_hidden_work_rows_omitted_by_the_per_statement_cap" --logger "console;verbosity=normal"
```

RED result: 1 failed as expected with `KeyNotFoundException` for missing `hiddenWorkOmitted`.

GREEN command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -m:1 -p:SkipDashboardUi=true --filter "FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~StatementMetricsTests" --logger "console;verbosity=minimal"
```

GREEN result: passed, 100/100, 0 skipped. No full gate was run. No tests have been run after this change's formatting step.

Formatter follow-up: `pwsh ./scripts/format-changed.ps1 -Mode working-tree` reported only final-newline differences in the three changed C# files and exited 2. `pwsh ./scripts/format-changed.ps1 -Mode working-tree -Fix` exited 0 (`format: OK`). No tests were rerun after this whitespace-only fix; the controller will run the final branch-format gate.
