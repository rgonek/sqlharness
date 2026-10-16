# Plan 077 evaluation

Task 0 freezes a portable real-MCP harness before product behavior changes. Later tasks must not rewrite the frozen protocol records or the pre-change trial log. Product changes start at Task 1.

## Evidence classes

| Class | Status after Task 0 |
|---|---|
| Unit semantics | Existing Core tests remain the authority for calculation and extraction. Synthetic module outcomes do not prove SQL execution. |
| Deterministic MCP protocol workflows | Implemented. SDK client talks to the test-only host over stdio. |
| Model-driven task trials | Nine pre-change runs recorded below. Post-change nine are Task 7. |
| Live database integration | Not authorized. Not run. |

External model-service availability is separate from local protocol-fixture reproducibility. `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~AgentOptimizationWorkflowTests` does not call a model.

## Portable journal baseline

`plans/evidence/077-workflow-baseline.json` is `replayable: false`. It records four anonymous sessions, eleven compare-plus-artifact workflows and forty calls. Whole-session emitted bytes are larger than compare-plus-artifact bytes because the journal also contains other operations. Discovery, model input and final chat are listed under `excluded` and were not manufactured from `raw_bytes` or byte/4. `sourceRevision` and `responseBudgetBytes` stay null.

## Test-only host

Project: `tests/SqlHarness.Mcp.WorkflowHost/SqlHarness.Mcp.WorkflowHost.csproj`.

The host is a separate test executable. It calls production `McpHost.CreateServerOptions` and `McpToolCatalog.Wire` with a module factory. Compare and matrix evidence is synthetic. Artifact and plan reads use the production offline reader, distiller and `CompareArtifactWriter`. `ForbiddenSessionFactory` counts connection attempts and throws; the process writes `workflow-host-stats.json` with `connectionAttempts` on exit. Stdout is MCP frames only.

Launch:

```powershell
dotnet tests/SqlHarness.Mcp.WorkflowHost/bin/<Configuration>/net10.0/SqlHarness.Mcp.WorkflowHost.dll `
  --scenario <id> --home <isolated-directory>
```

Required environment for the child process:

- `SQLHARNESS_HOME` = the same isolated directory
- `SQLHARNESS_077_FIXTURE_PW` = any dummy value (Postgres profile uses sql auth; the host never connects)

Scenario ids: `no-improvement`, `partial-matrix-32`, `colliding-operators`, `unavailable-metrics`, `request-scope-mapping`. Manifest: `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/manifest.json`.

SDK suite (repeatable, no model):

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~AgentOptimizationWorkflowTests
```

Frozen protocol records: `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/records/<scenario>-<budget>.json`. Model-visible UTF-8 bytes are measured after substituting the isolated home with `<home>` and artifact directory names with `<artifact-id>`. Raw protocol-frame byte counts include absolute `artifactDirectory` paths and are machine-local until Task 2 switches MCP to `artifactId`; they are logged by the test output, not stored in the snapshot.

## Session-scoped agent connection (Grok Build)

Grok CLI 1.0.60 has no `--mcp-config` flag. Each trial is a fresh `grok` process with a temporary working directory and `grok mcp add --scope project`. The runner does not edit `~/.grok/config.toml`. For the trial duration it replaces `~/.grok/trusted_folders.toml` with only the trial work directory, then restores the previous bytes.

The official nine copied the built host to `%TEMP%\sqlharness-077-host` so the MCP command did not point at the worktree. Each family copied only that family's two SQL files plus the prompt and `skills/sqlharness/SKILL.md`. Oracle files, fixture internals, other runs and this plan were absent from the trial directory.

Runner: `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/run-agent-trials.ps1`.

```powershell
pwsh -NoProfile -File tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/run-agent-trials.ps1 -Phase prechange
```

Equivalent connection for one run (official nine isolation):

```powershell
$work = "<trial-work>"
$home = "<isolated-home>"
$hostDll = Join-Path $env:TEMP "sqlharness-077-host\SqlHarness.Mcp.WorkflowHost.dll"
Set-Location $work
grok mcp add --scope project sqlharness `
  -e "SQLHARNESS_HOME=$home" `
  -e "SQLHARNESS_077_FIXTURE_PW=unused-fixture-password" `
  -- dotnet $hostDll --scenario no-improvement --home $home
# replace ~/.grok/trusted_folders.toml with only $work; restore original bytes in finally
grok --prompt-file prompt.md --model grok-4.6 --reasoning-effort medium `
  --always-approve --no-subagents --disable-web-search --max-turns 20 `
  --output-format json --session-id <guid> --cwd $work --debug-file <log> `
  --deny "Read(D:\\Dev\\**)" --deny "Grep(D:\\Dev\\**)" --deny "Edit(D:\\Dev\\**)" `
  --deny "Bash(*)"
```

The official nine and the committed runner use those four denials. `--always-approve` still lets the agent use filesystem tools inside trusted folders; the denials and trusted-folder replace are required.

Grok often stays alive after `headless: no pending background tasks, exiting`. The runner treats that debug tail or `"stopReason":"end_turn"` as agent completion, kills the process, and recovers the conclusion with `grok export <sessionId>`. Exit code 1 after that kill is not a model-service failure.

Bounds: 20 MCP tool calls and five minutes. Reaching either cap is incomplete, not success. `--max-turns 20` is a Grok CLI turn budget; it is not the D7 call bound. A stored run with `numTurns` 20 remains scorable when MCP tool calls were under 20 and duration was under five minutes. Model `grok-4.6`, effort `medium`. MCP call counts come from `grok export` lines matching `UseTool: sqlharness__`. Reported `input_tokens` / `output_tokens` come from Grok JSON usage; they are not manufactured from raw_bytes or byte/4. Scoring uses the hidden oracle's `requiredClaims`, `requiredEvidence`, `allowedSelectors`, and `forbiddenClaims`.

Detailed logs: `artifacts/plan-077/prechange/` (gitignored). Compact summary: `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/trials/prechange-summary.json`.

## Frozen scripted workflow costs (pre-change)

Discovery is `tools/list` UTF-8 bytes of the served catalog (separate from call text). Model-visible bytes are the normalized final text block.

| Scenario | Budget | Discovery | Calls | Model-visible UTF-8 (sum) |
|---|---|---|---|---|
| no-improvement | 4096 | 18399 | 2 | 3492 |
| no-improvement | 65536 | 18399 | 2 | 3492 |
| partial-matrix-32 | 4096 | 18399 | 2 | 12598 |
| partial-matrix-32 | 65536 | 18399 | 1 | 49312 |
| colliding-operators | 4096 | 18399 | 3 | 2960 |
| colliding-operators | 65536 | 18399 | 3 | 2960 |
| unavailable-metrics | 4096 | 18727 | 1 | 3962 |
| unavailable-metrics | 65536 | 18727 | 1 | 4154 |

At 4096 bytes the 32-case matrix compare omits 25 cases (`continuation` 7) and the follow-up `matrix-cells` page recovers them. At 65536 the compare fits in one call. BatchSize 8 is a policy-v2 `fail` (`elapsed-regressed`); BatchSize 16 is `inconclusive` (`unstable-elapsed`). Unavailable-metrics elapsed diagnostics use exact 0.8 vs 0.4 ms; whole-millisecond elapsed fields remain 1; CPU and logical reads are `unavailable`.

## Pre-change agent trials

Nine fresh Grok processes, three families × three runs, 2026-10-16, model `grok-4.6`, effort `medium`. Post-change nine are Task 7. No trial was rerun for a better answer. Invalid isolation attempts (oracle/repo reads) were discarded and are not in this table.

Official exports were rescored from the oracle fields. Isolation grep of the official exports found no `D:\Dev\sqlharness`, `oracle/`, `WorkflowModule`, or `requiredClaims`. Evidence-complete count is 5/9.

| Family | Run | Session | Seconds | MCP tools | Turns | Input tok | Output tok | Exit | Outcome |
|---|---|---|---|---|---|---|---|---|---|
| no-improvement | 1 | `9d987f0b-d73e-4c69-a2b0-26f84dd9239a` | 222 | 5 | 20 | 73906 | 6113 | 1 | failed-oracle; contaminated (temp/host-DLL search); no conclusion |
| no-improvement | 2 | `215e4f6f-ff88-493d-bfc7-12ce877755cc` | 184 | 7 | 20 | 74589 | 5435 | 1 | failed-oracle (no scored conclusion; 20 Grok turns, MCP calls 7) |
| no-improvement | 3 | `f96741b9-24c9-484c-b714-af2269b7c4bf` | 155 | 14 | 13 | 81134 | 5610 | 0 | evidence-complete (`sqlharness_query` outside allowed selectors) |
| partial-matrix-32 | 1 | `4d147426-b80e-482a-8d50-8b8259c4037e` | 124 | 6 | 12 | 44281 | 5842 | 0 | failed-oracle (reported 0 fail / 0 inconclusive) |
| partial-matrix-32 | 2 | `2acbd0f4-f2c7-47f7-88c0-9f7a65326fe8` | 162 | 19 | 9 | 91787 | 8514 | 0 | failed-oracle (13 cells unread) |
| partial-matrix-32 | 3 | `ef4ceb92-67c9-499a-bc3f-91a0c918ef18` | 151 | 9 | 13 | 72884 | 7932 | 0 | evidence-complete |
| colliding-operators | 1 | `5b86e5a0-c6c7-49f7-9d1b-1ab0ce8df039` | 53 | 9 | 6 | 30951 | 2991 | 0 | evidence-complete |
| colliding-operators | 2 | `a504a021-43c1-4012-a416-e65d45a4ac61` | 49 | 9 | 6 | 23389 | 2528 | 0 | evidence-complete |
| colliding-operators | 3 | `485447a6-3a40-491f-a4a4-dfa085c1e39a` | 62 | 9 | 6 | 31397 | 2941 | 0 | evidence-complete |

Spot checks of stored conclusions:

- no-improvement run 1 hit `max_turn_requests` while searching leftover `%TEMP%` trial directories and the host DLL; it is kept in the record and flagged `contaminated`.
- partial-matrix-32 run 1 recovered 32/32 cases through `artifact` `matrix-cells` and treated paging as recovered inventory, and reported 0 fail / 0 inconclusive against the then-broken fixture diagnostics.
- colliding-operators run 1 used `operators` then `plan`, did not treat shared `nodeId` 1 as identity, and located `SpillToTempDb` on candidate statement 1.

Two no-improvement runs exhausted 20 Grok turns with 5–7 MCP calls. They are not incomplete under the D7 cap. Exit 1 on those rows is the kill-after-end_turn recovery, not a timeout (`timedOut` false, `agentFinished` true).

## Clean-checkout reproduction

1. Clone or worktree this branch.
2. `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~AgentOptimizationWorkflowTests`
3. No original journal, operator `targets.json`, credentials or network database are required.
4. Agent trials additionally need a logged-in Grok CLI and model-service access.

## Concerns

- `InternalsVisibleTo` for `SqlHarness.Mcp.WorkflowHost` on Core and Mcp is a test-host seam, not a product fixture flag.
- Frozen model-visible bytes substitute isolated homes; on-wire MCP still emits absolute `artifactDirectory` paths until Task 2.
- Oracle scoring of the stored nine yields 5/9 evidence-complete. Task 7 must use that count, not the earlier keyword scores.
- Grok `--always-approve` can still filesystem-search unless `Bash(*)` and `Read/Grep/Edit(D:\Dev\**)` are denied and `trusted_folders.toml` is replaced for the trial.
- Grok headless often hangs after end_turn; kill-on-exit yields process exit 1 for some completed runs.
- Two no-improvement runs hit 20 Grok turns. The MCP call bound was not reached; those rows are not incomplete.
- no-improvement run 1 is flagged contaminated for leftover `%TEMP%` / host-DLL search and is kept in the record.
