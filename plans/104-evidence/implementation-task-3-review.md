### Spec Compliance

- ❌ Issues found: The brief requires a fixture proving legacy artifact readability for table metrics. The added `MeasureAndMeasureSet_AllSectionsRead` case seeds `#A1B2C3D4` in `historicalRows` but writes measure, named-set, and compare artifacts through the current `CompareArtifactWriter`. This proves current-format round trips, not that a pre-provenance artifact still reads with its `#8hex` table key and values unchanged (`tests/SqlHarness.Tests/ArtifactReaderTests.cs:1396`, `:1400`). Existing legacy reader cases cover overlap inventory and statement attribution, not table metrics (`ArtifactReaderTests.cs:472`, `:807`). Add a prior-format metrics JSON fixture and assert the key and values after `ArtifactReader.ReadSection(..., "metrics")`.
- ✅ The remaining visible requirements are covered: dashboard/export preserve persisted identities and totals; measure, set, and compare metrics round-trip; named Core/MCP tests cover per-run aggregation, collisions, clipped identities, and a later fitting entry; the current interface and metric docs describe aggregation and historical-name behavior.
- ✅ Confirmed from bounded diff hunks: both changed evidence notes explicitly mark the historical exact-build-only recommendation superseded, retain the observed-build evidence boundary, and state this is not a vendor guarantee (`review-5662e6d1..b4aaf52a.diff:199`, `:291`).

### Strengths

- The dashboard comparison helper now joins by exact stored labels. UI tests and Markdown export assertions retain distinct `#8hex` names and data-plus-LOB totals (`src/SqlHarness.Dashboard/ui/src/lib/tableIoDelta.ts:11`, `src/SqlHarness.Dashboard/ui/src/lib/tableIoDelta.test.ts:25`, `src/SqlHarness.Dashboard/ui/src/lib/prMarkdown.test.ts:56`).
- The artifact test round-trips single-measure, named-set, and compare metrics, including collision-safe aggregate labels and legacy-looking keys (`tests/SqlHarness.Tests/ArtifactReaderTests.cs:1396`).
- The named Core fixtures exercise parsed raw IO, per-run aggregation before distributions, incomplete provenance, and collision-safe labels (`tests/SqlHarness.Tests/BenchmarkSummaryTests.cs:14`, `:55`, `:79`, `:109`). The MCP fixtures retain clipped identity keys and keep `dbo.short` when a long identity cannot fit (`tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:1020`, `:1056`).
- MCP tool descriptions and current metric contracts describe per-run aggregation, collision-safe labels, and historical-name preservation without claiming a vendor guarantee (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:711`, `docs/mcp/tools.md:18`, `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md:114`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- Add a true prior-format table-metrics artifact fixture as described above. Current-writer round trips cannot catch a regression in reading old saved metrics.

#### Minor (Nice to Have)

- None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The consumer change is narrow and preserves exact persisted identities; the focused UI, Core, and MCP evidence supports the claimed behavior. The task-mandated legacy metrics compatibility case is absent, so the task package needs that coverage before approval.

### Reviewed evidence

- Recorded UI focused log: 2 files, 9 tests passed (`plans/104-evidence/task-3-logs/ui-green.txt`).
- Recorded Core and MCP focused logs: 213 and 54 tests passed with actual discovery (`plans/104-evidence/task-3-logs/core-focused.txt`, `mcp-focused.txt`). No suites were rerun.
- Recorded `git diff --check` exited 0; its log contains only Git LF-to-CRLF notices (`plans/104-evidence/task-3-logs/diff-check.txt`).
