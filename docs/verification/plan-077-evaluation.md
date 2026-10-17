# Plan 077 evaluation

Task 0 freezes a portable real-MCP harness before product behavior changes. Later tasks must not rewrite the frozen protocol records or the pre-change trial log. Product changes start at Task 1.

## Evidence classes

| Class | Task 7 status |
|---|---|
| Unit semantics | Core tests remain the authority for calculation and extraction. Task 6 recorded 163 Core and 81 MCP focused passes; final Windows gate is recorded below. Synthetic outcomes do not prove SQL execution. |
| Deterministic MCP protocol workflows | Candidate rerun uses the SDK client and production MCP mapping/adapter/artifact reader over stdio. 7/7 workflow tests passed; candidate records and byte comparison are below. Zero DB connections. |
| Model-driven task trials | Nine pre-change runs below (4/9 evidence-complete). The one nine-attempt candidate wave produced 9 provider errors (HTTP 402) before any model turn or MCP call. Paired model behavior and efficiency remain unverified. |
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

Bounds: 20 MCP tool calls and five minutes. Reaching either cap is incomplete, not success. `--max-turns 20` is a Grok CLI turn budget; it is not the D7 call bound. A stored run with `numTurns` 20 remains scorable when MCP tool calls were under 20 and duration was under five minutes. Model `grok-4.6`, effort `medium`. MCP call counts come from `grok export` lines matching `UseTool: sqlharness__`. Reported `input_tokens` / `output_tokens` come from Grok JSON usage; they are not manufactured from raw_bytes or byte/4. Scoring uses the hidden oracle's `requiredClaims`, `requiredEvidence`, `allowedSelectors`, and `forbiddenClaims`. Extra selectors outside `allowedSelectors` fail the outcome. Section-qualified evidence (`matrix-cells-pages-if-omitted`, `artifact-plan-for-warned-statement`) requires that section in the assistant export, not any `sqlharness_artifact` call.

Detailed logs: `artifacts/plan-077/prechange/` (gitignored). Compact summary: `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/trials/prechange-summary.json`.

## Frozen scripted workflow costs (pre-change)

Discovery is `tools/list` UTF-8 bytes of the served catalog (separate from call text). Model-visible bytes are the normalized final text block.

| Scenario | Budget | Discovery | Calls | Model-visible UTF-8 (sum) |
|---|---|---|---|---|
| no-improvement | 4096 | 18399 | 2 | 3454 |
| no-improvement | 65536 | 18399 | 2 | 3454 |
| partial-matrix-32 | 4096 | 18399 | 2 | 12598 |
| partial-matrix-32 | 65536 | 18399 | 1 | 48562 |
| colliding-operators | 4096 | 18399 | 3 | 2935 |
| colliding-operators | 65536 | 18399 | 3 | 2935 |
| unavailable-metrics | 4096 | 18727 | 1 | 3962 |
| unavailable-metrics | 65536 | 18727 | 1 | 4154 |

At 4096 bytes the 32-case matrix compare omits 25 cases (`continuation` 7) and the follow-up `matrix-cells` page recovers them. At 65536 the compare fits in one call. BatchSize 8 is a policy-v2 `fail` (`elapsed-regressed`); BatchSize 16 is `inconclusive` (`unstable-elapsed`). Unavailable-metrics elapsed diagnostics use exact 0.8 vs 0.4 ms; whole-millisecond elapsed fields remain 1; CPU and logical reads are `unavailable`.

## Pre-change agent trials

Nine fresh Grok processes, three families × three runs, 2026-10-16, model `grok-4.6`, effort `medium`. Post-change nine are Task 7. No trial was rerun for a better answer. Invalid isolation attempts (oracle/repo reads) were discarded and are not in this table.

Official exports were rescored from the oracle fields. Isolation grep of the official exports found no `D:\Dev\sqlharness`, `oracle/`, `WorkflowModule`, or `requiredClaims`. Evidence-complete count is 4/9.

| Family | Run | Session | Seconds | MCP tools | Turns | Input tok | Output tok | Exit | Outcome |
|---|---|---|---|---|---|---|---|---|---|
| no-improvement | 1 | `9d987f0b-d73e-4c69-a2b0-26f84dd9239a` | 222 | 5 | 20 | 73906 | 6113 | 1 | failed-oracle; contaminated (temp/host-DLL search); no conclusion |
| no-improvement | 2 | `215e4f6f-ff88-493d-bfc7-12ce877755cc` | 184 | 7 | 20 | 74589 | 5435 | 1 | failed-oracle (no scored conclusion; 20 Grok turns, MCP calls 7) |
| no-improvement | 3 | `f96741b9-24c9-484c-b714-af2269b7c4bf` | 155 | 14 | 13 | 81134 | 5610 | 0 | failed-oracle (`sqlharness_query` outside allowed selectors) |
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

## Task 7 candidate evidence

### Candidate identity and capture

The final protocol capture was rerun after the served-description wording correction. Candidate product behavior is based on `de0237888112ac7b5f8ede089df60557bd24d44e` plus the Task 7 `McpToolCatalog.cs` change (SHA256 `FDEB15587DE24C460944C867CC64F82E6BC3CA4149DE8BD3EF40B64F62DD5EAB` at capture). The fixture host, scenario inputs, oracle, scripted requests and trial prompt files were unchanged from the frozen baseline. The uncommitted test-harness diff against base `de02378` hashes to SHA256 `C7FD62F440C11E56ED78389698434BDD3E681C5899D084FB45E321FA27E1C2EC`; a SHA256 manifest of the changed harness files, captured post-change protocol records, and trial summary hashes to `0D36F12A8D099250A00A2D9702A750CBA7EA489BE81C4D51848040D2F65023EF`. The candidate SDK client persisted its actual records to `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/records/postchange/`; those files are distinct from the immutable pre-change records under `records/*.json`. They contain normalized synthetic SQL/artifact values, each actual response text, arguments, error flag, omissions, continuation and selected artifact ids.

Grok isolation used a disposable process `USERPROFILE` and `HOME`. `grok inspect` reported no user config or MCP servers from the original profile. Only `.grok/auth.json` was copied into the disposable home; no user config or trust file was copied. Project MCP configuration, skill copy and trusted-folder entry stayed under per-trial temporary directories and the disposable profile. The runner received the built fixture-host DLL directly, avoiding replacement of the shared temp host directory. The original profile was never addressed by the child processes.

### Deterministic MCP workflows

Command (with `SQLHARNESS_077_CAPTURE_DIR` set to `artifacts/plan-077/postchange-protocol`):

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore `
  --filter FullyQualifiedName~AgentOptimizationWorkflowTests `
  --logger "console;verbosity=detailed"
```

Result: 7 passed, 0 failed. The SDK initialized the real fixture server, listed the served tools, called compare/artifact/capabilities, recovered offline artifact pages, exercised request-scope mapping, and observed zero database connections. The current candidate envelope is MCP v3; frozen pre-change records are v2. That planned version change was retained in the comparison.

The following table sums the actual model-visible UTF-8 response blocks from producer and required follow-up calls. It lists tools/list discovery and stdout protocol frame bytes separately. Baseline call text is from the frozen Task 0 records; candidate text is from the tracked `postchange` records.

| Workflow | Budget | Baseline text bytes / calls / continuations | Candidate text bytes / calls / continuations | Text byte delta | Candidate discovery bytes | Candidate protocol frame bytes |
|---|---:|---:|---:|---:|---:|---:|
| no-improvement | 4096 | 3454 / 2 / 0 | 3402 / 2 / 0 | -52 | 19415 | 24815 |
| no-improvement | 65536 | 3454 / 2 / 0 | 3402 / 2 / 0 | -52 | 19415 | 24815 |
| partial-matrix-32 | 4096 | 12598 / 2 / 1 | 19373 / 2 / 1 | +6775 | 19415 | 51418 |
| partial-matrix-32 | 65536 | 48562 / 1 / 0 | 19108 / 1 / 0 | -29454 | 19415 | 50838 |
| colliding-operators | 4096 | 2935 / 3 / 0 | 2971 / 3 / 0 | +36 | 19415 | 24079 |
| colliding-operators | 65536 | 2935 / 3 / 0 | 2971 / 3 / 0 | +36 | 19415 | 24079 |
| unavailable-metrics | 4096 | 3962 / 1 / 0 | 2873 / 1 / 0 | -1089 | 19743 | 23192 |
| unavailable-metrics | 65536 | 4154 / 1 / 0 | 2873 / 1 / 0 | -1281 | 19743 | 23192 |

Candidate tools/list discovery is 1019 bytes larger than pre-change for each profile mode, still below the 32768-byte catalog limit. Protocol frame bytes are machine-local and the frozen baseline did not retain a corresponding frame-byte measurement, so no before/after frame comparison is available. All individual candidate tool responses fit their requested budgets.

The 4096-byte matrix producer returned four of 32 cases, declared 28 omitted, and advanced the cursor to 4; the real artifact follow-up recovered the remaining 28. Coverage remained 32 requested, 32 completed, 0 not executed. Its +6775 bytes pay for per-case policy decisions and coverage fields, which were absent from the v2 baseline matrix inventory. Candidate decisions include BatchSize 8 = `fail` (`elapsed-regressed`) and BatchSize 16 = `inconclusive` (`unstable-elapsed`). The 65536-byte candidate workflow includes all 32 decisions in one call while using 29454 fewer bytes than baseline. To retain complete case-level decisions, Task 7 amends the byte criterion in the plan; it does not claim a byte saving for the 4096 matrix case.

### Candidate model trials

One wave of nine fresh `grok-4.6` / `medium` attempts used the frozen prompts, scenario seeds and 20-call / 300-second bounds. The fixture host was the test-only server. No attempts were retried. All failed before an agent turn or MCP call with HTTP 402 `Grok Build usage balance exhausted`; model input/output usage is unavailable, not zero. The records are in `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/trials/postchange-summary.json`; local debug and stderr evidence is under ignored `artifacts/plan-077/postchange/`.

| Family | Run | Session | Seconds | MCP calls | Input / output tokens | Exit | Outcome |
|---|---:|---|---:|---:|---|---:|---|
| no-improvement | 1 | `5aee9824-8bef-4367-bfbf-b1b07223591d` | 18 | 0 | unavailable / unavailable | 1 | error: HTTP 402 |
| no-improvement | 2 | `329c4f37-9bba-45e7-b171-1e006415210c` | 5 | 0 | unavailable / unavailable | 1 | error: HTTP 402 |
| no-improvement | 3 | `99f5b3f1-d124-4b35-a8fe-646878483cfa` | 5 | 0 | unavailable / unavailable | 1 | error: HTTP 402 |
| partial-matrix-32 | 1 | `ebc8673c-4f34-4ab8-a805-55b3190ca325` | 5 | 0 | unavailable / unavailable | -1 | error: HTTP 402 |
| partial-matrix-32 | 2 | `39e05ba4-7cc8-44de-b6b7-3814d57e15ca` | 5 | 0 | unavailable / unavailable | 1 | error: HTTP 402 |
| partial-matrix-32 | 3 | `636937de-55e4-492e-8b84-f4805599b30a` | 4 | 0 | unavailable / unavailable | 1 | error: HTTP 402 |
| colliding-operators | 1 | `6e4b8a56-b2ef-4573-ae96-e5a3446f6d38` | 5 | 0 | unavailable / unavailable | 1 | error: HTTP 402 |
| colliding-operators | 2 | `21188557-1884-438d-9996-c2bddaf61c46` | 4 | 0 | unavailable / unavailable | 1 | error: HTTP 402 |
| colliding-operators | 3 | `a8b258ae-ac0a-474d-b3c3-a319858825cd` | 4 | 0 | unavailable / unavailable | 1 | error: HTTP 402 |

No candidate conclusion or evidence selection was produced, so correctness counts, unnecessary reads and comparable agent cost cannot be scored. The pre-change evidence-complete count remains 4/9; candidate behavioral acceptance and agent-efficiency claims are unverified. A rerun requires provider availability and is outside this candidate wave.

### Final verification

The final Windows gate passed on product commit `2ce4870` (Task 7 semantic changes were captured at `617153c`; `2ce4870` adds only branch-wide C# formatting cleanup), log directory `D:\temp\sqlharness-gate-logs\plan-077-task7-final-2ce4870-20261017`: UI-install skipped (lockfile unchanged), UI-check passed (50 files / 260 tests, 162.43s), restore passed (4.4s), build passed (14.5s), tests passed (Core 4060 passed / 11 skipped; MCP 425 passed / 4 skipped), and format skipped because the committed tree had no working-tree C# changes. The supplemental branch formatter was applied to 14 files, then verified successfully across 50 C# files with `pwsh scripts/format-changed.ps1 -Mode branch -BaseRef main`. Final gate: `verify: OK stages=6`, 5m05s. Before branch formatting, repeated default vmThreads UI runs stalled without summaries; a full `vitest run --pool=forks --maxWorkers=1 --reporter=verbose` completed 50 files / 260 tests in 170.26s. Vite now uses forks/1, trading runtime for a completed Windows gate while leaving vmThreads-specific behavior unverified. Linux and live-database lanes were not run; no install, push, merge or publish occurred.

## Concerns

- `InternalsVisibleTo` for `SqlHarness.Mcp.WorkflowHost` on Core and Mcp is a test-host seam, not a product fixture flag.
- Candidate model-visible bytes normalize isolated homes; absolute `artifactDirectory` remains a fixture response field normalized to `<home>` in the persisted records.
- Oracle scoring of the stored nine yields 4/9 evidence-complete. Task 7 must use that count, not the earlier keyword scores. Extra tools outside `allowedSelectors` fail the outcome.
- The candidate trial wave made no model calls because provider balance was exhausted. Its isolated-profile mechanism is reproducible, but agent behavior remains unverified.
- Grok headless often hangs after end_turn; kill-on-exit yields process exit 1 for some completed runs.
- Two no-improvement runs hit 20 Grok turns. The MCP call bound was not reached; those rows are not incomplete.
- no-improvement run 1 is flagged contaminated for leftover `%TEMP%` / host-DLL search and is kept in the record.
