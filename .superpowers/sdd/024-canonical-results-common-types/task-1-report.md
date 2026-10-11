# Plan 024, Task 1 (Phase A) implementation report

## Outcome

Phase A is implemented in commit `8b942d92295489d227bf43528f8d96d15689d770` (`Add canonical encodings for non-finite and SQL Server types`). It adds stable tagged encodings for non-finite `float`/`double` and invariant text encodings for `SqlHierarchyId`, `SqlGeography`, and `SqlGeometry`. SQL Server UDT nulls use the existing null envelope. Existing finite scalar tags and encodings are unchanged.

The copied Plan 024 document was reconciled to the current base and the approved phase boundary. It records the authorized `QueryResultCollectorTests` fixture update: infinity is now supported, so the test uses a genuinely unsupported test-only scalar while retaining the partial raw-message failure assertion. Collector production behavior was not changed. README and AGENTS describe only the Phase A mappings. Phase B arrays and PostgreSQL provider mappings remain pending.

## Implementation and focused checks

- Baseline from the controller, before edits: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CanonicalResults|FullyQualifiedName~ResultEquivalence|FullyQualifiedName~Snapshot"`; exit 0, 159 passed, 0 failed, 0 skipped.
- TDD red: new canonical tests failed against the old implementation for the expected non-finite and SQL Server UDT rejection paths.
- TDD green: focused `CanonicalResults` tests passed, 32/32. The `CanonicalResults|ResultEquivalence|Snapshot` filter then passed 164/164. The authorized partial-message collector regression passed 1/1.
- `dotnet build SqlHarness.sln -warnaserror`: exit 0, zero warnings and errors.
- Formatting: `dotnet format SqlHarness.sln --no-restore --verify-no-changes --include src/SqlHarness.Core/CanonicalResults.cs tests/SqlHarness.Tests/CanonicalResultsTests.cs tests/SqlHarness.Tests/QueryResultCollectorTests.cs` and `git diff --check` both exited 0 after formatting fixes.
- The stale rejection text check, `rg -n "Non-finite System.Double values are not supported" src`, returned no matches.
- Spatial and hierarchyid construction tests passed on Windows and Linux without platform skips.

## Full gates

Both final gates ran against implementation commit `8b942d9` after formatting.

- Windows: `pwsh -NoProfile -File scripts/verify.ps1`, exit 0, literal `verify: OK`; Core 3557/3557 passed; MCP 326 passed, 4 expected skipped; UI 32 files / 148 tests passed. Full log: `task-1-windows-verify-final.log`.
- Linux: `pwsh -NoProfile -File scripts/verify-linux.ps1`, exit 0, literal `verify-linux: OK`; Core 3557/3557 passed; MCP 327 passed, 3 expected skipped; UI 32 files / 148 tests passed. The named cancellation test passed in the final run. Full log: `task-1-linux-verify-rerun.log`.

Initial failures are retained separately. The first Windows gate (`task-1-windows-verify.log`) failed only the stale collector fixture after infinity became supported (Core 3556 passed / 1 failed; MCP 326 passed / 4 skipped). After the approved fixture correction, a Windows rerun passed tests but stopped at formatting (`task-1-windows-verify-rerun.log`); formatting was corrected and the final Windows gate then passed.

The first Linux gate (`task-1-linux-verify.log`) had one failure: `ResultEquivalenceTests.Compare_observes_cancellation_started_mid_flight` did not observe mid-flight cancellation within 10 attempts (Core 3556 passed / 1 failed; MCP 327 passed / 3 skipped). Without code changes, the focused test passed 1/1 in 2.1463 seconds (`task-1-linux-cancellation-focused.log`), and the full Linux rerun passed. The Linux gate was checked for an active competing gate before the rerun; none was observed.

## Review and limits

The CLI renderer formats result cells with invariant `Convert.ToString`, so the new types have a string conversion path. MCP bounded projection retains non-string cell objects and serializes the projected report. No throw was identified by source inspection, and the full offline gates passed; however, no SQL Server/PostgreSQL live read or end-to-end CLI/MCP result containing these provider values was run. Provider-returned runtime assembly behavior and MCP serialization for actual provider UDT instances remain unproven follow-ups. No database, dashboard, or API was contacted.

The plan index now marks Plan 024 `PARTIAL`, points to this report, records Phase B as pending, and states that no live SQL Server/PostgreSQL reads were exercised.

## Review fix (round 1)

The reviewer found that the initial null arm accepted every `INullable` implementation whose `IsNull` was true. Added a regression test using `SqlXml.Null`, an unsupported non-formattable provider value. Before the fix, the focused test failed because no `NotSupportedException` was thrown. Commit `8780fe0` removes the generic shortcut and recognizes null only for `SqlHierarchyId`, `SqlGeography`, and `SqlGeometry`; their existing null-envelope tests remain in the same canonical test group. Production no longer imports or depends on the generic `INullable` interface.

Fix verification, all against commit `8780fe0`:

- Red: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Unsupported_sql_nullable_null_remains_rejected" --no-restore` exited 1 as expected before the fix: 1 failed, `Assert.Throws()` reported that no exception was thrown.
- Green: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CanonicalResults" --no-restore` exited 0: 33 passed, 0 failed, 0 skipped.
- Focused regression set: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CanonicalResults|FullyQualifiedName~ResultEquivalence|FullyQualifiedName~Snapshot" --no-restore` exited 0: 165 passed, 0 failed, 0 skipped.
- Formatting: `dotnet format SqlHarness.sln --no-restore --include src/SqlHarness.Core/CanonicalResults.cs tests/SqlHarness.Tests/CanonicalResultsTests.cs` applied final-newline formatting after the first verify reported those two changed files. Then `dotnet format SqlHarness.sln --no-restore --verify-no-changes --include src/SqlHarness.Core/CanonicalResults.cs tests/SqlHarness.Tests/CanonicalResultsTests.cs` exited 0 with no diagnostics; `git diff --check` exited 0.
- Windows: `pwsh -NoProfile -File scripts/verify.ps1` exited 0, literal `verify: OK`; build had 0 warnings/errors, Core 3558 passed, MCP 326 passed / 4 expected skips. Full fix log: `task-1-fix-windows-verify.log`.
- Linux: `pwsh -NoProfile -File scripts/verify-linux.ps1` exited 0, literal `verify-linux: OK`; Core 3558 passed, MCP 327 passed / 3 expected skips. The new `Unsupported_sql_nullable_null_remains_rejected` test passed in this run. Full fix log: `task-1-fix-linux-verify.log`.

The fix gates repeated no product behavior beyond narrowing null acceptance to the three explicitly supported SQL Server UDT types. No live database or provider-result operation was run.
