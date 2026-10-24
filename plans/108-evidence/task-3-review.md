### Spec Compliance

- ✅ Spec compliant for the changed behavior: the regression sends real MCP client requests over in-memory transports with `variant` omitted; measure and measure-set succeed with public variant `query`, while compare returns an error naming `baseline, candidate` (`tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:131`, `:145`, `:161`). The artifact diagnostic assertion now includes the manifest artifact kind (`tests/SqlHarness.Tests/ArtifactReaderTests.cs:172`).
- ⚠️ Cannot verify from this diff: the specific compatibility assertions for scope mismatch, absent saved plans, invalid selectors, and the 4096-byte minimum budget are unchanged. The task report claims the focused Core/MCP runs exercised them; the included logs show 87 and 46 passing tests but do not list test names. Controller should retain the earlier task evidence or confirm those cases there.

### Strengths

- The test covers the MCP protocol path through `McpClient.CreateAsync` and `CallToolAsync`, with an unreachable synthetic profile and no database connection (`tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:103`, `:119`).
- The RED log shows the omitted-variant measure request failing before restoration; GREEN shows the focused regression passing. The broader focused logs also show nonzero discovered test counts and clean exits (`plans/108-evidence/task-3-red-mcp-omitted-variant.log:15`, `plans/108-evidence/task-3-green-mcp-omitted-variant.log:13`, `plans/108-evidence/task-3-focused-core.log:12`, `plans/108-evidence/task-3-focused-mcp.log:13`).
- The process-wide `SQLHARNESS_HOME` change is isolated by the test class's `McpScopeHome` collection (`tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:34`); the collection disables parallelization (`tests/SqlHarness.Mcp.Tests/McpScopeTests.cs:9`).

### Issues

#### Critical (Must Fix)
None.

#### Important (Should Fix)
None.

#### Minor (Nice to Have)
None.

### Assessment

**Task quality:** Approved

**Reasoning:** The implementation adds the requested protocol-level regression and strengthens the artifact-kind diagnostic assertion without product changes. The only evidence limit is that this diff's aggregate logs do not identify the unchanged compatibility cases individually.
