# Plan 104 Task 1 scoped review

Reviewed the Task 1 brief, implementation report, and fixed review package `0738ec69..26401c26`. The included green log reports 34 focused tests passing; I did not rerun them. The review found one specification issue in handling incomplete producer evidence.

## Spec compliance

**Verdict: Changes required.** The producer classification and within-run aggregation follow the accepted operational rule: the raw `#` plus eight hex name is typed before normalization, ordinary names remain Unknown, and the new typed identity is journal-only. The ordinary complete-run path sums anonymous counters before building distributions, while the existing per-run logical-read totals and LOB addition remain unchanged in the diff.

- **P2 — Incomplete IO evidence is counted as a zero sample.** [BenchmarkRunner.cs:228](/D:/Dev/sqlharness/.worktrees/plan-104/src/SqlHarness.Core/BenchmarkRunner.cs:228) falls through to `0L` for non-Unknown identities whenever a run lacks typed metadata. The producer deliberately sets that metadata to null when messages were omitted ([SqlServerDialect.cs:200](/D:/Dev/sqlharness/.worktrees/plan-104/src/SqlHarness.Core/Dialect/SqlServerDialect.cs:200)); legacy artifacts also have no producer metadata. If another repetition has the anonymous aggregate, the missing/incomplete repetition lowers its distribution as though the aggregate were measured absent. That conflicts with the brief's rule that unavailable evidence is not measured zero (brief line 15). Preserve an unavailable sample state for this case; retain zero for a complete typed run where the aggregate was genuinely absent.

No finding was raised for untested final collision coverage because the brief explicitly schedules that coverage for Task 2. The collision display-key handling in this diff distinguishes the common `table variables` aggregate/object pair.

## Quality

**Verdict: Pass; no independent quality finding.** The change keeps producer provenance in an internal typed value, excludes it from JSON, and scopes the new behavior to the IO parser and benchmark summary. The specification finding above concerns the meaning of a missing sample, rather than code structure or maintainability.

## Cannot verify in this review

- The included log is the evidence for the reported 34/34 focused pass; no tests were rerun as instructed.
- No live SQL Server, SQL probe, full verification gate, or broader suite was run. Cross-version raw-name behavior remains the user-accepted experimental rule, not an independently established Microsoft guarantee.
- Task 2's shared journal/persisted semantics and final collision coverage remain outside this Task 1 review.
- `git diff --check` is reported as passing but was not independently rerun.
