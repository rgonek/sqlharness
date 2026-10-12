# Plan 058: Benchmark evidence an agent can cite — plan section, stable temp-table keys, matrix values

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 27d2a9a..HEAD -- src/SqlHarness.Core/ArtifactReader.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/Diagnostics.cs src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Mcp/McpOperationMapper.cs src/SqlHarness.Mcp/Tools docs/mcp.md`. Plan 055 adds statement paging to `ArtifactReader`; reuse its cursor handling.

**Goal:** From a saved benchmark an agent can read the actual plan operators of one statement, per-table reads that survive repetitions, and the parameter value of every matrix cell.

**Architecture:** Artifacts already store each run's distilled plan as `plans/<variant>-<rep>-<run>-<plan>.plan.json`. A new `plan` section returns one statement's distilled plan from the last measured repetition of a variant, through a bounded stored-plan reader and paged operator projection; another saved measured repetition can be selected explicitly. The two STATISTICS IO parsers normalize session-suffixed temp-table names. The matrix projection passes the cell value it already holds.

**Tech Stack:** .NET 8/10, C#, xUnit.

**Spec:** [`docs/superpowers/specs/2026-10-12-agent-contract-review-design.md`](../docs/superpowers/specs/2026-10-12-agent-contract-review-design.md), decisions 9–10.

## Status

- Status: TODO
- Priority: P1
- Effort: M
- Risk: LOW (read-only additions; one documented value disclosure the caller already supplied)
- Depends on: 055
- Planned at: `27d2a9a`, 2026-10-12

## Global Constraints

- `plan` section arguments: `variant` (`baseline`, `candidate`, or `query` for measure), optional `statement` (zero-based, default 0), optional `parameterSet` (measure sets), optional `repetition` (measured only), optional `includeText` (default `false`).
- Default selection is the last measured repetition and is explicit; an available repetition can be selected. Plan nodes and operator lists have stable continuation; raw XML is never read.
- Temp-table key normalization: `[schema].[#name<underscores><12 hex>]` → `[schema].[#name]`; `#name<underscores><12 hex>` → `#name`. Other names unchanged.
- Matrix `parameterValue`: the cell's display value; JSON `null` only for a typed NULL value (`docs/mcp.md:100`).

## Review Focus

- A temp-table name that itself contains `__` must normalize to its full name (Task 2 test).
- An artifact written before this plan (manifest without `plan`) that has `.plan.json` files must still serve the section (Task 1 test).
- A `statement` index past the last statement must fail with a message naming the statement count (Task 1 test).
- A typed NULL matrix value must stay JSON `null` while other cells show strings (Task 3 test).
- A table variable or a permanent table whose name ends in 12 hex characters without a leading `#` must not be renamed (Task 2 test).

## Why this matters

An agent concluded "key lookup on every row" and "full scan" from read counts because no section exposed plan operators, while common SQL review standards ask for actual-plan evidence. Session-suffixed temp names fragmented per-table aggregation. Matrix cells hid the value they were measured with, contradicting the documentation.

## Current state (verify each)

1. `ArtifactReader.SupportedSections` (`:131-132`) has no plan section; `ArtifactManifest.ForReport` (`:22-36`) lists exactly the supported sections.
2. `Artifacts.cs:533-545` writes `DistillForArtifact` output next to each `.sqlplan` as `.plan.json`.
3. `StatisticsIoParser.Parse` (`Diagnostics.cs:20-40`) and `StatisticsIoDetailParser.Parse` (`:76-100`) key by the raw table name.
4. `AgentOutputProjection.cs:170` and `:687` pass `null` as the cell value; `docs/mcp.md:100` says the value is a string except for typed NULL, `:166` says values stay out.

---

### Task 1: Read stored plan evidence with stable selectors and full node pagination

**Evidence.** A noteworthy-operator summary cannot replace a review of actual/estimated rows, predicates
and execution counts. Selecting the last repetition alone says nothing about other measured repetitions.

**Current state.** Stored plans exist as JSON. PlanDistiller.cs explicitly states that its custom JSON
converters support serialization only. JsonSerializer.Deserialize<DistilledPlan> is therefore not an
implementation path. The existing ProjectPlan can drop child operators and cannot recover them by cursor.

**Files:**
- Modify: src/SqlHarness.Core/ArtifactReader.cs (plan section, selectors, bounded reader)
- Create: src/SqlHarness.Core/StoredPlanReader.cs
- Modify: src/SqlHarness.Core/AgentOutputProjection.cs (paged node projection)
- Modify: src/SqlHarness.Mcp/McpOperationMapper.cs, Tools/McpToolCatalog.cs,
  Tools/McpRequestToolHandlers.cs (artifact plan arguments and text policy)
- Modify: src/SqlHarness.Cli/Commands/ArtifactCommand.cs
- Test: tests/SqlHarness.Tests/ArtifactReaderTests.cs, tests/SqlHarness.Tests/PlanDistillerTests.cs,
  tests/SqlHarness.Mcp.Tests/McpMappingTests.cs, tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs

**Interfaces:**
- Produces: ArtifactPlanSelector(string Variant, int Statement = 0, string? ParameterSet = null,
  int? Repetition = null).
- Produces: StoredPlanReader.Read(string json) -> DistilledPlan, a bounded reader for the existing stored
  JSON shape, without changing the serialization-only converters or re-reading raw plan XML.
- Produces: ArtifactPlanNode(string NodePath, string? ParentPath, PlanNode Operator).
  Operator.Children is empty in this paged representation; parent/path reconstruct the hierarchy.
- Produces: ArtifactPlanSection(string ArtifactId, string Variant, string? ParameterSet, int Statement,
  int StatementCount, int Repetition, string Selection, IReadOnlyList<int> AvailableRepetitions,
  bool? PlanShapeStable, int NodeCount, IReadOnlyList<ArtifactPlanNode> Nodes, int? Continuation).
  Selection is last-measured or explicit. PlanShapeStable is null when fewer than two comparable
  measured plans are available; it never claims stable timing or row counts.
- Consumes: existing stored file identity from Artifacts.PlanStem, a selected statement and a zero-based
  node cursor. Missing indexes and statement-level metadata accompany the first page, with explicit
  omissions if needed; node pagination does not silently discard child operators.

**Change.** Use a bounded DTO/JsonDocument reader for the persisted shape. Validate variant/set selectors
before selecting a file, preserve owner checks and existing old-manifest support. No raw XML read.
Default to the last measured repetition and label it explicitly. Allow selecting another available
measured repetition. Compare physical/logical operator tree, object/index identity and predicate for
PlanShapeStable for the saved captured shape only; exclude costs, timing and actual/estimated row counts from that shape fingerprint.
Do not treat warm-up or absent plans as measured evidence.

Flatten the selected statement in deterministic preorder, with positional paths such as 0, 0/0, 0/1;
paginate the flattened nodes. Continuation must advance by the number of nodes actually returned after
byte-budget fitting, not by a preselected page size. Keep includeText=false as the existing default;
when true, predicates follow the current text policy and clipping is explicit. A page with no node
space returns a controlled budget error, never a non-advancing cursor.

- [ ] **Step 1: Write failing tests using synthetic stored-plan fixtures**

Assertions:
- The reader accepts existing serializer output including unknown additive properties, rejects malformed
  required fields and enforces the same file-size/depth bounds as the stored artifact contract.
- Default selection identifies the last measured repetition; explicit selection reads another one;
  missing statement/repetition names the available range. Include a fixture with several statements in
  one stored plan file; global statement ordinal indexes statements, not files.
- Identical operator shapes with different timing/row counts give PlanShapeStable=true; changed operator,
  index or predicate gives false; one available repetition gives null.
- With a deep/wide plan and a small budget, following continuation reconstructs every node exactly once,
  with correct parent paths. No child disappears because of the old ProjectPlan recursion limit.
- includeText=false omits predicates; true returns them under current bounds. Neither exposes raw XML.
- Old manifests with stored plans remain readable; wrong owner/scope and invalid selectors stay rejected.
- Corrupt/incomplete saved evidence reports the gap; it does not become a stable or empty plan.

- [ ] **Step 2: Run to verify failure**

Run: dotnet test SqlHarness.sln --filter "FullyQualifiedName~ArtifactReader|FullyQualifiedName~PlanDistiller|FullyQualifiedName~McpMapping|FullyQualifiedName~McpTokenBudget".
Expected: missing plan-reader/pagination contract or the old unsupported deserialization path.

- [ ] **Step 3: Implement**

Read each stored document's Statements collection through StoredPlanReader, build the selected variant/set's
measured statement inventory, then select the requested statement/repetition. Never equate file count with
statement count. Return flattened nodes through the budget-aware page projection.
ArtifactAsync accepts variant, statement, parameterSet, repetition, includeText for section=plan;
reject selector arguments for other sections. cursor is also accepted for plan nodes and operators.
Reuse the stored JSON version rather than requiring a new artifact format just to read existing plans.

Add cursor pagination to the existing operators section as well. This makes the design document's
statements/operators/plan continuation promise concrete. If the persisted operators summary already contains
only a capped subset, expose that provenance; only the plan-node section promises full saved-node coverage.

- [ ] **Step 4: Verify and commit**

Run the Step 2 filter; expected PASS. Commit scoped source/tests with
feat(058): stored plan reader with explicit repetition evidence and node pagination.

**Acceptance.** An agent can retrieve the whole saved operator tree under a bounded response budget,
identify the selected repetition and distinguish a stable shape from unavailable comparison evidence.
Reading a plan never asserts that a human/agent reviewed it.

---
### Task 2: Stable temp-table keys

**Files:**
- Modify: `src/SqlHarness.Core/Diagnostics.cs` (both parsers)
- Test: the STATISTICS IO parser tests (search `StatisticsIoParser.Parse` in `tests/`)

**Interfaces:**
- Produces: `internal static string TempTableKey.Normalize(string table)` in `Diagnostics.cs`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("[dbo].[#ClientPrices_____________________________________________________________________________________________________00000001C686]", "[dbo].[#ClientPrices]")]
[InlineData("#Work__Items_______________________________________________________________________________________________________000000000A1B", "#Work__Items")]
[InlineData("[dbo].[Orders]", "[dbo].[Orders]")]
[InlineData("[dbo].[Archive_000000000ABC]", "[dbo].[Archive_000000000ABC]")]
[InlineData("Worktable", "Worktable")]
public void Temp_table_keys_drop_the_session_suffix(string table, string expected) =>
    Assert.Equal(expected, TempTableKey.Normalize(table));

[Fact]
public void Repeated_runs_share_one_temp_table_key()
{
    var first = StatisticsIoParser.Parse("Table '#t__________________________________________________________________________________________________________________00000001C686'. Scan count 1, logical reads 8.");
    var second = StatisticsIoParser.Parse("Table '#t__________________________________________________________________________________________________________________00000001C687'. Scan count 1, logical reads 8.");
    Assert.Equal(first.LogicalReadsByTable.Keys, second.LogicalReadsByTable.Keys);
}
```

Use the property name `StatisticsIo` exposes on HEAD for per-table reads.

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Temp_table|FullyQualifiedName~Repeated_runs"` → FAIL.

- [ ] **Step 3: Implement**

```csharp
internal static partial class TempTableKey
{
    internal static string Normalize(string table)
    {
        var bracketed = Bracketed().Match(table);
        if (bracketed.Success)
            return bracketed.Groups["head"].Value + "]";
        var bare = Bare().Match(table);
        return bare.Success ? bare.Groups["head"].Value : table;
    }

    [GeneratedRegex(@"^(?<head>(?:\[[^\]]+\]\.)?\[#.+?)_{2,}[0-9A-Fa-f]{12}\]$", RegexOptions.CultureInvariant)]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"^(?<head>#.+?)_{2,}[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex Bare();
}
```

In both parsers wrap the extracted name: `var table = TempTableKey.Normalize(match.Groups["table"].Value.Replace("''", "'", StringComparison.Ordinal));`. When two lines of one run normalize to the same key (two temp tables of the same name cannot coexist in one session, but a dropped and recreated one can), sum their reads, as the parsers already do for repeated table lines.

- [ ] **Step 4: Run and commit** — `dotnet test tests/SqlHarness.Tests` → PASS.

```bash
git add src/SqlHarness.Core/Diagnostics.cs tests
git commit -m "fix(058): per-table reads key temp tables without the session suffix"
```

---

### Task 3: Matrix case inventories and references carry parameter values

**Evidence.** Existing constructors hardcode null for every displayed matrix value.
**Current state.** Plan 055 changes the matrix to a case inventory before optional detail.
Do not restore the old nested cells shape or apply detailLimit to case coverage.

**Files:** Modify src/SqlHarness.Core/AgentOutputProjection.cs and ArtifactReader.cs;
docs/mcp.md; test tests/SqlHarness.Tests/CompareMatrixTests.cs and
tests/SqlHarness.Mcp.Tests/McpOutputTests.cs.

**Interfaces:** Add ParameterValue to AgentMatrixCase and matrix-case continuation references.
Source the value from the bound source cell; a typed NULL remains null.

- [ ] Step 1: Add a synthetic matrix with int values "1", null, "20".
  Assert that cases and all continued references retain those exact values. Under a detail-limited
  projection the complete inventory remains intact if it fits; when the inventory itself cannot fit,
  continuation recovers all values once in index order. No data value is redacted merely for being data.
- [ ] Step 2: Run dotnet test SqlHarness.sln --filter "FullyQualifiedName~CompareMatrix|FullyQualifiedName~McpOutput".
  Expected: missing case-value property or all-null projection.
- [ ] Step 3: Populate values in inventory and continuation, sharing plan 055's schema.
  Document case/detail omission separately. Plan 061 later adds set labels to the same inventory.
- [ ] Step 4: Run the Step 2 filter; expected PASS.
- [ ] Step 5: Commit fix(058): matrix case inventory retains measured parameter values.

**Acceptance:** Visible case identity includes the supplied matrix value, and optional-detail limits
never erase another case's identity.

---
### Task 4: Documentation and gates

- [ ] **Step 1:** `docs/mcp.md` Artifacts section and Tools table: the `plan` section, its arguments and explicit repetition selection, shape-availability diagnostics and node/operator pagination; temp-table key normalization in the per-table reads description. `README.md` artifact section; `skills/sqlharness/SKILL.md` artifact sections list; `DocsSyncTests` pins.
- [ ] **Step 2:** `dotnet build SqlHarness.sln -warnaserror`; `pwsh scripts/verify.ps1`; `pwsh scripts/verify-linux.ps1`.
- [ ] **Step 3:** Status DONE; `plans/README.md` row.

```bash
git add docs README.md skills tests plans
git commit -m "docs(058): plan section, temp-table keys, matrix values"
```

## Done criteria

- [ ] `artifact(section: plan, variant: baseline, statement: n)` returns operators with object and index names for a real compare artifact.
- [ ] A temp table measured over 20 repetitions has one key whose median equals its per-run reads.
- [ ] Matrix cases/overview and continuation references show their values (adapt to plan 055 inventory contract).
- [ ] Paged plan nodes reconstruct the whole saved hierarchy; stored JSON is read without unsupported model deserialization.
- [ ] Both gates pass; README row updated.

## STOP conditions

- `.plan.json` files are not written for some benchmark path (for example PostgreSQL or parameter sets). Record which and serve the section only where they exist; do not distill raw plans at read time without a decision.
- Plan file names on HEAD no longer follow `<variant>-<rep>-<run>-<plan>`. Update the pattern from `Artifacts.PlanStem` and the tests together.
