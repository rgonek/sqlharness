# Plan 104 Task 1 evidence review

## Verdicts

- **Original Task 1: BLOCKED / NOT DONE.** The brief requires a behavior regression, focused RED, a narrow implementation, and confirmation of GREEN (`.superpowers/sdd/104-stable-local-object-io-aggregation/task-1-brief.md:3-5`). The report correctly labels the original task BLOCKED / NOT DONE and records reproduction only (`plans/104-evidence/task-1-report.md:6-12`). No implementation or GREEN result is claimed.
- **Bounded reproduction diagnostic: COMPLIANT.** It captures the requested missing-aggregate behavior, preserves the test patch and failing output, and explains why source implementation is blocked by absent provenance (`plans/104-evidence/task-1-report.md:10-12,22-28`). The patch applies cleanly under `git apply --check`. The evidence commit contains only the reproduction patch, report, and RED log (`.superpowers/sdd/104-stable-local-object-io-aggregation/review-160067ba..c0aa2f3a.diff:6-10`); the existing plan/index and resolution files remain separate worktree changes.

## Requirements and evidence quality

The saved focused run is a real RED: it discovered 36 tests and finished with 31 passed and 5 failed (`plans/104-evidence/task1-red.log:122-126`). The report names the five expected failures and the passing stable/long-key fixture (`plans/104-evidence/task-1-report.md:12`). The test patch covers repeat counts 2 and 3 and within-run summing (`plans/104-evidence/reproduction.patch:9-37`), a collision-shaped `#ABCDEF12` key (`plans/104-evidence/reproduction.patch:39-53`), and stable plus long escaped permanent keys with absent-run behavior (`plans/104-evidence/reproduction.patch:55-70`). The failed assertions are missing `table variables` keys, consistent with the existing implementation collecting exact string keys and assigning zero when a key is absent in a run (`src/SqlHarness.Core/BenchmarkRunner.cs:209-220`).

The fixtures are raw dictionaries of strings (`plans/104-evidence/reproduction.patch:80-88`), so their `#<8 hex>` spelling is not typed evidence that SQL Server emitted table-variable counters. The report states this explicitly and does not present the tests as classification proof (`plans/104-evidence/task-1-report.md:24-28,41`). In the collision test, the aggregate-key assertion comes before the explicit-temp assertion (`plans/104-evidence/reproduction.patch:49-53`), so the captured failure does not exercise whether a candidate algorithm would preserve that explicit key; the report calls out this limit (`plans/104-evidence/task-1-report.md:28`).

The summary-projection test is also a downstream check: it first builds the aggregate with `CreateVariantReport`, then projects it (`plans/104-evidence/reproduction.patch:109-123`). Since the aggregate lacks `table variables` already, its failing lookup does not isolate a projection defect. The report does not claim otherwise.

The evidence remains within the authorized diagnostic boundary. It does not claim detailed `TableIo` or LOB coverage, journal identity proof, a safe regex classification, or source changes (`plans/104-evidence/task-1-report.md:24-28,32-41`). This preserves the distinction among raw total reads, detailed LOB additions, exact permanent keys, and journal name-shape heuristics. No credential material or live database activity appears in the supplied evidence.

## Findings

No blocking issue in the bounded diagnostic package. The original Task 1 remains incomplete by design under the STOP restriction; the RED evidence supports that status and does not justify implementation without positive per-counter type provenance.
