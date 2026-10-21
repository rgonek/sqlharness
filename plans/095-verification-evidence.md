# Plan 095 verification evidence

## Scope and review boundary

Implementation began at immutable base `384357c031a321ab2f32cf99f049b9c0bc4c71f2`. Task 1 tests were added in `add7efa` and hardened in `c36886e`; Task 2's approved implementation is `733781e5fb2c407d676312ef0c04039f44a3f8ca`. Task 3 adds a real SDK workflow around the existing reader and updates the four human-facing contracts. The product branch is `plan-095`; whole-branch review and authorized local integration remain pending. No push, live database, paid agent call, or Linux gate was performed.

## Task 1 baseline and RED evidence

Before the Task 1 regressions, both scoped baselines passed:

| Command | Result |
|---|---|
| `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests"` | 98 passed, 0 failed, 0 skipped |
| `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~ArtifactReaderTests` | 63 passed, 0 failed, 0 skipped |

The new regressions then showed the intended RED against the unchanged implementation:

| Command | Result |
|---|---|
| MCP schema/budget filter above | 99 passed, 7 failed, 0 skipped, 106 total; the served artifact schema and behavior lacked `maxResultBytes`/`pageSize` |
| Core artifact reader filter above | 62 passed, 7 failed, 0 skipped, 69 total; the page-size API/default statement window was absent |

The complete RED logs remain in the local SDD workspace at `.superpowers/sdd/095-budget-aware-artifact-pages/task1-mcp-red.log` and `task1-core-red.log`; the concise outputs above preserve the evidence after that scratch workspace is removed. Task 1's strengthened refusal and cursor assertions were reviewed before Task 2.

## Task 2 focused GREEN evidence

The approved Task 2 report records these sequential focused passes on `733781e`:

| Command | Result |
|---|---|
| `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests"` | 106 passed, 0 failed |
| `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter FullyQualifiedName~ArtifactReaderTests` | 69 passed, 0 failed |
| `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpOutputTests"` | 129 passed, 0 failed |

These are focused test results, not a full verification gate.

## Task 3 SDK aggregation measurement

`AgentOptimizationWorkflowTests.Artifact_513_node_sdk_completion_measures_default_and_explicit_pages` creates one saved 513-node plan through the synthetic workflow host, then reads that same artifact through actual `McpClient` tool calls. The host forbids database connects and recorded `connectionAttempts=0`. Discovery and artifact reads use the real fixed-scope stdio server and SDK. The producer call is shared and excluded from both read-completion totals; tools/list discovery is reported separately.

| Read configuration | Artifact calls | Request bytes | Response bytes | Combined request + response |
|---|---:|---:|---:|---:|
| Omitted `pageSize` and `maxResultBytes` (128 source nodes/page, 65536-byte response default) | 5 | 1,207 | 192,080 | 193,287 |
| `pageSize=512`, `maxResultBytes=131072` | 2 | 555 | 189,910 | 190,465 |

The larger requested window reduced the read from five calls to two because all 512 requested source nodes fit within the larger response budget. Combined wire bytes decreased by 2,822; there was no total-byte increase in this fixture. These measurements are UTF-8 bytes on the SDK stdin/stdout protocol streams, not model tokens and not a token-savings claim. The selected response text also remained within its requested byte budget.

The serialized fixed-scope tools catalog was 28,675 UTF-8 bytes; the Postgres fixture catalog was 23,889 bytes. Both fit the 32,768-byte tools/list ceiling. The test compares every complete serialized node record and stable path across omitted defaults, explicit `pageSize=128`, and the larger request, confirming defaults compatibility and gap-free reconstruction.

Plan 077's `OperationAnnotations/off` records and manifest are historical baseline evidence and remain unchanged. The existing scripted workflow equality now excludes only `discoveryUtf8Bytes`, because serving the new artifact interface intentionally changes catalog size; current catalog sizes and the ceiling are asserted from live SDK discovery on each run. All other frozen workflow fields remain compared byte-for-byte after normalization.

The required Task 3 focused command passed after those changes:

`dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~AgentOptimizationWorkflowTests`

Result: 8 passed, 0 failed, 0 skipped; direct exit code 0. Full output is retained in `plans/095-evidence/task3-workflow.log` in the worktree.

## Remaining completion evidence

The final branch formatter, Windows `just verify`, tested commit/tree, and task/whole-branch review results are recorded here after they finish. Until then this is implementation-branch evidence only; it is not a review approval or integration result.
