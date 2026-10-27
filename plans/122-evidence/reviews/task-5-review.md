### Spec Compliance

- **Needs fixes.** `HasSpillWarning` can report false for unsupported spill warning forms and for an unmarked scalar UDF whose work is absent from the collected plan (`src/SqlHarness.Core/StatementMetrics.cs:401`, `:484`). Both violate the requirement that false needs complete supported material; see Important findings.
- **Cannot verify:** UI integration is outside this backend review and remains a separate task. No live SQL Server or full verification gate was run. The reported offline timing is noisy and serialized JSON size is not SQLite storage size.

### Strengths

- Selected-run provenance follows `SelectMedianTotalRun` and preserves repetition and statement source (`src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs:58`, `:103`); multiple source groups suppress the new scalar values (`src/SqlHarness.Core/StatementMetrics.cs:460`).
- Journal writes and reads gate on session marker 1 and SQL Server; historical raw-plan and actual extraction-cap tests exercise the reader and comparison path (`src/SqlHarness.Dashboard/JournalReader.cs:675`, `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs:591`, `:635`). All-cases and partial statement evidence are suppressed (`src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:282`).
- New internal statement fields use `JsonIgnore` (`src/SqlHarness.Core/StatementMetrics.cs:80`), avoiding a direct artifact/MCP serialization expansion.

### Issues

#### Critical

- None.

#### Important

1. `src/SqlHarness.Core/StatementMetrics.cs:401`, `:484`: the false branch checks runtime coverage but never checks whether warning elements are understood. `IsSpillWarning` recognizes only three names; a `Warnings` element containing an unsupported spill form such as `ExchangeSpillDetails` or `SpillOccurred` is ignored and can produce false. That is a false negative, not incomplete evidence. Recognize supported spill types correctly and return NULL where a warning form cannot be safely classified; add fixtures for unsupported spill warnings and complete clean plans.
2. `src/SqlHarness.Core/StatementMetrics.cs:401`: absence of `UdfElapsedTime`, TVF markers and nested `StmtSimple` does not prove the absence of scalar UDF work. A statement such as `SELECT dbo.f()` with a runtime-covered visible plan and no UDF timing attribute satisfies the false branch even though the separate UDF plan may be absent. SQL Server documents that UDF plans can be separate from the caller's XML plan ([Microsoft documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-exec-query-plan-transact-sql?view=sql-server-ver17)). Return NULL unless completeness of relevant hidden execution is established, including unmarked scalar UDFs; add a test without the attribute. Merely checking whether the attribute parses does not establish completeness.

#### Minor

- None.

### Assessment

**Task quality: Needs fixes.** The storage and selection guards are well supported, but the spill tri-state can assert a negative result without sufficient evidence. Focused tests were reported green; no suite was rerun in this review. UI implementation and final gate are outside this review.
