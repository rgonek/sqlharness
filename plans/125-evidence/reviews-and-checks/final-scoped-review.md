**All-cases statement and table evidence depends on whole-batch read availability — ADDRESSED.** `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:97` derives distinct direct-case identities or the independent snapshot union without the batch-read filter. Lines 102, 110–112, 122, 126–129 and 166–167 consistently use that universe for attribution eligibility, statement/table evidence, batch/shared sums and omission counts. Expected missing contributors remain visible to nullable reducers; explicit selection and cross-condition mapping are unchanged.

**Regression coverage checked:** `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs:328` covers one/all absent batch medians, sum/mean, complete statement coverage and an independently missing physical counter; line 350 covers partial attribution, a missing expected metric, 2/3 availability and whole-batch fallback. Updated existing expectations retain missing table contributors instead of silently aggregating the common subset.

**Evidence checked:** recorded RED output shows all three added cases failing on the original defect. `final-fix-green.log` reports 80/80 comparer tests passing. Final product `502f12ed` gate logs confirm UI 473, Core 4838/11 skipped and MCP 535/4 skipped; gate summary is successful and separate branch formatting passes nine C# files. No test suite was rerun during this scoped review.

**New breakage in the fix diff:** None identified.

**Out-of-scope observations:** None newly identified. The prior review's disclosed scope and evidence limits remain accepted; untouched branch behavior was not re-reviewed.

**Fix round: All findings addressed, no new Critical/Important breakage.** Scope: `a070c1ff..c9c0b0f2`; product `502f12ed`, evidence `c9c0b0f2`. Ready for the controller's documentation-status finalization.
