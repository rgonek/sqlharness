# Observation: partial matrix comparison leaves no journal metrics

Status: collecting observations. A follow-up implementation plan was requested separately:
[plan 076](../../plans/076-partial-matrix-journal-runs.md). Recorded 2026-10-15 against
repository HEAD `4f1e5a1`.

## Resolved by plan 076 — 2026-10-16

Completed cells are journaled. The failing cell contributes no metrics. The access-denied
rename cause was not found. The observation below is the 2026-10-15 record and is unchanged.

## Observed workflow

A three-cell matrix `compare` completed two cells and then failed while publishing the third
cell's artifact directory. The response had `status: partial`, exit code 6 and
`error.code: local_storage_failed`; the message reported access denied on the `.staging-<guid>`
directory. The two completed cells were returned in the response with artifact ids, and their
artifacts were readable afterwards. The agent did not retry; the missing case was covered by an
earlier comparison.

## Diagnostics inspected (read-only, bounded to this operation)

- Phase aggregates: connect, execute, artifact and other phases were recorded. Detailed events showed
  three connect successes and one failed artifact event between successful ones; no SQL error number.
- No leftover staging directory remained, and no final directory existed for the failed cell.
  Cleanup in `ArtifactDirectoryPublisher.cs:74-86` therefore ran.
- The journal had **no** `operation_metrics`, `operation_statements` or `operation_table_io` rows
  for this operation. Successful comparisons in the same session had complete rows for every cell.

## Verified source

- `src/SqlHarness.Core/CompareMatrixRunner.cs:93-107`: completed cell runs accumulate in `runs`,
  but `CompareMatrixCellFailedException` receives only the completed reports, not the runs.
- `src/SqlHarness.Core/SqlHarnessModule.cs:515-540`: both partial outcomes are built without
  `BenchmarkRuns`.
- `src/SqlHarness.Core/Journal/JournalingModule.cs:104-105` records benchmark metrics only when
  `BenchmarkRuns` is non-empty.

Classification: defect relative to the intent "first failure stops the run and retains completed
cells" (compare tool description). Completed cells survive in the response and artifacts but are
invisible to the journal and dashboard. Confidence: high (source path plus journal evidence). The
installed binary was not proven identical to HEAD.

The cause of the access-denied rename is **not established**. Hypotheses: a transient handle held
on the staging directory by another process (for example a file indexer or antivirus scan) during
`Directory.Move`. Classification of the rename failure itself: infrastructure, provisional. No
retry policy is proposed here.

## Open questions

- Does the access-denied rename recur, and does it correlate with a particular process?
- Should a bounded retry of the final rename be considered, given the existing preflight design?
