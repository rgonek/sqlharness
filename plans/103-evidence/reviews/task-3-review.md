### Spec Compliance

- ❌ Issues found: Task 3 requires missing evidence to remain unavailable (`global-constraints.md:6`; `task-3-brief.md:20`). `AgentStatementEvidenceProjection` derives keys from extracted rows and only adds all run keys when the global omitted count is nonzero (`src/SqlHarness.Core/AgentBenchmarkOutput.cs:370-375`). If one run/cell has no statement rows while another run in the same case has complete rows, the missing run is not represented and the surviving rows can label that case `complete` (`:380-387`). Include run case keys unconditionally and account for runs without extracted evidence as unavailable/partial with an explicit limitation.
- ⚠️ Cannot verify from this task diff: the branch formatter, full `just verify`, and independent whole-branch review remain controller-owned (`task-3-brief.md:13`; `task-3-report.md:35-37`).

### Strengths

The projection keeps variant/set/cell case keys, bounds compact output to eight cases, reports global statement/case omissions, and preserves hidden UDF limitations even when collected counters reconcile as complete (`src/SqlHarness.Core/AgentBenchmarkOutput.cs:45-63,360-410`; `tests/SqlHarness.Tests/StatementMetricsTests.cs:224-242`). MCP descriptions and artifact docs state the collected-counter boundary and point to saved statement evidence. The SDK regression follows continuation and verifies partial rows at 4096 and 65536 bytes (`tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:152-201`). The implementation report records the required focused passes; I did not rerun them per the reviewer contract.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `src/SqlHarness.Core/AgentBenchmarkOutput.cs:370-387` — Missing statement evidence for an individual run/cell is lost when other rows exist. The compact case can therefore appear complete despite a missing repetition. Include every run case and prevent `complete` when any expected run lacks statement evidence; report the gap explicitly.

#### Minor (Nice to Have)

- `src/SqlHarness.Core/AgentBenchmarkOutput.cs:374-384` — A nonzero global `OmittedStatements` count marks every case partial, even cases unaffected by the cap. Keep the conservative status, but expose omission scope by case so unaffected cases do not receive an inaccurate partial label.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The compact projection is bounded and communicates its counter-coverage limits, but it can suppress missing per-run evidence and report the remaining case as complete. That conflicts with the task's fail-closed evidence semantics.
