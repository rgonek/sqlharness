### Spec Compliance

- ❌ Issues found: the matrix and bounded diagnostic are complete, but the follow-up states an unverified cause as fact. The task brief requires uncertain causes to remain unresolved; see [plans/107-evidence/107-list-runtime-source-follow-up.md:10].
- ⚠️ Cannot verify from diff: the MCP log records the informational version from the test assembly, via `typeof(McpParameterBatchTests).Assembly`, rather than directly identifying the loaded `SqlHarness.Mcp` assembly [tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs:152]. The build likely shares the commit version, but the diff does not establish that. The controller should confirm that relationship or log the server assembly’s identity.

### Strengths

- The MCP fixture runs 12 real tool requests across both `IN` spellings: singleton, multi-value, empty list, multiple sets, setup reuse, and a caller `@Ids1` collision [tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs:86]. The request/response log confirms identical baseline and candidate SQL, the intended named sets, and zero connection attempts [plans/107-evidence/task-2-report.md:5].
- Core coverage exercises named sets, setup reuse, and the generated-name collision [tests/SqlHarness.Tests/CompareTests.cs:109]. The preserved expected-contract runs are red while the committed characterization runs are green, which fits the controller’s bounded-diagnosis ruling [plans/107-evidence/task-2-report.md:36].
- The follow-up keeps the proposed implementation narrow, preserves caller SQL and parameters, and explicitly excludes unrelated limit changes [plans/107-evidence/107-list-runtime-source-follow-up.md:22].

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- [plans/107-evidence/107-list-runtime-source-follow-up.md:10] Attributes the parenthesized-form collision to combining setup, baseline, and candidate classification bindings. The recorded responses establish the rejection message and phase, but do not establish that internal mechanism; non-setup requests reproduce the same collision too. Mark this as a hypothesis for the follow-up or add source-level evidence before prescribing that cause. This is plan-mandated because the brief says uncertain causes must stay unresolved.

#### Minor (Nice to Have)

- [.superpowers/sdd/107-list-runtime-build-diagnosis/task-2-report.md:1] The implementation report is committed under the SDD scratch directory while its durable logs, request shapes, and follow-up already live under `plans/107-evidence`. After final review, remove the scratch report from the branch; there is no need to duplicate it into durable evidence.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The corrected request matrix and zero-connection MCP evidence are strong, and no production behavior changed. The causal handoff overstates what the diagnostic proves, and the recorded source identity should be confirmed as the server assembly before treating it as definitive.


