### Spec Compliance

- ✅ Spec compliant. The implementation reuses the saved Showplan and `TableIo` inputs, keeps batch and statement scopes plus run/statement identities, emits logical reads, LOB reads, Showplan `ActualScans`, and `STATISTICS IO ScanCount` separately, and keeps grant/compile samples grouped by variant, parameter set, matrix cell, scope, plan document, and plan statement. Incomplete object counters become null; legacy artifacts normalize new object evidence to unavailable. Source and response bounds carry omission counts, and statement paging retains its continuation behavior. Evidence: `StatementMetrics.cs:720`; `StatementMetrics.cs:812`; `ArtifactReader.cs:606`; `ArtifactReader.cs:655`; `ArtifactReader.cs:728`; `ArtifactReader.cs:1055`; `ArtifactReader.cs:1124`; `AgentOutputProjection.cs:588`; `AgentOutputProjection.cs:622`; `AgentOutputProjection.cs:685`.
- ⚠️ Cannot verify from this task diff: Task 3 workflow tests, user documentation, and the full Windows `just verify` gate. Task 2's report records focused Core and MCP results (218 and 59 passed); I did not rerun them. Task 1's persisted shape is treated as approved per the review assignment.

### Strengths

- The two scan sources have explicit source names and separate fields: Showplan `ActualScans` is copied to `showplanActualScans`, while the SQL Server `STATISTICS IO` count remains in its own run/table distribution (`StatementMetrics.cs:303`; `StatementMetrics.cs:812`; `ArtifactReader.cs:71`; `ArtifactReader.cs:655`; `ArtifactReader.cs:728`).
- Plan diagnostics retain fallback batch scope and are grouped without summing grant or compile values; each numeric field reports its own present sample count and availability (`StatementMetrics.cs:742`; `ArtifactReader.cs:631`; `ArtifactReader.cs:655`).
- The projection catches only the typed statement-fit condition and advances to lower detail candidates. If none fits, the existing render-budget error remains reachable (`McpResultAdapter.cs:156`; `McpResultAdapter.cs:165`; `McpResultAdapter.cs:190`).
- Existing offline artifact paging is preserved, and legacy rows explicitly report new object evidence as unavailable instead of zero (`ArtifactReader.cs:1105`; `ArtifactReader.cs:1124`).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

None.

#### Minor (Nice to Have)

- `tests/SqlHarness.Tests/ArtifactReaderTests.cs:836`: the new behavior regression uses only complete, nonzero object counters and available scan-count runs. It does not pin the important distinction between a real zero and an absent/partial counter, nor the source- and response-omission counts for the newly added object lists. The implementation paths appear to handle these cases (`StatementMetrics.cs:812`; `AgentOutputProjection.cs:622`; `AgentOutputProjection.cs:657`), but a small partial/zero fixture and a bounded-list assertion would guard those contract edges.

### Assessment

**Task quality:** Approved

**Reasoning:** The implementation meets the scoped behavior and keeps source identity, missing values, list bounds, and the MCP fit fallback explicit. The focused regression could cover partial/zero counters and nested-list omissions more directly, but the inspected code handles the required cases and this gap does not block the task review.
