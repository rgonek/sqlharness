# Plan 095 verification evidence

## Scope and review boundary

Implementation began at immutable base `384357c031a321ab2f32cf99f049b9c0bc4c71f2`. Task 1 tests were added in `add7efa` and hardened in `c36886e`; Task 2's approved implementation is `733781e5fb2c407d676312ef0c04039f44a3f8ca`. Task 3 adds a real SDK workflow around the existing reader and updates the four human-facing contracts. The product branch is `plan-095`; all task and whole-branch reviews are approved; local integration completed at `55f06a5`. No push, live database, paid agent call, or Linux gate was performed.

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

The serialized fixed-scope tools catalog was 28,675 UTF-8 bytes; the Postgres fixture catalog was 23,889 bytes. Both fit the 32,768-byte tools/list ceiling. The test compares every complete serialized node record across omitted defaults, explicit `pageSize=128`, and the larger request. The final-fix follow-up below additionally asserts the exact expected path preorder and uniqueness for each mode, so the SDK workflow independently establishes gap-free reconstruction.

Plan 077's `OperationAnnotations/off` records and manifest are historical baseline evidence and remain unchanged. The existing scripted workflow equality now excludes only `discoveryUtf8Bytes`, because serving the new artifact interface intentionally changes catalog size; current catalog sizes and the ceiling are asserted from live SDK discovery on each run. All other frozen workflow fields remain compared byte-for-byte after normalization.

The required Task 3 focused command passed after those changes:

`dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~AgentOptimizationWorkflowTests`

Result: 8 passed, 0 failed, 0 skipped; direct exit code 0. Full output is retained in `plans/095-evidence/task3-workflow.log` in the worktree.

## Final product verification and review boundary

The final product revision tested by the direct branch formatter and the final Windows gate is `84b47966d8c249df64cf99345d1317faf8daa76e` (`fix(095): retain statement detail limit`), with tree `fa6ffad72b8a93b0299b80a114df59066197502d`. The later Task 3 evidence/report commit changes only plans and records; it does not change that tested product tree.

The first full gate attempt exposed a real regression from Task 2: statement projection no longer honored its existing `detailLimit`. The relevant existing assertion, `Agent_projection_bounds_statement_entries_and_operator_details`, failed because it expected one selected statement at limit 1 and received two. The stage log is retained as `plans/095-evidence/gate-attempt-1-core-tests.log`; the complete failed console is `plans/095-evidence/verify-console.log`. Restoring `.Take(detailLimit)` in statement selection preserved the established projection contract while retaining byte-fit accounting. The focused regression then passed 1/1, and the final full Core and MCP suites passed on the fix.

After the final product commit, the required direct branch format check passed:

`pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 384357c031a321ab2f32cf99f049b9c0bc4c71f2`

Result: `format: 13 changed C# file(s) (branch)`, `format: OK`, direct exit code 0. Output: `plans/095-evidence/branch-format-direct-final.log`. The branch `-Fix` pass also returned exit 0 (`branch-format-fix-regression.log`). The final `just verify` format stage skipped because no C# files were uncommitted; the direct branch check covered all branch-changed C# files.

The required final Windows gate was run on that same product revision/tree:

`just verify -LogDirectory D:/temp/sqlharness-plan095-verify`

Runner PID 22656; direct exit code 0; `verify: OK stages=5 8m28s`. Stages: `ui-install` skipped because the lockfile was unchanged; build passed in 35.3s; UI check passed in 5m08s (64 files, 361 tests); Core passed 4,595, failed 0, skipped 11 (4,606 total); MCP passed 489, failed 0, skipped 4 (493 total); format skipped for zero uncommitted C# changes. Final console and per-stage logs are retained under `plans/095-evidence/verify-console-final.log` and `verify-final-*.log`; stage logs were initially written under `D:/temp/sqlharness-plan095-verify`.

Task 3's actual-SDK test measures a deterministic synthetic saved-artifact fixture and asserts the host attempted zero DB connections. It does not invoke an LLM/provider, establish production latency, measure token consumption, or represent live saved database evidence. The byte and call comparison applies only to this same 513-node fixture, the listed defaults, and the two explicit request values. The live SDK discovery asserts current catalog sizes against the 32,768-byte ceiling; Plan 077's frozen `OperationAnnotations/off` snapshots and manifest remain unchanged. Only the historical workflow comparison field `discoveryUtf8Bytes` is normalized out, since the served catalog intentionally changed; runtime catalog byte/limit assertions are separate.

This implementation branch has focused Task 3 results and a green direct formatter and final Windows gate. Task 3 review and independent whole-branch review remain pending. Local integration is authorized only after those reviews approve; this work has not merged, pushed, or accessed a live database.

## Final review fix: exact SDK traversal proof

The whole-branch review identified one nonblocking Minor: SDK completion compared 513 paths/records across three modes but did not independently establish the fixture's expected traversal. The SDK path test now asserts the exact sequence `0`, `0/0` through `0/511`, and 513 unique paths for omitted defaults, explicit `pageSize=128`, and `pageSize=512` with the larger byte budget. The traversal helper fails after 513 calls, rejects a continuation response with no nodes, and requires every continuation cursor to advance beyond the requested cursor. No product behavior changed.

The final product/test revision is `419f32a745f6bb8a08289e440fd241d8d17ea23f`, tree `08eae298821c962a8549de25a82332c8ef2e0478`, based on immutable Task 3 base `384357c031a321ab2f32cf99f049b9c0bc4c71f2`.

| Command | Result |
|---|---|
| `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~AgentOptimizationWorkflowTests` | 8 passed, 0 failed, 0 skipped; direct exit 0 (`plans/095-evidence/final-fix-focused-sdk.log`) |
| `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 384357c031a321ab2f32cf99f049b9c0bc4c71f2 -Fix` | 13 changed C# files, `format: OK`, direct exit 0 (`plans/095-evidence/final-fix-format-fix.log`) |
| `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 384357c031a321ab2f32cf99f049b9c0bc4c71f2` | 13 changed C# files, `format: OK`, direct exit 0 (`plans/095-evidence/final-fix-format-direct.log`) |
| `just verify -LogDirectory D:/temp/sqlharness-plan095-final-review-verify` | `verify: OK stages=5 9m20s`, direct exit 0. UI: 361 tests; Core: 4,595 passed, 11 skipped, 0 failed; MCP: 489 passed, 4 skipped, 0 failed. Build passed; formatter stage skipped because the committed tree had no uncommitted C# files, covered by the direct branch formatter above. Durable console and stage output are under `plans/095-evidence/final-review-*`. |

The original Task 3 review finding is resolved by commit `419f32a`. A final scoped re-review of this revision remains pending with the controller; no whole-branch approval, merge, or push is claimed here.

## Review approval and delivery

All task spec/quality reviews are approved. Sol low approved the whole branch and the final scoped re-review of `f0f7d60..e8b03db`; the sole Minor is addressed and no findings remain. Reports are archived in `plans/095-evidence/*review.md` and `*rereview.md`. Final verified product/test revision is `419f32a`; subsequent commits change documentation/evidence only. The owner authorized local merge into main after completion and green checks. No push is authorized or performed. Linux, paid-agent trials and live databases were not used.

## Local integration

Authorized local merge completed into `main` at `55f06a5`. `git diff --exit-code 419f32a HEAD -- src tests` returned 0 after merging, confirming the Windows-verified product/test tree is unchanged. Subsequent edits archive logs and update delivery documentation only; no redundant gate was run for these documentation-only edits. Main was clean before merging. No push occurred. Earlier gate/focused logs and the Task 1 behavioral RED are retained alongside the final logs under `plans/095-evidence/`.
