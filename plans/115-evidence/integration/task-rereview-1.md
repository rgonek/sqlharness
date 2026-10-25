## Prior finding

- ✅ **Addressed:** both post-execution rejection branches now call `QuerySelectionFailure`; it carries forward the receipt, operation reference, journal/eligibility flags, parent reference, and coverage (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:125-139`). The served adapter therefore retains the eligible operation reference and completes the original receipt with the emitted error footprint, as asserted by the two new regressions (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1673-1738`).
- ✅ **MachineError remains correct:** the helper constructs a fresh Safety outcome with explicit `SqlHarnessError.From(SqlHarnessExitCode.Safety, message)`, avoiding a record-copy with a stale computed error. Both regressions assert `safety_rejected` and the expected message.

## New breakage

- None found in the fix diff. The helper preserves the journal fields while keeping the bounded structured safety response; it does not set query selection or execute a second query.

## Out of scope and verification

- The resolution report records RED 2/2, GREEN 2/2, the expanded 164/164 focused suite, and branch formatting for 8 C# files (`integration/resolution-report.md`, Integration fix round 1). I did not rerun these checks.
- The full Windows `just verify` is controller-owned and currently running on `0d37fa9e`; its result is not verified here. The upcoming Sol low combined review is outside this scoped rereview.

## Verdict

**Task review: Approved.** The prior Important finding is addressed, no new breakage was found, and the integrated full-gate result remains pending.