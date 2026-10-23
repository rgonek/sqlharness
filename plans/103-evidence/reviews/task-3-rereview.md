### Finding Verdicts

- Missing statement evidence for an individual run/cell can leave the compact case complete — **ADDRESSED**. `src/SqlHarness.Core/AgentBenchmarkOutput.cs:375-395` unions run cases with extracted-row cases, detects missing repetitions within each variant/set/cell, emits an explicit limitation, and yields `partial` when evidence survives or `unavailable` when the case has none. Regression `tests/SqlHarness.Tests/StatementMetricsTests.cs:246-273` covers a missing candidate repetition and an entirely empty baseline case. The appended report records the covering focused suite as 116/116 passed; I did not rerun it.

### New Breakage in the Fix Diff

None. The fix adds no `ExpectedRunCount` or `MissingRunCount` fields; `AgentStatementEvidenceCase` retains its existing serialized shape (`src/SqlHarness.Core/AgentBenchmarkOutput.cs:46-55`). The current-contract golden `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/records/no-improvement-65536.json:26` still represents the existing case fields with five statements per variant and no gap limitation. `AgentOptimizationWorkflowTests.Scripted_workflows_fit_4096_and_65536_and_freeze_portable_records` compares that serialized response with the golden.

### Out-of-Scope Observations

None.

### Verdict

**Fix round:** All findings addressed, no new Critical/Important breakage.
