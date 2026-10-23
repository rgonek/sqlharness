**Hidden-work items beyond the per-statement cap lack omission metadata** — ADDRESSED — `src/SqlHarness.Core/StatementMetrics.cs:752` records the exact recognized-row difference as `HiddenWorkOmitted`; `src/SqlHarness.Core/ArtifactReader.cs:141` exposes it while retaining the existing `NameTruncated` field. Limitations are derived from all recognized rows (`StatementMetrics.cs:753`). The added regression asserts 16 visible rows and `hiddenWorkOmitted = 2` (`tests/SqlHarness.Tests/ArtifactReaderTests.cs:688`).

### New Breakage in the Fix Diff

None.

### Out-of-Scope Observations

None.

### Checks

The appended fix report names the focused `ArtifactReaderTests|StatementMetricsTests` suite and records 100/100 passed, 0 skipped. Suite not rerun, per contract.

### Verdict

**Fix round:** All findings addressed, no new Critical/Important breakage.
