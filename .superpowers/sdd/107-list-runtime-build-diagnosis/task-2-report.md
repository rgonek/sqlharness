# Task 2 report: exact offline compare paths

## Result

The known-source offline Core and MCP paths reproduce a current-source defect for named int-list parameters. The test host identifies itself as `0.0.0-dev+4e36feebe7b7d48b72225c5a9c3a9c19b90a7a50`. All 12 real-SDK MCP dry-run requests completed with zero connection attempts.

Expected contract: the ten ordinary requests (both `IN` spellings across singleton, multi-value, empty-list, multiple named sets and setup reuse) succeed. The two caller-supplied `@Ids1` collision requests reject. Observed: all 12 reject.

| Request family | Expected | Observed |
|---|---|---|
| `IN @Ids`, no scalar collision | successful dry-run | `safety_rejected`, `ParseError` during baseline classification (or setup classification for setup reuse) |
| `IN (@Ids)`, non-empty list | successful dry-run | `safety_rejected`, generated `@Ids1` reported as colliding with another supplied SQL parameter |
| `IN (@Ids)`, empty list | successful dry-run | `safety_rejected`, generated `@Ids` reported as colliding |
| caller supplies `@Ids1` | rejected | rejected; parenthesized form reports the collision, bare form stops earlier with `ParseError` |

The initial exploratory fixture that used a plain `SELECT` as compare setup was invalid for the setup contract. Its exact output is retained in `task-2-core-invalid-setup-attempt.log`; it is not used as defect evidence. The confirmed setup-reuse case uses `SELECT Id INTO #ids ...`, and still reproduces the same failures.

## Tests and evidence

Core focused command:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -m:1 -p:IsTestProject=true -p:SkipDashboardUi=true -p:UseSharedCompilation=false --filter "FullyQualifiedName~CompareTests|FullyQualifiedName~ListParameterExpanderTests"
```

Final result: 65 passed, 0 failed. Exact log: [task-2-core-tests.log](../../../plans/107-evidence/task-2-core-tests.log).

MCP focused command (real test discovery enabled):

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore -m:1 -p:IsTestProject=true -p:SkipDashboardUi=true -p:UseSharedCompilation=false --filter "FullyQualifiedName~McpParameterBatchTests" --logger "console;verbosity=detailed"
```

Final result: 5 passed, 0 failed; the diagnostic test logs all 12 exact request/response pairs and asserts zero connection attempts. Exact log: [task-2-mcp-tests.log](../../../plans/107-evidence/task-2-mcp-tests.log). Private-target-free request shapes are in [task-2-request-shapes.json](../../../plans/107-evidence/task-2-request-shapes.json).

The expected-contract runs were intentionally red before converting the committed tests to green characterization assertions. They are preserved in [task-2-core-red-contract.log](../../../plans/107-evidence/task-2-core-red-contract.log) and [task-2-mcp-red-contract.log](../../../plans/107-evidence/task-2-mcp-red-contract.log). The detailed MCP log reports source informational version `0.0.0-dev+4e36feebe7b7d48b72225c5a9c3a9c19b90a7a50`.

The narrow implementation handoff is [107-list-runtime-source-follow-up.md](../../../plans/107-evidence/107-list-runtime-source-follow-up.md). No production behavior was changed. No live database, credentials, installation, restart or historical workload replay was used. The full Windows gate remains for task finalization after all executable changes.

## Changed files

- `tests/SqlHarness.Tests/CompareTests.cs` — Core current-source characterization for both SQL forms, named sets, setup reuse and caller collision.
- `tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs` — real-SDK isolated-host characterization, 12 exact requests, source identity and zero-connect assertion.
- `plans/107-evidence/*` — exact test logs, synthetic request shapes and scoped follow-up handoff.
