# SQLHarness 088 Task 2 report

## Implementation and contracts

- Added `StatementSpanPairer`, which parses executed SQL through the shared `SqlServerDocument` ScriptDom entry point and reads Showplan `StmtSimple` text and server statement IDs with DTD processing disabled and the existing 16 Mi-character plan-document bound.
- Pairing searches forward through ScriptDom statement spans, so planless `DECLARE` and `CREATE TABLE` statements do not shift later matches. It records `exact`, `psp_suffix`, `normalized`, `prefix`, `ambiguous`, or `unmatched`; normalized matching ignores whitespace tokens while retaining literal and comment tokens. A duplicated 3999-character prefix stays ambiguous.
- `BenchmarkRunner` retains the exact SQL passed to the dialect in a `JsonIgnore` journal-only field. The builder uses this transformed/executed text for occurrence spans and statement hashes. It persists spans and `StatementId`/`StatementCompId` into the existing occurrence columns without changing the schema version. SQL and plan documents remain behind the existing `journal.storeSensitive` write gate.
- Unparsable executed SQL yields unmatched occurrence rows while retaining the plan document. PostgreSQL JSON plans continue to have no SQL Server span pairs.

## TDD and verification evidence

- RED: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~StatementSpanPairerTests` initially failed to compile because `StatementSpanPairer` did not exist (seven CS0103 errors). This confirmed the new tests were not passing against the pre-implementation tree.
- During implementation, the focused pairer run also exposed bad test XML composition and mismatch handling for terminators; the fixtures and implementation were corrected, then the focused suite passed.
- GREEN, focused pairing and persistence: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~StatementSpanPairerTests|FullyQualifiedName~Multi_statement_documents_persist_one_occurrence_per_document_statement_index"` passed: 8 passed, 0 failed, 0 skipped.
- GREEN, final relevant regression set: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~StatementSpanPairerTests|FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~ActivityJournalBenchmarkTests|FullyQualifiedName~JournalContractTests|FullyQualifiedName~JournalRetentionTests|FullyQualifiedName~JournalingBenchmarkTests|FullyQualifiedName~StatementMetricsTests|FullyQualifiedName~PlanMetricsExtractorTests"` passed: 84 passed, 0 failed, 0 skipped.
- `git diff --check` passed. The full `just verify` gate was not run, per the controller's Task 2 instructions; it remains for Task 7.

## Files changed

- `src/SqlHarness.Core/Plans/StatementSpanPairer.cs`
- `src/SqlHarness.Core/Artifacts.cs`
- `src/SqlHarness.Core/BenchmarkRunner.cs`
- `src/SqlHarness.Core/Journal/BenchmarkJournalRecord.cs`
- `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs`
- `src/SqlHarness.Core/Journal/ActivityJournal.cs`
- `src/SqlHarness.Core/PlanIdentity.cs`
- `tests/SqlHarness.Tests/StatementSpanPairerTests.cs`
- `tests/SqlHarness.Tests/Journal/ActivityJournalBenchmarkTests.cs`
- `plans/088-plan-visualization.md`
- `plans/README.md`

## Self-review and concerns

- All Task 2 match states and the whitespace, PSP suffix, long statement, ambiguous prefix, planless statement, and parser-failure cases are covered. The journal integration check verifies exact occurrence spans are paired with server IDs and the statement hash is computed from the executed SQL span.
- No schema bump was made because Task 1 had already reserved these columns. The existing per-operation document byte budget and storage gate are unchanged.
- No implementation concerns remain. Full repository verification is pending Task 7.

## Review-fix round (2026-10-18)

- `StatementSpanPairer.PairDocuments` parses the execution once and processes plan statements in document and statement order with one shared cursor. Repeated candidate spans are deferred; they remain `ambiguous` unless a later unique statement anchors one remaining candidate or equal-cardinality, same-signature repetitions establish the ordered mapping. The journal occurrence and statement-metric paths use this same pairing.
- Normalized matching now compares token-type/text sequences with explicit token boundaries and ignores only whitespace tokens. PSP suffix removal recognizes a balanced, terminal `OPTION (PLAN PER VALUE(...))` token sequence at the statement's top level, so matching text inside comments and literals is retained. The persisted-occurrence regression asserts exact offsets and lengths.
- `StatementMetricsExtractor` hashes the full executed span when pairing succeeds. Both `JournalBenchmarkBuilder.BuildStatements` and `ArtifactStatementProjector.Project` pass the shared document pairs and exact execution text to it, so report/artifact and journal consumers use the same statement identity.
- Execution-text callsite trace: `BenchmarkRunner.ExecuteAsync` retains the incoming `sql` value in `CompareRunArtifact.ExecutedSql` (`src/SqlHarness.Core/BenchmarkRunner.cs:89`). `SqlServerDialect` constructs `SqlExecutionCommand(sql, parameters, timeoutSeconds)` without transforming `sql` (`src/SqlHarness.Core/Dialect/SqlServerDialect.cs:154`), and `SqlExecution.BindCommand` assigns `execution.Sql` directly to `SqlCommand.CommandText` (`src/SqlHarness.Core/SqlExecution.cs:573`). Parameters are bound separately. Thus the retained string is the exact statement text sent through this command path.
- The store-sensitive gate and schema v17 remain unchanged; the existing per-operation 16 MiB raw plan-document budget remains covered by the focused journal tests. No live database was used.

### Review-fix TDD and verification

- RED: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~StatementSpanPairerTests|FullyQualifiedName~Ordered_plan_documents_share_statement_pairing|FullyQualifiedName~Multi_statement_documents_persist_one_occurrence_per_document_statement_index|FullyQualifiedName~Statement_metrics_hash_uses_the_paired_full_executed_span"` failed 6 tests and passed 9. The failures covered token-boundary collision, repeated normalized/exact/PSP candidates, cross-document repeated SQL reusing the first span, and the artifact report hashing the truncated Showplan text.
- GREEN: the same focused command passed: 15 passed, 0 failed, 0 skipped. Comment/literal PSP-safety cases passed in a separate focused run: 2 passed, 0 failed, 0 skipped.
- GREEN, final relevant journal and metrics regression set: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~StatementSpanPairerTests|FullyQualifiedName~StatementMetricsTests|FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~ActivityJournalBenchmarkTests|FullyQualifiedName~JournalContractTests"` passed: 57 passed, 0 failed, 0 skipped. This includes schema, sensitive-storage gate, and budget coverage.
- `pwsh ./scripts/format-changed.ps1 -Mode working-tree` passed (`format: OK`) after the formatter corrected import spacing and final newlines; `git diff --check` passed. The full `just verify` gate remains deferred to Task 7; the baseline MCP handshake failure is recorded in the Task 0 report.
