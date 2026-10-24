# Plan 104 Task 1 reproduction report

Date: 2026-10-24
Checkout: `D:\Dev\sqlharness\.worktrees\plan-104`
Source base: `160067b` (per task handoff)
Status: **BLOCKED for original Task 1; NOT DONE**

## Scope and result

I added temporary behavior tests for generated-name aggregation at repeat counts 2 and 3, summing two generated variables within each run, summary projection, preservation of a colliding explicit `#ABCDEF12`, and stable/long escaped names. No product source was edited. The exact temporary test diff is saved in `plans/104-evidence/reproduction.patch`; the test changes have been removed from the checkout.

The focused run discovered 36 tests: 31 passed and 5 failed. The five intended aggregate assertions fail because `BenchmarkReports.CreateVariantReport` produces no `table variables` entry for changing `#<8 hex>` dictionary keys. Failures cover repeat=2, repeat=3, two variables per run, the generated/explicit collision fixture, and summary projection. The stable permanent and long escaped key fixture passed. The captured output, including the nonzero exit, is in `plans/104-evidence/task1-red.log`.

Command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~BenchmarkRunTests|FullyQualifiedName~BenchmarkSummaryTests" --logger "console;verbosity=normal"
```

Test discovery was enabled with `-p:IsTestProject=true`. No database was used. No full gate or broad suite rerun was performed.

## Why the original assertion is blocked

The report path receives only per-run `LogicalReadsByTable` dictionaries keyed by strings. It has no object-kind or provenance field that can distinguish an engine-generated table-variable observation from an explicit local temp table with the same `#ABCDEF12` spelling. Grouping by the 8-hex pattern would therefore satisfy the generated-name median while also merging the explicit-temp collision. Retaining exact keys avoids that false merge but leaves each generated per-run key with the existing absent-key zero behavior and no aggregate median.

This reproduction covers raw benchmark logical-read dictionary values. It does not cover detailed `TableIo` counters or LOB reads, and it does not establish object identity from journal reconciliation. Detailed counters likewise have no object-kind provenance in the reviewed model; the journal path's 8-hex regex is a name-shape heuristic, not positive evidence. Raw total logical reads, detailed LOB addition, and journal regex grouping must remain separate evidence paths.

The combined collision test encodes both desired aggregation and explicit identity. It currently fails first because the aggregate key is absent; that failure alone does not demonstrate that a candidate aggregation algorithm would merge the explicit row. The current data model cannot prove a safe generated/explicit distinction. No exact SQL Server emission rule or IO-to-plan association was tested.

## Files and cleanup

- `plans/104-evidence/reproduction.patch` — exact temporary test diff; `git apply --check` succeeds.
- `plans/104-evidence/task1-red.log` — captured focused RED output with exit code 1.
- `plans/104-evidence/task-1-report.md` — this report.
- Temporary edits to `tests/SqlHarness.Tests/BenchmarkRunTests.cs` and `BenchmarkSummaryTests.cs` were removed by restoring those paths, which were clean before this reproduction. No product source changes were made.

The pre-existing parent changes in `plans/104-stable-local-object-io-aggregation.md` and `plans/README.md`, and the separate resolution proposal/review files, were preserved and excluded from this evidence commit.

## Self-review and concerns

The tests compile and execute; the failures are missing aggregate-key behavior, not compilation errors. Repeat 2/3 and within-run summing are covered. Stable permanent keys, a long quoted/backslash key, an absent key, and a literal explicit `#ABCDEF12` are included. The current benchmark dictionary API cannot attach typed evidence to those fixture rows, so these tests are a reproduction of the requested behavior, not proof that implementation from current inputs is sound. Detailed LOB and journal behavior remain outside this reproduction's executable assertions.