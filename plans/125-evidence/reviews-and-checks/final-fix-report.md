# Final-review fix — plan 125

Base: `a070c1ff54e3a64ad9fe01d3ab25ffcd2cc7b1bc`. Product fix: `502f12ed0c9c767d41d080255f4d683d713d432e`. Final scoped re-review remains pending.

## Finding and change

The whole-branch review found that all-case statement and table projections used the subset with paired batch logical-read medians. Direct comparisons now use the distinct recorded case identities, and independent snapshots use the union of their case identities. The same source already drove timing; the reducers now receive it for statement and table IO, omitted-statement counts, and batch/shared sums. Explicit case selection is unchanged. A changed-statements verdict requires complete attribution across the entire selected universe, including expected cases without metric records. Missing table counters and unproved statement absence stay unavailable; recorded zero remains zero. Cross-condition mapped-case logic is unchanged.

## Focused verification

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter 'FullyQualifiedName~Statement_and_table_contributors_do_not_depend_on_batch_reads|FullyQualifiedName~Missing_batch_reads_do_not_hide_unavailable_statement_or_missing_case' --logger 'console;verbosity=normal'` failed all 3 cases before the correction: 22 expected vs 10, empty statement collection when all batch medians were missing, and an incorrectly available 10 instead of null. [Raw log](../logs/final-fix-red.log).
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter 'FullyQualifiedName~OptimizationComparerTests'` passed 80/80 after correction and formatting. [Raw log](../logs/final-fix-green.log). Fixtures cover one/all absent batch medians, sum/mean, available statement counts, partial statement attribution, expected missing case, nullable table counters and verdict fallback. Existing cross-condition and single-case coverage ran in the same suite.
- `pwsh ./scripts/format-changed.ps1 -Fix` succeeded on two files ([log](../logs/final-fix-format-fix.log)).

## Self-review

The direct case source includes expected records even when a variant has no metric; the independent source takes both sides' metric identities. The existing single-case selection and cross-condition matching path remain intact. Batch-level eligibility is not used to exclude independently available IO, and incomplete statement attribution cannot upgrade the verdict. Missing metric records cannot be turned into zero by table or statement reducers. Whole-batch sums are unavailable if a selected contributor lacks reads. No SQL execution, database schema, policy, credentials or API schema changed. Browser fixtures return static JSON and cannot exercise backend recalculation; the change was verified through backend tests and the full UI suite instead of claiming a fresh browser recalculation test. No live database, installation, publication, restart, merge or push.

## Final gate

`just verify -LogDirectory plans/125-evidence/logs/final-fix-gate` passed on product `502f12ed`: build passed, UI 473/473, Core 4838 passed / 11 skipped, MCP 535 passed / 4 skipped. The lockfile was unchanged, so UI installation was skipped. Working-tree formatting was skipped after the product commit. Total gate duration 15m38s. [Summary](../logs/final-fix-gate-summary.log), [build](../logs/final-fix-gate/build.log), [UI](../logs/final-fix-gate/ui-check.log), [Core](../logs/final-fix-gate/test-SqlHarness.Tests.log), [MCP](../logs/final-fix-gate/test-SqlHarness.Mcp.Tests.log).

`pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 672e6265` passed for all 9 changed C# files ([log](../logs/final-fix-branch-format.log)). Both checks used the pinned SDK and process-local `TEMP`/`TMP=C:\s125`; the gate used private Node 24.18.0. Final scoped re-review remains pending.
