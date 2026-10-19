# Plan 082 final whole-branch fix report

## Findings and changes

Both Important findings in `final-review.md` were fixed in one product commit, `b9f60924d09cdfa13f07106662549fcca8ee1f56` (`feat(082): fix statement comparison breakdowns`). The comparer now joins comparison object rows by case-insensitive object name and derives before/after values from the corresponding side and selected cases. Confirmed absence contributes zero to sum/mean; unavailable attribution remains null. The rows retain `not-executed` status, and incomplete attribution still fails closed to the whole-batch basis.

The single-run measured after-only row contract remains unchanged. No journal schema, extractor, or live SQL path changed. Existing sensitive-text policy, retained-plan links, and hidden-work behavior remain in place.

## RED and GREEN evidence

Before production changes, this command reproduced both comparer failures:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~OptimizationComparerTests
```

It exited `1` with 33 passed and 2 failed. The object regression received two `dbo.T` rows, 20 → 20 and 10 → 10, instead of one joined row. The all-cases branch regression expected the confirmed-side total `10` and received `null`. The unavailable-attribution regression passed, confirming that its null behavior was already protected.

After the fix, comparer and API coverage passed:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~DashboardServerTests.Optimization_endpoints_validate_selectors_and_return_scoped_comparison|FullyQualifiedName~DashboardServerTests.Optimization_compare_endpoint_returns_one_object_row_with_side_specific_counts"
```

Result: exit `0`, 37 passed, 0 failed. The coverage checks the 20 → 10 object change, one-sided objects, all-cases confirmed-missing zero under mean, unavailable attribution staying null, and one joined object row in the comparison API response.

The UI regression passed:

```powershell
npm test -- src/components/optimization/StatementRows.test.tsx
```

Run from `src/SqlHarness.Dashboard/ui`; result: exit `0`, 1 file and 4 tests passed. The rendered expandable detail contains one object row with its before and after counters.

Branch formatting passed on the committed product tree:

```powershell
pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef ca4c8b8
```

Direct exit `0`; 23 branch-changed C# files checked. The first run reported `FINALNEWLINE` for the three edited C# files because each had an extra trailing CRLF. Those two trailing bytes were removed manually; the rerun exited `0`.

## Final Windows gate

The exact tested product revision was `b9f60924d09cdfa13f07106662549fcca8ee1f56`. The required gate command was:

```powershell
pwsh ./scripts/verify.ps1 -LogDirectory .superpowers/sdd/082-statement-io-attribution/task-final-fix-verify-b9f6092/logs
```

Direct exit `0`; `verify: OK stages=5 5m47s`. Build passed with 0 warnings and errors. UI check passed 60 files / 313 tests, plus lint and typecheck. `SqlHarness.Tests` passed 4,509 tests with 11 skipped and no failures; `SqlHarness.Mcp.Tests` passed 435 with 4 skipped and no failures. The gate's format stage skipped because the committed tree had zero changed C# files; the explicit branch format result above covers this product diff. Direct exit is retained at `task-final-fix-verify-b9f6092/just-verify.exit`; per-stage logs are in the sibling `logs/` directory.

The UI check log records `ECONNREFUSED` diagnostics for localhost port 3000 after its tests. The stage exited successfully; this is not described as pristine-console output. Linux verification was explicitly skipped. No synthetic SQL Server UAT rerun was needed for Dashboard comparison assembly changes.

## Files and remaining review

Product commit `b9f6092` changes:

- `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs`
- `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs`
- `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs`
- `src/SqlHarness.Dashboard/ui/src/components/optimization/StatementRows.test.tsx`

The evidence and Plan 082 index updates are documentation-only and will be committed separately. The final Sol low whole-branch re-review is pending; Plan 082 remains unmerged and is not marked done. No push was made.
