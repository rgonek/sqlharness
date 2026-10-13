# Plan 055: Agent output reaches the model whole — one representation, a real budget, stepped lists, a compact envelope

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 27d2a9a..HEAD -- src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Mcp/McpResultAdapter.cs src/SqlHarness.Mcp/McpLimits.cs src/SqlHarness.Mcp/McpServerOptions.cs src/SqlHarness.Mcp/Tools/McpToolCatalog.cs src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Core/ArtifactReader.cs docs/mcp.md README.md skills/sqlharness/SKILL.md`. Read every change in that list before editing; adapt line numbers, keep the intent. A changed file alone is not a STOP condition.

**Goal:** An agent receives every list item that fits a 64 KiB text budget, in order of usefulness, without a duplicated representation or envelope padding.

**Architecture:** Tool results become one text block; the budget measures that text. All reports share one descending detail ladder; per-table reads are ordered by significance before limits apply. Queries are compacted before the final fit check; matrix case inventories take precedence over repeated optional detail. Envelope `schemaVersion` becomes 2.

**Tech Stack:** .NET 8/10, C#, xUnit, ModelContextProtocol C# SDK.

**Spec:** [`docs/superpowers/specs/2026-10-12-agent-contract-review-design.md`](../docs/superpowers/specs/2026-10-12-agent-contract-review-design.md), decisions 1–4.

## Status

- Status: DONE (implementation commits `52bdc23..f5ac90c`; Windows and Linux gates passed; whole-branch review/re-review clean; locally merged to main at `4bb783a`; no post-merge gates after docs/plans-only main drift)
- Priority: P1
- Effort: M
- Risk: MEDIUM (shared projection for MCP and CLI agent output; wire contract version bump)
- Depends on: —
- Planned at: `27d2a9a`, 2026-10-12

## Global Constraints

- Default per-call budget: `65536` bytes of UTF-8 text. Process ceiling `--max-result-bytes`: default `262144`, range `4096..1048576`.
- A call's `maxResultBytes` may raise or lower the budget; values above the process ceiling are clamped to it.
- Detail ladder for every report: `int.MaxValue, 128, 64, 32, 16, 8, 4, 2, 1, 0`.
- Envelope `schemaVersion`: `2` (MCP and CLI `--output agent`).
- Cell limit stays 512 characters; clipped retained cells carry positional metadata. JSON is never truncated mid-document.
- Query valueType describes CLR values, never inferred SQL types.
- Matrices retain a complete lightweight case inventory before optional per-case detail when it fits.
- No `structuredContent`, no `outputSchema` on any tool.

## Review Focus

- A result that fits at 64 KiB but exceeded the old 16 KiB wire budget must now arrive untruncated (Task 2 test).
- A call with `maxResultBytes` above the ceiling must not fail; it is clamped (Task 2 test).
- Per-table reads where several tables have equal medians must order deterministically (Task 1 test).
- A query with zero rows and no messages must still serialize `resultSets` with columns (Task 3 test).
- `recordsAffected` of `0` (a real zero) must stay visible; only `-1` is omitted (Task 3 test).
- Rich matrix detail must not crowd out completed cases (Task 4 test).
- Clipped query cells must be identifiable without changing their value type (Task 3 test).

## Why this matters

A session's catalog listing, matrix cases, statement diagnostics and useful per-table reads were
obstructively truncated while a richer inventory could have fit. The adapter serializes both text and
structured representations; this spends transport budget twice. How a particular client injects those
representations into model context must be measured separately, not assumed. Small query outputs can
have envelope overhead larger than their row data. Private workload counts/results are omitted here.
## Current state (verify each)

1. `AgentOutputProjection.CalculateDetailLimit` (`:48-54`) returns 1 for `(16384, 512)`; `DefaultCandidateDetailLimits` (`:79-92`) yields `int.MaxValue, 1, 0` for every non-query report. `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs:500-515` pins that ladder.
2. `ClipVariant` (`:343-348`) and the `ArtifactMetricsSection` case (`:261-272`) `Take` per-table reads in dictionary order.
3. `McpResultAdapter.Build` (`:426-436`) sets both `Content` and `StructuredContent`; `TryBuildWithinBudget` (`:419-424`) measures the serialized `CallToolResult`; `EmittedFootprint` (`:246-258`) counts the same bytes.
4. `McpToolCatalog` sets `UseStructuredContent = true` and `OutputSchema` in both catalogs (≈ `:507-508`, `:571-575`).
5. `McpLimits.CallToolResultBudgetBytes = 16 KiB` (`:53`); `McpResultBudget.Resolve` (`McpResultAdapter.cs:47-64`) only lowers.
6. `AgentQueryProjection` (`AgentOutputProjection.cs:29-38`) carries the full target, `ResultHash`, `RawFootprint`; columns carry .NET type names (`QueryResultCollector.cs:64`).
7. `ArtifactReader.ReadSection` (`:146-176`) rejects a cursor for every section except `matrix-cells`.

---

### Task 1: One detail ladder; per-table reads by significance

**Files:**
- Modify: `src/SqlHarness.Core/AgentOutputProjection.cs:48-92` (ladder), `:261-272`, `:343-348` (ordering)
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs:157`, `src/SqlHarness.Mcp/McpResultAdapter.cs:172` (callers)
- Test: `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs`

**Interfaces:**
- Produces: `public static IReadOnlyList<int> AgentOutputProjection.CandidateDetailLimits` (replaces `GetCandidateDetailLimits` and `CalculateDetailLimit`).
- Produces: `internal static IEnumerable<KeyValuePair<string, CompareDistribution>> AgentOutputProjection.BySignificance(IReadOnlyDictionary<string, CompareDistribution> reads)`.

- [x] **Step 1: Replace the pinned ladder test and add the ordering test**

Replace `Query_candidate_ladder_samples_rows_before_dropping_shape` with:

```csharp
[Fact]
public void Every_report_uses_one_descending_ladder()
{
    Assert.Equal(
        [int.MaxValue, 128, 64, 32, 16, 8, 4, 2, 1, 0],
        AgentOutputProjection.CandidateDetailLimits);
}

[Fact]
public void Per_table_reads_order_by_median_then_max_then_name()
{
    var reads = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
    {
        ["Worktable"] = new(0, 0, 0),
        ["[dbo].[B]"] = new(28, 28, 28),
        ["[dbo].[A]"] = new(280, 282, 290),
        ["[dbo].[C]"] = new(20, 28, 40),
    };

    var ordered = AgentOutputProjection.BySignificance(reads).Select(pair => pair.Key).ToArray();

    Assert.Equal(["[dbo].[A]", "[dbo].[C]", "[dbo].[B]", "Worktable"], ordered);
}
```

- [x] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~AgentOutputTests.Every_report|FullyQualifiedName~AgentOutputTests.Per_table"`
Expected: build error, `CandidateDetailLimits` and `BySignificance` do not exist.

- [x] **Step 3: Implement**

In `AgentOutputProjection.cs`, delete `CalculateDetailLimit`, `QueryRowSampleLadder`, `GetCandidateDetailLimits`, `QueryCandidateDetailLimits`, `DefaultCandidateDetailLimits` and add:

```csharp
/// <summary>
/// Detail limits tried in order until the rendered envelope fits the budget. The first
/// entry renders the report without truncation. Every report type uses the same steps.
/// </summary>
public static IReadOnlyList<int> CandidateDetailLimits { get; } = [int.MaxValue, 128, 64, 32, 16, 8, 4, 2, 1, 0];

internal static IEnumerable<KeyValuePair<string, CompareDistribution>> BySignificance(
    IReadOnlyDictionary<string, CompareDistribution> reads) =>
    reads.OrderByDescending(pair => pair.Value.Median)
        .ThenByDescending(pair => pair.Value.Max)
        .ThenBy(pair => pair.Key, StringComparer.Ordinal);
```

In `ClipVariant` and the `ArtifactMetricsSection` case replace `variant.LogicalReadsByTable.ToArray()` with `BySignificance(variant.LogicalReadsByTable).ToArray()`.

In `Renderer.cs:157` and `McpResultAdapter.cs:172` replace `AgentOutputProjection.GetCandidateDetailLimits(...).Distinct()` with `AgentOutputProjection.CandidateDetailLimits`. Fix the remaining compile errors in tests that call the removed members (`AgentOutputTests.cs:620` loop → iterate `CandidateDetailLimits`).

- [x] **Step 4: Add the behavior test for a two-cell matrix**

In `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs` add a test that builds a `SqlHarnessCompareMatrixReport` with two cells from the existing compare fixture helpers in that file (search for `SqlHarnessCompareMatrixReport(` in the test project and reuse the nearest builder) and asserts:

```csharp
var result = McpResultAdapter.Adapt(SqlHarnessOutcome.Success(report), "compare");
using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
var cells = document.RootElement.GetProperty("result").GetProperty("cells");
Assert.Equal(2, cells.GetArrayLength());
Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("truncation").ValueKind);
```

If `SqlHarnessOutcome.Success` has a different factory name, use the one the neighbouring tests use.

- [x] **Step 5: Run focused tests**

Run: `dotnet test SqlHarness.sln --filter "FullyQualifiedName~AgentOutput|FullyQualifiedName~McpTokenBudget|FullyQualifiedName~McpOutput"`
Expected: PASS. A test that asserted `detailLimit: 1` for a small report is a pin of the defect: change it to assert the untruncated result and say so in the commit body.

- [x] **Step 6: Commit**

```bash
git add src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Mcp/McpResultAdapter.cs tests
git commit -m "fix(055): one stepped detail ladder for every report; reads by significance"
```

---

### Task 2: One text representation; budget measures the text; 64 KiB default

**Files:**
- Modify: `src/SqlHarness.Mcp/McpResultAdapter.cs` (`Build`, `TryBuildWithinBudget`, `EmittedFootprint`, `McpResultBudget.Resolve`, remove `OutputSchema`)
- Modify: `src/SqlHarness.Mcp/McpLimits.cs:53`, `src/SqlHarness.Mcp/McpServerOptions.cs:34`
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (both `McpServerToolCreateOptions` blocks; every `maxResultBytes` description)
- Modify: `src/SqlHarness.Mcp/McpOperationMapper.cs` (capabilities `Limits`)
- Modify: `src/SqlHarness.Cli/Commands/McpServeCommand.cs:52` (option help text)
- Test: `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`, `McpToolSchemaTests.cs`, `McpOutputTests.cs`

**Interfaces:**
- Produces: `McpLimits.DefaultCallToolResultBudgetBytes = 65536`, `McpLimits.DefaultProcessResultCeilingBytes = 262144` (replaces `CallToolResultBudgetBytes`).
- Produces: `McpResultBudget.Resolve(int processCeilingBytes, int? callBytes, ...)` → `min(callBytes ?? DefaultCallToolResultBudgetBytes, processCeilingBytes)`.
- Produces: `McpResultAdapter.MeasureBytes(CallToolResult)` returns the UTF-8 byte count of the text block(s).

- [x] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Results_carry_one_text_block_and_no_structured_content()
{
    var result = McpResultAdapter.Adapt(SqlHarnessOutcome.Success(new SqlHarnessPingReport("s", "d", "l")), "inspect");
    Assert.Null(result.StructuredContent);
    Assert.Single(result.Content.OfType<TextContentBlock>());
}

[Fact]
public void Budget_counts_only_the_text_the_model_reads()
{
    var result = McpResultAdapter.Adapt(LargeQueryOutcome(targetBytes: 40_000), "query");
    var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
    Assert.Equal(Encoding.UTF8.GetByteCount(text), McpResultAdapter.MeasureBytes(result));
    using var document = JsonDocument.Parse(text);
    Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("truncation").ValueKind);
}

[Theory]
[InlineData(null, 262144, 65536)]
[InlineData(131072, 262144, 131072)]
[InlineData(900000, 262144, 262144)]
[InlineData(8192, 262144, 8192)]
[InlineData(null, 16384, 16384)]
public void Call_budget_resolves_within_the_process_ceiling(int? call, int ceiling, int expected) =>
    Assert.Equal(expected, McpResultBudget.Resolve(ceiling, call).MaximumBytes);

[Fact]
public void Tools_advertise_no_output_schema()
{
    foreach (var tool in McpToolCatalog.CreateTools(RequestProcess()).Select(tool => tool.ProtocolTool))
        Assert.Null(tool.OutputSchema);
}
```

`LargeQueryOutcome(targetBytes)` builds a `SqlHarnessQueryReport` with one result set of short string rows until its serialized envelope is about `targetBytes` (use `QueryShapeFixtures` if the MCP test project references it; otherwise build rows in a loop in a private helper). `RequestProcess()` is the `McpProcessContext.Create(...)` block from `Request_catalog_stays_within_tools_list_budget`, extracted into a private helper. Replace the `SqlHarnessPingReport` constructor arguments with whatever the record takes on HEAD.

- [x] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpTokenBudget|FullyQualifiedName~McpToolSchema"`
Expected: FAIL — structured content present; 40 KB report truncated; resolve rejects 900000 / returns 16384 for null.

- [x] **Step 3: Implement**

`McpLimits.cs`:

```csharp
/// <summary>Default per-call budget: UTF-8 bytes of the text block the model reads.</summary>
public const int DefaultCallToolResultBudgetBytes = 64 * 1024;

/// <summary>Default process ceiling for any single call (<c>--max-result-bytes</c>).</summary>
public const int DefaultProcessResultCeilingBytes = 256 * 1024;
```

Delete `CallToolResultBudgetBytes`; fix every reference (`McpServerOptions.MaxResultBytes` default → `DefaultProcessResultCeilingBytes`; `McpResultBudget` default → `DefaultCallToolResultBudgetBytes`).

`McpResultBudget.Resolve`:

```csharp
public static McpResultBudget Resolve(
    int processCeilingBytes,
    int? callMaximumBytes,
    int? callMaximumCellCharacters = null,
    int processMaximumCellCharacters = McpLimits.DefaultMaximumCellCharacters)
{
    var ceiling = new McpResultBudget(processCeilingBytes, processMaximumCellCharacters);
    ceiling.Validate();
    var requested = callMaximumBytes ?? McpLimits.DefaultCallToolResultBudgetBytes;
    var effectiveBytes = Math.Max(McpLimits.MinCallToolResultBudgetBytes, Math.Min(requested, ceiling.MaximumBytes));
    var effectiveCells = callMaximumCellCharacters.HasValue
        ? Math.Min(callMaximumCellCharacters.Value, ceiling.MaximumCellCharacters)
        : ceiling.MaximumCellCharacters;
    var resolved = new McpResultBudget(effectiveBytes, effectiveCells);
    resolved.Validate();
    return resolved;
}
```

`McpResultAdapter`: delete `OutputSchema`; keep `ValidateEnvelope` (it pins the envelope in tests). Change:

```csharp
public static int MeasureBytes(CallToolResult result)
{
    ArgumentNullException.ThrowIfNull(result);
    return result.Content.OfType<TextContentBlock>().Sum(block => Encoding.UTF8.GetByteCount(block.Text));
}

private static bool TryBuildWithinBudget(byte[] envelopeUtf8, bool isError, int maximumBytes, out CallToolResult result)
{
    result = Build(envelopeUtf8, isError);
    return envelopeUtf8.Length <= maximumBytes;
}

private static CallToolResult Build(byte[] envelopeUtf8, bool isError) => new()
{
    Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(envelopeUtf8) }],
    IsError = isError,
};
```

`EmittedFootprint`: count bytes and newlines of the text block(s) only (same loop over `Encoding.UTF8.GetBytes(text)`). Update the class summary comments that say "both representations".

`McpToolCatalog`: in both option blocks remove `UseStructuredContent` and `OutputSchema` and the comment about them. Every `maxResultBytes` parameter description becomes `"Response budget 4096..1048576 bytes of result text; default 65536; clamped to the process ceiling."`.

Capabilities (`McpOperationMapper.BuildCapabilities`, both overloads): replace `["callToolResultBudgetBytes"]` with `["defaultResultBytes"] = McpLimits.DefaultCallToolResultBudgetBytes` and `["maxResultBytes"] = context.MaxResultBytes`.

- [x] **Step 4: Run focused tests, then fix pins**

Run: `dotnet test SqlHarness.sln --filter "FullyQualifiedName~Mcp"`
Expected: new tests PASS. Existing tests that assert `StructuredContent`, `OutputSchema`, `callToolResultBudgetBytes`, or the 16384 default fail; update each to the new contract (no structured content; budget 65536; ceiling 262144). Do not delete a test that checks a budget is enforced — lower its budget explicitly instead.

- [x] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp src/SqlHarness.Cli/Commands/McpServeCommand.cs tests/SqlHarness.Mcp.Tests
git commit -m "feat(055): text-only MCP results; budget measures model-visible text, 64 KiB default"
```

---

### Task 3: Compact query values without inventing SQL types or hiding clipped cells

**Evidence.** Query schemas expose CLR field types. Mapping System.DateTime to SQL-looking datetime
loses the distinction between source date, datetime and datetime2; a CLR string says nothing about source
Unicode or size. A global clipping count does not identify the incomplete cell.

**Current state.** QueryResultCollector.cs obtains GetFieldType, not the source SQL type.
AgentOutputProjection.ClipText clips a prefix and increments only an aggregate counter.

**Files:**
- Modify: src/SqlHarness.Core/AgentOutputProjection.cs (compact records, aliases, cell metadata)
- Modify: src/SqlHarness.Mcp/McpResultAdapter.cs and CLI agent-envelope version
- Test: tests/SqlHarness.Tests/Cli/AgentOutputTests.cs, tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs

**Interfaces:**
- Produces: AgentQueryColumn(string Name, string ValueType, bool AllowNull).
- Produces: AgentClippedCell(int Row, int Column, long OriginalLength, int ReturnedLength, string Unit).
  Row/column are zero-based within the projected set; Unit is utf16-code-units or bytes.
- Produces: AgentCompactResultSet(int Ordinal, IReadOnlyList<AgentQueryColumn> Columns,
  IReadOnlyList<IReadOnlyList<object?>> Rows, long RowCount, long OmittedRowCount,
  int OmittedColumnCount, IReadOnlyList<AgentClippedCell>? ClippedCells).
- Produces: AgentCompactQuery(string Database, string StatementClassification,
  IReadOnlyList<AgentCompactResultSet> ResultSets, int OmittedResultSetCount,
  IReadOnlyList<string?>? Messages, int? RecordsAffected, long DurationMilliseconds).
- Produces: internal static string ValueTypeName(string clrType); aliases name CLR values, not SQL types.

**Change.** Compact before the final response-budget test. Preserve result-set identity, column order and
nullability. Remove resultHash/rawFootprint/full target from query projection. Omit empty messages, zero
omission counters, absent clippedCells, and recordsAffected only when negative. Keep recordsAffected=0
and empty-result columns. Do not alter the canonical schema used by hashing or equivalence.
Provider SQL metadata is outside this task; never guess it from CLR names.

- [x] **Step 1: Write failing behavior tests**

Synthetic fixtures assert:
- System.DateTime -> dateTime, System.DateTimeOffset -> dateTimeOffset, System.String -> string,
  System.Int16 -> int16, System.Int32 -> int32, System.Int64 -> int64, System.Boolean -> boolean,
  System.Decimal -> decimal, System.Byte[] -> bytes. Unmapped types retain their CLR name.
- Two different source SQL date types with the same CLR type both report dateTime, never a guessed SQL type.
  Type shortening never changes the canonical schema, result hash or equivalence.
- Clipped strings remain string prefixes in rows; clippedCells identifies exact row/column and lengths.
  Surrogate boundaries are preserved and ReturnedLength is the actual returned prefix length.
- Clipped binary values retain the existing explicit binary-prefix representation; metadata uses bytes.
- Omitted rows have no clippedCells entries. Positions are local to each result set.
- Uncut values and nulls are unchanged; no clipping metadata appears for an uncut set.
- Empty results keep columns; recordsAffected=0 is present and -1 absent.
- A compact envelope that fits is retained even when the previous verbose envelope would not fit.

- [x] **Step 2: Run to verify failure**

Run: dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~AgentOutputTests".
Expected: compact types/aliases absent or clipping positions missing.

- [x] **Step 3: Implement**

Construct clipping metadata while projecting retained cells, then compact before the final fit check.
Use JsonIgnore defaults only for the named fields. Introduce one shared agent-envelope version constant
with value 2 for MCP and CLI; fitting and emitted-byte accounting use the same final serialization.

- [x] **Step 4: Verify and commit**

Run: dotnet test SqlHarness.sln --filter "FullyQualifiedName~AgentOutput|FullyQualifiedName~McpTokenBudget|FullyQualifiedName~Canonical|FullyQualifiedName~Equivalence".
Expected: PASS. Commit scoped source/tests with feat(055): compact query values with explicit clipping metadata.

**Acceptance.** Short types are explicitly CLR value types; no SQL type is fabricated. Every clipped
retained cell is addressable; default-field removal preserves genuine zero/empty-result information.

---

### Task 4: Compare matrix overview comes before repeated detail

**Evidence.** Projection can retain one rich comparison and omit other cases. Repeating targets,
classifications and metric explanations per cell spends budget before the agent can assess coverage.

**Current state.** AgentOutputProjection recursively projects cell.Compare using one detail limit.
There is no separate decision-oriented inventory of cases.

**Files:**
- Modify: src/SqlHarness.Core/AgentOutputProjection.cs, src/SqlHarness.Core/BenchmarkSummary.cs
- Modify: src/SqlHarness.Core/ArtifactReader.cs (case-inventory continuation)
- Test: tests/SqlHarness.Tests/Cli/AgentOutputTests.cs, tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs

**Interfaces:**
- Produces: AgentMatrixCase(int Index, string ArtifactId, bool? Equivalent,
  string EquivalenceAvailability, AgentMedianMetrics Baseline, AgentMedianMetrics Candidate,
  AgentMedianMetrics Delta, IReadOnlyList<string> Warnings).
- Produces: AgentMedianMetrics(decimal? CpuMilliseconds, decimal? ElapsedMilliseconds, long? LogicalReads).
  Delta is candidate minus baseline. Unavailable metrics and disabled equivalence stay null.
  EquivalenceAvailability is compared, not-requested or unavailable; it never certifies domain equivalence.
- Produces: matrix fields cases, details, caseCount, omittedCaseCount, omittedDetailCount,
  matrixArtifactId, continuation. Shared target, parameter definitions and method occur once.
- Consumes: completed cells and retained artifact ids; unexecuted cases have no invented outcomes.
  Existing partial-operation status/error remains authoritative.

**Change.** Serialize the shared envelope and minimal case inventory first. Spend the remaining budget on
per-table reads, distributions and operators. The detail ladder applies to optional detail first.
If the inventory itself cannot fit, page it in index order through the scope-owned artifact reader.
Keep individual-cell reads. Plan 058 adds visible parameter values; 061 adds labels additively.

- [x] **Step 1: Write failing tests**

Synthetic six-case matrix with large per-table/operator detail, budgets 4096 and 65536:
all cases survive whenever the shared envelope plus inventory fits, even when detail does not;
identities, medians, deltas and equivalence match source; unavailable CPU is null, compareResults=off
is not equivalent=true. A larger inventory pages each case exactly once. A partial run preserves
root status/error and contains outcomes only for completed cases.

- [x] **Step 2: Run to verify failure**

Run: dotnet test SqlHarness.sln --filter "FullyQualifiedName~AgentOutput|FullyQualifiedName~McpTokenBudget".

- [x] **Step 3: Implement**

Add inventory-first matrix projection; spend remaining budget on optional detail. Preserve scope checks.
Preserve existing latency-regression diagnostics in case details. Do not add an adoption verdict: a metrics overview is not plan review. Update pins of the old nested
cell format. The inventory and its artifact continuation share one schema.

- [x] **Step 4: Verify and commit**

Run the Step 2 filter; expected PASS. Commit feat(055): matrix case inventory before optional detail.

**Acceptance.** Every completed case fitting as inventory is visible; detail omission cannot be mistaken
for an experiment that executed only one case.

---
### Task 5: Statements section pages with a cursor

**Files:**
- Modify: `src/SqlHarness.Core/ArtifactReader.cs` (`ReadSection` cursor rule `:153-154`, `ReadStatements`, `ArtifactStatementsSection` record `:78-82`)
- Modify: `src/SqlHarness.Core/AgentOutputProjection.cs:283-302` (continuation)
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `McpRequestToolHandlers.cs:147` (cursor description)
- Test: `tests/SqlHarness.Tests/Cli/ArtifactCommandTests.cs` (or the artifact reader tests next to it)

**Interfaces:**
- Produces: `ArtifactStatementsSection(..., int OmittedStatements) { int? Continuation { get; init; } }`; `ReadSection(..., cursor)` accepts a cursor for `statements` and `matrix-cells`.

- [x] **Step 1: Write the failing test**

Using the existing artifact fixture builder in the artifact tests (search `StatementsFileName` or `statements.json` in `tests/`), write an artifact with 300 statements and assert:

```csharp
var first = (ArtifactStatementsSection)ArtifactReader.ReadSection(root, id, "statements");
var projected = (ArtifactStatementsSection)AgentOutputProjection.Project(first, 512, 128, out _, 65536)!;
Assert.Equal(128, projected.Statements.Count);
Assert.Equal(128, projected.Continuation);

var second = (ArtifactStatementsSection)ArtifactReader.ReadSection(root, id, "statements", cursor: 128);
Assert.Equal(128, second.Statements[0].StatementOrdinal);
```

Use the ordinal property name the record has on HEAD.

- [x] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Artifact"`
Expected: FAIL — "The artifact cursor is invalid."

- [x] **Step 3: Implement**

`ReadSection`: allow a cursor when `section is MatrixCellsSection or StatementsSection`. `ReadStatements(directory, artifactId, manifest, cursor ?? 0)` skips `cursor` statements and records `Cursor`. Add to the record:

```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public int? Continuation { get; init; }
[JsonIgnore]
public int Cursor { get; init; }
```

In the projection case set `Continuation = selected.Length < statements.Statements.Count ? statements.Cursor + selected.Length : null`. Update the cursor parameter description: `"Zero-based continuation cursor for matrix-cells and statements pages."`.

- [x] **Step 4: Run and commit**

Run: `dotnet test SqlHarness.sln --filter "FullyQualifiedName~Artifact"` → PASS.

```bash
git add src tests
git commit -m "feat(055): statements artifact section pages with a cursor"
```

---

### Task 6: Documentation and gates

**Files:**
- Modify: `docs/mcp.md` (Start, Tools table note, Result envelope/budgets, query fallback paragraph, Context cost), `README.md` (agent output, budgets), `skills/sqlharness/SKILL.md`, `tests/SqlHarness.Tests/DocsSyncTests.cs` pins if they quote changed text.

- [x] **Step 1: Update docs**

State: one text block; budget = UTF-8 bytes of that text, default 65536, ceiling `--max-result-bytes` default 262144, a call may raise or lower within the ceiling; ladder `unbounded, 128, …, 1, 0` for every report; per-table reads ordered by median reads; compact query valueType and clippedCells fields; inventory-first matrix projection; `schemaVersion` 2; `statements` paging. Remove every claim about `structuredContent`, `outputSchema`, "both representations" and the 16384 default. In "Context cost", state that emitted bytes are the text the model reads.

- [x] **Step 2: Run gates**

Run: `dotnet build SqlHarness.sln -warnaserror` → 0 warnings. `pwsh scripts/verify.ps1` → `verify: OK`. `pwsh scripts/verify-linux.ps1` → `verify-linux: OK`.

- [x] **Step 3: Commit and index**

Set this plan's Status to DONE with the commit range; update the row in `plans/README.md`.

```bash
git add docs README.md skills tests plans
git commit -m "docs(055): text-only results, 64 KiB budget, stepped lists, compact query envelope"
```

## Done criteria

- [x] Removed MCP output schema and structured content assignments; removed obsolete detail-limit and old call-budget symbols. The source check treats names as exact symbols, so the required `DefaultCallToolResultBudgetBytes` is not a false positive for the removed `CallToolResultBudgetBytes` name.
- [x] A two-cell compare matrix and a 40 KB query report arrive untruncated under the default budget.
- [x] Per-table reads list the largest table first.
- [x] Matrix inventories precede detail; clipping identifies exact cells without guessing SQL types.
- [x] Both gates pass; README row updated.

## STOP conditions

- The SDK version on HEAD requires `structuredContent` for a capability SQLHarness advertises (for example a negotiated protocol revision that makes it mandatory). Report the revision and stop before removing it.
- A client in `docs/mcp.md` "Client configuration" is documented to read only `structuredContent`. Stop and ask the operator.
