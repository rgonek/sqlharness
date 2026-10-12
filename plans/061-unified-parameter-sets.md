# Plan 061: One parameter-set contract for measure and compare

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 27d2a9a..HEAD -- src/SqlHarness.Core/Contracts.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/CompareMatrixRunner.cs src/SqlHarness.Core/SqlParameterMatrix.cs src/SqlHarness.Core/MeasureParameterSets.cs src/SqlHarness.Core/ParameterSetFileReader.cs src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Mcp docs/mcp.md README.md`. Plans 056 (name normalizer, dry run), 058 (matrix values) and 060 (tool surface) are expected first.

**Goal:** An agent varies parameters the same way for `measure` and `compare`: inline sets, set files, or a one-parameter matrix, with the same names, validation and report labels.

**Architecture:** `SqlHarnessParameterSetInput` gains typed parameters so inline JSON sets need no declaration text. A single Core resolver turns `matrix`, `parameterSets` or `parameterSetFiles` into an ordered list of named sets, each validated with the plan 056 name normalizer and binder. `compare` runs the generalized matrix runner over those sets (one connection per set, as today); `measure` runs its parameter-set runner (rotation on one connection, as today). The compare matrix report labels each cell with its set name and bound parameters.

**Tech Stack:** .NET 8/10, C#, xUnit, ModelContextProtocol C# SDK.

**Spec:** [`docs/superpowers/specs/2026-10-12-agent-contract-review-design.md`](../docs/superpowers/specs/2026-10-12-agent-contract-review-design.md), decision 14.

## Status

- Status: TODO
- Priority: P3
- Effort: L
- Risk: MEDIUM (report shape of compare matrix changes; MCP argument rename)
- Depends on: 056, 058, 060
- Planned at: `27d2a9a`, 2026-10-12

## Global Constraints

- MCP arguments on both `measure` and `compare`: `parameterSets` (inline array of `{ name, parameters: [{ name, type, value }] }`), `parameterSetFiles` (array of paths under an input root), `matrix` (`{ name, type, values }`). At most one of the three per call. `paramSetFiles` is removed (breaking; the contract already changes in 060).
- Set count: 2..32 for `parameterSets`/`parameterSetFiles`, matrix values 2..32 (current matrix bound on HEAD wins if lower).
- A set name follows the existing safe-label rule (`Artifacts.SafeSetLabel`); a matrix set is named by its index and carries its value as `parameterValue`.
- Every set binds the same parameter names; a set missing a name or adding one is rejected naming the set and the parameter.
- `parameters` (fixed) combine with every set; a name present in both is rejected.

## Review Focus

- Two sets that bind the same parameter to different types must be rejected (Task 1 test).
- A typed NULL inside an inline set must bind as NULL of its type (Task 1 test).
- A matrix with one value must be rejected as before; a one-set `parameterSets` must be rejected with the same rule (Task 1 test).
- A dry run with `parameterSets` must bind every set without connecting (Task 2 test).
- Compare over sets must still open one connection per set (Task 2 test).

## Why this matters

`measure` varies parameters through `.sqljson` files only; `compare` only through a single-parameter matrix. An agent comparing a query across representative cases (typical value, largest value, the other code path) needs several parameters per case in `compare`, and an inline form for both, without writing files.

## Current state (verify each)

1. `SqlHarnessParameterSetInput(string Name, IReadOnlyList<string> Parameters)` (`Contracts.cs:112-117`) holds declaration text only.
2. `SqlHarnessCompareMatrixOperation` (`Contracts.cs:58-75`) takes one `Matrix`/`TypedMatrix`; `CompareMatrixRun` (`CompareMatrixRunner.cs:3-9`) binds one matrix parameter per cell.
3. `SqlHarnessMeasureOperation.ParameterSets` (`Contracts.cs:119-126`); MCP `measure` accepts `paramSetFiles` only; MCP `compare` accepts `matrix` only.
4. `CompareMatrixCellReport(int Index, string? ParameterValue, SqlHarnessCompareReport Compare)` (`Contracts.cs:102-105`).

---

### Task 1: Core resolver for parameter sets

**Files:**
- Create: `src/SqlHarness.Core/ParameterSetResolver.cs`
- Modify: `src/SqlHarness.Core/Contracts.cs` (`SqlHarnessParameterSetInput.TypedParameters`)
- Test: `tests/SqlHarness.Tests/ParameterSetResolverTests.cs`

**Interfaces:**
- Produces: `public sealed record SqlHarnessParameterSetInput(string Name, IReadOnlyList<string> Parameters) { public IReadOnlyList<SqlHarnessParameterInput>? TypedParameters { get; init; } }`
- Produces: `internal sealed record ResolvedParameterSet(string Name, string? ParameterValue, IReadOnlyList<SqlHarnessParameter> Parameters);`
- Produces: `internal static IReadOnlyList<ResolvedParameterSet> ParameterSetResolver.Resolve(ISqlDialect dialect, IReadOnlyList<SqlHarnessParameter> fixedParameters, SqlHarnessParameterMatrixInput? matrix, IReadOnlyList<SqlHarnessParameterSetInput>? sets);`

- [ ] **Step 1: Write the failing tests**

```csharp
public class ParameterSetResolverTests
{
    private static readonly ISqlDialect Dialect = SqlDialects.For(SqlEngine.SqlServer);

    [Fact]
    public void Matrix_becomes_one_set_per_value()
    {
        var sets = ParameterSetResolver.Resolve(Dialect, [], new("BatchSize", "int", ["1", "20"]), null);
        Assert.Equal(["0", "1"], sets.Select(set => set.Name));
        Assert.Equal(["1", "20"], sets.Select(set => set.ParameterValue));
    }

    [Fact]
    public void Inline_sets_bind_typed_parameters_including_null()
    {
        var sets = ParameterSetResolver.Resolve(Dialect, [], null,
        [
            new("typical", []) { TypedParameters = [new("@ClientId", "int", "1039"), new("Note", "nvarchar", null)] },
            new("largest", []) { TypedParameters = [new("ClientId", "int", "4193"), new("Note", "nvarchar", "x")] },
        ]);
        Assert.Equal(DBNull.Value, sets[0].Parameters.Single(p => p.Name == "@Note").Value);
        Assert.Equal(4000, sets[0].Parameters.Single(p => p.Name == "@Note").Size);
    }

    [Fact]
    public void Sets_with_different_parameter_names_are_rejected()
    {
        var error = Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(Dialect, [], null,
        [
            new("a", []) { TypedParameters = [new("ClientId", "int", "1")] },
            new("b", []) { TypedParameters = [new("GroupId", "int", "1")] },
        ]));
        Assert.Contains("'b'", error.Message);
    }

    [Fact]
    public void Sets_with_different_types_for_one_name_are_rejected()
    {
        Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(Dialect, [], null,
        [
            new("a", []) { TypedParameters = [new("ClientId", "int", "1")] },
            new("b", []) { TypedParameters = [new("ClientId", "bigint", "1")] },
        ]));
    }

    [Fact]
    public void A_single_set_is_rejected_like_a_single_matrix_value()
    {
        Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(Dialect, [], null,
            [new("only", []) { TypedParameters = [new("ClientId", "int", "1")] }]));
    }

    [Fact]
    public void A_fixed_parameter_repeated_in_a_set_is_rejected()
    {
        var fixedParameters = Dialect.BindParameters([new SqlHarnessParameterInput("Date", "datetime", "2026-01-01T00:00:00")]);
        Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(Dialect, fixedParameters, null,
        [
            new("a", []) { TypedParameters = [new("Date", "datetime", "2026-01-02T00:00:00")] },
            new("b", []) { TypedParameters = [new("Date", "datetime", "2026-01-03T00:00:00")] },
        ]));
    }
}
```

- [ ] **Step 2: Run to verify failure** — build error.

- [ ] **Step 3: Implement**

`Resolve` rejects both `matrix` and `sets` together; for a matrix, delegates value binding to the existing `SqlParameterMatrix` binder and names sets `"0"`, `"1"`, …; for sets, binds each set's `TypedParameters ?? Parameters` (declarations through `SqlParameterParser.ToInput`) with `dialect.BindParameters`, checks the label with the safe-label rule, the 2..32 count, identical name sets, identical `SqlDbType` per name, and no overlap with `fixedParameters`. Messages name the set and the parameter.

- [ ] **Step 4: Run** → PASS. **Step 5: Commit**

```bash
git add src/SqlHarness.Core tests/SqlHarness.Tests/ParameterSetResolverTests.cs
git commit -m "feat(061): one resolver for matrix values and parameter sets"
```

---

### Task 2: Compare and measure use the resolver

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs` (`SqlHarnessCompareMatrixOperation.ParameterSets`; `CompareMatrixCellReport` gains `SetName` and `Parameters`), `CompareMatrixRunner.cs` (`CompareMatrixRun.Sets` replaces `MatrixValues`/`DisplayValues`), `SqlHarnessModule.cs` (compare-matrix and measure paths call the resolver), `AgentOutputProjection.cs` (cell label), `ArtifactReader.cs`/`Artifacts.cs` if the matrix index stores labels
- Test: `tests/SqlHarness.Tests/CompareMatrixTests.cs`, `MeasureTests.cs`

**Interfaces:**
- Consumes: `ParameterSetResolver.Resolve`, `ResolvedParameterSet`.
- Produces: `CompareMatrixCellReport(int Index, string? ParameterValue, SqlHarnessCompareReport Compare) { public string SetName { get; init; } public IReadOnlyList<BenchmarkParameterReport> SetParameters { get; init; } }`; `SqlHarnessCompareMatrixReport.ParameterName`/`ParameterType` become nullable (null for multi-parameter sets).

- [ ] **Step 1: Write the failing tests**

`CompareMatrixTests.cs`:

```csharp
[Fact]
public async Task Compare_over_inline_sets_opens_one_session_per_set()
{
    using var artifacts = new DirectoryArtifactWriter();
    var factory = new MatrixSessionFactory();
    var operation = Matrix("BatchSize:int=1,20") with
    {
        Matrix = "",
        TypedMatrix = null,
        ParameterSets =
        [
            new("small", []) { TypedParameters = [new("BatchSize", "int", "1")] },
            new("large", []) { TypedParameters = [new("BatchSize", "int", "100")] },
        ],
    };

    var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

    var report = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
    Assert.Equal(["small", "large"], report.Cells.Select(cell => cell.SetName));
    Assert.Equal(2, factory.ConnectCount);
}

[Fact]
public async Task Dry_run_over_sets_binds_every_set_without_connecting()
{
    // Same operation with DryRun = true: Success, SqlHarnessDryRunReport.ParameterSets == 2, ConnectCount == 0.
}
```

Write the second test's body in full following the first. `MeasureTests.cs`: a measure with inline `ParameterSets` (no files) runs both sets on one session (`ConnectCount == 1`) and reports both set names.

- [ ] **Step 2: Run to verify failure** — build error / no `ParameterSets`.

- [ ] **Step 3: Implement**

Add `IReadOnlyList<SqlHarnessParameterSetInput>? ParameterSets { get; init; }` to `SqlHarnessCompareMatrixOperation` (the operation is selected when either a matrix or sets are given; rename nothing else in Core). In the module's compare-matrix path replace the matrix-binding loop with `ParameterSetResolver.Resolve(...)` and build `CompareMatrixRun` from the resolved sets (`Parameters = [.. fixed, .. set.Parameters]` per cell). In the measure path, resolve inline sets with the same resolver and pass bound sets to `MeasureParameterSetRunner` (files keep their reader, then go through the resolver for the shared checks). Cell reports carry `SetName`, `ParameterValue` (matrix only) and `SetParameters` (`BenchmarkParameterReport` of the set's parameters). The agent projection shows `setName` on each cell and omitted-cell reference.

- [ ] **Step 4: Run** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareMatrix|FullyQualifiedName~Measure|FullyQualifiedName~ParameterSet"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core tests/SqlHarness.Tests
git commit -m "feat(061): compare and measure run over the same resolved parameter sets"
```

---

### Task 3: MCP arguments

**Files:**
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `McpRequestToolHandlers.cs`, `McpToolArguments.cs` (new `McpParameterSetArgument`), `McpOperationMapper.cs` (measure and compare mappers)
- Test: `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`, `McpToolSchemaTests.cs`, `McpTokenBudgetTests.cs` (tools/list budget)

**Interfaces:**
- Produces: `public sealed record McpParameterSetArgument(string Name, McpParameterArgument[] Parameters);`

- [ ] **Step 1: Write the failing tests** — mapping: `compare` with `parameterSets` maps to `SqlHarnessCompareMatrixOperation.ParameterSets`; `measure` with `matrix` maps to measure sets named `"0"`, `"1"`; any two of `parameterSets`/`parameterSetFiles`/`matrix` together are rejected with `"Use one of parameterSets, parameterSetFiles or matrix."`; schema: both tools expose the three arguments and neither exposes `paramSetFiles`.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement** — add the three arguments to both tools in both handler classes (descriptions: `"Inline parameter sets: 2..32 of {name, parameters}."`, `"Parameter-set files (.sqljson) under an input root."`, `"One-parameter shorthand: {name, type, values}."`); update `ThrowIfUnknown` lists; map files through the existing reader into `SqlHarnessParameterSetInput`; map inline sets with `TypedParameters`. Keep tools/list within its budget; shorten descriptions rather than dropping arguments.
- [ ] **Step 4: Run** `dotnet test tests/SqlHarness.Mcp.Tests` → PASS.
- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests
git commit -m "feat(061)!: measure and compare share parameterSets, parameterSetFiles and matrix"
```

---

### Task 4: Documentation and gates

- [ ] **Step 1:** `docs/mcp.md` (measure/compare rows, matrix paragraph `:100` now covering sets, artifacts for cells), `README.md` ("Measure parameter sets" section becomes "Parameter sets" for both commands; state the execution difference: measure rotates on one connection, compare uses one connection per set), `skills/sqlharness/SKILL.md`, `DocsSyncTests` pins.
- [ ] **Step 2:** `dotnet build SqlHarness.sln -warnaserror`; `pwsh scripts/verify.ps1`; `pwsh scripts/verify-linux.ps1`.
- [ ] **Step 3:** Status DONE; `plans/README.md` row.

```bash
git add docs README.md skills tests plans
git commit -m "docs(061): one parameter-set contract for measure and compare"
```

## Done criteria

- [ ] Both tools accept the three forms with identical validation messages.
- [ ] Compare over inline sets reports one cell per set with its name and parameters.
- [ ] Measure over a matrix rotates its values on one connection.
- [ ] Both gates pass; README row updated.

## STOP conditions

- The CLI `--matrix` text grammar or `--param-set` files would change meaning. The CLI keeps its grammar; only the Core operation and MCP arguments change. Stop if a shared parser change would alter CLI behavior.
- Matrix artifacts on disk store `parameterValue` in a way older readers depend on. Keep reading old artifacts; write the new labels additively.
