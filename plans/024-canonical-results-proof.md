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

---

# Plan 024, Task 2 (Phase B) implementation report

## Outcome

Phase B is implemented in commit `3d097ff` (`Add canonical PostgreSQL result encodings`) on `fix/plan-024-canonical-types`, based on phase A's `beea534`. Canonical arrays are recursively prepared before JSON writing, then emitted with scalar envelopes; multidimensional arrays include dimensions and row-major values. PostgreSQL `inet`, `macaddr`, and `bit varying` values use invariant named encodings. Byte arrays retain the existing `bytes` encoding. Npgsql 10's default `cidr` value, `System.Net.IPNetwork`, retains its existing generic `IFormattable` tag and invariant text so its supported hash remains unchanged.

No live database, provider read, dashboard, or API was used. The gates exercise the offline test suites only.

## Provider mapping and compatibility evidence

The Npgsql 10.0 release notes say PostgreSQL `cidr` maps to `System.Net.IPNetwork` by default instead of `NpgsqlCidr`: https://www.npgsql.org/doc/release-notes/10.0.html. Reflection against the installed Npgsql 10.0.3 assembly confirmed `System.Net.IPNetwork` implements `System.IFormattable`; the obsolete `NpgsqlTypes.NpgsqlCidr` exists but does not implement it. Before this change, `IPNetwork` therefore already passed through the generic scalar arm under type tag `System.Net.IPNetwork`. The implementation preserves that encoding and adds a regression pinning its exact JSON and SHA-256 across `pl-PL` and `en-US`. No obsolete `NpgsqlCidr` arm was added.

## Focused checks

- `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CanonicalResults|FullyQualifiedName~ResultEquivalence|FullyQualifiedName~Snapshot"`: exit 0, 171 passed, 0 failed, 0 skipped.
- `dotnet build SqlHarness.sln -warnaserror`: exit 0, 0 warnings, 0 errors.
- `dotnet format SqlHarness.sln --no-restore --verify-no-changes --include src/SqlHarness.Core/CanonicalResults.cs tests/SqlHarness.Tests/CanonicalResultsTests.cs`: exit 0. `git diff --check`: exit 0.
- One early focused run failed only because the new expected JSON mistakenly used IPNetwork text length 13; actual UTF-8 length is 12. Correcting that test expectation produced the green run above. This was a test expectation correction, not a production-behavior RED. No pre-implementation RED was run.

Tests cover stable and order-sensitive hashes, exact array JSON, strings with null elements, empty arrays, multidimensional shape and row-major values, byte-array compatibility, and rejection of an unsupported nested element without changing the accumulator. IPAddress, PhysicalAddress, BitArray, and IPNetwork encodings are pinned under invariant culture checks.

## Full gates

Both gates ran against implementation commit `3d097ff`.

- Windows command: `pwsh -NoProfile -File scripts/verify.ps1`; exit 0; literal `verify: OK`. Build: 0 warnings/errors. Core: 3564 passed. MCP: 326 passed, 4 expected skips. UI: 32 files and 148 tests passed. Full log: `task-2-windows-verify.log`.
- Linux command: `pwsh -NoProfile -File scripts/verify-linux.ps1`; exit 0; literal `verify-linux: OK`. Build: 0 warnings/errors. Core: 3564 passed. MCP: 327 passed, 3 expected skips. UI: 32 files and 148 tests passed. Full log: `task-2-linux-verify.log`. The ext4 gate clone had no competing Linux gate process before launch.

Phase A evidence, including the initial Linux cancellation timing flake and its focused plus full clean reruns, is retained in the preceding Phase A report in `plans/024-canonical-results-proof.md`. Neither phase used live SQL Server/PostgreSQL reads.

## Files and self-review

Phase B changed `src/SqlHarness.Core/CanonicalResults.cs`, `tests/SqlHarness.Tests/CanonicalResultsTests.cs`, `README.md`, `AGENTS.md`, and the provider reconciliation in `plans/024-canonical-results-common-types.md`. The array arm is after the existing byte-array arm in `Write`; `Prepare` keeps `byte[]` on its primitive path, while every other array recursively prepares all elements before a writer can be mutated. Existing Phase A tags and envelopes were preserved.

No unresolved concerns.

---

# Plan 024, whole-branch final fix report

## Finding and bounded correction

The whole-branch review found that the canonical array hash included multidimensional shape, while snapshot persistence stored only scalar type/null/value fields. Consequently, equal flattened elements with different shapes could produce an `Identical` snapshot verdict. The controller authorized a narrow extension to `SnapshotStore` and `SnapshotDiffer`, their regressions, and Plan 024 evidence/index updates.

Commit `0b9f659` (`Preserve array dimensions in snapshot diffs`) adds optional `dimensions` metadata to `SnapshotScalar`, populated from the existing canonical array envelope and omitted when absent. `SnapshotDiffer` compares dimensions structurally. This preserves ordinary scalar persisted bytes and the version 1 format; old version 1 scalar documents load and diff successfully without a migration. Equal shapes remain identical, while a 2×3 and 3×2 array with the same row-major elements report one `cell-changed` location. `SnapshotRunner` reports `SnapshotVerdict.Different` and exit code 8 for the shape-only change.

No canonical tags, result-equivalence rules, readers, renderer, lower-bound contract, or live behavior changed. No live database, dashboard, or API was used.

## Regression evidence

- RED: `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~SnapshotDifferTests.Different_multidimensional_array_shapes_with_same_elements_are_different' --no-restore` exited 1 before implementation: expected one difference, actual zero.
- GREEN: `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~SnapshotStoreTests|FullyQualifiedName~SnapshotDifferTests|FullyQualifiedName~SnapshotTests' --no-restore` exited 0: 62 passed, 0 failed, 0 skipped.
- Broader regression filter: `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~CanonicalResults|FullyQualifiedName~ResultEquivalence|FullyQualifiedName~Snapshot' --no-restore` exited 0: 176 passed, 0 failed, 0 skipped.
- Formatting: `dotnet format SqlHarness.sln --no-restore --verify-no-changes --include src/SqlHarness.Core/SnapshotStore.cs src/SqlHarness.Core/SnapshotDiffer.cs tests/SqlHarness.Tests/SnapshotStoreTests.cs tests/SqlHarness.Tests/SnapshotDifferTests.cs tests/SqlHarness.Tests/SnapshotTests.cs` exited 0; `git diff --check` exited 0.

The store regression confirms array dimensions survive save/load and ordinary scalar JSON omits the optional property. A hand-authored legacy version 1 scalar document without dimensions remains readable and diffable.

## Final-fix full gates

The implementation commit was `0b9f659` before the final full gates.

- Windows: `pwsh -NoProfile -File scripts/verify.ps1`, exit 0, literal `verify: OK`; build had 0 warnings/errors; Core 3569 passed; MCP 326 passed / 4 expected skips; UI 32 files / 148 tests passed. Log: `.superpowers/sdd/024-canonical-results-common-types/task-final-fix-windows-verify.log`.
- Linux: `pwsh -NoProfile -File scripts/verify-linux.ps1`, exit 0, literal `verify-linux: OK`; build had 0 warnings/errors; Core 3569 passed; MCP 327 passed / 3 expected skips; UI 32 files / 148 tests passed. The ext4 clone synced to commit `0b9f659` before the gate. Log: `.superpowers/sdd/024-canonical-results-common-types/task-final-fix-linux-verify.log`.

## Scoped re-review and controller ruling

The controller's final-fix re-review marked the original finding **ADDRESSED** and found no new Critical or Important issue. It identified one Minor compatibility edge: a multidimensional snapshot created on this unmerged Plan 024 branch lacks dimensions; comparing it with a fresh same-shape capture reports a difference and returns exit 8.

The controller accepts the conservative difference. The pre-Plan 024 base `aae13b9` rejected ordinary arrays, so released snapshots cannot contain multidimensional arrays. A dimensionless snapshot cannot prove its shape, and treating it as identical would risk a false equality. Branch-local development snapshots may need recapture. Legacy version 1 documents remain readable, and ordinary scalar bytes remain unchanged.

No merge, push, or unrelated workspace cleanup was performed.
