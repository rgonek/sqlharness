# Plan 063: Missing-index hints carry an overlap verdict against the table's existing indexes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 472d5b7..HEAD -- src/SqlHarness.Core/PlanDistiller.cs src/SqlHarness.Core/ArtifactReader.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Cli/Commands/Renderer.cs README.md skills/sqlharness/SKILL.md docs/mcp.md`. Plan 061 (parameter sets) touches `SqlHarnessModule` measure/compare paths; rebase on it if it lands first.

**Goal:** When a measured SQL Server plan contains a missing-index hint, the saved artifact's `plan` section reports, per hint, whether an existing index of that table already covers it, nearly covers it, or not.

**Architecture:** `measure` and `compare` already save actual plans; the `artifact` reader works offline on those files. So the index inventory is captured while the measured session is still open: after the last measured run, only on SQL Server, only when some saved plan has hints, with one fixed read-only catalog batch bounded to the hinted tables. The inventory is stored in the artifact as `index-inventory.json`. At read time `ArtifactReader` classifies each hint of the selected statement against that inventory with the `IndexOverlapClassifier` restored from git history (deleted in `fa0ed76`) and corrected for the gaps found during plan 060's review. No new command, tool or argument: both CLI `artifact --section plan` and MCP `sqlharness_artifact` gain the field.

**Tech Stack:** .NET 8/10, C#, xUnit, Microsoft.Data.SqlClient (SQL Server catalog views `sys.indexes`, `sys.index_columns`, `sys.columns`).

**Spec:** No separate spec. Origin: plan 060 "Revision 2026-10-13" (the deleted `indexes` command answered a different question than the statement under optimization; the useful question is the overlap of *this plan's* hint) and the idea row in `plans/README.md`. Decisions are recorded in this plan's Design decisions section; the executor treats them as the spec.

## Status

- Status: DONE on feature branch `plan-063` (not merged into main). Windows `just verify` exited 0 on the earlier documentation commit `50a8b0f`: `verify: OK  stages=6  2m58s` (ui-install 27,1s; ui-check 30,8s, 238 tests; restore 6,1s; build 7,2s, 0 warnings/errors; test 1m46s, SqlHarness.Tests 3,884 passed/11 skipped, MCP 387 passed/4 skipped; format skipped, 0 changed C# files). The later correction to when a missing `overlap` has an evidence gap was not re-gated because executable behavior did not change. Linux was not run. No push, installation, or publication. Evidence: [history](history.md).
- Priority: P2
- Effort: M
- Risk: LOW–MEDIUM (one extra read-only catalog batch on the measured session after measurement; additive artifact field)
- Depends on: 060 (DONE)
- Planned at: `472d5b7`, 2026-10-14

## Design decisions

1. **Capture at measure time, read offline.** `artifact` never connects, so overlap cannot be computed at read time from the database. The catalog batch runs on the measured session after the last measured repetition (timings are unaffected), before the session is disposed.
2. **Only when needed.** No hint in any saved plan of the operation → no catalog batch, no inventory file. PostgreSQL never has hints → never runs.
3. **Bounded.** At most 8 distinct hinted tables per artifact (first appearance order across runs, variants and statements); at most 64 indexes per table. Excess is reported as an evidence gap, never silently dropped.
4. **Best effort.** A catalog failure (permission, timeout, cancellation of the batch only) never fails the measure/compare: the artifact records the failure as an inventory gap and verdicts become unavailable. Cancellation of the whole operation still cancels.
5. **No filter text.** The inventory stores `hasFilter` only; filter definitions are not needed for the verdict (a filtered index is never better than `partial-key`).
6. **Verdict vocabulary is the old one:** `covered`, `include-gap`, `partial-key`, `new-shape`. Absent inventory → no `overlap` field and an evidence gap, never a guessed `new-shape`.
7. **Classifier corrections over the deleted version:** disabled, hypothetical and columnstore indexes and heaps never match; a rowstore clustered index covers every column; every nonclustered index on a clustered table also covers the clustered key columns (row locator).
8. **The saved `.sqlplan` path stays offline and unchanged:** `plan <file>` has no inventory and shows hints without verdicts.

## Global Constraints

- Verdict values (JSON, kebab-case): `covered`, `include-gap`, `partial-key`, `new-shape`.
- Inventory limits: `MaxInventoryTables = 8`, `MaxIndexesPerTable = 64`.
- Inventory file name: `index-inventory.json` in the artifact directory root.
- SQL Server only. The catalog batch is a fixed constant passing the existing safety classifier as `SqlUsage.Query` (pinned by a test).
- No new CLI command, MCP tool or argument (CLI/MCP parity rule: both surfaces read the same artifact section).
- Measure/compare exit codes and timings are unchanged by inventory success or failure.
- Hint `Database`/`Schema`/`Table` are stored without brackets.

## Review Focus

- A hint naming a table in a non-default schema with the same table name as one in `dbo` must match only its own schema (Task 2 and Task 3 tests).
- A hinted table dropped or renamed between the measured runs and the catalog batch yields `objectFound: false` and no verdict for that hint, not `new-shape` (Task 3 and Task 4 tests).
- A login without `VIEW DEFINITION` (catalog rows missing or error) leaves measure exit 0 and the artifact with an inventory gap (Task 3 test).
- More than 8 distinct hinted tables: the first 8 get verdicts, the rest get no verdict and the gap names how many were skipped (Task 3 test).
- An artifact written before this plan (no `index-inventory.json`) still reads; hints appear without `overlap` and one gap explains why (Task 4 test).

---

### Task 1: Hints keep their database and schema

**Files:**
- Modify: `src/SqlHarness.Core/PlanDistiller.cs` (`MissingIndex` record, `ParseMissingIndexes`, `Columns`)
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs` (~373, text rendering of a hint)
- Test: `tests/SqlHarness.Tests/PlanDistillerTests.cs`

**Interfaces:**
- Produces: `public sealed record MissingIndex(string? Database, string? Schema, string Table, IReadOnlyList<string> EqualityColumns, IReadOnlyList<string> InequalityColumns, IReadOnlyList<string> IncludeColumns, double Impact);` — all names unbracketed (`[dbo]` → `dbo`, `[Order]]s]` → `Order]s`). `Database`/`Schema` are omitted from JSON when null (`JsonIgnoreCondition.WhenWritingNull`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Missing_index_keeps_unbracketed_database_schema_table_and_columns()
{
    var plan = PlanDistiller.Distill(ShowplanWithMissingIndex(
        """<MissingIndex Database="[db]" Schema="[sales]" Table="[Order]]s]"><ColumnGroup Usage="EQUALITY"><Column Name="[CustomerId]" ColumnId="2" /></ColumnGroup><ColumnGroup Usage="INCLUDE"><Column Name="[Total]" ColumnId="5" /></ColumnGroup></MissingIndex>"""));

    var hint = Assert.Single(plan.Statements[0].MissingIndexes);
    Assert.Equal("db", hint.Database);
    Assert.Equal("sales", hint.Schema);
    Assert.Equal("Order]s", hint.Table);
    Assert.Equal(["CustomerId"], hint.EqualityColumns);
    Assert.Equal(["Total"], hint.IncludeColumns);
}

[Fact]
public void Missing_index_json_names_database_and_schema()
{
    var json = JsonSerializer.Serialize(PlanDistiller.Distill(ShowplanWithMissingIndex(
        """<MissingIndex Database="[db]" Schema="[dbo]" Table="[Orders]"><ColumnGroup Usage="EQUALITY"><Column Name="[CustomerId]" ColumnId="2" /></ColumnGroup></MissingIndex>""")));
    Assert.Contains("\"database\":\"db\"", json);
    Assert.Contains("\"schema\":\"dbo\"", json);
    Assert.Contains("\"table\":\"Orders\"", json);
}
```

`ShowplanWithMissingIndex(string missingIndexXml)` wraps the element in the minimal Showplan used by the existing missing-index tests in this file (reuse or extract their fixture; the `MissingIndexGroup Impact` attribute stays). Use the serializer options the existing JSON tests in this file use.

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~PlanDistillerTests"` → FAIL (record has no `Database`/`Schema`; names keep brackets).

- [ ] **Step 3: Implement**

```csharp
public sealed record MissingIndex(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Schema,
    string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns,
    double Impact);

// in ParseMissingIndexes:
new MissingIndex(
    Unbracket(Attribute(index, "Database")),
    Unbracket(Attribute(index, "Schema")),
    Unbracket(Attribute(index, "Table")) ?? string.Empty,
    Columns(index, "EQUALITY"),
    Columns(index, "INEQUALITY"),
    Columns(index, "INCLUDE"),
    Number(group, "Impact") ?? 0)

// Columns(...) maps each name through Unbracket.

internal static string? Unbracket(string? name) =>
    name is { Length: >= 2 } && name[0] == '[' && name[^1] == ']'
        ? name[1..^1].Replace("]]", "]", StringComparison.Ordinal)
        : name;
```

Fix every constructor call of `MissingIndex` (`PostgresPlanDistiller` uses an empty array — unaffected; tests that build `MissingIndex` positionally get `null, null` first). Text renderer: print `schema.table` when the schema is known.

- [ ] **Step 4: Run** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~PlanDistiller|FullyQualifiedName~ArtifactReader|FullyQualifiedName~AgentOutput|FullyQualifiedName~Renderer"` → PASS. Update existing assertions that expected bracketed names; list them in the commit body.

- [ ] **Step 5: Commit** `feat(063): missing-index hints keep unbracketed database and schema`

---

### Task 2: Overlap classifier

**Files:**
- Create: `src/SqlHarness.Core/Indexes/IndexOverlapClassifier.cs`, `src/SqlHarness.Core/Indexes/IndexInventory.cs` (records only)
- Test: `tests/SqlHarness.Tests/IndexOverlapClassifierTests.cs`

**Interfaces:**
- Produces:
```csharp
public enum IndexOverlapVerdict { Covered, IncludeGap, PartialKey, NewShape }   // JSON: kebab-case via a converter or JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower)

public sealed record InventoryIndex(
    int IndexId, string Name, string Type,          // Type: sys.indexes.type_desc, e.g. CLUSTERED, NONCLUSTERED, HEAP, CLUSTERED COLUMNSTORE
    IReadOnlyList<string> KeyColumns,               // key_ordinal order
    IReadOnlyList<string> IncludeColumns,
    bool Unique, bool PrimaryKey, bool Disabled, bool Hypothetical, bool HasFilter);

public sealed record InventoryTable(string Schema, string Table, bool ObjectFound,
    IReadOnlyList<InventoryIndex> Indexes, bool IndexesTruncated);

public sealed record IndexInventory(string Database, DateTimeOffset CapturedAt,
    IReadOnlyList<InventoryTable> Tables, IReadOnlyList<string> Gaps);

public sealed record MatchedIndex(string Name, IReadOnlyList<string> KeyColumns, IReadOnlyList<string> IncludeColumns, bool Filtered);

public sealed record IndexOverlap(IndexOverlapVerdict Verdict,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MatchedIndex? Index,
    int MatchedKeyColumns, int HintKeyColumns, IReadOnlyList<string> MissingIncludeColumns);

public static class IndexOverlapClassifier
{
    public static IndexOverlap Classify(MissingIndex hint, InventoryTable table);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class IndexOverlapClassifierTests
{
    private static MissingIndex Hint(string[] eq, string[] ineq, string[] inc) =>
        new("db", "dbo", "Orders", eq, ineq, inc, 50);

    private static InventoryIndex Ix(int id, string name, string[] keys, string[]? inc = null,
        string type = "NONCLUSTERED", bool disabled = false, bool hypothetical = false, bool filter = false) =>
        new(id, name, type, keys, inc ?? [], false, false, disabled, hypothetical, filter);

    private static InventoryTable Table(params InventoryIndex[] indexes) =>
        new("dbo", "Orders", true, indexes, false);

    [Fact]
    public void Full_key_and_includes_is_covered()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], ["OrderDate"], ["Total"]),
            Table(Ix(2, "IX_C_D", ["CustomerId", "OrderDate"], ["Total"])));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
        Assert.Equal("IX_C_D", overlap.Index!.Name);
        Assert.Equal(2, overlap.MatchedKeyColumns);
        Assert.Empty(overlap.MissingIncludeColumns);
    }

    [Fact]
    public void Equality_columns_may_lead_in_any_order()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["B", "A"], [], []), Table(Ix(2, "IX_AB", ["A", "B"])));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
    }

    [Fact]
    public void Missing_include_is_include_gap_naming_the_columns()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], [], ["Total", "Status"]),
            Table(Ix(2, "IX_C", ["CustomerId"], ["Status"])));
        Assert.Equal(IndexOverlapVerdict.IncludeGap, overlap.Verdict);
        Assert.Equal(["Total"], overlap.MissingIncludeColumns);
    }

    [Fact]
    public void Leading_prefix_only_is_partial_key()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId", "Status"], [], []),
            Table(Ix(2, "IX_C", ["CustomerId"])));
        Assert.Equal(IndexOverlapVerdict.PartialKey, overlap.Verdict);
        Assert.Equal(1, overlap.MatchedKeyColumns);
        Assert.Equal(2, overlap.HintKeyColumns);
    }

    [Fact]
    public void Non_leading_match_is_new_shape()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["Status"], [], []),
            Table(Ix(2, "IX_C_S", ["CustomerId", "Status"])));
        Assert.Equal(IndexOverlapVerdict.NewShape, overlap.Verdict);
        Assert.Null(overlap.Index);
    }

    [Fact]
    public void Filtered_index_is_never_better_than_partial_key()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], [], []),
            Table(Ix(2, "IX_C_f", ["CustomerId"], filter: true)));
        Assert.Equal(IndexOverlapVerdict.PartialKey, overlap.Verdict);
        Assert.True(overlap.Index!.Filtered);
    }

    [Theory]
    [InlineData("NONCLUSTERED", true, false)]
    [InlineData("NONCLUSTERED", false, true)]
    [InlineData("NONCLUSTERED COLUMNSTORE", false, false)]
    [InlineData("HEAP", false, false)]
    public void Disabled_hypothetical_columnstore_and_heap_never_match(string type, bool disabled, bool hypothetical)
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], [], []),
            Table(Ix(2, "IX", ["CustomerId"], type: type, disabled: disabled, hypothetical: hypothetical)));
        Assert.Equal(IndexOverlapVerdict.NewShape, overlap.Verdict);
    }

    [Fact]
    public void Clustered_index_covers_every_column()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["OrderId"], [], ["Total", "Status"]),
            Table(Ix(1, "PK_Orders", ["OrderId"], type: "CLUSTERED")));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
    }

    [Fact]
    public void Nonclustered_index_covers_the_clustered_key_as_row_locator()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["CustomerId"], [], ["OrderId"]),
            Table(Ix(1, "PK_Orders", ["OrderId"], type: "CLUSTERED"), Ix(2, "IX_C", ["CustomerId"])));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
        Assert.Equal("IX_C", overlap.Index!.Name);
    }

    [Fact]
    public void Strongest_match_wins_then_more_key_columns_then_fewer_missing_includes_then_lower_index_id()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["A", "B"], [], ["X"]),
            Table(Ix(3, "IX_A", ["A"]), Ix(4, "IX_AB_noX", ["A", "B"]), Ix(5, "IX_AB_X", ["A", "B"], ["X"])));
        Assert.Equal("IX_AB_X", overlap.Index!.Name);
    }

    [Fact]
    public void Column_names_compare_case_insensitively()
    {
        var overlap = IndexOverlapClassifier.Classify(Hint(["customerid"], [], []), Table(Ix(2, "IX_C", ["CustomerId"])));
        Assert.Equal(IndexOverlapVerdict.Covered, overlap.Verdict);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~IndexOverlapClassifierTests"` → build error.

- [ ] **Step 3: Implement**

Start from the deleted classifier: `git show fa0ed76^:src/SqlHarness.Core/IndexOverlapClassifier.cs`. Keep its key-matching algorithm (equality columns lead the key in any order; the first other column ends that segment; then inequality columns in order), its ranking (`Covered 4 > IncludeGap 3 > PartialKey 2 > NewShape 1`, then more matched key columns, then fewer missing includes, then lower `IndexId`) and the filtered-index downgrade. Change:

- Input types: `MissingIndex` + `InventoryTable` instead of `IndexCandidate`/`ExistingIndex`; drop the schema/table equality check (the caller passes the hint's own table).
- `IsIgnored(index)`: `Type` is `HEAP` or contains `COLUMNSTORE`, or `Disabled`, or `Hypothetical`.
- Coverage set for includes: key ∪ include columns, plus — when the index `Type` is `CLUSTERED` — every column (treat as covering all includes), plus — for a nonclustered index when the table has an enabled rowstore `CLUSTERED` index — that clustered index's key columns.
- `MatchedIndex` carries the matched index's key and include lists so an agent can propose the exact `CREATE`/`ALTER` change.

- [ ] **Step 4: Run** the filter → PASS.

- [ ] **Step 5: Commit** `feat(063): index overlap classifier with clustered and disabled-index corrections`

---

### Task 3: Capture the inventory on the measured session

**Files:**
- Create: `src/SqlHarness.Core/Indexes/IndexInventoryQuery.cs` (constant SQL, parameters, reader), `src/SqlHarness.Core/Indexes/IndexInventoryCapture.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs` (measure ~653-690, measure parameter sets ~608-627, compare and compare matrix paths: call capture after the last measured run, before the artifact write), `src/SqlHarness.Core/Artifacts.cs` (`Write(...)` accepts an optional `IndexInventory?` and writes `index-inventory.json` in the staging directory)
- Test: `tests/SqlHarness.Tests/IndexInventoryCaptureTests.cs`, existing measure/compare tests with `FakeMeasureSession` (`tests/SqlHarness.Tests/MeasureTests.cs:685`)

**Interfaces:**
- Consumes: `MissingIndex` (Task 1), `IndexInventory`/`InventoryTable`/`InventoryIndex` (Task 2).
- Produces:
```csharp
internal static class IndexInventoryCapture
{
    public const int MaxInventoryTables = 8;
    public const int MaxIndexesPerTable = 64;

    /// Distinct (schema, table) of all hints whose Database is null or equals the session database, in first-appearance order.
    internal static IReadOnlyList<(string Schema, string Table)> HintedTables(IEnumerable<DistilledPlan> plans, string database, out int skippedTables, out int otherDatabaseHints);

    /// Returns null when no plan has a hint or the engine is not SQL Server. Never throws except OperationCanceledException for the operation token.
    internal static Task<IndexInventory?> CaptureAsync(SqlEngine engine, ISqlSession session, IReadOnlyList<string> planXmls, int timeoutSeconds, CancellationToken ct);
}
```

The catalog batch (constant; eight slots, unused slots NULL):

```sql
DECLARE @targets TABLE (slot int NOT NULL, schema_name sysname NOT NULL, table_name sysname NOT NULL, object_id int NULL);
INSERT INTO @targets (slot, schema_name, table_name, object_id)
SELECT v.slot, v.schema_name, v.table_name,
       OBJECT_ID(QUOTENAME(v.schema_name) + N'.' + QUOTENAME(v.table_name))
FROM (VALUES (1, @schema1, @table1), (2, @schema2, @table2), (3, @schema3, @table3), (4, @schema4, @table4),
             (5, @schema5, @table5), (6, @schema6, @table6), (7, @schema7, @table7), (8, @schema8, @table8))
     AS v(slot, schema_name, table_name)
WHERE v.schema_name IS NOT NULL AND v.table_name IS NOT NULL;

SELECT t.slot, CAST(CASE WHEN t.object_id IS NULL THEN 0 ELSE 1 END AS bit) AS object_found
FROM @targets AS t ORDER BY t.slot;

DECLARE @indexes TABLE (slot int NOT NULL, object_id int NOT NULL, index_id int NOT NULL);
INSERT INTO @indexes (slot, object_id, index_id)
SELECT ranked.slot, ranked.object_id, ranked.index_id
FROM (
    SELECT t.slot, i.object_id, i.index_id,
           ROW_NUMBER() OVER (PARTITION BY t.slot ORDER BY i.index_id) AS position
    FROM @targets AS t
    INNER JOIN sys.indexes AS i ON i.object_id = t.object_id
) AS ranked
WHERE ranked.position <= 65;

SELECT x.slot, i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key,
       i.is_disabled, i.is_hypothetical, i.has_filter
FROM @indexes AS x
INNER JOIN sys.indexes AS i ON i.object_id = x.object_id AND i.index_id = x.index_id
ORDER BY x.slot, i.index_id;

SELECT x.slot, ic.index_id, c.name AS column_name, ic.key_ordinal, ic.is_included_column
FROM @indexes AS x
INNER JOIN sys.index_columns AS ic ON ic.object_id = x.object_id AND ic.index_id = x.index_id
INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
ORDER BY x.slot, ic.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
```

Parameters `@schema1..8`, `@table1..8` are `nvarchar(128)`, bound through the existing `SqlHarnessParameter` path. The batch returns at most 65 indexes per slot; the reader keeps 64 and sets `IndexesTruncated` when the 65th appears. Column rows of the dropped 65th index are ignored.

- [ ] **Step 1: Write the failing tests**

- `Inventory_batch_passes_the_query_classifier`: `SqlDialects.For(SqlEngine.SqlServer).Classify(IndexInventoryQuery.Sql, SqlUsage.Query, "db", false, null, <no temps>)` is allowed (use the classifier call shape the module uses for `query`).
- `Hinted_tables_are_distinct_in_first_appearance_order_and_capped_at_eight`: hints on `T1, T2, T1, T3` → `[T1, T2, T3]`, `skippedTables == 0`; hints on ten distinct tables `T1..T10` → `[T1..T8]`, `skippedTables == 2`.
- `Hints_for_another_database_are_counted_not_queried`.
- `No_hint_means_no_catalog_batch`: `FakeMeasureSession` records commands; a measure whose plans have no hints executes no command after the last repetition and writes no `index-inventory.json`.
- `Measure_with_a_hint_captures_the_inventory_after_the_last_repetition`: the recorded command order is setup, warm-up, repetitions 1..N, then the inventory batch; the artifact directory has `index-inventory.json` with the scripted tables and indexes.
- `Same_table_name_in_two_schemas_maps_each_hint_to_its_own_slot` (Review Focus 1).
- `Missing_object_is_recorded_as_not_found` (Review Focus 2): the scripted first result set returns `object_found = 0` → `InventoryTable.ObjectFound == false`, no indexes.
- `Catalog_failure_keeps_measure_successful_and_records_a_gap` (Review Focus 3): the session throws `SqlException`-equivalent (the fake's SQL failure) on the inventory batch → outcome exit 0, `index-inventory.json` has empty `tables` and one gap `"Index inventory unavailable: <redacted message>."`.
- `More_than_eight_tables_records_a_skip_gap` (Review Focus 4): gap text `"Index inventory covers the first 8 hinted tables; 2 more were skipped."`.
- `Postgres_never_runs_the_inventory_batch`.
- `Compare_captures_one_inventory_for_both_variants` and one matrix case: each cell artifact gets its own inventory from its own connection.
- `Operation_cancellation_during_capture_cancels_the_operation`: cancelling the operation token while the inventory batch runs yields the normal cancelled outcome.

Script result sets with `FakeMeasureSession` the way existing measure tests script plan XML (extend it to return scripted rows for a command whose text equals `IndexInventoryQuery.Sql`).

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~IndexInventory|FullyQualifiedName~MeasureTests|FullyQualifiedName~CompareTests"` → FAIL.

- [ ] **Step 3: Implement**

`CaptureAsync`: return null unless `engine == SqlServer`; distill each plan XML once (reuse the distilled plans the artifact writer already prepares if that avoids a second distillation — pass them in instead of XML when the call site has them); collect hinted tables; if none, return null. Execute the batch with `timeoutSeconds = Math.Min(30, operationTimeout)`; catch every exception except `OperationCanceledException` when `ct.IsCancellationRequested`, and return an inventory with no tables and the gap message redacted through the module's `SecretRedactor`/known-secrets path. `CapturedAt` = UTC now; `Database` = `session.Identity` database. Call sites pass the result to `_artifactWriter.Write(...)`; `Artifacts.Write` serializes it with `ArtifactDirectoryPublisher.JsonOptions` to `index-inventory.json` when not null. Do not add it to `report.json`, the journal or the CLI/MCP operation result.

- [ ] **Step 4: Run** the Step 2 filter plus `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Artifact"` → PASS.

- [ ] **Step 5: Commit** `feat(063): capture the hinted tables' index inventory after measurement`

---

### Task 4: The artifact plan section reports an overlap per hint

**Files:**
- Modify: `src/SqlHarness.Core/ArtifactReader.cs` (`ArtifactPlanSection.MissingIndexes` type, `ReadPlan` ~411-505, new bounded reader for `index-inventory.json`)
- Modify: `src/SqlHarness.Core/AgentOutputProjection.cs` (~269-319: projection of plan-section hints), `src/SqlHarness.Cli/Commands/Renderer.cs` (text rendering of the artifact plan section, if it prints hints)
- Test: `tests/SqlHarness.Tests/ArtifactReaderTests.cs`, `tests/SqlHarness.Tests/AgentOutputProjectionTests.cs` (or the file that tests `--output agent` plan sections), `tests/SqlHarness.Mcp.Tests` artifact output test that pins the plan section shape (search for `missingIndexes`)

**Interfaces:**
- Consumes: `IndexOverlapClassifier.Classify`, `IndexInventory` (Tasks 2–3), file name `index-inventory.json`.
- Produces:
```csharp
public sealed record ArtifactMissingIndex(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Schema,
    string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns,
    double Impact,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IndexOverlap? Overlap);
// ArtifactPlanSection.MissingIndexes becomes IReadOnlyList<ArtifactMissingIndex>.
```

- [ ] **Step 1: Write the failing tests**

- `Plan_section_hint_carries_the_overlap_verdict`: an artifact fixture with one plan hint on `dbo.Orders` and an `index-inventory.json` holding `IX_C(CustomerId) INCLUDE(Status)` → the section's hint has `overlap.verdict == "include-gap"`, `overlap.index.name == "IX_C"`, `missingIncludeColumns == ["Total"]` (assert on serialized JSON to pin the kebab-case value).
- `Legacy_artifact_without_inventory_reads_without_overlap` (Review Focus 5): no `index-inventory.json` → hint present, no `overlap` property, `evidenceGaps` contains `"Index inventory was not captured for this artifact; missing-index hints have no overlap verdict."`.
- `Not_found_table_gives_no_verdict` (Review Focus 2): inventory table with `objectFound: false` → no `overlap`, gap `"dbo.Orders was not found when the index inventory was captured."`.
- `Skipped_table_gives_no_verdict_and_inventory_gaps_are_surfaced`: inventory gaps are appended to `evidenceGaps` once.
- `Truncated_index_list_downgrades_new_shape_to_unavailable`: when `IndexesTruncated` is true and the best verdict is `new-shape`, omit `overlap` and add gap `"dbo.Orders has more than 64 indexes; a new-shape verdict cannot be trusted."` (any better verdict stays).
- `Invalid_inventory_file_is_a_gap_not_a_failure`: corrupt JSON → section reads, gap `"Index inventory is unreadable."`.
- `Hint_for_another_database_has_no_verdict`.
- Agent output: `--output agent` / compact projection keeps `overlap` on the hints it keeps (Take limit unchanged).
- MCP: the served `sqlharness_artifact` plan section carries `overlap` (one served test with an artifact fixture).

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ArtifactReader|FullyQualifiedName~AgentOutput"` and `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~Artifact"` → FAIL.

- [ ] **Step 3: Implement**

In `ReadPlan`, when `cursor == 0`: read `index-inventory.json` with the existing bounded-file helpers (`IsPlainFile`, `ReadBoundedText`, size cap like other small artifact files); deserialize into `IndexInventory`. For each hint of the selected statement: find the inventory table by case-insensitive (`Schema`, `Table`) — a hint with null `Schema` matches `dbo`; a hint whose `Database` differs (ordinal ignore case) from `inventory.Database` gets no verdict. Build `ArtifactMissingIndex` from the hint plus `Classify(...)` result, applying the truncation rule above. Gap strings are exactly the ones in Step 1 and are added once each. Pages after the first keep `MissingIndexes = []` as today.

- [ ] **Step 4: Run** the Step 2 filters → PASS. Then `dotnet build SqlHarness.sln -warnaserror` and `dotnet format --verify-no-changes`.

- [ ] **Step 5: Commit** `feat(063): artifact plan section reports an overlap verdict per missing-index hint`

---

### Task 5: Documentation, skill, plan index

- [ ] **Step 1:** Follow the writing-for-agents rules (`mattpocock-skills:writing-for-agents`): one source of truth, positive target behaviour, no restating `--help`.
  - `README.md`: the artifact `plan` section reference documents `missingIndexes[].overlap` (verdict values and meaning, `index` with key/include columns, `missingIncludeColumns`), when the inventory is captured (SQL Server, hints present, after the last measured run, best effort, limits 8 tables / 64 indexes), `index-inventory.json` in the artifact directory (add it to the locally sensitive outputs list only if README lists artifact files individually), and that `plan <file>` shows hints without verdicts.
  - `skills/sqlharness/SKILL.md`: replace "check existing indexes with a scoped `query`" with the verdict-driven step: `covered` → no new index, look at why the plan did not use it; `include-gap` → extend the named index's INCLUDE; `partial-key` → compare against the named index before proposing a new one; `new-shape` → a hypothesis to prove with `compare` after approved DDL; no `overlap` → read `evidenceGaps`, fall back to a scoped catalog `query`. Creating or altering an index stays a persistent mutation needing approval.
  - `docs/mcp.md`: one line in the `sqlharness_artifact` plan section description if it lists fields.
  - `src/SqlHarness.Core/Capabilities.cs`: update the artifact/plan text only if it enumerates plan-section fields.
- [ ] **Step 2:** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DocsSync|FullyQualifiedName~Contracts|FullyQualifiedName~Capabilities"` → PASS; `dotnet build SqlHarness.sln -warnaserror`.
- [ ] **Step 3:** `plans/README.md`: 063 row DONE with evidence; remove the "Missing-index hint overlap (idea from 060, not accepted)" bullet. Gates: `pwsh ./scripts/verify.ps1`; the Linux gate per the operator's procedure (confirm the shared WSL clone is idle; the operator runs `! pwsh ./scripts/verify-linux.ps1`).
- [ ] **Step 4: Commit** `docs(063): overlap verdict for missing-index hints`

## Done criteria

- [ ] A measure of a SQL Server query whose plan has a hint produces `index-inventory.json`, and `artifact <id> --section plan` shows `overlap` for that hint (CLI and MCP).
- [ ] No hint, or PostgreSQL → no catalog batch and no inventory file.
- [ ] Inventory failure never changes the measure/compare exit code; it surfaces as an evidence gap.
- [ ] Old artifacts read unchanged apart from one explanatory gap.
- [ ] Both gates green (Linux per the operator's procedure).

## STOP conditions

- The inventory batch fails the safety classifier on HEAD. Do not weaken the classifier; report the statement.
- Capturing on the measured session would require reordering measured runs or changing their timing (e.g. the session cannot run another batch after the last repetition). Stop and report.
- A test shows that `sys.index_columns` column names cannot be matched to showplan hint column names for a supported collation. Stop and report the case.
