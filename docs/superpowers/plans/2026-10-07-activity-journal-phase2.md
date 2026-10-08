# Activity Journal — Phase 2 (metrics capture) Implementation Plan

**Status (2026-10-08):** DONE — benchmark metrics in the journal on main (6105c8c, 75e693b, 0fdc9ec, 257fae7). Checkboxes below were not maintained during execution.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** For every successful `measure` (including `--param-set`), `compare`, and `compare --matrix`, the activity journal stores per-variant performance metrics, full per-table `STATISTICS IO`, plan-derived diagnostics, and plan identity hashes. With `journal.storeSensitive`, it also stores the deduplicated, gzip-compressed full plans. Agent-visible output stays byte-identical.

**Architecture:**
- The SQL Server dialect additionally parses every `STATISTICS IO` counter into a new non-serialized property on `CompareRunArtifact`.
- The module attaches the measured run artifacts it already has to the outcome through a new internal, non-serialized `SqlHarnessOutcome.BenchmarkRuns`.
- After completing the row, `JournalingModule` turns those runs into a `BenchmarkJournalRecord` with a pure builder (grouping, medians, plan metrics extraction) and passes it to the new `IActivityJournal.RecordBenchmark`.
- `ActivityJournal` writes it into four new tables (schema v2) and stores full plans only when `storeSensitive` is on.

**Tech Stack:** .NET 8, C#, `Microsoft.Data.Sqlite` 10.0.8, `System.IO.Compression`, `System.Xml.Linq`, `System.Text.Json`, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md` ("Data model", "Metrics capture changes", "Implementation phases → 2"). Phase 1 plan, for the existing journal code: `docs/superpowers/plans/2026-10-06-activity-journal-phase1.md`.

## Global Constraints

- Target framework stays `net8.0`. `TreatWarningsAsErrors` is on, and `dotnet format --verify-no-changes` must pass.
- Agent-visible output (CLI human, `--json`, `--json-summary`, MCP results), artifact files (`report.json`, `runs.jsonl`, `manifest.json`, plan files), and exit codes are byte-identical with the journal on or off. New fields on `CompareRunArtifact` and `SqlHarnessOutcome` are internal and carry `[JsonIgnore]`.
- Journal writes stay best-effort. A failure in building or writing benchmark data never changes the outcome. The existing one-line-per-process stderr warning is the only diagnostic.
- Never stored: `--param` / `--param-set` values, `compare --matrix` values (`CompareMatrixCellReport.ParameterValue`), passwords, tokens, connection strings, result cells, plan predicates, statement text (outside the `plans` table).
- Full plan documents go to the `plans` table only when `journal.storeSensitive` is `true`. Plan identity hashes (`PlanIdentity.Hash`) and every numeric metric are stored regardless.
- Metric numbers that the run marks as unavailable (truncated `STATISTICS` messages, Postgres CPU) are stored as `NULL`, never as `0`.
- Plan parsing for the journal prohibits DTDs, uses no resolver, caps input at 16 MiB, and never throws out of the extractor.
- Both gates must be green before the work is done: `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1`.

## Review Focus

1. **A `compare --matrix` value** (for example a tenant id) must never reach `activity.db`, whether through `summary_json`, metrics, or table names. Cells are identified by index only. Pinned by Task 6, `Matrix_values_never_reach_the_database`.
2. **Gzip hides markers from byte scans.** A sensitivity test that only scans bytes would pass even if plans were wrongly stored. The hash-only test must also assert the `plans` table is empty. Pinned by Task 6, `Hash_only_mode_stores_metrics_but_no_plans_or_sql`.
3. **Truncated `STATISTICS` output** (message budget exceeded) must store `NULL` reads and no table IO rather than zeros that look measured. Pinned by Task 4, `Unavailable_metrics_are_null_and_table_io_is_skipped`.
4. **A malformed, DTD-bearing, or oversized plan document** must yield empty plan metrics, never an exception that loses the whole benchmark record. Pinned by Task 2, `Malformed_or_dtd_plans_yield_empty_metrics`.
5. **A v1 journal from phase 1** must migrate in place to v2 and keep its rows. Pinned by Task 5, `Version_1_database_migrates_to_version_2_and_keeps_rows`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/SqlHarness.Core/Diagnostics.cs` (modify) | `TableIoCounters`, `StatisticsIoDetailParser` |
| `src/SqlHarness.Core/Artifacts.cs` (modify) | `CompareRunArtifact.TableIo`, `CompareRunArtifact.MatrixCell` |
| `src/SqlHarness.Core/Dialect/SqlServerDialect.cs` (modify) | Fill `TableIo` |
| `src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs` (new) | Showplan XML / EXPLAIN JSON → `PlanMetrics` |
| `src/SqlHarness.Core/Contracts.cs` (modify) | `SqlHarnessOutcome.BenchmarkRuns` |
| `src/SqlHarness.Core/SqlHarnessModule.cs`, `CompareMatrixRunner.cs` (modify) | Attach runs to successful outcomes |
| `src/SqlHarness.Core/Journal/BenchmarkJournalRecord.cs` (new) | Public journal data records |
| `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs` (new) | Runs → `BenchmarkJournalRecord` |
| `src/SqlHarness.Core/Journal/JournalSummary.cs` (new) | Report → bounded `summary_json` |
| `src/SqlHarness.Core/Journal/JournalSchema.cs`, `ActivityJournal.cs`, `IActivityJournal.cs`, `JournalModels.cs`, `OperationJournalDescriber.cs`, `JournalingModule.cs` (modify) | v2 schema, writes, wiring |
| `tests/SqlHarness.Tests/Journal/*` (new and modified), `tests/SqlHarness.Tests/{Measure,Compare,CompareMatrix}Tests.cs` (modify) | Tests |
| `AGENTS.md`, `README.md`, spec (modify) | Documentation |

---

### Task 1: Full `STATISTICS IO` counters per table

**Files:**
- Modify: `src/SqlHarness.Core/Diagnostics.cs`, `src/SqlHarness.Core/Artifacts.cs`, `src/SqlHarness.Core/Dialect/SqlServerDialect.cs`
- Test: `tests/SqlHarness.Tests/StatisticsIoDetailParserTests.cs` (new)

**Interfaces:**
- Produces:
  - `internal sealed record TableIoCounters(string Table, long ScanCount, long LogicalReads, long PhysicalReads, long PageServerReads, long ReadAheadReads, long LobLogicalReads, long LobPhysicalReads, long LobReadAheadReads)`
  - `internal static class StatisticsIoDetailParser { IReadOnlyList<TableIoCounters> Parse(string text); }`
  - On `CompareRunArtifact`: `[JsonIgnore] public IReadOnlyList<TableIoCounters> TableIo { get; init; } = [];` and `[JsonIgnore] public int? MatrixCell { get; init; }` (`MatrixCell` is set in Task 3).

- [ ] **Step 1: Write the failing parser tests**

`tests/SqlHarness.Tests/StatisticsIoDetailParserTests.cs`:

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class StatisticsIoDetailParserTests
{
    [Fact]
    public void Modern_line_with_page_server_counters_is_fully_parsed()
    {
        const string text = "Table 'Orders'. Scan count 3, logical reads 120, physical reads 2, page server reads 0, read-ahead reads 40, page server read-ahead reads 0, lob logical reads 7, lob physical reads 1, lob page server reads 0, lob read-ahead reads 5, lob page server read-ahead reads 0.";

        var table = Assert.Single(StatisticsIoDetailParser.Parse(text));

        Assert.Equal(new TableIoCounters("Orders", 3, 120, 2, 0, 40, 7, 1, 5), table);
    }

    [Fact]
    public void Legacy_line_without_page_server_counters_defaults_missing_to_zero()
    {
        const string text = "Table 'Clients'. Scan count 1, logical reads 5, physical reads 0, lob logical reads 0.";

        Assert.Equal(new TableIoCounters("Clients", 1, 5, 0, 0, 0, 0, 0, 0), Assert.Single(StatisticsIoDetailParser.Parse(text)));
    }

    [Fact]
    public void Worktables_quoted_names_and_repeated_tables_are_handled()
    {
        const string text = """
            Table 'Worktable'. Scan count 0, logical reads 0, physical reads 0, read-ahead reads 0.
            Table 'O''Brien'. Scan count 1, logical reads 4, physical reads 1, read-ahead reads 0.
            SQL Server Execution Times: CPU time = 1 ms, elapsed time = 2 ms.
            Table 'O''Brien'. Scan count 2, logical reads 6, physical reads 0, read-ahead reads 3.
            """;

        var tables = StatisticsIoDetailParser.Parse(text);

        Assert.Equal(["Worktable", "O'Brien"], tables.Select(table => table.Table));
        Assert.Equal(new TableIoCounters("O'Brien", 3, 10, 1, 0, 3, 0, 0, 0), tables[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SQL Server parse and compile time: CPU time = 0 ms.")]
    [InlineData("Table 'Broken'. Scan count x, logical reads.")]
    public void Text_without_counters_yields_no_rows_or_zero_rows(string text)
    {
        var tables = StatisticsIoDetailParser.Parse(text);

        Assert.All(tables, table => Assert.Equal(0, table.LogicalReads));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~StatisticsIoDetailParserTests`
Expected: build FAILS with `The name 'StatisticsIoDetailParser' does not exist`.

- [ ] **Step 3: Implement the parser**

Append to `src/SqlHarness.Core/Diagnostics.cs`, after `StatisticsIoParser`. The existing parser stays unchanged because it feeds agent reports.

```csharp
internal sealed record TableIoCounters(
    string Table,
    long ScanCount,
    long LogicalReads,
    long PhysicalReads,
    long PageServerReads,
    long ReadAheadReads,
    long LobLogicalReads,
    long LobPhysicalReads,
    long LobReadAheadReads);

/// <summary>
/// Every STATISTICS IO counter per table, for the activity journal only.
/// Missing counters (older servers) read as zero; a table repeated across
/// statements is summed. Agent reports keep using <see cref="StatisticsIoParser"/>.
/// </summary>
internal static class StatisticsIoDetailParser
{
    private static readonly Regex TableLine = new(
        @"Table\s+'(?<table>(?:''|[^'])+)'\.(?<counters>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Counter = new(
        @"^(?<name>[A-Za-z][A-Za-z -]*?)\s+(?<value>\d+)\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<TableIoCounters> Parse(string text)
    {
        var tables = new Dictionary<string, TableIoCounters>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (Match line in TableLine.Matches(text))
        {
            var table = line.Groups["table"].Value.Replace("''", "'", StringComparison.Ordinal);
            var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in line.Groups["counters"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Counter.Match(segment);
                if (match.Success && long.TryParse(match.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    values[match.Groups["name"].Value.Trim()] = value;
            }

            long Get(string name) => values.GetValueOrDefault(name);
            var counters = new TableIoCounters(
                table,
                Get("scan count"),
                Get("logical reads"),
                Get("physical reads"),
                Get("page server reads"),
                Get("read-ahead reads"),
                Get("lob logical reads"),
                Get("lob physical reads"),
                Get("lob read-ahead reads"));
            if (tables.TryGetValue(table, out var existing))
            {
                tables[table] = existing with
                {
                    ScanCount = existing.ScanCount + counters.ScanCount,
                    LogicalReads = existing.LogicalReads + counters.LogicalReads,
                    PhysicalReads = existing.PhysicalReads + counters.PhysicalReads,
                    PageServerReads = existing.PageServerReads + counters.PageServerReads,
                    ReadAheadReads = existing.ReadAheadReads + counters.ReadAheadReads,
                    LobLogicalReads = existing.LobLogicalReads + counters.LobLogicalReads,
                    LobPhysicalReads = existing.LobPhysicalReads + counters.LobPhysicalReads,
                    LobReadAheadReads = existing.LobReadAheadReads + counters.LobReadAheadReads,
                };
            }
            else
            {
                tables[table] = counters;
                order.Add(table);
            }
        }

        return order.Select(table => tables[table]).ToArray();
    }
}
```

- [ ] **Step 4: Add the artifact properties**

`src/SqlHarness.Core/Artifacts.cs`. Give `CompareRunArtifact` a body. Keep the positional parameters exactly as they are, then add:

```csharp
internal sealed record CompareRunArtifact(
    string Variant, int Repetition, long CpuTimeMilliseconds, long ElapsedTimeMilliseconds,
    long LogicalReads, IReadOnlyDictionary<string, long> LogicalReadsByTable,
    string ResultHash, IReadOnlyList<string> PlanXmls, int MessageCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParameterSet = null,
    BenchmarkRunMetrics? Metrics = null)
{
    /// <summary>Every STATISTICS IO counter per table (SQL Server). Journal only; never serialized.</summary>
    [JsonIgnore]
    public IReadOnlyList<TableIoCounters> TableIo { get; init; } = [];

    /// <summary>Zero-based compare --matrix cell index; never the matrix value. Journal only; never serialized.</summary>
    [JsonIgnore]
    public int? MatrixCell { get; init; }
}
```

- [ ] **Step 5: Fill `TableIo` in the SQL Server dialect**

`src/SqlHarness.Core/Dialect/SqlServerDialect.cs`, in `ExecuteBenchmarkRunAsync`. Replace the two lines that parse `io` and `time` and the `new CompareRunArtifact(...)` expression with:

```csharp
            var statistics = string.Join(Environment.NewLine, messages);
            var io = StatisticsIoParser.Parse(statistics);
            var time = StatisticsTimeParser.Parse(statistics);
            var plans = result.PlanXmls.Select(ExecutionPlanParser.Parse).ToArray();
            var artifact = new CompareRunArtifact(
                variant,
                repetition,
                time.CpuTimeMs,
                time.ElapsedTimeMs,
                io.LogicalReads,
                io.Tables,
                result.Canonical.Hash,
                result.PlanXmls,
                messages.Length,
                Metrics: TruncatedMetricsOrNull(consumed.OmittedMessageCount))
            {
                // Truncated message windows make the counters partial; the journal then records none.
                TableIo = consumed.OmittedMessageCount > 0 ? [] : StatisticsIoDetailParser.Parse(statistics),
            };
```

- [ ] **Step 6: Run the parser tests**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~StatisticsIoDetailParserTests`
Expected: all PASS. The dialect wiring (`TableIo` filled for real measure runs) is asserted in Task 3, once runs are reachable from the outcome.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Core/Diagnostics.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/Dialect/SqlServerDialect.cs tests/SqlHarness.Tests/StatisticsIoDetailParserTests.cs
git commit -m "Parse every STATISTICS IO counter for the journal"
```

---

### Task 2: Plan metrics extractor

**Files:**
- Create: `src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs`
- Test: `tests/SqlHarness.Tests/Journal/PlanMetricsExtractorTests.cs`

**Interfaces:**
- Produces (public, because Task 4 puts them on the public journal record):
  - `public sealed record PlanWait(string WaitType, long WaitTimeMs, long WaitCount)`
  - `public sealed record PostgresBufferCounters(long SharedHit, long SharedRead, long SharedDirtied, long SharedWritten, long TempRead, long TempWritten)`
  - `public sealed record PlanMetrics(long? GrantRequestedKb, long? GrantGrantedKb, long? GrantMaxUsedKb, int? Dop, long? CompileTimeMs, long? CompileCpuMs, int SpillCount, bool HasWarnings, bool HasImplicitConversion, int MissingIndexCount, IReadOnlyList<PlanWait> Waits, PostgresBufferCounters? Postgres)` with `static PlanMetrics Empty`
  - `internal static class PlanMetricsExtractor { PlanMetrics Extract(string document); PlanMetrics Extract(IEnumerable<string> documents); }`

- [ ] **Step 1: Write the failing tests**

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class PlanMetricsExtractorTests
{
    internal const string ActualPlan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.564" Build="16.0.1000.6">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT * FROM dbo.Orders WHERE Note = 'SQLH_SQL_MARKER'" StatementType="SELECT">
              <QueryPlan DegreeOfParallelism="4" MemoryGrant="4096" CachedPlanSize="40" CompileTime="12" CompileCPU="10" CompileMemory="300">
                <MissingIndexes>
                  <MissingIndexGroup Impact="80.5">
                    <MissingIndex Database="[db]" Schema="[dbo]" Table="[Orders]">
                      <ColumnGroup Usage="EQUALITY"><Column Name="[CustomerId]" ColumnId="2" /></ColumnGroup>
                    </MissingIndex>
                  </MissingIndexGroup>
                </MissingIndexes>
                <MemoryGrantInfo SerialRequiredMemory="512" SerialDesiredMemory="1024" RequiredMemory="1024" DesiredMemory="4096" RequestedMemory="4096" GrantWaitTime="0" GrantedMemory="4096" MaxUsedMemory="256" MaxQueryMemory="100000" />
                <WaitStats>
                  <Wait WaitType="PAGEIOLATCH_SH" WaitTimeMs="40" WaitCount="7" />
                  <Wait WaitType="CXPACKET" WaitTimeMs="15" WaitCount="3" />
                </WaitStats>
                <QueryTimeStats CpuTime="30" ElapsedTime="80" />
                <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="Sort">
                  <Warnings><SpillToTempDb SpillLevel="1" SpilledThreadCount="1" /></Warnings>
                  <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan">
                    <Warnings><PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(nvarchar(50),[Note],0)" /></Warnings>
                  </RelOp>
                </RelOp>
                <ParameterList><ColumnReference Column="@p" ParameterCompiledValue="N'SQLH_PARAM_MARKER'" /></ParameterList>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    internal const string ExplainJson = """
        [{ "Plan": { "Node Type": "Gather", "Workers Launched": 2,
                     "Shared Hit Blocks": 100, "Shared Read Blocks": 20, "Shared Dirtied Blocks": 1,
                     "Shared Written Blocks": 0, "Temp Read Blocks": 5, "Temp Written Blocks": 6,
                     "Plans": [ { "Node Type": "Sort", "Sort Space Type": "Disk",
                                  "Plans": [ { "Node Type": "Hash", "Hash Batches": 4 } ] } ] },
           "Planning Time": 0.5, "Execution Time": 12.0 }]
        """;

    [Fact]
    public void Showplan_metrics_are_extracted()
    {
        var metrics = PlanMetricsExtractor.Extract(ActualPlan);

        Assert.Equal(4096, metrics.GrantRequestedKb);
        Assert.Equal(4096, metrics.GrantGrantedKb);
        Assert.Equal(256, metrics.GrantMaxUsedKb);
        Assert.Equal(4, metrics.Dop);
        Assert.Equal(12, metrics.CompileTimeMs);
        Assert.Equal(10, metrics.CompileCpuMs);
        Assert.Equal(1, metrics.SpillCount);
        Assert.True(metrics.HasWarnings);
        Assert.True(metrics.HasImplicitConversion);
        Assert.Equal(1, metrics.MissingIndexCount);
        Assert.Equal([new PlanWait("PAGEIOLATCH_SH", 40, 7), new PlanWait("CXPACKET", 15, 3)], metrics.Waits);
        Assert.Null(metrics.Postgres);
    }

    [Fact]
    public void Extracted_metrics_carry_no_statement_text_or_parameter_values()
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(PlanMetricsExtractor.Extract(ActualPlan));

        Assert.DoesNotContain("SQLH_SQL_MARKER", serialized);
        Assert.DoesNotContain("SQLH_PARAM_MARKER", serialized);
        Assert.DoesNotContain("CONVERT_IMPLICIT", serialized);
    }

    [Fact]
    public void Explain_json_metrics_are_extracted()
    {
        var metrics = PlanMetricsExtractor.Extract(ExplainJson);

        Assert.Equal(new PostgresBufferCounters(100, 20, 1, 0, 5, 6), metrics.Postgres);
        Assert.Equal(3, metrics.Dop);
        Assert.Equal(2, metrics.SpillCount);
        Assert.Null(metrics.GrantGrantedKb);
        Assert.Empty(metrics.Waits);
    }

    [Fact]
    public void Multiple_documents_are_combined()
    {
        var metrics = PlanMetricsExtractor.Extract([ActualPlan, ActualPlan]);

        Assert.Equal(8192, metrics.GrantGrantedKb);
        Assert.Equal(4, metrics.Dop);
        Assert.Equal(2, metrics.SpillCount);
        Assert.Equal(2, metrics.MissingIndexCount);
        Assert.Equal(new PlanWait("PAGEIOLATCH_SH", 80, 14), metrics.Waits[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<ShowPlanXML")]
    [InlineData("{ not json")]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"boom\">]><ShowPlanXML>&e;</ShowPlanXML>")]
    public void Malformed_or_dtd_plans_yield_empty_metrics(string document) =>
        Assert.Equal(PlanMetrics.Empty, PlanMetricsExtractor.Extract(document));

    [Fact]
    public void Plan_without_runtime_elements_has_null_grant_and_no_waits()
    {
        const string estimated = """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><RelOp NodeId="0" PhysicalOp="Index Seek" /></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

        var metrics = PlanMetricsExtractor.Extract(estimated);

        Assert.Null(metrics.GrantGrantedKb);
        Assert.Null(metrics.Dop);
        Assert.Empty(metrics.Waits);
        Assert.Equal(0, metrics.SpillCount);
    }
}
```

`PlanMetrics.Empty` equality with `Assert.Equal` compares the `Waits` list by reference. Make `Empty` a single static instance and return that same instance on every failure path, so `Assert.Equal` succeeds through reference equality of the list.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~PlanMetricsExtractorTests`
Expected: build FAILS with `The name 'PlanMetricsExtractor' does not exist`.

- [ ] **Step 3: Implement**

`src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SqlHarness.Core;

public sealed record PlanWait(string WaitType, long WaitTimeMs, long WaitCount);

public sealed record PostgresBufferCounters(
    long SharedHit, long SharedRead, long SharedDirtied, long SharedWritten, long TempRead, long TempWritten);

/// <summary>Numeric plan diagnostics only: no statement text, predicates, or parameter values.</summary>
public sealed record PlanMetrics(
    long? GrantRequestedKb,
    long? GrantGrantedKb,
    long? GrantMaxUsedKb,
    int? Dop,
    long? CompileTimeMs,
    long? CompileCpuMs,
    int SpillCount,
    bool HasWarnings,
    bool HasImplicitConversion,
    int MissingIndexCount,
    IReadOnlyList<PlanWait> Waits,
    PostgresBufferCounters? Postgres)
{
    public static PlanMetrics Empty { get; } = new(null, null, null, null, null, null, 0, false, false, 0, [], null);
}

/// <summary>
/// Journal-only plan diagnostics from actual Showplan XML (SQL Server) or
/// EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) (Postgres). Never throws: an
/// unreadable document yields <see cref="PlanMetrics.Empty"/>.
/// </summary>
internal static class PlanMetricsExtractor
{
    internal const int MaximumCharacters = 16 * 1024 * 1024;

    internal static PlanMetrics Extract(IEnumerable<string> documents)
    {
        var parts = documents.Select(Extract).Where(part => !ReferenceEquals(part, PlanMetrics.Empty)).ToArray();
        if (parts.Length == 0)
            return PlanMetrics.Empty;
        if (parts.Length == 1)
            return parts[0];

        var waits = parts.SelectMany(part => part.Waits)
            .GroupBy(wait => wait.WaitType, StringComparer.Ordinal)
            .Select(group => new PlanWait(group.Key, group.Sum(wait => wait.WaitTimeMs), group.Sum(wait => wait.WaitCount)))
            .OrderByDescending(wait => wait.WaitTimeMs).ThenBy(wait => wait.WaitType, StringComparer.Ordinal)
            .ToArray();
        var postgres = parts.Select(part => part.Postgres).OfType<PostgresBufferCounters>().ToArray();
        return new PlanMetrics(
            SumOrNull(parts.Select(part => part.GrantRequestedKb)),
            SumOrNull(parts.Select(part => part.GrantGrantedKb)),
            SumOrNull(parts.Select(part => part.GrantMaxUsedKb)),
            parts.Max(part => part.Dop),
            SumOrNull(parts.Select(part => part.CompileTimeMs)),
            SumOrNull(parts.Select(part => part.CompileCpuMs)),
            parts.Sum(part => part.SpillCount),
            parts.Any(part => part.HasWarnings),
            parts.Any(part => part.HasImplicitConversion),
            parts.Sum(part => part.MissingIndexCount),
            waits,
            postgres.Length == 0 ? null : new PostgresBufferCounters(
                postgres.Sum(p => p.SharedHit), postgres.Sum(p => p.SharedRead), postgres.Sum(p => p.SharedDirtied),
                postgres.Sum(p => p.SharedWritten), postgres.Sum(p => p.TempRead), postgres.Sum(p => p.TempWritten)));
    }

    internal static PlanMetrics Extract(string document)
    {
        if (string.IsNullOrWhiteSpace(document) || document.Length > MaximumCharacters)
            return PlanMetrics.Empty;
        try
        {
            var trimmed = document.AsSpan().TrimStart();
            return trimmed[0] is '{' or '[' ? ExtractExplain(document) : ExtractShowplan(document);
        }
        catch (Exception exception) when (exception is XmlException or JsonException or FormatException
            or OverflowException or InvalidOperationException or KeyNotFoundException)
        {
            return PlanMetrics.Empty;
        }
    }

    private static PlanMetrics ExtractShowplan(string document)
    {
        using var reader = XmlReader.Create(new StringReader(document), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters,
        });
        var root = XDocument.Load(reader);
        var elements = root.Descendants().ToArray();
        XElement[] Named(string localName) => elements.Where(element => element.Name.LocalName == localName).ToArray();

        var queryPlans = Named("QueryPlan");
        var grants = Named("MemoryGrantInfo");
        var waits = Named("Wait")
            .Where(wait => wait.Parent?.Name.LocalName == "WaitStats")
            .GroupBy(wait => Attribute(wait, "WaitType") ?? "UNKNOWN", StringComparer.Ordinal)
            .Select(group => new PlanWait(
                group.Key,
                group.Sum(wait => Long(wait, "WaitTimeMs") ?? 0),
                group.Sum(wait => Long(wait, "WaitCount") ?? 0)))
            .OrderByDescending(wait => wait.WaitTimeMs).ThenBy(wait => wait.WaitType, StringComparer.Ordinal)
            .ToArray();
        return new PlanMetrics(
            SumOrNull(grants.Select(grant => Long(grant, "RequestedMemory"))),
            SumOrNull(grants.Select(grant => Long(grant, "GrantedMemory"))),
            SumOrNull(grants.Select(grant => Long(grant, "MaxUsedMemory"))),
            queryPlans.Select(plan => (int?)Long(plan, "DegreeOfParallelism")).Max(),
            SumOrNull(queryPlans.Select(plan => Long(plan, "CompileTime"))),
            SumOrNull(queryPlans.Select(plan => Long(plan, "CompileCPU"))),
            Named("SpillToTempDb").Length,
            Named("Warnings").Length > 0,
            Named("PlanAffectingConvert").Length > 0
                || elements.Any(element => element.Attributes().Any(attribute =>
                    attribute.Value.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase))),
            Named("MissingIndexGroup").Length,
            waits,
            null);
    }

    private static PlanMetrics ExtractExplain(string document)
    {
        using var json = JsonDocument.Parse(document, new JsonDocumentOptions { MaxDepth = 256 });
        var root = json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0
            ? json.RootElement[0]
            : json.RootElement;
        if (!root.TryGetProperty("Plan", out var plan))
            return PlanMetrics.Empty;

        var spills = 0;
        var workers = 0;
        Walk(plan);
        void Walk(JsonElement node)
        {
            if (Text(node, "Sort Space Type") == "Disk")
                spills++;
            if (Number(node, "Hash Batches") > 1)
                spills++;
            workers = Math.Max(workers, (int)Number(node, "Workers Launched"));
            if (node.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                    Walk(child);
            }
        }

        return new PlanMetrics(
            null, null, null,
            workers > 0 ? workers + 1 : null,
            null, null,
            spills,
            plan.TryGetProperty("Warnings", out _),
            false,
            0,
            [],
            new PostgresBufferCounters(
                Number(plan, "Shared Hit Blocks"), Number(plan, "Shared Read Blocks"),
                Number(plan, "Shared Dirtied Blocks"), Number(plan, "Shared Written Blocks"),
                Number(plan, "Temp Read Blocks"), Number(plan, "Temp Written Blocks")));
    }

    private static long? SumOrNull(IEnumerable<long?> values)
    {
        long? sum = null;
        foreach (var value in values)
        {
            if (value is { } number)
                sum = (sum ?? 0) + number;
        }

        return sum;
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    private static long? Long(XElement element, string name) =>
        Attribute(element, name) is { } value && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~PlanMetricsExtractorTests`
Expected: all PASS. If the DTD case throws instead of returning `Empty`, add the thrown exception type to the `catch` filter. Do not catch `Exception`.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs tests/SqlHarness.Tests/Journal/PlanMetricsExtractorTests.cs
git commit -m "Extract numeric plan diagnostics for the journal"
```

---

### Task 3: Expose measured runs on successful outcomes

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`, `src/SqlHarness.Core/SqlHarnessModule.cs`, `src/SqlHarness.Core/CompareMatrixRunner.cs`
- Test: new facts in `tests/SqlHarness.Tests/MeasureTests.cs`, `CompareTests.cs`, `CompareMatrixTests.cs`

**Interfaces:**
- Consumes: `CompareRunArtifact.TableIo`, `CompareRunArtifact.MatrixCell` (Task 1).
- Produces:
  - `SqlHarnessOutcome`: `[JsonIgnore] internal IReadOnlyList<CompareRunArtifact>? BenchmarkRuns { get; init; }`, the measured (non-warm-up) runs of a successful benchmark, in execution order.
  - `CompareMatrixResult`: `internal IReadOnlyList<CompareRunArtifact> Runs { get; init; } = [];` every cell's runs with `MatrixCell` set.

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Tests/MeasureTests.cs` (class `SqlHarnessMeasureTests`), add these three facts:

```csharp
    [Fact]
    public async Task Measure_runs_carry_full_table_io_for_the_journal()
    {
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure(3));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var runs = outcome.BenchmarkRuns!;
        Assert.Equal(3, runs.Count);
        Assert.All(runs, run =>
        {
            var table = Assert.Single(run.TableIo);
            Assert.Equal("Clients", table.Table);
            Assert.Equal(1, table.ScanCount);
            Assert.Equal(run.LogicalReads, table.LogicalReads);
        });
    }

    [Fact]
    public async Task Measure_parameter_sets_expose_runs_with_set_names()
    {
        var session = FakeMeasureSession.Create();

        var outcome = await Module(session).ExecuteAsync(Measure(2) with
        {
            SetupSql = null,
            QuerySql = "SELECT @n AS Value",
            ParameterSets =
            [
                new SqlHarnessParameterSetInput("small", ["n:int=7"]),
                new SqlHarnessParameterSetInput("large", ["n:int=8"]),
            ],
        });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(4, outcome.BenchmarkRuns!.Count);
        Assert.Equal(["large", "small", "small", "large"], outcome.BenchmarkRuns.Select(run => run.ParameterSet));
        Assert.All(outcome.BenchmarkRuns, run => Assert.Null(run.MatrixCell));
    }

    [Fact]
    public async Task Failed_measure_exposes_no_runs()
    {
        var outcome = await Module(FakeMeasureSession.Create()).ExecuteAsync(Measure(1) with { QuerySql = "DELETE FROM dbo.Clients" });

        Assert.NotEqual(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Null(outcome.BenchmarkRuns);
    }
```

The expected set order `["large", "small", "small", "large"]` follows the rotation rule (round r starts at index r mod setCount). If `FakeMeasureSession` cannot answer `SELECT @n AS Value`, use the query text from the existing successful measure tests and keep the parameter sets. If the fake rejects parameter sets entirely, **STOP** and report.

`CompareTests.cs`:

```csharp
    [Fact]
    public async Task Compare_exposes_measured_runs_of_both_variants()
    {
        var session = FakeCompareSession.Create();

        var outcome = await Module(session).ExecuteAsync(Compare(repeat: 2));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(["baseline", "candidate", "candidate", "baseline"], outcome.BenchmarkRuns!.Select(run => run.Variant));
        Assert.All(outcome.BenchmarkRuns, run => Assert.NotEmpty(run.TableIo));
    }
```

`CompareMatrixTests.cs`:

```csharp
    [Fact]
    public async Task Matrix_exposes_runs_with_cell_indexes_only()
    {
        using var artifacts = new DirectoryArtifactWriter();

        var outcome = await Module(new MatrixSessionFactory(), artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100"));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var runs = outcome.BenchmarkRuns!;
        Assert.Equal([0, 1, 2], runs.Select(run => run.MatrixCell!.Value).Distinct());
        Assert.All(runs, run => Assert.Contains(run.Variant, new[] { "baseline", "candidate" }));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~exposes|FullyQualifiedName~Measure_runs_carry_full_table_io"`
Expected: build FAILS with `'SqlHarnessOutcome' does not contain a definition for 'BenchmarkRuns'`.

- [ ] **Step 3: Add the outcome property**

`src/SqlHarness.Core/Contracts.cs`, inside `SqlHarnessOutcome` after `MachineError`:

```csharp
    /// <summary>
    /// Measured benchmark runs of a successful measure/compare, for the activity journal only.
    /// Internal and never serialized: agent output and artifacts are unchanged by its presence.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<CompareRunArtifact>? BenchmarkRuns { get; init; }
```

- [ ] **Step 4: Keep matrix runs**

`src/SqlHarness.Core/CompareMatrixRunner.cs`. Give `CompareMatrixResult` a body:

```csharp
internal sealed record CompareMatrixResult(
    SqlHarnessCompareMatrixReport Report,
    OutputFootprint RawFootprint)
{
    /// <summary>Every cell's measured runs, tagged with the zero-based cell index (never the value).</summary>
    internal IReadOnlyList<CompareRunArtifact> Runs { get; init; } = [];
}
```

In `RunAsync`, declare `var runs = new List<CompareRunArtifact>();` next to `reports`. After `reports.Add(...)` in the success branch, add:

```csharp
                var cellIndex = index;
                runs.AddRange(cell.Runs.Select(run => run with { MatrixCell = cellIndex }));
```

Change the final return to:

```csharp
        return new CompareMatrixResult(
            new SqlHarnessCompareMatrixReport(run.ParameterName, run.ParameterType, reports),
            new OutputFootprint(bytes, lines))
        {
            Runs = runs,
        };
```

- [ ] **Step 5: Attach runs in the module's success paths**

`src/SqlHarness.Core/SqlHarnessModule.cs`. Change only the four success-outcome constructions:

1. Parameter-set measure, `var setSuccess = ...`:
   ```csharp
                var setSuccess = new SqlHarnessOutcome(SqlHarnessExitCode.Success, setReport, null)
                {
                    BenchmarkRuns = execution.Runs.Select(run => run.Artifact).ToArray(),
                };
   ```
2. Single measure, `var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);` in `ExecuteMeasureAsync`:
   ```csharp
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null)
            {
                BenchmarkRuns = runs.Select(run => run.Artifact).ToArray(),
            };
   ```
3. Compare, in `ExecuteCompareAsync`:
   ```csharp
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, cell.Report, null)
            {
                BenchmarkRuns = cell.Runs,
            };
   ```
4. Matrix, in `ExecuteCompareMatrixAsync`:
   ```csharp
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, result.Report, null)
            {
                BenchmarkRuns = result.Runs,
            };
   ```

`WithReceipt` and `Checked` use `outcome with { ... }`, which preserves the property.

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~exposes|FullyQualifiedName~Measure_runs_carry_full_table_io|FullyQualifiedName~Failed_measure_exposes_no_runs"`
Expected: PASS.

Run: `dotnet test SqlHarness.sln --filter "FullyQualifiedName!~Integration"`
Expected: all PASS. Existing artifact and output golden tests prove the new properties are not serialized.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/CompareMatrixRunner.cs tests/SqlHarness.Tests/MeasureTests.cs tests/SqlHarness.Tests/CompareTests.cs tests/SqlHarness.Tests/CompareMatrixTests.cs
git commit -m "Expose measured benchmark runs to the journal without changing output"
```

---

### Task 4: Benchmark journal record builder

**Files:**
- Create: `src/SqlHarness.Core/Journal/BenchmarkJournalRecord.cs`, `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs`
- Test: `tests/SqlHarness.Tests/Journal/JournalBenchmarkBuilderTests.cs`

**Interfaces:**
- Consumes: `CompareRunArtifact` (with `TableIo`, `MatrixCell`, `ParameterSet`, `Metrics`), `PlanMetricsExtractor`, `PlanIdentity.Hash`, `BenchmarkMetricReport.Unavailable`, `Distribution.From`.
- Produces (public data records):
  - `public sealed record JournalWait(string WaitType, double AverageWaitMs, double AverageWaitCount)`
  - `public sealed record JournalTableIo(string Table, long LogicalReads, long? ScanCount, long? PhysicalReads, long? PageServerReads, long? ReadAheadReads, long? LobLogicalReads, long? LobPhysicalReads, long? LobReadAheadReads, int ColdRuns)`
  - `public sealed record JournalPlanLink(int Repetition, int Ordinal, string Hash)`
  - `public sealed record JournalVariantMetrics(string Variant, string? ParameterSet, int? MatrixCell, int Runs, CompareDistribution? ElapsedMilliseconds, CompareDistribution? CpuMilliseconds, CompareDistribution? LogicalReads, long? GrantRequestedKb, long? GrantGrantedKb, long? GrantMaxUsedKb, int? Dop, long? CompileTimeMs, long? CompileCpuMs, int SpillCount, bool HasWarnings, bool HasImplicitConversion, int MissingIndexCount, IReadOnlyList<JournalWait> Waits, PostgresBufferCounters? Postgres, IReadOnlyList<JournalTableIo> TableIo, IReadOnlyList<JournalPlanLink> PlanLinks)`
  - `public sealed record JournalPlanDocument(string Hash, string Format, string Document)` with `Format` either `showplan-xml` or `explain-json`
  - `public sealed record BenchmarkJournalRecord(IReadOnlyList<JournalVariantMetrics> Variants, IReadOnlyList<JournalPlanDocument> PlanDocuments)`
  - `internal static class JournalBenchmarkBuilder { BenchmarkJournalRecord Build(IReadOnlyList<CompareRunArtifact> runs); const int MaximumWaits = 10; }`

- [ ] **Step 1: Write the failing tests**

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalBenchmarkBuilderTests
{
    private static CompareRunArtifact Run(
        string variant, int repetition, long cpu, long elapsed, long reads,
        string? set = null, int? cell = null, IReadOnlyList<string>? plans = null,
        IReadOnlyList<TableIoCounters>? io = null, BenchmarkRunMetrics? metrics = null) =>
        new(variant, repetition, cpu, elapsed, reads,
            new Dictionary<string, long> { ["Orders"] = reads }, "hash", plans ?? [], 1, set, metrics)
        {
            TableIo = io ?? [new TableIoCounters("Orders", 1, reads, 0, 0, 0, 0, 0, 0)],
            MatrixCell = cell,
        };

    [Fact]
    public void Runs_are_grouped_by_variant_set_and_cell_in_first_appearance_order()
    {
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("baseline", 1, 10, 12, 5, cell: 0), Run("candidate", 1, 8, 9, 3, cell: 0),
            Run("candidate", 2, 9, 10, 4, cell: 0), Run("baseline", 2, 11, 13, 6, cell: 0),
            Run("baseline", 1, 20, 22, 7, cell: 1),
        ]);

        Assert.Equal(
            [("baseline", (int?)0, 2), ("candidate", 0, 2), ("baseline", 1, 1)],
            record.Variants.Select(v => (v.Variant, v.MatrixCell, v.Runs)));
        Assert.Equal(new CompareDistribution(10, 10, 11), record.Variants[0].CpuMilliseconds);
        Assert.Equal(new CompareDistribution(5, 5, 6), record.Variants[0].LogicalReads);
    }

    [Fact]
    public void Table_io_is_median_per_counter_with_cold_run_count()
    {
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 10, io: [new TableIoCounters("Orders", 1, 10, 4, 0, 8, 0, 0, 0)]),
            Run("measure", 2, 1, 1, 12, io: [new TableIoCounters("Orders", 1, 12, 0, 0, 0, 0, 0, 0)]),
            Run("measure", 3, 1, 1, 14, io: [new TableIoCounters("Orders", 3, 14, 0, 0, 0, 0, 0, 0), new TableIoCounters("Worktable", 0, 2, 0, 0, 0, 0, 0, 0)]),
        ]);

        var orders = record.Variants[0].TableIo.Single(t => t.Table == "Orders");
        Assert.Equal(12, orders.LogicalReads);
        Assert.Equal(1, orders.ScanCount);
        Assert.Equal(1, orders.ColdRuns);
        var worktable = record.Variants[0].TableIo.Single(t => t.Table == "Worktable");
        Assert.Equal(0, worktable.LogicalReads);
        Assert.Equal(["Orders", "Worktable"], record.Variants[0].TableIo.Select(t => t.Table));
    }

    [Fact]
    public void Runs_without_detail_fall_back_to_logical_reads_by_table()
    {
        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 0, 5, 9, io: [])]);

        var table = Assert.Single(record.Variants[0].TableIo);
        Assert.Equal(("Orders", 9L), (table.Table, table.LogicalReads));
        Assert.Null(table.ScanCount);
        Assert.Null(table.PhysicalReads);
    }

    [Fact]
    public void Unavailable_metrics_are_null_and_table_io_is_skipped()
    {
        var unavailable = new BenchmarkRunMetrics(
            BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Unavailable, null, false, null, null,
            BenchmarkMetricReport.Unavailable, null, null, null, BenchmarkMetricReport.ResultStatement,
            BenchmarkMetricText.StatementRows, []);

        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 0, 0, 0, metrics: unavailable, io: [])]);

        var variant = record.Variants[0];
        Assert.Null(variant.CpuMilliseconds);
        Assert.Null(variant.ElapsedMilliseconds);
        Assert.Null(variant.LogicalReads);
        Assert.Empty(variant.TableIo);
    }

    [Fact]
    public void Plan_metrics_are_aggregated_and_documents_deduplicated()
    {
        var plan = PlanMetricsExtractorTests.ActualPlan;
        var record = JournalBenchmarkBuilder.Build(
        [
            Run("measure", 1, 1, 1, 1, plans: [plan]),
            Run("measure", 2, 1, 1, 1, plans: [plan]),
        ]);

        var variant = record.Variants[0];
        Assert.Equal(4096, variant.GrantGrantedKb);
        Assert.Equal(256, variant.GrantMaxUsedKb);
        Assert.Equal(4, variant.Dop);
        Assert.Equal(1, variant.SpillCount);
        Assert.True(variant.HasImplicitConversion);
        Assert.Equal(new JournalWait("PAGEIOLATCH_SH", 40, 7), variant.Waits[0]);
        Assert.Equal([(1, 0), (2, 0)], variant.PlanLinks.Select(link => (link.Repetition, link.Ordinal)));
        Assert.Single(variant.PlanLinks.Select(link => link.Hash).Distinct());
        var document = Assert.Single(record.PlanDocuments);
        Assert.Equal("showplan-xml", document.Format);
        Assert.Equal(variant.PlanLinks[0].Hash, document.Hash);
    }

    [Fact]
    public void Waits_are_capped_at_ten()
    {
        var waits = string.Concat(Enumerable.Range(1, 12).Select(i => $"<Wait WaitType=\"W{i:D2}\" WaitTimeMs=\"{i}\" WaitCount=\"1\" />"));
        var plan = $"""<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><WaitStats>{waits}</WaitStats></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

        var record = JournalBenchmarkBuilder.Build([Run("measure", 1, 1, 1, 1, plans: [plan])]);

        Assert.Equal(JournalBenchmarkBuilder.MaximumWaits, record.Variants[0].Waits.Count);
        Assert.Equal("W12", record.Variants[0].Waits[0].WaitType);
    }

    [Fact]
    public void Empty_input_yields_empty_record() =>
        Assert.Empty(JournalBenchmarkBuilder.Build([]).Variants);
}
```

`PlanMetricsExtractorTests.ActualPlan` is `internal const`, so it is reachable from this class.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~JournalBenchmarkBuilderTests`
Expected: build FAILS with `The name 'JournalBenchmarkBuilder' does not exist`.

- [ ] **Step 3: Implement the records**

`src/SqlHarness.Core/Journal/BenchmarkJournalRecord.cs`:

```csharp
namespace SqlHarness.Core;

public sealed record JournalWait(string WaitType, double AverageWaitMs, double AverageWaitCount);

/// <summary>Per-table medians across measured runs. Detail counters are null when a run lacked STATISTICS IO detail (Postgres).</summary>
public sealed record JournalTableIo(
    string Table,
    long LogicalReads,
    long? ScanCount,
    long? PhysicalReads,
    long? PageServerReads,
    long? ReadAheadReads,
    long? LobLogicalReads,
    long? LobPhysicalReads,
    long? LobReadAheadReads,
    int ColdRuns);

public sealed record JournalPlanLink(int Repetition, int Ordinal, string Hash);

public sealed record JournalVariantMetrics(
    string Variant,
    string? ParameterSet,
    int? MatrixCell,
    int Runs,
    CompareDistribution? ElapsedMilliseconds,
    CompareDistribution? CpuMilliseconds,
    CompareDistribution? LogicalReads,
    long? GrantRequestedKb,
    long? GrantGrantedKb,
    long? GrantMaxUsedKb,
    int? Dop,
    long? CompileTimeMs,
    long? CompileCpuMs,
    int SpillCount,
    bool HasWarnings,
    bool HasImplicitConversion,
    int MissingIndexCount,
    IReadOnlyList<JournalWait> Waits,
    PostgresBufferCounters? Postgres,
    IReadOnlyList<JournalTableIo> TableIo,
    IReadOnlyList<JournalPlanLink> PlanLinks);

/// <summary>Full plan text. Sensitive: the journal stores it only with journal.storeSensitive.</summary>
public sealed record JournalPlanDocument(string Hash, string Format, string Document);

public sealed record BenchmarkJournalRecord(
    IReadOnlyList<JournalVariantMetrics> Variants,
    IReadOnlyList<JournalPlanDocument> PlanDocuments);
```

- [ ] **Step 4: Implement the builder**

`src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs`:

```csharp
namespace SqlHarness.Core;

/// <summary>
/// Pure aggregation of measured runs into journal rows: grouping by
/// (variant, parameter set, matrix cell), medians, plan diagnostics, plan
/// identity links, and distinct plan documents. Reads no parameter or matrix values.
/// </summary>
internal static class JournalBenchmarkBuilder
{
    internal const int MaximumWaits = 10;

    internal static BenchmarkJournalRecord Build(IReadOnlyList<CompareRunArtifact> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var documents = new Dictionary<string, JournalPlanDocument>(StringComparer.Ordinal);
        var variants = runs
            .GroupBy(run => (run.Variant, run.ParameterSet, run.MatrixCell))
            .Select(group => BuildVariant(group.Key.Variant, group.Key.ParameterSet, group.Key.MatrixCell, group.ToArray(), documents))
            .ToArray();
        return new BenchmarkJournalRecord(variants, documents.Values.ToArray());
    }

    private static JournalVariantMetrics BuildVariant(
        string variant,
        string? parameterSet,
        int? matrixCell,
        CompareRunArtifact[] runs,
        Dictionary<string, JournalPlanDocument> documents)
    {
        var cpuAvailable = runs.All(run => run.Metrics?.CpuTimeAvailability != BenchmarkMetricReport.Unavailable);
        var elapsedAvailable = runs.All(run => run.Metrics?.ElapsedTimeAvailability != BenchmarkMetricReport.Unavailable);
        var readsAvailable = runs.All(run => run.Metrics?.LogicalReadsAvailability != BenchmarkMetricReport.Unavailable);

        var planMetrics = runs.Select(run => PlanMetricsExtractor.Extract(run.PlanXmls)).ToArray();
        var links = new List<JournalPlanLink>();
        foreach (var run in runs)
        {
            for (var ordinal = 0; ordinal < run.PlanXmls.Count; ordinal++)
            {
                var document = run.PlanXmls[ordinal];
                var hash = PlanIdentity.Hash(document);
                links.Add(new JournalPlanLink(run.Repetition, ordinal, hash));
                documents.TryAdd(hash, new JournalPlanDocument(hash, IsJson(document) ? "explain-json" : "showplan-xml", document));
            }
        }

        var waits = planMetrics
            .SelectMany(metrics => metrics.Waits)
            .GroupBy(wait => wait.WaitType, StringComparer.Ordinal)
            .Select(group => new JournalWait(
                group.Key,
                group.Sum(wait => (double)wait.WaitTimeMs) / runs.Length,
                group.Sum(wait => (double)wait.WaitCount) / runs.Length))
            .OrderByDescending(wait => wait.AverageWaitMs).ThenBy(wait => wait.WaitType, StringComparer.Ordinal)
            .Take(MaximumWaits)
            .ToArray();
        var postgres = planMetrics.Select(metrics => metrics.Postgres).OfType<PostgresBufferCounters>().ToArray();

        return new JournalVariantMetrics(
            variant,
            parameterSet,
            matrixCell,
            runs.Length,
            elapsedAvailable ? Spread(runs.Select(run => run.ElapsedTimeMilliseconds)) : null,
            cpuAvailable ? Spread(runs.Select(run => run.CpuTimeMilliseconds)) : null,
            readsAvailable ? Spread(runs.Select(run => run.LogicalReads)) : null,
            Median(planMetrics.Select(metrics => metrics.GrantRequestedKb)),
            Median(planMetrics.Select(metrics => metrics.GrantGrantedKb)),
            Median(planMetrics.Select(metrics => metrics.GrantMaxUsedKb)),
            planMetrics.Max(metrics => metrics.Dop),
            Median(planMetrics.Select(metrics => metrics.CompileTimeMs)),
            Median(planMetrics.Select(metrics => metrics.CompileCpuMs)),
            planMetrics.Max(metrics => metrics.SpillCount),
            planMetrics.Any(metrics => metrics.HasWarnings),
            planMetrics.Any(metrics => metrics.HasImplicitConversion),
            planMetrics.Max(metrics => metrics.MissingIndexCount),
            waits,
            postgres.Length == 0 ? null : new PostgresBufferCounters(
                MedianOf(postgres.Select(p => p.SharedHit)), MedianOf(postgres.Select(p => p.SharedRead)),
                MedianOf(postgres.Select(p => p.SharedDirtied)), MedianOf(postgres.Select(p => p.SharedWritten)),
                MedianOf(postgres.Select(p => p.TempRead)), MedianOf(postgres.Select(p => p.TempWritten))),
            readsAvailable ? TableIo(runs) : [],
            links);
    }

    private static IReadOnlyList<JournalTableIo> TableIo(CompareRunArtifact[] runs)
    {
        var detailed = runs.All(run => run.TableIo.Count > 0);
        var tables = runs
            .SelectMany(run => detailed ? run.TableIo.Select(io => io.Table) : run.LogicalReadsByTable.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return tables.Select(table =>
        {
            if (!detailed)
            {
                return new JournalTableIo(
                    table,
                    MedianOf(runs.Select(run => run.LogicalReadsByTable.GetValueOrDefault(table))),
                    null, null, null, null, null, null, null, 0);
            }

            var counters = runs
                .Select(run => run.TableIo.FirstOrDefault(io => io.Table == table)
                    ?? new TableIoCounters(table, 0, 0, 0, 0, 0, 0, 0, 0))
                .ToArray();
            return new JournalTableIo(
                table,
                MedianOf(counters.Select(c => c.LogicalReads)),
                MedianOf(counters.Select(c => c.ScanCount)),
                MedianOf(counters.Select(c => c.PhysicalReads)),
                MedianOf(counters.Select(c => c.PageServerReads)),
                MedianOf(counters.Select(c => c.ReadAheadReads)),
                MedianOf(counters.Select(c => c.LobLogicalReads)),
                MedianOf(counters.Select(c => c.LobPhysicalReads)),
                MedianOf(counters.Select(c => c.LobReadAheadReads)),
                counters.Count(c => c.PhysicalReads + c.ReadAheadReads + c.LobPhysicalReads + c.LobReadAheadReads > 0));
        }).ToArray();
    }

    private static CompareDistribution Spread(IEnumerable<long> values)
    {
        var distribution = Distribution.From(values);
        return new CompareDistribution(distribution.Min, distribution.Median, distribution.Max);
    }

    private static long MedianOf(IEnumerable<long> values) => Distribution.From(values).Median;

    private static long? Median(IEnumerable<long?> values)
    {
        var present = values.OfType<long>().ToArray();
        return present.Length == 0 ? null : MedianOf(present);
    }

    private static bool IsJson(string document)
    {
        var trimmed = document.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '{' or '[';
    }
}
```


- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~JournalBenchmarkBuilderTests`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Core/Journal/BenchmarkJournalRecord.cs src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs tests/SqlHarness.Tests/Journal/JournalBenchmarkBuilderTests.cs
git commit -m "Aggregate measured runs into journal benchmark records"
```

---

### Task 5: Schema v2, benchmark writes, artifact directory and summary

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs`, `ActivityJournal.cs`, `IActivityJournal.cs`, `JournalModels.cs`, `OperationJournalDescriber.cs`
- Create: `src/SqlHarness.Core/Journal/JournalSummary.cs`
- Test: modify `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`; create `tests/SqlHarness.Tests/Journal/ActivityJournalBenchmarkTests.cs` and `tests/SqlHarness.Tests/Journal/JournalSummaryTests.cs`

**Interfaces:**
- Consumes: `BenchmarkJournalRecord` (Task 4).
- Produces:
  - `IActivityJournal.RecordBenchmark(JournalHandle? handle, BenchmarkJournalRecord record)`, a default interface method that does nothing, so existing fakes compile.
  - `OperationEnd` gains trailing optional parameters `string? ArtifactDirectory = null, string? SummaryJson = null`.
  - `internal static class JournalSummary { string? Build(object? report); const int MaximumOperators = 10; }`
  - `JournalSchema.CurrentVersion = 2`, `JournalSchema.Version2` DDL.

- [ ] **Step 1: Write the failing tests**

In `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`, rename `Open_creates_schema_version_1_in_wal_mode` to `Open_creates_current_schema_in_wal_mode` and change its version assertion to:

```csharp
        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
```

`tests/SqlHarness.Tests/Journal/ActivityJournalBenchmarkTests.cs`:

```csharp
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class ActivityJournalBenchmarkTests
{
    private static IActivityJournal Open(JournalTempDirectory temp, bool storeSensitive) =>
        ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));

    private static BenchmarkJournalRecord Record(string document = PlanMetricsExtractorTests.ActualPlan) =>
        JournalBenchmarkBuilder.Build(
        [
            new CompareRunArtifact("baseline", 1, 10, 12, 5, new Dictionary<string, long> { ["Orders"] = 5 }, "h", [document], 1)
            {
                TableIo = [new TableIoCounters("Orders", 1, 5, 2, 0, 1, 0, 0, 0)],
                MatrixCell = 2,
            },
        ]);

    [Fact]
    public void Version_1_database_migrates_to_version_2_and_keeps_rows()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = JournalSchema.Version1 + """
                INSERT INTO sessions (session_key, agent_kind, transport, source, host_pid, first_seen, last_seen)
                VALUES ('cli:old', 'claude', 'cli', 'process-tree', 1, 't', 't');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        Open(temp, storeSensitive: false);

        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM sessions WHERE session_key = 'cli:old'"));
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operation_metrics"));
    }

    [Fact]
    public void Benchmark_record_writes_metrics_table_io_and_plan_links()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: false);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());

        journal.RecordBenchmark(handle, Record());

        var metric = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_metrics").Single();
        Assert.Equal("baseline", metric["variant"]);
        Assert.Equal(2L, metric["matrix_cell"]);
        Assert.Equal(1L, metric["runs"]);
        Assert.Equal(10L, metric["cpu_ms_median"]);
        Assert.Equal(4096L, metric["grant_granted_kb"]);
        Assert.Equal(256L, metric["grant_max_used_kb"]);
        Assert.Equal(4L, metric["dop"]);
        Assert.Equal(1L, metric["spill_count"]);
        Assert.Equal(1L, metric["has_implicit_conversion"]);
        Assert.Contains("PAGEIOLATCH_SH", (string)metric["waits_json"]!);
        var io = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_table_io").Single();
        Assert.Equal(("Orders", 5L, 2L, 1L), ((string)io["table_name"]!, (long)io["logical_reads"]!, (long)io["physical_reads"]!, (long)io["cold_runs"]!));
        var link = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_plans").Single();
        Assert.Equal(1L, link["repetition"]);
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT hash FROM plans"));
    }

    [Fact]
    public void Plans_are_stored_compressed_and_deduplicated_only_when_sensitive()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);
        var first = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        var second = journal.Begin(JournalTestData.Session(), JournalTestData.Start());

        journal.RecordBenchmark(first, Record());
        journal.RecordBenchmark(second, Record());

        var plan = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM plans").Single();
        Assert.Equal("showplan-xml", plan["format"]);
        using var gzip = new GZipStream(new MemoryStream((byte[])plan["gz"]!), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var text = reader.ReadToEnd();
        Assert.Equal(PlanMetricsExtractorTests.ActualPlan, text);
        Assert.Equal((long)System.Text.Encoding.UTF8.GetByteCount(text), plan["raw_size"]);
        Assert.Equal(2, JournalDb.Rows(temp.DatabasePath, "SELECT metric_id FROM operation_plans").Count);
    }

    [Fact]
    public void Null_handle_and_empty_record_are_no_ops()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);

        journal.RecordBenchmark(null, Record());
        journal.RecordBenchmark(journal.Begin(JournalTestData.Session(), JournalTestData.Start()), new BenchmarkJournalRecord([], []));

        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operation_metrics"));
    }

    [Fact]
    public void Complete_stores_artifact_directory_and_summary()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: false);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());

        journal.Complete(handle, JournalTestData.End() with { ArtifactDirectory = "/a/b", SummaryJson = """{"kind":"measure"}""" });

        var row = JournalDb.Rows(temp.DatabasePath, "SELECT artifact_dir, summary_json FROM operations").Single();
        Assert.Equal("/a/b", row["artifact_dir"]);
        Assert.Equal("""{"kind":"measure"}""", row["summary_json"]);
    }
}
```

`tests/SqlHarness.Tests/Journal/JournalSummaryTests.cs`:

```csharp
using System.Text.Json;
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalSummaryTests
{
    private static readonly SqlHarnessTargetIdentityReport Target = new("s", "d", "s", "d", "profile");

    private static CompareVariantReport Variant(string name, params CompareOperatorReport[] operators) => new(
        name, new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), operators, []);

    private static SqlHarnessCompareReport Compare(bool? equivalent) =>
        new(Target, 1, 2, equivalent, Variant("baseline", new CompareOperatorReport(1, "Sort", "Orders", false, true, false)), Variant("candidate"), "/dir");

    [Fact]
    public void Measure_summary_has_stability_and_flagged_operators_only()
    {
        var report = new SqlHarnessMeasureReport(Target, 3, 3, true,
            Variant("measure",
                new CompareOperatorReport(1, "Index Seek", "Orders", false, false, false),
                new CompareOperatorReport(2, "Sort", "Orders", false, true, false)),
            "/dir");

        using var json = JsonDocument.Parse(JournalSummary.Build(report)!);

        Assert.Equal("measure", json.RootElement.GetProperty("kind").GetString());
        Assert.True(json.RootElement.GetProperty("resultsStable").GetBoolean());
        var op = Assert.Single(json.RootElement.GetProperty("operators").EnumerateArray());
        Assert.Equal("Sort", op.GetProperty("physicalOp").GetString());
        Assert.True(op.GetProperty("spill").GetBoolean());
    }

    [Fact]
    public void Compare_summary_has_equivalence_mode_and_both_variants()
    {
        using var json = JsonDocument.Parse(JournalSummary.Build(Compare(false))!);

        Assert.Equal("compare", json.RootElement.GetProperty("kind").GetString());
        Assert.False(json.RootElement.GetProperty("resultsEquivalent").GetBoolean());
        Assert.Equal("Ordered", json.RootElement.GetProperty("comparison").GetString());
        Assert.Single(json.RootElement.GetProperty("baselineOperators").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("candidateOperators").EnumerateArray());
    }

    [Fact]
    public void Matrix_summary_never_contains_matrix_values()
    {
        var report = new SqlHarnessCompareMatrixReport("Tenant", "nvarchar(20)",
        [
            new CompareMatrixCellReport(0, "SQLH_MATRIX_MARKER_A", Compare(true)),
            new CompareMatrixCellReport(1, "SQLH_MATRIX_MARKER_B", Compare(false)),
        ]);

        var summary = JournalSummary.Build(report)!;

        Assert.DoesNotContain("SQLH_MATRIX_MARKER", summary);
        using var json = JsonDocument.Parse(summary);
        Assert.Equal(2, json.RootElement.GetProperty("cells").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("equivalentCells").GetInt32());
        Assert.Equal("Tenant", json.RootElement.GetProperty("parameterName").GetString());
    }

    [Fact]
    public void Operators_are_capped()
    {
        var operators = Enumerable.Range(1, 15).Select(i => new CompareOperatorReport(i, "Sort", "T", true, false, false)).ToArray();
        var report = new SqlHarnessMeasureReport(Target, 1, 1, true, Variant("measure", operators), null);

        using var json = JsonDocument.Parse(JournalSummary.Build(report)!);

        Assert.Equal(JournalSummary.MaximumOperators, json.RootElement.GetProperty("operators").GetArrayLength());
    }

    [Fact]
    public void Non_benchmark_reports_have_no_summary() =>
        Assert.Null(JournalSummary.Build(new SqlHarnessPingReport(Target, "srv", "db", "login", 1)));
}
```


Add to `tests/SqlHarness.Tests/Journal/OperationJournalDescriberTests.cs`:

```csharp
    [Fact]
    public void Benchmark_end_carries_artifact_directory_summary_and_matrix_target()
    {
        var identity = new SqlHarnessTargetIdentityReport("srv", "db", "srv-actual", "db-actual", "profile");
        var variant = new CompareVariantReport("measure", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []);
        var measure = new SqlHarnessMeasureReport(identity, 1, 1, true, variant, "/artifacts/m1");
        var compare = new SqlHarnessCompareReport(identity, 1, 2, true, variant, variant, "/artifacts/c1");
        var matrix = new SqlHarnessCompareMatrixReport("BatchSize", "int", [new CompareMatrixCellReport(0, "1", compare)]);

        var measureEnd = OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.Success, measure, null), 1);
        var matrixEnd = OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null), 1);

        Assert.Equal("/artifacts/m1", measureEnd.ArtifactDirectory);
        Assert.NotNull(measureEnd.SummaryJson);
        Assert.Equal("srv-actual", matrixEnd.Server);
        Assert.Null(matrixEnd.ArtifactDirectory);
        Assert.Contains("compare-matrix", matrixEnd.SummaryJson);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ActivityJournalBenchmarkTests|FullyQualifiedName~JournalSummaryTests|FullyQualifiedName~OperationJournalDescriberTests|FullyQualifiedName~ActivityJournalTests"`
Expected: build FAILS (`RecordBenchmark`, `JournalSummary`, `ArtifactDirectory` missing).

- [ ] **Step 3: Schema v2**

`src/SqlHarness.Core/Journal/JournalSchema.cs`. Set `CurrentVersion = 2` and add:

```csharp
    internal const string Version2 = """
        CREATE TABLE operation_metrics (
            id INTEGER PRIMARY KEY,
            operation_id INTEGER NOT NULL REFERENCES operations(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            variant TEXT NOT NULL,
            parameter_set TEXT,
            matrix_cell INTEGER,
            runs INTEGER NOT NULL,
            elapsed_ms_min INTEGER, elapsed_ms_median INTEGER, elapsed_ms_max INTEGER,
            cpu_ms_min INTEGER, cpu_ms_median INTEGER, cpu_ms_max INTEGER,
            logical_reads_min INTEGER, logical_reads_median INTEGER, logical_reads_max INTEGER,
            grant_requested_kb INTEGER, grant_granted_kb INTEGER, grant_max_used_kb INTEGER,
            dop INTEGER, compile_time_ms INTEGER, compile_cpu_ms INTEGER,
            spill_count INTEGER NOT NULL,
            has_warnings INTEGER NOT NULL,
            has_implicit_conversion INTEGER NOT NULL,
            missing_index_count INTEGER NOT NULL,
            waits_json TEXT,
            pg_shared_hit INTEGER, pg_shared_read INTEGER, pg_shared_dirtied INTEGER, pg_shared_written INTEGER,
            pg_temp_read INTEGER, pg_temp_written INTEGER
        );
        CREATE INDEX ix_operation_metrics_operation ON operation_metrics(operation_id, ordinal);
        CREATE TABLE operation_table_io (
            metric_id INTEGER NOT NULL REFERENCES operation_metrics(id) ON DELETE CASCADE,
            table_name TEXT NOT NULL,
            logical_reads INTEGER NOT NULL,
            scan_count INTEGER, physical_reads INTEGER, page_server_reads INTEGER, read_ahead_reads INTEGER,
            lob_logical_reads INTEGER, lob_physical_reads INTEGER, lob_read_ahead_reads INTEGER,
            cold_runs INTEGER NOT NULL,
            PRIMARY KEY (metric_id, table_name)
        );
        CREATE TABLE operation_plans (
            metric_id INTEGER NOT NULL REFERENCES operation_metrics(id) ON DELETE CASCADE,
            repetition INTEGER NOT NULL,
            ordinal INTEGER NOT NULL,
            plan_hash TEXT NOT NULL,
            PRIMARY KEY (metric_id, repetition, ordinal)
        );
        CREATE INDEX ix_operation_plans_hash ON operation_plans(plan_hash);
        CREATE TABLE plans (
            hash TEXT PRIMARY KEY,
            format TEXT NOT NULL,
            raw_size INTEGER NOT NULL,
            gz BLOB NOT NULL,
            first_seen TEXT NOT NULL
        );
        """;
```

`operation_plans` holds only hashes, so it is written in every mode. `plans` holds full documents and is written only with `storeSensitive`. `operation_plans.plan_hash` therefore has no foreign key to `plans`.

In `ActivityJournal.Migrate`, under the lock, replace the single `if (locked < 1)` line with:

```csharp
            if (locked < 1)
                Execute(connection, JournalSchema.Version1);
            if (locked < 2)
                Execute(connection, JournalSchema.Version2);
```

- [ ] **Step 4: Interface, models, and writes**

`IActivityJournal.cs`, add to the interface:

```csharp
    /// <summary>Per-variant benchmark metrics; full plan documents only when the journal stores sensitive content.</summary>
    void RecordBenchmark(JournalHandle? handle, BenchmarkJournalRecord record) { }
```

`NullActivityJournal` inherits the no-op.

`JournalModels.cs`, extend `OperationEnd` with two trailing optional parameters:

```csharp
    long? RawTokens = null,
    string? ArtifactDirectory = null,
    string? SummaryJson = null);
```

`ActivityJournal.Complete`. Add `artifact_dir = $artifact, summary_json = $summary,` to the `UPDATE operations SET` list, and these parameters:

```csharp
            update.Parameters.AddWithValue("$artifact", (object?)end.ArtifactDirectory ?? DBNull.Value);
            update.Parameters.AddWithValue("$summary", (object?)end.SummaryJson ?? DBNull.Value);
```

`ActivityJournal`, add (with `using System.IO.Compression;` and `using System.Text;`):

```csharp
    public void RecordBenchmark(JournalHandle? handle, BenchmarkJournalRecord record)
    {
        if (handle is null || record is null || record.Variants.Count == 0)
            return;
        try
        {
            var now = Timestamp(_time.GetUtcNow());
            using var connection = Connect();
            using var transaction = connection.BeginTransaction();
            for (var ordinal = 0; ordinal < record.Variants.Count; ordinal++)
            {
                var variant = record.Variants[ordinal];
                var metricId = InsertMetric(connection, transaction, handle.OperationId, ordinal, variant);
                foreach (var table in variant.TableIo)
                    InsertTableIo(connection, transaction, metricId, table);
                foreach (var link in variant.PlanLinks)
                {
                    using var insert = Command(connection, transaction, """
                        INSERT OR IGNORE INTO operation_plans (metric_id, repetition, ordinal, plan_hash)
                        VALUES ($metric, $repetition, $ordinal, $hash);
                        """);
                    insert.Parameters.AddWithValue("$metric", metricId);
                    insert.Parameters.AddWithValue("$repetition", link.Repetition);
                    insert.Parameters.AddWithValue("$ordinal", link.Ordinal);
                    insert.Parameters.AddWithValue("$hash", link.Hash);
                    insert.ExecuteNonQuery();
                }
            }

            if (_storeSensitive)
            {
                foreach (var document in record.PlanDocuments)
                {
                    var bytes = Encoding.UTF8.GetBytes(document.Document);
                    using var insert = Command(connection, transaction, """
                        INSERT OR IGNORE INTO plans (hash, format, raw_size, gz, first_seen)
                        VALUES ($hash, $format, $size, $gz, $now);
                        """);
                    insert.Parameters.AddWithValue("$hash", document.Hash);
                    insert.Parameters.AddWithValue("$format", document.Format);
                    insert.Parameters.AddWithValue("$size", bytes.LongLength);
                    insert.Parameters.AddWithValue("$gz", Gzip(bytes));
                    insert.Parameters.AddWithValue("$now", now);
                    insert.ExecuteNonQuery();
                }
            }

            using var touch = Command(connection, transaction, "UPDATE operations SET updated_at = $now WHERE id = $id;");
            touch.Parameters.AddWithValue("$now", now);
            touch.Parameters.AddWithValue("$id", handle.OperationId);
            touch.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (Exception)
        {
            Warn();
        }
    }

    private static long InsertMetric(SqliteConnection connection, SqliteTransaction transaction, long operationId, int ordinal, JournalVariantMetrics v)
    {
        using var insert = Command(connection, transaction, """
            INSERT INTO operation_metrics (operation_id, ordinal, variant, parameter_set, matrix_cell, runs,
                elapsed_ms_min, elapsed_ms_median, elapsed_ms_max, cpu_ms_min, cpu_ms_median, cpu_ms_max,
                logical_reads_min, logical_reads_median, logical_reads_max,
                grant_requested_kb, grant_granted_kb, grant_max_used_kb, dop, compile_time_ms, compile_cpu_ms,
                spill_count, has_warnings, has_implicit_conversion, missing_index_count, waits_json,
                pg_shared_hit, pg_shared_read, pg_shared_dirtied, pg_shared_written, pg_temp_read, pg_temp_written)
            VALUES ($operation, $ordinal, $variant, $set, $cell, $runs,
                $eMin, $eMed, $eMax, $cMin, $cMed, $cMax, $rMin, $rMed, $rMax,
                $gReq, $gGrant, $gUsed, $dop, $compileTime, $compileCpu,
                $spills, $warnings, $implicit, $missing, $waits,
                $pgHit, $pgRead, $pgDirtied, $pgWritten, $pgTempRead, $pgTempWritten)
            RETURNING id;
            """);
        insert.Parameters.AddWithValue("$operation", operationId);
        insert.Parameters.AddWithValue("$ordinal", ordinal);
        insert.Parameters.AddWithValue("$variant", v.Variant);
        insert.Parameters.AddWithValue("$set", (object?)v.ParameterSet ?? DBNull.Value);
        insert.Parameters.AddWithValue("$cell", (object?)v.MatrixCell ?? DBNull.Value);
        insert.Parameters.AddWithValue("$runs", v.Runs);
        AddSpread(insert, "$e", v.ElapsedMilliseconds);
        AddSpread(insert, "$c", v.CpuMilliseconds);
        AddSpread(insert, "$r", v.LogicalReads);
        insert.Parameters.AddWithValue("$gReq", (object?)v.GrantRequestedKb ?? DBNull.Value);
        insert.Parameters.AddWithValue("$gGrant", (object?)v.GrantGrantedKb ?? DBNull.Value);
        insert.Parameters.AddWithValue("$gUsed", (object?)v.GrantMaxUsedKb ?? DBNull.Value);
        insert.Parameters.AddWithValue("$dop", (object?)v.Dop ?? DBNull.Value);
        insert.Parameters.AddWithValue("$compileTime", (object?)v.CompileTimeMs ?? DBNull.Value);
        insert.Parameters.AddWithValue("$compileCpu", (object?)v.CompileCpuMs ?? DBNull.Value);
        insert.Parameters.AddWithValue("$spills", v.SpillCount);
        insert.Parameters.AddWithValue("$warnings", v.HasWarnings ? 1 : 0);
        insert.Parameters.AddWithValue("$implicit", v.HasImplicitConversion ? 1 : 0);
        insert.Parameters.AddWithValue("$missing", v.MissingIndexCount);
        insert.Parameters.AddWithValue("$waits", v.Waits.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(v.Waits, WaitJson));
        insert.Parameters.AddWithValue("$pgHit", (object?)v.Postgres?.SharedHit ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgRead", (object?)v.Postgres?.SharedRead ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgDirtied", (object?)v.Postgres?.SharedDirtied ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgWritten", (object?)v.Postgres?.SharedWritten ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgTempRead", (object?)v.Postgres?.TempRead ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgTempWritten", (object?)v.Postgres?.TempWritten ?? DBNull.Value);
        return (long)insert.ExecuteScalar()!;
    }

    private static void InsertTableIo(SqliteConnection connection, SqliteTransaction transaction, long metricId, JournalTableIo t)
    {
        using var insert = Command(connection, transaction, """
            INSERT OR REPLACE INTO operation_table_io (metric_id, table_name, logical_reads, scan_count, physical_reads,
                page_server_reads, read_ahead_reads, lob_logical_reads, lob_physical_reads, lob_read_ahead_reads, cold_runs)
            VALUES ($metric, $table, $logical, $scan, $physical, $pageServer, $readAhead, $lobLogical, $lobPhysical, $lobReadAhead, $cold);
            """);
        insert.Parameters.AddWithValue("$metric", metricId);
        insert.Parameters.AddWithValue("$table", t.Table);
        insert.Parameters.AddWithValue("$logical", t.LogicalReads);
        insert.Parameters.AddWithValue("$scan", (object?)t.ScanCount ?? DBNull.Value);
        insert.Parameters.AddWithValue("$physical", (object?)t.PhysicalReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pageServer", (object?)t.PageServerReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$readAhead", (object?)t.ReadAheadReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$lobLogical", (object?)t.LobLogicalReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$lobPhysical", (object?)t.LobPhysicalReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$lobReadAhead", (object?)t.LobReadAheadReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$cold", t.ColdRuns);
        insert.ExecuteNonQuery();
    }

    private static readonly JsonSerializerOptions WaitJson = new(JsonSerializerDefaults.Web);

    private static void AddSpread(SqliteCommand command, string prefix, CompareDistribution? value)
    {
        command.Parameters.AddWithValue(prefix + "Min", (object?)value?.Min ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "Med", (object?)value?.Median ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "Max", (object?)value?.Max ?? DBNull.Value);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(bytes);
        return output.ToArray();
    }
```

Place `WaitJson` with the other static fields at the top of the class, so `dotnet format` and the member-order conventions in the file stay satisfied.

- [ ] **Step 5: Summary builder and describer**

`src/SqlHarness.Core/Journal/JournalSummary.cs`:

```csharp
using System.Text.Json;

namespace SqlHarness.Core;

/// <summary>
/// Bounded, value-free operation summary for the journal: verdicts, counts, and
/// flagged operators (physical op, object, flags). Never parameter or matrix
/// values, predicates, statement text, or result data.
/// </summary>
internal static class JournalSummary
{
    internal const int MaximumOperators = 10;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static string? Build(object? report) => report switch
    {
        SqlHarnessMeasureReport measure => Serialize(new
        {
            kind = "measure",
            resultsStable = measure.ResultsStable,
            operators = Operators(measure.Query),
        }),
        SqlHarnessMeasureSetReport sets => Serialize(new
        {
            kind = "measure-sets",
            sets = sets.Sets.Count,
            stableSets = sets.Sets.Count(set => set.ResultsStable),
        }),
        SqlHarnessCompareReport compare => Serialize(new
        {
            kind = "compare",
            comparison = compare.Equivalence.Mode.ToString(),
            resultsEquivalent = compare.ResultsEquivalent,
            baselineOperators = Operators(compare.Baseline),
            candidateOperators = Operators(compare.Candidate),
        }),
        SqlHarnessCompareMatrixReport matrix => Serialize(new
        {
            kind = "compare-matrix",
            parameterName = matrix.ParameterName,
            parameterType = matrix.ParameterType,
            cells = matrix.Cells.Count,
            equivalentCells = matrix.Cells.Count(cell => cell.Compare.ResultsEquivalent == true),
        }),
        _ => null,
    };

    private static object[] Operators(CompareVariantReport variant) =>
        variant.Operators
            .Where(op => op.HasWarnings || op.HasSpill || op.HasImplicitConversion)
            .Take(MaximumOperators)
            .Select(op => (object)new
            {
                nodeId = op.NodeId,
                physicalOp = op.PhysicalOp,
                @object = op.Object,
                warnings = op.HasWarnings,
                spill = op.HasSpill,
                implicitConversion = op.HasImplicitConversion,
            })
            .ToArray();

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
```

`OperationJournalDescriber`:
1. Add to `TargetOf`:
   ```csharp
        SqlHarnessCompareMatrixReport { Cells.Count: > 0 } value => value.Cells[0].Compare.Target,
   ```
2. In `DescribeEnd`, compute these and pass them as the new trailing arguments of `OperationEnd`:
   ```csharp
        var artifactDirectory = outcome.Report switch
        {
            SqlHarnessMeasureReport value => value.ArtifactDirectory,
            SqlHarnessMeasureSetReport value => value.ArtifactDirectory,
            SqlHarnessCompareReport value => value.ArtifactDirectory,
            _ => null,
        };
   ```
   and `JournalSummary.Build(outcome.Report)` for `SummaryJson`. Use named arguments (`ArtifactDirectory: artifactDirectory, SummaryJson: ...`) after `RawTokens`.

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal"`
Expected: all PASS, including the phase-1 journal tests.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Core/Journal tests/SqlHarness.Tests/Journal
git commit -m "Store benchmark metrics, table IO, plan links and opt-in plans in journal schema v2"
```

---

### Task 6: Wire benchmark records into the journaling module

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalingModule.cs`
- Test: `tests/SqlHarness.Tests/Journal/JournalingBenchmarkTests.cs` (new)

**Interfaces:**
- Consumes: `SqlHarnessOutcome.BenchmarkRuns` (Task 3), `JournalBenchmarkBuilder.Build` (Task 4), `IActivityJournal.RecordBenchmark` (Task 5).

- [ ] **Step 1: Write the failing tests**

```csharp
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class JournalingBenchmarkTests
{
    private static readonly SqlTargetRequest Target = new("local", new Dictionary<string, string> { ["tenant"] = "acme" });

    private sealed class FakeModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(outcome);
    }

    private static SqlHarnessOutcome BenchmarkOutcome(int? matrixCell = null) =>
        new(SqlHarnessExitCode.Success, null, null)
        {
            BenchmarkRuns =
            [
                new CompareRunArtifact("measure", 1, 10, 12, 5, new Dictionary<string, long> { ["Orders"] = 5 }, "h",
                    [PlanMetricsExtractorTests.ActualPlan], 1)
                {
                    TableIo = [new TableIoCounters("Orders", 1, 5, 0, 0, 0, 0, 0, 0)],
                    MatrixCell = matrixCell,
                },
            ],
        };

    private static async Task<JournalTempDirectory> RunAsync(SqlHarnessOutcome outcome, bool storeSensitive, SqlHarnessOperation? operation = null)
    {
        var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, TimeProvider.System);
        var module = new JournalingModule(new FakeModule(outcome), () => journal, () => JournalTestData.Session());
        var returned = await module.ExecuteAsync(operation ?? new SqlHarnessMeasureOperation(Target, null, "SELECT 'SQLH_SQL_MARKER'", ["p:nvarchar=SQLH_PARAM_MARKER"], 30, 1));
        Assert.Same(outcome.Report, returned.Report);
        Assert.Equal(outcome.ExitCode, returned.ExitCode);
        return temp;
    }

    [Fact]
    public async Task Hash_only_mode_stores_metrics_but_no_plans_or_sql()
    {
        using var temp = await RunAsync(BenchmarkOutcome(), storeSensitive: false);

        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operation_metrics"));
        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT plan_hash FROM operation_plans"));
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT hash FROM plans"));
        var bytes = JournalDb.AllBytes(temp.DatabasePath);
        Assert.False(JournalDb.Contains(bytes, "SQLH_SQL_MARKER"));
        Assert.False(JournalDb.Contains(bytes, "SQLH_PARAM_MARKER"));
        Assert.False(JournalDb.Contains(bytes, "CONVERT_IMPLICIT"));
    }

    [Fact]
    public async Task Sensitive_mode_stores_the_plan_and_sql_text()
    {
        using var temp = await RunAsync(BenchmarkOutcome(), storeSensitive: true);

        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT hash FROM plans"));
        Assert.True(JournalDb.Contains(JournalDb.AllBytes(temp.DatabasePath), "SQLH_SQL_MARKER"));
    }

    [Fact]
    public async Task Matrix_values_never_reach_the_database()
    {
        var compare = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"), 1, 2, true,
            new CompareVariantReport("baseline", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []),
            new CompareVariantReport("candidate", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []),
            null);
        var report = new SqlHarnessCompareMatrixReport("Tenant", "nvarchar(20)", [new CompareMatrixCellReport(0, "SQLH_MATRIX_MARKER", compare)]);
        var outcome = BenchmarkOutcome(matrixCell: 0) with { Report = report };

        using var temp = await RunAsync(outcome, storeSensitive: true);

        Assert.Equal(0L, JournalDb.Rows(temp.DatabasePath, "SELECT matrix_cell FROM operation_metrics").Single()["matrix_cell"]);
        Assert.False(JournalDb.Contains(JournalDb.AllBytes(temp.DatabasePath), "SQLH_MATRIX_MARKER"));
    }

    [Fact]
    public async Task Failed_outcome_records_no_benchmark()
    {
        var failed = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "boom");

        using var temp = await RunAsync(failed, storeSensitive: true);

        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operation_metrics"));
    }

    [Fact]
    public async Task Builder_failure_never_changes_the_outcome()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null)
        {
            BenchmarkRuns = [null!],
        };

        using var temp = await RunAsync(outcome, storeSensitive: false);

        Assert.Equal("succeeded", JournalDb.Rows(temp.DatabasePath, "SELECT status FROM operations").Single()["status"]);
    }
}
```

`Matrix_values_never_reach_the_database` uses `storeSensitive: true` on purpose: even in the most permissive mode, matrix values must not be stored. The plan document comes from `ActualPlan`, which does not contain the matrix marker.


- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~JournalingBenchmarkTests`
Expected: FAIL. `operation_metrics` is empty because the module does not call `RecordBenchmark` yet.

- [ ] **Step 3: Implement**

`src/SqlHarness.Core/Journal/JournalingModule.cs`, in `RunAsync`, immediately after the `var completed = Complete(...)` line and before the early-return `if`:

```csharp
        if (completed && journal is not null && handle is not null && outcome.BenchmarkRuns is { Count: > 0 } runs)
            RecordBenchmark(journal, handle, runs);
```

Add the helper next to `Complete`:

```csharp
    private static void RecordBenchmark(IActivityJournal journal, JournalHandle handle, IReadOnlyList<CompareRunArtifact> runs)
    {
        try
        {
            // Plan parsing and aggregation run inside the guard: a malformed run never
            // turns a successful benchmark into an exception.
            journal.RecordBenchmark(handle, JournalBenchmarkBuilder.Build(runs));
        }
        catch (Exception)
        {
            // Best-effort: the journal row stays completed without benchmark detail.
        }
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal"`
Expected: all PASS.

Run: `dotnet test SqlHarness.sln --filter "FullyQualifiedName!~Integration"`
Expected: all PASS, including `JournalContractTests` (byte-identical CLI output) and the MCP suite.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/Journal/JournalingModule.cs tests/SqlHarness.Tests/Journal/JournalingBenchmarkTests.cs
git commit -m "Record benchmark metrics for every successful measure and compare"
```

---

### Task 7: Documentation, spec alignment, and gates

**Files:**
- Modify: `AGENTS.md`, `README.md`, `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md`

- [ ] **Step 1: Documentation**

`AGENTS.md`: in the activity-journal bullet added in phase 1 (under **Safety contract**), append:

```markdown
For successful `measure` and `compare` runs the journal also stores per-variant medians, memory grant, DOP, compile, spill and wait diagnostics, every `STATISTICS IO` counter per table, and plan identity hashes; matrix cells are recorded by index only, never by value. Full plans are stored (gzip, deduplicated by plan shape) only with `journal.storeSensitive: true`; they embed statement text and may embed parameter values (`ParameterCompiledValue`).
```

`README.md`: add the same facts to the `### Activity journal` subsection, plus one sentence: "A stored plan is the first actual plan observed for its shape (`PlanIdentity`); per-run metrics are kept separately, so later runs with the same shape do not store another copy."

- [ ] **Step 2: Align the spec with the implementation**

In `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md`:

- Under `### operation_metrics`, replace "parameter set by name hash" with "parameter set by name".
- Replace the `### plans and operation_plans (only with storeSensitive)` section body with:

  ```markdown
  - `operation_plans(metric_id, repetition, ordinal, plan_hash)` — always stored; plan identity hashes are not sensitive.
  - `plans(hash TEXT PRIMARY KEY, format, raw_size, gz BLOB, first_seen)` — only with `storeSensitive`: deduplicated by the existing `PlanIdentity` hash, gzip-compressed (measured: median plan 21 KB, max 227 KB, ~17× compression). A stored plan is the first actual plan observed for its shape.
  ```

- Under `### operation_table_io`, add: "Postgres rows carry relation buffer reads as `logical_reads`; the SQL Server-only counters are null."

- [ ] **Step 3: Run both gates**

Run: `pwsh ./scripts/verify.ps1`
Expected: restore, build (`-warnaserror`), test, and format all succeed. If `format` fails, run `dotnet format SqlHarness.sln` and include the changes.

Run: `pwsh ./scripts/verify-linux.ps1`
Expected: all four stages succeed in WSL.

- [ ] **Step 4: Commit**

```bash
git add AGENTS.md README.md docs/superpowers/specs/2026-10-06-activity-dashboard-design.md
git commit -m "Document journal benchmark metrics and align the dashboard spec"
```

---

## Not in this phase

- **Phase 3:** `sqlharness dashboard`, the lock file, auth, the read-only API (which reads the tables added here), SSE, `abandoned` detection, and `watch` `progress_json`.
- **Phase 4:** React/shadcn SPA and the `ui` gate stage.
- **Phase 5:** autostart, idle shutdown, and retention. Retention relies on the `ON DELETE CASCADE` foreign keys added here, so it needs `PRAGMA foreign_keys = ON`, which `Connect` already sets.
- Benchmark detail for failed or partially completed matrix runs, and IO statistics for `query`. Both stay out of scope, as the spec states.
