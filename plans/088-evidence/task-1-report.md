# SQLHarness 088 Task 1 report

## Implementation and contracts

- Replaced fresh schema `plans` and `operation_plans` with `plan_documents` keyed by uppercase SHA-256 of the document's raw UTF-8 bytes and `plan_occurrences` keyed by metric, repetition, document ordinal, and statement index. `shape_hash` retains `PlanIdentity.Hash` as the plan-shape identity.
- Bumped the fresh schema to v17 and set v17 as the first retained version. Existing v14-v16 journals are archived and replaced; their rows are not migrated. The occurrence schema already reserves Task 2's `span_start`, `span_length`, `match_kind`, `statement_id`, and `statement_comp_id`; spans and server ids default to NULL and `match_kind` defaults to `unmatched`.
- Added a named 16 MiB per-operation budget over raw UTF-8 sizes of distinct document SHA values admitted by that operation. Repeated references to one SHA cost bytes once for that operation regardless of global storage; over-budget occurrences have `omitted = 1` and no document SHA. A later smaller document can still fit. The internal open overload accepts a budget for focused tests.
- `journal.storeSensitive` now gates both documents and occurrences. Retention deletes unreferenced plan documents. The existing reader's plan lookup and variant query use the new tables so the intermediate tree builds and reads current journals.
- The existing `JournalPlanLink.Hash` field is transitional and now carries the document SHA; Task 5 will replace the existing endpoint and payload contracts. Omitted rows are persisted explicitly, but the current dashboard payload does not yet expose an omission reason.

## TDD and verification evidence

- RED: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Equal_shape_statements_keep_distinct_content_addressed_documents_per_repetition` failed as expected: `Expected: 2`, `Actual: 1`. The old builder keyed both documents by `QueryPlanHash`, collapsing the equal-shape documents.
- GREEN: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~ActivityJournalBenchmarkTests|FullyQualifiedName~JournalContractTests|FullyQualifiedName~JournalRetentionTests|FullyQualifiedName~JournalingBenchmarkTests|FullyQualifiedName~JournalReaderTests|FullyQualifiedName~MutationJournalTests|FullyQualifiedName~DashboardServerTests"` passed: 176 passed, 0 failed, 0 skipped.
- Coverage includes separate stored actual plans by repetition, equal-shape/different-text statements, identical-byte deduplication, explicit budget omission plus later admission using a multi-byte UTF-8 character, no documents or occurrences with the gate off, reserved v17 defaults, no row migration from v16, and orphan cleanup.
- The one-time full test project run completed at 4,322 passed, 29 skipped, and 3 failed. Two failures were stale assertions caused by this change (schema expected 16; hash-only links were expected with `storeSensitive=false`) and were corrected. `DashboardLifecycleTests.Shutdown_uses_guarded_loopback_post_without_token_in_url_and_does_not_follow_redirects` failed in the parallel run because the captured request trace was empty (`""`, expected `POST /api/shutdown HTTP/1.1`); its isolated rerun passed 1/1 in 223 ms. The full repository verification gate was not run per controller instruction; the baseline gate already had an unrelated MCP handshake regression.
- Isolated transient-failure check: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Shutdown_uses_guarded_loopback_post_without_token_in_url_and_does_not_follow_redirects` passed 1/1.

## Files changed

- `src/SqlHarness.Core/Journal/JournalSchema.cs`
- `src/SqlHarness.Core/Journal/BenchmarkJournalRecord.cs`
- `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs`
- `src/SqlHarness.Core/Journal/ActivityJournal.cs`
- `src/SqlHarness.Core/Journal/JournalRetention.cs`
- `src/SqlHarness.Core/PlanIdentity.cs`
- `src/SqlHarness.Dashboard/JournalReader.cs`
- Journal and dashboard tests under `tests/SqlHarness.Tests`

## Self-review and concerns

- The per-operation budget and omitted-row decision are deterministic and transaction-scoped. SHA is calculated from raw document text before compression, while shape hashing remains separate.
- Span pairing is intentionally not implemented in this task; the schema records the required Task 2 fields as unmatched defaults. UI/API presentation of omitted occurrences remains for Task 5.
- The full suite did not pass in one parallel run because of the single dashboard lifecycle trace failure above. The scoped implementation suite passed after corrections, and the lifecycle test passed alone.

## Review round 1 fix

- `JournalBenchmarkBuilder` now emits one occurrence per document-local `StmtSimple` index instead of fixing every occurrence at index 0. Statement counting uses `StatementMetricsExtractor`'s DTD-prohibited, resolver-disabled, 16 Mi-character-bounded XML reader. Documents without a parseable Showplan statement retain a fallback occurrence at index 0 so non-Showplan and malformed plan documents still preserve their document occurrence.
- Added a multi-statement Showplan storage test that verifies both occurrence rows use the same repetition/document ordinal/document SHA and have indices 0 and 1. Added a writer test proving a repeated SHA at an exact 4-byte operation budget is stored for both repetitions and charged once.
- RED: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~Multi_statement_documents_persist_one_occurrence_per_document_statement_index` failed as expected with `Expected: 2`, `Actual: 1`.
- GREEN: the same command after implementation passed: 1 passed, 0 failed, 0 skipped.
- Scoped regression command: `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~ActivityJournalBenchmarkTests|FullyQualifiedName~JournalContractTests|FullyQualifiedName~JournalRetentionTests|FullyQualifiedName~JournalingBenchmarkTests|FullyQualifiedName~JournalReaderTests|FullyQualifiedName~MutationJournalTests|FullyQualifiedName~DashboardServerTests|FullyQualifiedName~StatementMetricsTests|FullyQualifiedName~PlanMetricsExtractorTests"` passed: 198 passed, 0 failed, 0 skipped. After adding the exact-fit repeated-SHA test, the focused builder/writer command `dotnet test tests\SqlHarness.Tests\SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~ActivityJournalBenchmarkTests"` passed: 25 passed, 0 failed, 0 skipped.
- Files amended: `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs`, `src/SqlHarness.Core/StatementMetrics.cs`, and `tests/SqlHarness.Tests/Journal/ActivityJournalBenchmarkTests.cs`.
