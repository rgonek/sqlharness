### Finding Verdicts

- **Unsupported warning can yield false — ADDRESSED.** `src/SqlHarness.Core/StatementMetrics.cs:305` collects warnings for the selected QueryPlan, and `:310` and `:408` suppress false for unrecognized warning elements, attributes and empty warning containers. Known spill wins even alongside unknown warnings (`:407`). Fixtures cover unsupported elements, attributes and a known spill combined with unknown evidence (`tests/SqlHarness.Tests/JournalStatementGrantTests.cs:45`, `:61`).
- **Unmarked scalar UDF can yield false — ADDRESSED.** `src/SqlHarness.Core/StatementMetrics.cs:410` now requires `IsLiteralSelect`; `:491` rejects parse failures, expressions other than literals, and query clauses that could execute hidden work. The no-attribute UDF case is tested (`tests/SqlHarness.Tests/JournalStatementGrantTests.cs:79`). This intentionally returns NULL for most realistic clean queries; the controller has accepted that conservative boundary.

### New Breakage in the Fix Diff

- None identified. The selected-run builder test exercises false, NULL and true (`tests/SqlHarness.Tests/Journal/JournalBenchmarkBuilderTests.cs:69`). The report records 113 focused tests passing and a clean diff check; these were not rerun.

### Out-of-Scope Observations

- None. The additional ScriptDom parse overhead was not measured by the earlier offline probe; the fix report discloses this limitation.

### Verdict

**Fix round: All findings addressed, no new Critical/Important breakage.**

**Spec compliance: Approved** for the reviewed backend scope. **Task quality: Approved.** UI integration and full verification remain outside this review; no live SQL Server result is claimed.
