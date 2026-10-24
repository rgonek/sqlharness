# Plan 104 Task 2 implementation report

## Result

Centralized SQL Server STATISTICS IO name classification and carried the typed identity through detailed parsing, incremental accumulation, omitted-text fallback, benchmark reports, journal summaries, and statement reconciliation.

`StatisticsIoTableIdentity.FromRawName` classifies the decoded raw producer name before temp-name normalization. It recognizes only the exact bare `#` plus eight hexadecimal characters as an anonymous table-variable identity. Names whose explicit-local suffix is recognized by `TempTableKey` keep local-temp provenance and the normalized base name. Other forms remain Unknown. Historical rows and manually constructed counters without producer metadata remain Unknown and are never reclassified from their text.

Light and detailed paths now share that classification. Detailed counters and the accumulator group anonymous table variables by identity within each run. The bounded message buffer also retains the prior raw per-run logical-read map, so omitted text can use the complete accumulator for typed summaries without replacing the existing per-run map with a synthetic name. LOB logical reads remain included in logical-read totals.

The SQL Server omitted-text fallback produces typed table summaries when detailed IO evidence is complete, even when the message window omitted text. Incomplete detailed evidence still leaves typed distributions unavailable. Statement reconciliation uses producer identity for new observed rows; legacy observed hashes remain exact Unknown names.

Benchmark total-read maps, distributions, journal rows, and median-run selection group by typed identity. `AllocateDisplayNames` reserves every real object name first. It leaves each real exact key intact and labels the anonymous group `table variables` when that key is free, otherwise `table variables (aggregate)`, then numbered variants until the label is free. Names that already resemble a disambiguated label remain intact.

Compare baseline and candidate reports share one label map built from both sides. Journal rows share one label map built from the full operation, while their metric sums remain grouped by variant, parameter set, and matrix cell. This keeps the anonymous group aligned in asymmetric cases where only one side has a real object named `table variables` or an aggregate-like label. No persisted typed-provenance schema was added; new journal rows contain the finalized collision-safe labels, and old saved rows are not reinterpreted.

## Evidence

The new collision regression first failed as expected: the aggregate distribution overwrote the real `table variables` entry (`Expected: 7`, `Actual: 30`). Its raw RED output is in `plans/104-evidence/implementation-task-2/red-label-collision.log`.

The final focused test command was:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~StatisticsParserTests|FullyQualifiedName~StatisticsIoDetailParserTests|FullyQualifiedName~BenchmarkSummaryTests|FullyQualifiedName~StatementReconcilerTests|FullyQualifiedName~JournalBenchmarkBuilderTests|FullyQualifiedName~DialectMessageBudgetTests" --no-restore
```

Result: 91 passed, 0 failed, 0 skipped. The raw output is in `plans/104-evidence/implementation-task-2/focused-green-final.log`. Coverage includes the complete detail-accumulator fallback after message omission, preservation of the raw per-run name map, exact aggregate-label collisions including existing suffix-like names, paired compare and journal label stability, and Unknown behavior for legacy hashes.

`git diff --check` passed. No full `just verify`, live database test, merge, or push was performed; the review follow-up and controller-owned Task 3 sequencing are recorded below.

## Fix round 1: mixed-message completeness (review P2)

The review found that a 3615 message with one parseable row and another table row missing a parseable logical-read counter was treated as complete. `ParseRows` now reports message-level completeness. Any unrecognized table row makes the accumulator incomplete, while successfully parsed rows are still retained. Recognized columnstore `Segment reads` and `Segment skipped` lines remain ignorable. The omitted-text fallback therefore cannot publish typed summaries when a mixed message made the detail evidence incomplete.

The RED command targeted `Mixed_valid_and_unparseable_table_rows_mark_accumulation_incomplete` and `Mixed_unparseable_io_prevents_omitted_text_fallback_from_claiming_complete_identity`. Both failed before the fix as expected: the accumulator remained complete and the omitted-text path exposed typed identity. Raw output: `plans/104-evidence/implementation-task-2/fix-round1-red.log`.

The two new regressions passed after the fix (2 passed, 0 failed), recorded in `plans/104-evidence/implementation-task-2/fix-round1-focused-green.log`. The complete affected classes then passed with this command:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~StatisticsIoDetailParserTests|FullyQualifiedName~DialectMessageBudgetTests" --no-restore
```

Result: 22 passed, 0 failed, 0 skipped. Raw output: `plans/104-evidence/implementation-task-2/fix-round1-green.log`. Provider framing for this mixed case remains unverified; the synthetic regression directly exercises the bounded parser and fallback contract.

## Review boundary

This task changes Core aggregation and journal input only. The dashboard fold-removal and MCP/artifact consumer checks remain in Task 3. The recognition rule is the accepted empirical operational rule for the observed build; it does not establish a cross-version Microsoft guarantee.
