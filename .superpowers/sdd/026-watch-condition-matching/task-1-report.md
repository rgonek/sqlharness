# Task 1 report — Plan 026 RED tests

## Changes

- Added boolean operand/value cases, quoted text cases (including doubled single-quote escaping), and small-decimal/exponent numeric cases to `tests/SqlHarness.Tests/WatchConditionTests.cs`.
- The requested case `Status = completed` against `Completed` was already present in `Text_equality_is_ordinal_case_sensitive`; it remains unchanged and continues to pin ordinal, case-sensitive behavior.
- No production files were modified.

## Verification

Command:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~WatchConditionTests --logger "console;verbosity=normal" -m:1
```

Result: exit code `1` as expected for the RED stage. `Test Run Failed. Total tests: 48; Passed: 38; Failed: 10.` All 36 pre-existing test cases passed. Of the 12 added rows, 10 failed on the expected unsupported behavior and 2 passed because those negative cases were already satisfied. Failures were the boolean true/false matching cases, all three quoted-text cases, and all three numeric exponent/small-decimal cases. The pre-existing unquoted case passed.

The build emitted Vite's existing large-chunk warning (>500 kB); build completed. An earlier parallel invocation encountered a locked test assembly while another build was active. The final single-node run completed and produced the RED evidence above.

## Self-review

- Diff is limited to the requested test file; production code is unchanged.
- All requested additions are represented. Doubled single quote is tested as an escaped quote.
- Existing rows pass; new failures identify the requested gaps.

## Concerns

- None for Task 1. The expected RED failures must be resolved by the implementation task.
