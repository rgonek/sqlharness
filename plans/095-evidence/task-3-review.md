### Spec Compliance

- ✅ Spec compliant for Task 3: the test uses `McpClient` calls against one saved 513-node artifact, compares omitted defaults with explicit `pageSize=128`, records request and response stream bytes for each completion, and demonstrates fewer calls at the larger page/budget (`tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:153`, `:169`, `:175`; `plans/095-verification-evidence.md:43`).
- ✅ The task does not present wire bytes as token savings. It records the fixed-scope and PostgreSQL catalog sizes separately and preserves the frozen Plan 077 artifacts while normalizing only `discoveryUtf8Bytes` in workflow equality (`plans/095-verification-evidence.md:48`, `:50`).
- ⚠️ Cannot verify from this Task 3 diff: the earlier Task 2 changes to the Core capabilities payload and full fixed/request served schemas are outside this diff. The current MCP tool declaration exposes page size 1..512 and defaults at `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:710`, but the new SDK test asserts catalog size, not those exact metadata values. The whole-branch review should check the capabilities payload and both served schemas against the docs.

### Strengths

- The workflow creates the artifact through the synthetic host, reads it through actual SDK calls, and compares 513 paths plus complete serialized node records across omitted defaults, explicit defaults, and the larger request (`tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:153`, `:169`, `:172`, `:173`). The recorded result is five calls / 193,287 combined request-response bytes versus two calls / 190,465 bytes; response text is separately checked against each byte budget (`tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:176`, `:178`; `plans/095-evidence/task3-workflow.log:37`).
- The no-database claim is backed by the fixture: `ConnectAsync` increments the counter and throws, the host persists the count, and the test asserts zero after shutdown (`tests/SqlHarness.Mcp.WorkflowHost/WorkflowModule.cs:396`, `tests/SqlHarness.Mcp.WorkflowHost/Program.cs:112`, `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:187`, `:721`).
- The statement-limit correction restores `Take(detailLimit)` while retaining whole-record byte-fit selection and cursor advancement by `selected.Count`; the existing regression asserts one statement and one operator at detail limit 1 (`src/SqlHarness.Core/AgentOutputProjection.cs:562`, `:595`, `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs:267`).
- Human-facing README and MCP docs state the byte budget, source-window bounds/defaults, and continuation behavior (`README.md:385`, `docs/mcp/runtime.md:12`, `docs/mcp/artifacts.md:11`). The evidence ledger records focused SDK output, final formatter, and final Windows gate without claiming live DB, provider-token, or production-latency evidence (`plans/095-verification-evidence.md:43`, `:62`, `:76`; `plans/095-evidence/verify-console-final.log:8`; `plans/095-evidence/branch-format-direct-final.log:2`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- None.

#### Minor (Nice to Have)

- `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:169` and `:171` assert 513 returned paths and compare the three runs, but do not assert that the 513 paths are unique or equal the fixture's expected traversal. A duplicated path replacing a missing one could satisfy those checks. Add an expected-path or distinct-count assertion to make the SDK completion test independently prove gap-free reconstruction.

### Assessment

**Task quality:** Approved

**Reasoning:** The implementation and evidence meet Task 3's SDK, call-count, byte-accounting, catalog-cap, no-DB, and documentation requirements. The remaining note is a low-severity assertion gap; the required final gate and direct branch-format results are present in the evidence.
