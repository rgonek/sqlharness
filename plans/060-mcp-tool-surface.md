# Plan 060: A smaller MCP surface — no inspect, no validate, built-in recipes, no busy gate

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 27d2a9a..HEAD -- src/SqlHarness.Mcp src/SqlHarness.Core/QueryStoreTop.cs src/SqlHarness.Core/IndexAnalysisQuery.cs src/SqlHarness.Core/Capabilities.cs tests/SqlHarness.Mcp.Tests docs/mcp.md README.md skills/sqlharness/SKILL.md`. Plan 056 must be DONE (`dryRun` replaces `validate`). Plans 055 and 059 change the same handlers; rebase on them.

**Goal:** The MCP server exposes only tools an agent cannot replace with `query`, runs database calls concurrently, and ships the catalog SQL it used to hide behind probes as recipes `query` can run.

**Architecture:** `sqlharness_inspect` (all kinds, `ping` included) and `sqlharness_validate` leave both MCP catalogs. The Query Store and index-analysis SQL become embedded recipes: `query` accepts `recipe` as a third source next to `sql` and `file`, and capabilities lists recipes with their parameters only on includeDetails=true. A test pins each recipe to the Core constant the CLI still uses. The process-wide execution gate is removed; cancellation and deadlines stay per call. The CLI keeps its commands.

**Tech Stack:** .NET 8/10, C#, xUnit, ModelContextProtocol C# SDK.

**Spec:** [`docs/superpowers/specs/2026-10-12-agent-contract-review-design.md`](../docs/superpowers/specs/2026-10-12-agent-contract-review-design.md), decisions 7, 12, 13, 15.

## Status

- Status: TODO
- Priority: P2
- Effort: M
- Risk: MEDIUM (removes tools from a published MCP contract; concurrency change)
- Depends on: 055, 056, 059
- Planned at: `27d2a9a`, 2026-10-12

## Global Constraints

- MCP tools after this plan: `sqlharness_capabilities`, `sqlharness_query`, `sqlharness_measure`, `sqlharness_compare`, `sqlharness_watch`, `sqlharness_snapshot`, `sqlharness_plan`, `sqlharness_artifact` (8). `McpLimits.MaxTools = 8`.
- Recipes: `query-store-top` and `index-analysis`, SQL Server only, embedded resources, text identical to `QueryStoreTop.Sql` and `IndexAnalysisQuery.Sql`.
- `query` sources: exactly one of `sql`, `file`, `recipe`.
- No `busy` outcome exists. Concurrent calls run concurrently; each keeps its own deadline and cancellation.
- MCP capabilities defaults to runtime configuration only; recipe details and diagnostics are opt-in.
- CLI commands (`ping`, `schema`, `counts`, `space`, `qstop`, `indexes`, `validate`) are unchanged.

## Review Focus

- Two concurrent `query` calls to two different request scopes must both succeed and return their own targets (Task 3 test).
- Cancelling one of two concurrent calls must not cancel the other (Task 3 test).
- A recipe on a PostgreSQL scope must fail before connecting with a message naming the engine (Task 2 test).
- A client calling a removed tool must get the SDK's unknown-tool error, not a crash (Task 1 test).
- A recipe must pass the same safety classification as inline SQL; a recipe never bypasses it (Task 2 test).

## Why this matters

`schema`, `counts`, `space`, `qstop` and `indexes` repeat what an agent writes with `query`; `qstop` is the SQL Server twin of `pgstop`, which was rejected as a duplicate of `query`. `ping` adds nothing: every connection verifies `DB_NAME()` against the resolved target (exit 4) and every result names the database. `validate` cannot see setup SQL or a matrix; `dryRun` (plan 056) checks the real arguments. The busy gate rejected two independent reads to two databases in one session while never protecting a benchmark from another session, a colleague or a tester.

## Current state (verify each)

1. `McpToolCatalog.ToolNames` and both `CreateTools` overloads register 10 tools; `McpLimits.MaxTools = 10` and `CreateTools` throws unless exactly that many are built.
2. `McpExecutionGate` (`src/SqlHarness.Mcp/McpExecutionGate.cs`) holds one `SemaphoreSlim(1,1)` for `query`, `measure`, `compare`, `watch`, `snapshot`, `inspect`, plus the cancelled-result constants.
3. `QueryStoreTop.Sql` (`:13-137`) uses `@topQueries`, `@top`, `@windowMinutes`; `IndexAnalysisQuery.Sql` (`:16-222`) uses `@top`, `@objectSchema`, `@objectName`, `@observedAt` and declares `@candidates` itself.
4. Tests referencing the gate or the removed tools: `McpLifecycleTests.cs` (43 hits), `McpToolSchemaTests.cs` (36), `McpCancellationTests.cs` (13), `McpStdioProcessTests.cs` (15), `McpTokenBudgetTests.cs`, `McpStderrLeakRegressionTests.cs`, `McpRevisionSmokeTests.cs`, `McpSecretRedactionTests.cs`, `McpProtocolTests.cs`, `McpCallRegistryTests.cs`, `McpRequestScopeIsolationTests.cs`, `McpOutputTests.cs`, `tests/SqlHarness.Tests/ContractsTests.cs`.

---

### Task 1: Remove `inspect` and `validate` from MCP

**Files:**
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (`ToolNames`, both `CreateTools`, `McpToolHandlers.InspectAsync`/`ValidateAsync`), `McpRequestToolHandlers.cs` (`InspectAsync`, `ValidateAsync`), `src/SqlHarness.Mcp/McpOperationMapper.cs` (inspect and validate mappers, `ValidateUsages`, inspect kinds), `src/SqlHarness.Mcp/McpLimits.cs` (`MaxTools = 8`)
- Test: `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`, `McpProtocolTests.cs` and the files in Current state 4

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

Remove the two tools from `ToolNames` and both catalogs, delete the handler methods and the mapper methods they alone used (`MapInspect*`, `MapValidateAsync`, `ValidateUsages`, inspect-kind sets), set `MaxTools = 8`. Keep Core `SqlValidation` (the CLI `validate` command uses it) and the Core inspect operations (CLI).

- [ ] **Step 4: Update the affected tests**

For each file in Current state 4: delete tests whose only subject is a removed tool; rewrite tests that used `inspect ping` as a cheap database call to use `query` with `SELECT 1 AS A`; rewrite tests that used `validate` to use `query` with `dryRun: true`. Run `dotnet test tests/SqlHarness.Mcp.Tests` → PASS. List every deleted test name in the commit body.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests
git commit -m "feat(060)!: remove sqlharness_inspect and sqlharness_validate from MCP"
```

---

### Task 2: Built-in recipes for `query`

**Files:**
- Create: `src/SqlHarness.Core/Recipes/query-store-top.sql`, `src/SqlHarness.Core/Recipes/index-analysis.sql` (embedded resources), `src/SqlHarness.Core/Recipes/SqlRecipes.cs`
- Modify: `src/SqlHarness.Core/SqlHarness.Core.csproj` (`<EmbeddedResource Include="Recipes\*.sql" />`)
- Modify: `src/SqlHarness.Mcp/McpInputReader.cs` or `McpOperationMapper.MapQueryAsync` (third source), both `QueryAsync` handlers (`recipe` argument), `McpOperationMapper.BuildCapabilities` (`recipes` list), `McpCapabilitiesDocument`
- Test: `tests/SqlHarness.Tests/SqlRecipesTests.cs`, `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`

**Interfaces:**
- Produces: `public sealed record SqlRecipe(string Name, string Engine, string Description, IReadOnlyList<SqlRecipeParameter> Parameters, string Sql);`, `public sealed record SqlRecipeParameter(string Name, string Type, string Description);`, `public static class SqlRecipes { public static IReadOnlyList<SqlRecipe> All { get; } public static SqlRecipe Get(string name); }`

- [ ] **Step 1: Write the failing tests**

```csharp
public class SqlRecipesTests
{
    [Fact]
    public void Recipes_match_the_cli_probe_sql()
    {
        Assert.Equal(Normalize(QueryStoreTop.Sql), Normalize(SqlRecipes.Get("query-store-top").Sql));
        Assert.Equal(Normalize(IndexAnalysisQuery.Sql), Normalize(SqlRecipes.Get("index-analysis").Sql));
    }

    [Theory]
    [InlineData("query-store-top")]
    [InlineData("index-analysis")]
    public void Recipes_pass_the_query_classifier(string name)
    {
        var decision = new SqlSafetyClassifier().Classify(SqlRecipes.Get(name).Sql, SqlUsage.Query, "db", false, null);
        Assert.True(decision.Allowed, decision.RejectionDescription);
    }

    [Fact]
    public void Unknown_recipe_lists_the_known_names()
    {
        var error = Assert.Throws<ArgumentException>(() => SqlRecipes.Get("nope"));
        Assert.Contains("query-store-top", error.Message);
        Assert.Contains("index-analysis", error.Message);
    }

    private static string Normalize(string sql) => sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
```

`McpMappingTests.cs`: `query` with `recipe: "query-store-top"` and parameters `topQueries`, `top`, `windowMinutes` maps to a `SqlHarnessQueryOperation` whose `Sql` equals the recipe; `recipe` together with `sql` is rejected (`"Exactly one of sql, file or recipe."`); a recipe on a scope whose engine is `postgres` is rejected before connecting with a message naming the engine.

- [ ] **Step 2: Run to verify failure** — build error.

- [ ] **Step 3: Implement**

Copy each constant's SQL verbatim into its `.sql` file. `SqlRecipes` loads them from the assembly manifest (`typeof(SqlRecipes).Assembly.GetManifestResourceStream(...)`) once and declares metadata:

```csharp
new SqlRecipe("query-store-top", "sqlserver",
    "Top Query Store queries by total duration over a recent window.",
    [new("topQueries", "int", "Queries ranked before trimming."),
     new("top", "int", "Rows returned."),
     new("windowMinutes", "int", "Lookback window in minutes.")],
    Load("query-store-top.sql")),
new SqlRecipe("index-analysis", "sqlserver",
    "Missing-index candidates with the existing indexes of their tables (catalog and DMV evidence; no overlap verdict).",
    [new("top", "int", "Candidates returned."),
     new("objectSchema", "nvarchar(128)", "Optional schema filter; NULL for all."),
     new("objectName", "nvarchar(128)", "Optional table filter; NULL for all."),
     new("observedAt", "datetimeoffset", "Timestamp recorded with the evidence.")],
    Load("index-analysis.sql")),
```

Read each recipe's parameters from its SQL on HEAD and correct the lists above if they differ. `Get` throws `ArgumentException($"Unknown recipe '{name}'. Known recipes: {string.Join(", ", All.Select(r => r.Name))}.")`.

MCP: `query` gains `[Description("Built-in recipe name from capabilities(includeDetails: true); exclusive with sql and file.")] string? recipe = null`. The mapper resolves the source (exactly one of three), checks the recipe engine against the scope's engine, and uses the recipe SQL; everything after that is the normal query path (classification, binding, execution). `capabilities` gains optional recipe details (name, engine, description, parameters; no SQL text), exposed only when includeDetails=true in Task 4.

- [ ] **Step 4: Run** `dotnet test SqlHarness.sln --filter "FullyQualifiedName~Recipe|FullyQualifiedName~McpMapping|FullyQualifiedName~Capabilities"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(060): built-in SQL recipes for query; capabilities details lists them"
```

---

### Task 3: Remove the busy gate

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

### Task 4: Capabilities is compact runtime configuration, not duplicated tool help

**Evidence.** The tool catalog already defines callable tools and argument syntax. A skill's blanket
capabilities requirement adds repeated discovery even when the connection's frozen configuration is known.
The current capabilities response repeats tool names, static input limits and protocol/analysis text.

**Current state.** McpCapabilitiesDocument and BuildCapabilities return the same broad shape for every
call. Input roots are correctly paged. Task 2 adds recipe metadata; do not make every inline-query task
pay for its names, parameter lists and descriptions.

**Files:**
- Modify: src/SqlHarness.Mcp/McpOperationMapper.cs (document and both BuildCapabilities paths)
- Modify: src/SqlHarness.Mcp/McpProcessContext.cs, McpScope.cs (frozen configuration identity)
- Modify: src/SqlHarness.Mcp/Tools/McpToolCatalog.cs, McpRequestToolHandlers.cs,
  McpToolArguments.cs (includeDetails; both catalogs)
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
- Consumes/extends: capabilities(includeDetails: bool = false, includeDiagnostics: bool = false,
  inputRootCursor: int = 0). Details contains recipe metadata from Task 2 only when requested.
  Diagnostics remains opt-in. Neither default nor detail mode restores duplicate tools/schema/help.
- Keep input-root pagination exact and compatible with plan 055's text-byte accounting.
  CLI capabilities is outside this MCP output change.

**Change.** Remove the default tools list, static schema limits, duplicated protocol-version lists,
recipe descriptions and tutorial/static-analysis boilerplate. Protocol/server metadata belongs to MCP
initialization; operation-specific errors still describe safety denials. Keep the runtime facts callers
cannot derive from input schemas or user policy. No additional tool or per-operation discovery call.

A caller may reuse this configuration for the same known connection/process. Reconnect/restart or
uncertain identity invalidates reuse; a version string alone cannot validate the cache. Explicit task
scope is always freshly derived. Do not add configurationRef to every query result merely to poll it.

- [ ] **Step 1: Add failing behavior and schema tests**

Synthetic runtime fixtures assert:
- Default response contains enforced roots/profiles/engines/ceilings and configurationRef, but no tools,
  supportedProtocolVersions, recipe metadata or copied static parameter constraints.
- includeDetails=true contains both built-in recipe definitions with their exact required parameter
  contracts; includeDetails=false never includes them. Invalid optional arguments keep normal errors.
- includeDiagnostics=true adds only existing nonsecret diagnostic fields; neither diagnostics nor
  details silently converts default output back into a full catalog.
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
Expected: current broad output lacks configurationRef and ignores includeDetails.

- [ ] **Step 3: Implement**

Replace the broad MCP document with the runtime shape. Generate immutable identity in context creation
and preserve it in fixed/request mapping and pagination. Make Task 2 recipe metadata opt-in; update
both catalogs and unknown-argument allowlists. Capabilities remains offline and target-free.
Document which fields are omitted in fixed scope instead of fabricating an allowed-profile list.
Do not change connection authorization, scope validation, secrets or CLI capabilities.

- [ ] **Step 4: Verify and commit**

Run Step 2 filter; expected PASS. Commit feat(060): compact runtime capabilities with optional details.

**Acceptance.** An inline operation with known runtime configuration requires no capability call.
An unknown configuration or recipe contract is discoverable without repeating the tool catalog.

---
### Task 5: Documentation, skill, gates

- [ ] **Step 1:** `docs/mcp.md`: Tools table (8 tools; `query` recipes; `dryRun` in place of `validate`), remove `inspect`/`validate` sections and the `busy` status, rewrite "Concurrency" (calls run concurrently; benchmarks can be disturbed by any other load on the server — SQLHarness does not serialize them; run comparisons when the server is quiet and repeat them). `README.md` MCP section and CLI/MCP parity notes (CLI keeps its commands). `skills/sqlharness/SKILL.md`: replace `inspect ping` guidance with "the first result's `database` is the verified target"; recipes; dry run; optional capabilities only for unknown runtime configuration; runtime-only fields, includeDetails and configuration cache invalidation. `DocsSyncTests` pins.
- [ ] **Step 2:** `dotnet build SqlHarness.sln -warnaserror`; `pwsh scripts/verify.ps1`; `pwsh scripts/verify-linux.ps1`.
- [ ] **Step 3:** Status DONE; `plans/README.md` row; mark plan 039 (busy hint) as superseded by 060 in the index.

```bash
git add docs README.md skills tests plans
git commit -m "docs(060): eight MCP tools, recipes, concurrent calls"
```

## Done criteria

- [ ] `tools/list` returns exactly the eight tools in Global Constraints.
- [ ] `query` runs both recipes on a SQL Server scope and rejects them on PostgreSQL before connecting.
- [ ] `rg -n "busy|McpExecutionGate|sqlharness_inspect|sqlharness_validate" src docs/mcp.md skills` → no matches (CLI help for its own `validate`/`ping` commands excepted).
- [ ] Default capabilities omits duplicated tool help; details/diagnostics are opt-in and configuration identity is stable.
- [ ] Concurrency tests pass on both gates.

## STOP conditions

- Any Core component used by MCP calls is not safe for concurrent use (for example a shared `SqlHarnessModule` field mutated per call, the artifact writer, or the journal connection). Stop, list the component, and fix it in a separate task before removing the gate.
- A recipe fails the classifier on HEAD. Do not weaken the classifier; report the statement.
