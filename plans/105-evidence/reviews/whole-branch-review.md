# Plan 105 whole-branch review

Reviewed `33651662..d78f06ea` against `plans/105-artifact-diagnostic-evidence.md`, the SDD ledger and the three task reviews. This was a read-only review of product and test changes; I did not run the full Windows gate, which the controlling agent already started on `d78f06ea`.

## Strengths

- The implementation keeps SQL Server Showplan diagnostics separate from STATISTICS IO ScanCount and exposes nullable numeric fields, source labels, availability, and bounded object lists. It does not expose raw XML through artifact reads.
- The reader retains old statement artifacts by normalizing absent diagnostic fields to unavailable. The projection preserves exact statement selectors and reports omitted nested evidence under byte limits.
- The workflow fixture exercises distinct baseline and candidate grants and compile values at both minimum and default budgets, including statement continuation.

## Issues

### Important

1. **A corrupt saved diagnostics file becomes a successful empty metrics result.** `src/SqlHarness.Core/ArtifactReader.cs:606-668` catches `JsonException` and `ArtifactReadException` while reading `statements.json`, then returns an empty distribution with `availability="unavailable"`. The corresponding `statements` read rejects invalid saved evidence at lines 1055-1102. For a manifest that advertises statements, truncation, invalid JSON, or an oversized file therefore makes `section=metrics` report a successful absence rather than a storage/evidence failure. This crosses the plan's negative-result boundary and can lead an operator to conclude no diagnostic evidence was captured. Preserve the legacy missing-field result for valid older files, but propagate a content-free artifact failure for corrupt or unreadable advertised diagnostics.

2. **Different QueryPlan nodes can be aggregated as one source identity.** `src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs:82-101` emits one diagnostic per `QueryPlan`, but identifies each only by document ordinal and nearest `StmtSimple` index (or null for batch scope). `src/SqlHarness.Core/ArtifactReader.cs:630-639` groups distributions by that key. If a document contains two query plans under one statement, or two unowned batch query plans, their grant and compile values become samples of one across-run distribution even within a single repetition. The `SampleCount` then implies repeated observations of one source. Add a stable QueryPlan ordinal to the persisted identity and grouping, or mark such ambiguous sources partial/unavailable rather than merging them.

### Minor

1. **The workflow pager can loop forever if continuation disappears.** `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:177-204` loops until four rows are collected, but sets `cursor=null` and repeats from the beginning when the response has fewer rows and no continuation. Give the loop a bounded page count and assert that a missing continuation is valid only after the expected rows are collected. This is a test-harness robustness issue, not a product blocker.

## Declined to judge

- Whether live SQL Server versions produce every Showplan field in the fixture: no live database or installed binary was exercised, and the plan explicitly defers live acceptance.
- Linux behavior: the plan calls only for the Windows gate and says Linux remains unverified unless requested.
- The exact model-token saving from these reads: response UTF-8 bytes are measured, but token cost is outside this implementation's evidence.

## Assessment

**Ready to merge: No.** The saved metrics error boundary and QueryPlan identity need fixes or an explicit source-backed ruling before this branch can claim the plan's correctness contract. The ongoing Windows gate and post-fix format check remain separate verification prerequisites.
