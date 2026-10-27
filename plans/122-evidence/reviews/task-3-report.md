# Task 3 backend report

Base: b7045c33. Scope: bounded SQL preview in existing OperationSummary projection; only session evidence version exactly 1, using retained baseline sql_text. SQLite substr returns at most 240 Unicode code points and a separate one-character truncation indicator; no per-row query, SQL search, extra storage or pagination changes. Missing SQL yields null/false; empty SQL yields empty/false. Compare uses baseline even if candidate exists.

TDD RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter "FullyQualifiedName~Activity_preview" --nologo` failed 2/2 with missing sqlPreview JSON property, as expected.
GREEN: same command passed 2/2. Covers 240 ASCII, 241 supplementary Unicode characters, empty and null, disabled sensitive retention, old and unsupported version, compare baseline, and three-page cursor coverage.

Additional focused regression: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --no-build --filter "FullyQualifiedName~Dashboard.JournalReaderTests" --nologo` failed 2, passed 80: Operation_pages_filter_by_profile_and_multiple_dimensions_without_losing_cursor_matches (missing cursor), Unprofiled_scope_and_minute_bounds_match_statistics_and_drilldown (empty result). These failures concern profile/dimension filters and require controller investigation; not asserted to be pre-existing. `git diff --check` passed. Full Windows gate deferred to controller as instructed.

Self-review: projection applies to shared operation reads; exact version predicate prevents older and unsupported versions. No SQL is fetched separately or saved. SQLite text substr terminates at embedded NUL, so a SQL string containing NUL has a shorter preview and may not indicate truncation; this edge is not covered by the specified tests and needs a decision if embedded NUL must be represented.

Files: src/SqlHarness.Dashboard/DashboardModels.cs, src/SqlHarness.Dashboard/JournalReader.cs, tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs. UI excluded by dispatch.

## Follow-up: resolved reader regressions and embedded NUL

Root cause: Operations appends o.vars_json after OperationColumns, but read it at hardcoded index 43; Task 3 added preview at 43 and truncation at 44, so dimension filtering parsed SQL preview instead of vars_json. The existing two regression tests reproduced both failures. Changed that lookup to reader.GetOrdinal("vars_json"); both tests passed 2/2 independently, confirming the cause. Inspected all OperationColumns uses: this is the only appended column reader.

Embedded NUL RED: Activity_preview_is_unavailable_for_sql_with_embedded_nul failed with a string prefix instead of null. SQLite substr stops at NUL. The SQL projection now detects NUL with instr and emits null/false for that SQL, avoiding an incomplete preview; SQL remains in SQLite and only the bounded prefix is returned for other strings. Focused Activity_preview tests passed 3/3.

Final focused reader suite: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --no-build --filter "FullyQualifiedName~Dashboard.JournalReaderTests" --nologo` passed 83/83. `git diff --check` passed. No full gate run per controller instruction.
