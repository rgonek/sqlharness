# Plan 060: A smaller agent surface — no inspect probes, no validate, no recipes, no busy gate

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 27d2a9a..HEAD -- src/SqlHarness.Mcp src/SqlHarness.Cli src/SqlHarness.Core/QueryStoreTop.cs src/SqlHarness.Core/IndexAnalysisQuery.cs src/SqlHarness.Core/Capabilities.cs tests/SqlHarness.Mcp.Tests docs/mcp.md README.md skills/sqlharness/SKILL.md`. Plan 056 must be DONE (`dryRun` replaces `validate`). Plans 055 and 059 change the same handlers; rebase on them.

## Revision 2026-10-13 (operator decision, supersedes the recipe design)

The operator ruled during execution:

1. **CLI/MCP parity.** What leaves MCP leaves the CLI too; the two APIs stay consistent with small exceptions. The earlier constraint "CLI commands are unchanged" is withdrawn.
2. **No recipes.** The `recipe` source for `query` and the embedded `query-store-top`/`index-analysis` SQL are dropped. `qstop` and `indexes` leave both surfaces with `ping`, `schema`, `counts`, `space` and `validate` (option A).

Evidence and reasoning (consulted Grok and Codex; Grok and this controller chose A, Codex chose keeping `qstop`/`indexes`; all three rejected embedded recipes):

- The activity journal (2026-10-08..13) shows 69 CLI `query`, 1 CLI `ping`, 0 `qstop`/`indexes`/`schema`/`counts`/`space`/`inspect` calls on either surface.
- The operator's usual workflow points the agent at one concrete SQL statement to optimize. `qstop` ranks the whole server (top N by total duration), has no lookup of one statement, and statement X is often absent. `indexes --object T` reports every missing-index DMV group on a table since server start (other statements' predicates), and its catalog result sets are driven by `@candidates`, so a table with no candidates returns no existing-index inventory.
- The missing-index question for X is already answered by the measured plan: `PlanStatement.MissingIndexes` (`ArtifactReader.cs`) carries table, equality/inequality/include columns and impact, and is projected in compact output.
- A recipe returns raw result sets only: it loses the six-set assembly, `IndexOverlapClassifier` verdicts and Query Store text handling, and the budget clips the raw sets.
- Follow-up candidate (not part of this plan): an overlap verdict for the plan's own `missingIndexes` hint against the existing indexes of that table, as a field of the `plan` artifact. The deleted classifier stays in git history (`IndexOverlapClassifier.cs`).

**Goal:** CLI and MCP expose only operations an agent cannot replace with `query`; MCP database calls run concurrently; `dryRun` is available on both surfaces in place of `validate`; MCP capabilities returns compact runtime configuration.

**Architecture:** `sqlharness_inspect` (all kinds) and `sqlharness_validate` leave both MCP catalogs. The CLI gains `--dry-run` on `query`, `measure` and `compare` (Core already supports `DryRun`), then loses `ping`, `schema`, `counts`, `space`, `qstop`, `indexes` and `validate`, with the Core operations, reports, artifacts and classifiers only they used. Journal and dashboard keep reading historical rows with the removed operation names. The process-wide execution gate is removed; cancellation and deadlines stay per call. MCP capabilities becomes runtime-only.

**Tech Stack:** .NET 8/10, C#, xUnit, ModelContextProtocol C# SDK, System.CommandLine (CLI).

**Spec:** [`docs/superpowers/specs/2026-10-12-agent-contract-review-design.md`](../docs/superpowers/specs/2026-10-12-agent-contract-review-design.md), decisions 7, 12, 13, 15 — decisions 12 and 15 amended by the revision above (Task 6 records the amendment in the spec).

## Status

- Status: DONE (gates pending)
- Priority: P2
- Effort: L
- Risk: MEDIUM (removes tools and commands from published contracts; concurrency change)
- Depends on: 055, 056, 059
- Planned at: `27d2a9a`, 2026-10-12; revised 2026-10-13 at `3059e06`

## Global Constraints

- MCP tools after this plan: `sqlharness_capabilities`, `sqlharness_query`, `sqlharness_measure`, `sqlharness_compare`, `sqlharness_watch`, `sqlharness_snapshot`, `sqlharness_plan`, `sqlharness_artifact` (8). `McpLimits.MaxTools = 8`.
- CLI commands after this plan: `query`, `measure`, `compare`, `watch`, `snapshot`, `plan`, `artifact`, `capabilities`, `doctor`, `mcp`, `dashboard` (plus any existing command not listed in the removal set). Removed: `ping`, `schema`, `counts`, `space`, `qstop`, `indexes`, `validate`.
- `query`, `measure`, `compare` accept a dry run on both surfaces: MCP `dryRun: true`, CLI `--dry-run`. A dry run classifies the exact arguments offline and never connects.
- No recipes; `query` sources stay exactly `sql` and `file`.
- No `busy` outcome exists. Concurrent calls run concurrently; each keeps its own deadline and cancellation.
- MCP capabilities defaults to runtime configuration only; diagnostics stay opt-in. No `includeDetails` argument (nothing to detail without recipes).
- Historical journal rows with removed operation names (`ping`, `schema`, `counts`, `space`, `qstop`, `indexes`) still load and display in the dashboard.

## Review Focus

- Two concurrent `query` calls to two different request scopes must both succeed and return their own targets (Task 4 test).
- Cancelling one of two concurrent calls must not cancel the other (Task 4 test).
- A client calling a removed MCP tool must get the SDK's unknown-tool error, not a crash (Task 1 test).
- CLI `--dry-run` must report the same classification as MCP `dryRun` for the same arguments, and never open a connection (Task 2 test).
- Removing Core code must not remove anything `query`, `measure`, `compare`, `watch`, `snapshot`, `plan`, `artifact`, journal or dashboard still use (Task 3).

## Why this matters

`schema`, `counts`, `space`, `qstop` and `indexes` repeat what an agent writes with `query`, or answer a different question than the statement under optimization (see Revision). `ping` adds nothing: every connection verifies `DB_NAME()` against the resolved target (exit 4) and every result names the database. `validate` cannot see setup SQL or a matrix; `dryRun` (plan 056) checks the real arguments. The busy gate rejected two independent reads to two databases in one session while never protecting a benchmark from another session, a colleague or a tester.

## Current state (verify each)

1. `McpToolCatalog.ToolNames` and both `CreateTools` overloads register 10 tools; `McpLimits.MaxTools = 10` and `CreateTools` throws unless exactly that many are built.
2. `McpExecutionGate` (`src/SqlHarness.Mcp/McpExecutionGate.cs`) holds one `SemaphoreSlim(1,1)` for `query`, `measure`, `compare`, `watch`, `snapshot`, `inspect`, plus the cancelled-result constants.
3. CLI commands live in `src/SqlHarness.Cli/Commands/` (`PingCommand`, `SchemaCommand`, `CountsCommand`, `SpaceCommand`, `QueryStoreTopCommand`, `IndexesCommand`, `ValidateCommand`), wired in `SqlHarnessCommands.cs`/`SqlHarnessCli.cs`. CLI has no dry-run switch (`Capabilities.cs`: "Core/MCP dryRun ... with no CLI switch"); Core operations carry `DryRun` (`Contracts.cs`).
4. Core code tied to the removed commands includes `QueryStoreTop.cs`, `QueryStoreArtifacts.cs`, `IndexAnalysisQuery.cs`, `IndexAnalysisArtifacts.cs`, `IndexOverlapClassifier.cs`, the `SqlHarness{Schema,Ping,Counts,Space,QueryStoreTop,...}Operation`/report records in `Contracts.cs`, their branches in `SqlHarnessModule.cs`, `OperationReportContract.cs`, `AgentOutputProjection.cs`, `OperationJournalDescriber.cs`, `SqlHarnessPaths.QueryStoreDir`, `Capabilities.cs`, `Renderer.cs`. `JournalReader.cs:455` lists operation names the dashboard treats as database operations.
5. Tests referencing the gate or the removed tools: `McpLifecycleTests.cs`, `McpToolSchemaTests.cs`, `McpCancellationTests.cs`, `McpStdioProcessTests.cs`, `McpTokenBudgetTests.cs`, `McpStderrLeakRegressionTests.cs`, `McpRevisionSmokeTests.cs`, `McpSecretRedactionTests.cs`, `McpProtocolTests.cs`, `McpCallRegistryTests.cs`, `McpRequestScopeIsolationTests.cs`, `McpOutputTests.cs`, `tests/SqlHarness.Tests/ContractsTests.cs`.

---

### Task 1: Remove `inspect` and `validate` from MCP

**Files:**
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (`ToolNames`, both `CreateTools`, `McpToolHandlers.InspectAsync`/`ValidateAsync`), `McpRequestToolHandlers.cs` (`InspectAsync`, `ValidateAsync`), `src/SqlHarness.Mcp/McpOperationMapper.cs` (inspect and validate mappers, `ValidateUsages`, inspect kinds), `src/SqlHarness.Mcp/McpLimits.cs` (`MaxTools = 8`)
- Test: `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`, `McpProtocolTests.cs` and the files in Current state 5

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Catalog_serves_the_eight_agent_tools()
{
    Assert.Equal(
        ["sqlharness_capabilities", "sqlharness_query", "sqlharness_measure", "sqlharness_compare",
         "sqlharness_watch", "sqlharness_snapshot", "sqlharness_plan", "sqlharness_artifact"],
        McpToolCatalog.ToolNames);
}

[Fact]
public async Task Removed_tool_returns_an_unknown_tool_error()
{
    await using var client = await ConnectInMemoryClientAsync();
    var error = await Assert.ThrowsAnyAsync<McpException>(() =>
        client.CallToolAsync("sqlharness_inspect", new Dictionary<string, object?> { ["kind"] = "ping" }).AsTask());
    Assert.Contains("sqlharness_inspect", error.Message);
}
```

`ConnectInMemoryClientAsync` is the in-memory client setup used in `McpProtocolTests.cs` (reuse it; extract a helper if it is inline). Use the exception type that file's other unknown-tool assertions use.

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~Catalog_serves|FullyQualifiedName~Removed_tool"` → FAIL.

- [ ] **Step 3: Implement**

Remove the two tools from `ToolNames` and both catalogs, delete the handler methods and the mapper methods they alone used (`MapInspect*`, `MapValidateAsync`, `ValidateUsages`, inspect-kind sets), set `MaxTools = 8`. Keep Core `SqlValidation` and the Core inspect operations for now (Task 3 removes what the CLI no longer needs).

- [ ] **Step 4: Update the affected tests**

For each file in Current state 5: delete tests whose only subject is a removed tool; rewrite tests that used `inspect ping` as a cheap database call to use `query` with `SELECT 1 AS A`; rewrite tests that used `validate` to use `query` with `dryRun: true`. Run `dotnet test tests/SqlHarness.Mcp.Tests` → PASS. List every deleted test name in the commit body.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests
git commit -m "feat(060)!: remove sqlharness_inspect and sqlharness_validate from MCP"
```

---

### Task 2: CLI `--dry-run` on `query`, `measure` and `compare`

**Files:**
- Modify: the CLI wiring of `query`, `measure`, `compare` (`src/SqlHarness.Cli/SqlHarnessCli.cs` and/or `Commands/SqlHarnessCommands.cs`), `Commands/Renderer.cs` if dry-run reports need text rendering
- Test: CLI tests under `tests/SqlHarness.Tests` that cover these commands' option parsing and output (find the existing ones; follow their style)

**Interfaces:**
- Consumes: Core `DryRun` on the query/measure/compare operations (`Contracts.cs`) and `SqlHarnessDryRunReport` (plan 056), the same path MCP `dryRun: true` uses.
- Produces: `--dry-run` (bool option) on the three commands. Every other option of the command (profile, `--var`, `--param`, `--setup`, matrix/param-set options, `--file`/inline SQL, output modes) is accepted unchanged and classified as given. Exit codes follow the existing contract: `0` when the dry run reports the arguments allowed, `2` on a validation/safety rejection.

- [ ] **Step 1: Write the failing tests**

- `query <profile> --file x.sql --param ... --dry-run --json` returns the dry-run report, exit 0, and the session factory is never asked for a session (use the CLI tests' existing fake/recording factory; if none exists, assert through the narrowest seam the CLI tests already use).
- `measure ... --setup <persistent mutation> --dry-run` returns exit 2 with the same rejection text the MCP `dryRun` path reports for the same arguments.
- `compare ... --dry-run` classifies both baseline and candidate (and matrix/param sets if given) without connecting.
- The dry-run report for one argument set is identical (same JSON) between CLI `--json` and the Core report MCP receives (build the Core operation through both mappers if a seam exists; otherwise compare against a Core-level expected report).

- [ ] **Step 2: Run to verify failure** — the option does not exist (parse error).

- [ ] **Step 3: Implement** — add the option, set `DryRun = true` on the operation the command already builds, render the existing dry-run report in each output mode the command supports. No connection, no journal semantics different from MCP's dry run.

- [ ] **Step 4: Run** the CLI test project filter for these commands → PASS. Update `Capabilities.cs` text that says "with no CLI switch".

- [ ] **Step 5: Commit** `feat(060): --dry-run on CLI query, measure and compare`

---

### Task 3: Remove `ping`, `schema`, `counts`, `space`, `qstop`, `indexes`, `validate` from the CLI and Core

**Files:**
- Delete: the seven command classes in `src/SqlHarness.Cli/Commands/`; Core files used only by them (expected: `QueryStoreTop.cs`, `QueryStoreArtifacts.cs`, `IndexAnalysisQuery.cs`, `IndexAnalysisArtifacts.cs`, `IndexOverlapClassifier.cs`; `SqlValidation` and schema/counts/space/ping operations only if nothing else uses them — verify each with a reference search before deleting)
- Modify: `SqlHarnessCli.cs`/`SqlHarnessCommands.cs` (registration), `Contracts.cs`, `SqlHarnessModule.cs`, `OperationReportContract.cs`, `AgentOutputProjection.cs`, `OperationJournalDescriber.cs`, `SqlHarnessPaths.cs` (`QueryStoreDir` if unused), `Capabilities.cs`, `Renderer.cs`
- Keep: `JournalReader.cs`/`LiveFeed.cs`/dashboard UI ability to read and show historical rows named `ping`, `schema`, `counts`, `space`, `qstop`, `indexes` (journal rows already exist on operators' machines)
- Test: delete tests whose only subject is a removed command/operation; keep or add one dashboard/journal test proving a historical `qstop` (or `ping`) row still loads.

- [ ] **Step 1: Write the failing tests** — the CLI root command lists exactly the remaining commands (Global Constraints); invoking `sqlharness qstop ...` is an unknown-command parse error (exit per the existing parse-error contract); a journal containing a historical `ping` row still loads in `JournalReader`.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** — delete the commands and the Core code only they use. Before deleting any Core type, search references; if `snapshot`, `watch`, `plan`, `artifact`, `doctor`, journal or dashboard still use it, keep it and say so in the report. Remove dead constants/paths left behind. Do not change journal schema.
- [ ] **Step 4: Run** `dotnet build SqlHarness.sln -warnaserror` and the affected test projects (`tests/SqlHarness.Tests`, `tests/SqlHarness.Mcp.Tests`) → PASS. List deleted tests in the commit body.
- [ ] **Step 5: Commit** `feat(060)!: remove ping, schema, counts, space, qstop, indexes and validate commands`

---

### Task 4: Remove the busy gate

**Files:**
- Modify: `src/SqlHarness.Mcp/McpExecutionGate.cs` (keep only cancellation constants; rename the class to `McpCallOutcome`), `src/SqlHarness.Mcp/McpProcessContext.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (`RunDbAsync`, `Wire`, `CreateTools` parameters), `McpRequestToolHandlers.cs`
- Test: `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs`, `McpCancellationTests.cs`, `McpRequestScopeIsolationTests.cs`

- [ ] **Step 1: Write the failing tests**

In `McpCancellationTests.cs`, reuse `CoreSessionFactory(blockFirstSession: true)`:

```csharp
[Fact]
public async Task A_second_query_runs_while_the_first_is_blocked()
{
    var factory = new CoreSessionFactory(blockFirstSession: true);
    await using var server = await StartServerAsync(factory);

    var first = server.Client.CallToolAsync("sqlharness_query", QueryArgs("SELECT 1 AS A")).AsTask();
    await factory.FirstSessionEntered;
    var second = await server.Client.CallToolAsync("sqlharness_query", QueryArgs("SELECT 2 AS A"));

    Assert.False(second.IsError);
    factory.ReleaseFirstSession();
    Assert.False((await first).IsError);
}

[Fact]
public async Task Cancelling_one_call_leaves_the_concurrent_call_running()
{
    // Start two blocked calls, cancel the first through its token, release the second; the second succeeds.
}
```

Build `StartServerAsync`, `QueryArgs`, `FirstSessionEntered`, `ReleaseFirstSession` from what the file already uses for its blocked-session tests (rename to match). Write the second test's body in the same style: two `CallToolAsync` with separate `CancellationTokenSource`s, cancel the first, assert its result is the cancelled outcome and the second's is success.

`McpRequestScopeIsolationTests.cs`: two concurrent queries on two different request scopes each return their own `database` (field name after plan 055).

- [ ] **Step 2: Run to verify failure** — the second call returns `busy`.

- [ ] **Step 3: Implement**

Delete `BusyCode`, `BusyMessage`, `BusyResult`, `BusyHint`, `DbTools`, `IsDbTool`, the semaphore, `TryEnterDb`, `ExitDb`, `RunningTool`. Rename the remaining class to `McpCallOutcome` holding `CancelledCode`/`CancelledMessage` and the cancelled-result builder. Remove the gate parameter from `CreateTools`/`Wire`/`McpProcessContext` and from `RunDbAsync`; keep its deadline linking and cancellation mapping. Delete tests whose only subject is the busy outcome; list them in the commit body. Remove plan 039's busy hint from docs.

- [ ] **Step 4: Run** `dotnet test tests/SqlHarness.Mcp.Tests` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests
git commit -m "feat(060)!: database tool calls run concurrently; remove the busy gate"
```

---

### Task 5: Capabilities is compact runtime configuration, not duplicated tool help

**Evidence.** The tool catalog already defines callable tools and argument syntax. A skill's blanket
capabilities requirement adds repeated discovery even when the connection's frozen configuration is known.
The current capabilities response repeats tool names, static input limits and protocol/analysis text.

**Current state.** McpCapabilitiesDocument and BuildCapabilities return the same broad shape for every
call. Input roots are correctly paged.

**Files:**
- Modify: src/SqlHarness.Mcp/McpOperationMapper.cs (document and both BuildCapabilities paths)
- Modify: src/SqlHarness.Mcp/McpProcessContext.cs, McpScope.cs (frozen configuration identity)
- Modify: src/SqlHarness.Mcp/Tools/McpToolCatalog.cs, McpRequestToolHandlers.cs,
  McpToolArguments.cs (both catalogs)
- Test: tests/SqlHarness.Mcp.Tests/McpMappingTests.cs, McpOutputTests.cs, McpTokenBudgetTests.cs,
  McpToolSchemaTests.cs, McpScopeTests.cs

**Interfaces:**
- Produces: runtime-only capability fields configurationRef, scopeMode, engines, allowedProfiles
  (request mode), inputRoots plus existing paging/count/completeness metadata, processLimits.
- configurationRef is an opaque random value generated once per server process/frozen context.
  Every page/read for that context returns the same value; a recreated process gets another.
  Do not derive it from paths, credentials or configuration content. It is not authorization.
- engines describes actual configured support; fixed-scope mode uses its engine, not a guessed SQL Server.
- processLimits exposes enforced maxResultBytes and maxOperationSeconds, not a second copy of all
  static argument ranges already described in tools/list.
- Consumes/extends: capabilities(includeDiagnostics: bool = false, inputRootCursor: int = 0).
  Diagnostics remains opt-in and never restores duplicate tools/schema/help. No includeDetails
  argument (revision 2026-10-13: no recipes).
- Keep input-root pagination exact and compatible with plan 055's text-byte accounting.
  CLI capabilities is outside this MCP output change.

**Change.** Remove the default tools list, static schema limits, duplicated protocol-version lists
and tutorial/static-analysis boilerplate. Protocol/server metadata belongs to MCP
initialization; operation-specific errors still describe safety denials. Keep the runtime facts callers
cannot derive from input schemas or user policy. No additional tool or per-operation discovery call.

A caller may reuse this configuration for the same known connection/process. Reconnect/restart or
uncertain identity invalidates reuse; a version string alone cannot validate the cache. Explicit task
scope is always freshly derived. Do not add configurationRef to every query result merely to poll it.

- [ ] **Step 1: Add failing behavior and schema tests**

Synthetic runtime fixtures assert:
- Default response contains enforced roots/profiles/engines/ceilings and configurationRef, but no tools,
  supportedProtocolVersions or copied static parameter constraints.
- includeDiagnostics=true adds only existing nonsecret diagnostic fields; it never silently converts
  output back into a full catalog. Invalid optional arguments keep normal errors; an `includeDetails`
  argument is rejected as unknown.
- The same context returns the same reference across input-root pages and calls; a new context differs.
  No path or secret is embedded in that reference.
- Fixed/request contexts retain correct mode and engine data; no input root or allowed profile is lost.
- At a small budget, every returned root page fits plan 055's final text-byte measurement; continuation
  advances and eventually covers all representable roots. Profile omissions must be explicit, not treated
  as an empty allowlist. Old paginated-root tests remain meaningful under the new document shape.
- Compact response bytes are smaller than a synthetic old broad response carrying the same runtime
  information. This is an output-size check, not an invented model-token measurement.

- [ ] **Step 2: Run to verify failure**

Run: dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpMapping|FullyQualifiedName~McpOutput|FullyQualifiedName~McpTokenBudget|FullyQualifiedName~McpToolSchema|FullyQualifiedName~InputRoot".
Expected: current broad output lacks configurationRef.

- [ ] **Step 3: Implement**

Replace the broad MCP document with the runtime shape. Generate immutable identity in context creation
and preserve it in fixed/request mapping and pagination. Update both catalogs and unknown-argument
allowlists. Capabilities remains offline and target-free.
Document which fields are omitted in fixed scope instead of fabricating an allowed-profile list.
Do not change connection authorization, scope validation, secrets or CLI capabilities.

- [ ] **Step 4: Verify and commit**

Run Step 2 filter; expected PASS. Commit feat(060): compact runtime capabilities.

**Acceptance.** An inline operation with known runtime configuration requires no capability call.
An unknown configuration is discoverable without repeating the tool catalog.

---
### Task 6: Documentation, skill, spec amendment, plan index

- [ ] **Step 1:** `docs/mcp.md`: Tools table (8 tools; `dryRun` in place of `validate`), remove `inspect`/`validate` sections and the `busy` status, rewrite "Concurrency" (calls run concurrently; benchmarks can be disturbed by any other load on the server — SQLHarness does not serialize them; run comparisons when the server is quiet and repeat them). `README.md`: remove the seven CLI commands, document `--dry-run`, MCP section, CLI/MCP parity. `skills/sqlharness/SKILL.md`: replace `validate`/`inspect ping` guidance with `--dry-run`/`dryRun` and "the first result's `database` is the verified target"; for missing indexes read the measured plan's `missingIndexes` and check existing indexes with a scoped `query`; optional capabilities only for unknown runtime configuration; runtime-only fields and configuration cache invalidation. `AGENTS.md`: step 1 uses `--dry-run` instead of `validate`, example block, reference map (drop `counts`, `space`, `schema`, `qstop`, `indexes`), safety list (drop `qstop`'s `queries.jsonl`), intro sentence ("schema and storage inspection"). `Capabilities.cs` in sync. `DocsSyncTests` pins.
- [ ] **Step 2:** Spec `docs/superpowers/specs/2026-10-12-agent-contract-review-design.md`: add an "Amendment 2026-10-13" to decisions 12 and 15 (parity; no recipes; `qstop`/`indexes` removed with the rest; capabilities without includeDetails) with the evidence from this plan's Revision.
- [ ] **Step 3:** `dotnet build SqlHarness.sln -warnaserror`; docs tests. Gates (`pwsh scripts/verify.ps1`, then `pwsh scripts/verify-linux.ps1`) run once after the final review.
- [ ] **Step 4:** Status DONE; `plans/README.md` row (effort L, revised scope); mark plan 039 (busy hint) as superseded by 060 in the index; add the follow-up candidate "missing-index hint overlap in the `plan` artifact" as an unaccepted idea (not a plan).

```bash
git add docs README.md AGENTS.md skills src tests plans
git commit -m "docs(060): eight MCP tools, CLI parity, dry run, concurrent calls"
```

## Done criteria

- [ ] `tools/list` returns exactly the eight tools in Global Constraints.
- [ ] The CLI has no `ping`, `schema`, `counts`, `space`, `qstop`, `indexes`, `validate`; `query`/`measure`/`compare` accept `--dry-run`.
- [ ] `rg -n "busy|McpExecutionGate|sqlharness_inspect|sqlharness_validate|recipe" src docs/mcp.md skills AGENTS.md` → no matches tied to the removed surface (unrelated dashboard wording excepted).
- [ ] Historical journal rows with removed operation names still load in the dashboard.
- [ ] Default capabilities omits duplicated tool help; diagnostics are opt-in and configuration identity is stable.
- [ ] Concurrency tests pass on both gates.

## STOP conditions

- Any Core component used by MCP calls is not safe for concurrent use (for example a shared `SqlHarnessModule` field mutated per call, the artifact writer, or the journal connection). Stop, list the component, and fix it in a separate task before removing the gate.
- A Core type slated for deletion in Task 3 is still used by a kept command, the journal or the dashboard. Keep it and report; do not rewrite the kept consumer to make the deletion possible.
