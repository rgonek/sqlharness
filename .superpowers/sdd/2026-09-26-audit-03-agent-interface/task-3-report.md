# Audit 03 Task 3 report

## Result

Implemented the budgeted `--output agent` projection on baseline `0d67946`. No live database connection or user profile access was used. The T2 `capabilities` report already exposes `agentOutputBytes` (4096..1048576, default 16384) and `agentCellChars` (0..4096, default 512); I verified those existing values and kept the keys stable.

## Changes

- Added bounded projections for query result sets/rows/cells/messages, counts, schema detail, measure parameter sets, benchmark summaries, and compare matrix cells. Projection limits are applied before serialization; the response loop serializes only progressively bounded projections.
- Added `--max-output-bytes` and `--max-cell-chars` to agent-capable command settings. The byte budget includes the whole compact envelope and trailing newline. Invalid limits return a structured validation error before dispatch.
- Kept raw query result hashes, equivalence reports, metric availability, primary metrics, and already written artifact paths. Projection does not create query result artifacts. Truncation reports omitted detail and applied limits. A response that cannot fit returns a structured `output_budget_too_small` error rather than partial JSON bytes.
- Replaced `OutputCaptureWriter`'s retained output copy with incremental UTF-8 byte and line counters.
- Documented projection order and behavior in `docs/superpowers/specs/2026-09-26-agent-output-contract.md`.
- Added regression coverage for 1000 tables, 100 matrix cells, 100 KB warnings, 20 KB artifact paths, a large Unicode cell, byte-budget options, preserved raw hash/equivalence, and UTF-8 footprint counting.

## Byte measurements

Measurements include the final newline and came from the focused projection tests with a configured 4096-byte budget:

| Fixture | Emitted UTF-8 bytes |
|---|---:|
| 1000 table rows | 2376 |
| 100 matrix cells with 100 KB warning and 20 KB artifact path per cell | 2700 |
| Unicode cell with 10,000 repetitions of `語😀` | 1004 |

These are byte counts, not token estimates.

## Verification

- Focused `AgentOutputTests` and `BenchmarkSummaryTests`: 25 passed, 0 failed.
- Full `dotnet test --no-restore`: 1,705 passed, 1 failed, 7 opt-in integration tests skipped. The failure was `ProcessRunnerTests.RunAsync_CancellationTerminatesEntireProcessTree`, timing out while waiting for its helper to publish both process IDs; no projection test failed. Task 1's report records this same process-tree test timeout.
- `git diff --check`: passed; Git reported only configured LF-to-CRLF working-copy warnings.

## Review notes

- Existing `BenchmarkSummaryProjector` already caps noteworthy operators at ten and sorts warning/spill/conversion cases first, so that behavior remains in use.
- Existing T2 capability limits already matched the new option ranges and defaults, so no capability shape change was needed.
- Full-suite status includes one known unrelated process test timeout; the full suite was run once as requested.

## Follow-up review fixes

The task review found three gaps. Fixed them in follow-up commit `ae7b74c`:

- Added bounded projections for plans, space, watch, snapshots, Query Store, indexes, ping, and gain. Unknown future report types now produce a small typed omission result; they no longer fail solely because the full report exceeds the response budget. Schema `omittedObjects` now counts objects only, with nested omissions reported separately.
- Passed configured limits into validation and parser error rendering. Long error text, hints, paths, and command names are clipped and counted.
- Set a conservative detail allowance from the configured byte and cell limits before serialization; plan nodes also have a separate node cap. This keeps the first candidate projection bounded at the 4 KiB minimum. The output counter now joins UTF-16 surrogate pairs split across writer calls.

Focused command and output after the fixes:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName~Cli.AgentOutputTests|FullyQualifiedName~BenchmarkSummaryTests'
Passed! - Failed: 0, Passed: 28, Skipped: 0, Total: 28
```

The latest measured responses, including newline, were 378 bytes for the 1000-table fixture, 1461 bytes for the large matrix fixture, and 1002 bytes for the Unicode cell fixture, each with a 4096-byte budget. Added tests also cover large plan trees, long validation errors under a custom budget, and surrogate pairs split across writes. `git diff --check` passed. The full suite was not rerun for this review follow-up; the original full-suite run and its unrelated timeout are recorded above.
