# Measure Parameter Sets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `measure` to execute at least two named multi-parameter sets in one verified session with setup once, rotated measured order, per-set stability, and no copied parameter values.

**Architecture:** The CLI reads bounded strict JSON into value-bearing operation inputs that are treated as secrets. Core validates all set shapes before authentication, hashes typed values into value-free metadata, reuses the benchmark run seam on one session, and projects a distinct multi-set report through the existing artifact writer.

**Tech Stack:** .NET 10, C# 14, Spectre.Console.Cli, Microsoft.Data.SqlClient, System.Text.Json, SHA-256, xUnit

## Global Constraints

- Existing `measure` behavior is unchanged when no `--param-set` is supplied.
- Multi-set mode requires at least two files; exactly one returns exit `2`.
- Each file is strict UTF-8 JSON, at most 64 KiB, with only `name` and `parameters`.
- Every set has the same parameter names and SQL types; fixed `--param` names cannot collide.
- All file/JSON/parameter/reference validation occurs before authentication.
- One connection is used; setup runs zero/one time; warm-ups follow user order.
- In one-based round `r`, measured execution starts at `r % setCount` and wraps.
- Result stability is calculated within each set only; no cross-set equivalence exists.
- SQLHarness executes no plan-cache control statement and always reports the cache warning.
- Reports, safe errors, run metadata, filenames, and gain records contain no values, source paths, or original parameter strings.
- Server-produced `.sqlplan` remains locally sensitive and is stored unchanged.
- Existing single-set `measure`, `compare`, and `compare --matrix` remain compatible.

---

### Task 1: Read strict bounded parameter-set files in the CLI

**Files:**
- Modify: `src/SqlHarness.Core/Contracts.cs`
- Modify: `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`
- Create: `src/SqlHarness.Cli/Infrastructure/ParameterSetFileReader.cs`
- Create: `tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs`
- Create: `tests/SqlHarness.Tests/ParameterSetFileReaderTests.cs`

**Interfaces:**
- Consumes: repeated `--param-set <PATH>` and existing measure settings.
- Produces: `SqlHarnessParameterSetInput`, extended `SqlHarnessMeasureOperation.ParameterSets`, and `ParameterSetFileReader.ReadAsync(string, CancellationToken)`.

- [ ] **Step 1: Write failing strict-file and parser tests**

```csharp
[Fact]
public async Task Measure_dispatches_two_parameter_sets_in_user_order()
{
    using var first = TempJson("""{"name":"small","parameters":["id:int=1"]}""");
    using var second = TempJson("""{"name":"large","parameters":["id:int=999"]}""");
    var module = new FakeModule();

    var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([
        "measure", "dev", "--query", QueryFile,
        "--param-set", first.Path, "--param-set", second.Path
    ]);

    Assert.Equal(0, exit);
    var operation = Assert.IsType<SqlHarnessMeasureOperation>(
        Assert.Single(module.Operations));
    Assert.Equal(["small", "large"], operation.ParameterSets.Select(x => x.Name));
}
```

Test one-set rejection, 64 KiB boundary, BOM, comments, trailing comma/content,
duplicate/unknown properties, missing/extra fields, invalid/duplicate labels,
empty parameters, invalid UTF-8, IO failure, and that errors contain neither
path nor parameter strings.

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~MeasureParameterSetCommandTests|FullyQualifiedName~ParameterSetFileReaderTests"
```

Expected: FAIL because the option, reader, and contracts are missing.

- [ ] **Step 3: Add input contracts and strict reader**

```csharp
public sealed record SqlHarnessParameterSetInput(
    string Name, IReadOnlyList<string> Parameters);

public sealed record SqlHarnessMeasureOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string QuerySql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    IReadOnlyList<SqlHarnessParameterSetInput>? ParameterSets = null)
    : SqlHarnessOperation;
```

Read at most `65_537` bytes and reject when count exceeds `65_536`. Decode with
`new UTF8Encoding(false, true)`. Use `Utf8JsonReader` with comments/trailing
commas disabled and manually enforce exact properties and duplicates. Validate
labels with `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`.

The CLI must finish reading every set before dispatch; add file contents,
paths, and original parameter strings to neither output nor error messages.

- [ ] **Step 4: Run Task 1 tests**

Expected: PASS.

- [ ] **Step 5: Commit file input support**

```powershell
git add src/SqlHarness.Core/Contracts.cs src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs src/SqlHarness.Cli/Infrastructure/ParameterSetFileReader.cs tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs tests/SqlHarness.Tests/ParameterSetFileReaderTests.cs
git commit -m "feat: read measure parameter set files"
```

### Task 2: Validate shapes and hash typed values

**Files:**
- Create: `src/SqlHarness.Core/MeasureParameterSets.cs`
- Create: `tests/SqlHarness.Tests/MeasureParameterSetValidationTests.cs`
- Create: `tests/SqlHarness.Tests/TypedParameterHasherTests.cs`

**Interfaces:**
- Consumes: fixed parameter strings, set inputs, setup SQL, and query SQL.
- Produces: `PreparedMeasureParameterSet`, `MeasureParameterSetValidator.Prepare(IReadOnlyList<string>, IReadOnlyList<SqlHarnessParameterSetInput>, string?, string)`, and `TypedParameterHasher.Hash(IReadOnlyList<SqlHarnessParameter>)`.

- [ ] **Step 1: Write validation and hashing tests**

```csharp
[Fact]
public void Prepare_combines_fixed_and_set_parameters_without_values_in_metadata()
{
    var sets = MeasureParameterSetValidator.Prepare(
        ["tenant:nvarchar=acme"],
        [new("small", ["id:int=1"]), new("large", ["id:int=999"])],
        null, "select @tenant, @id");

    Assert.Equal(["small", "large"], sets.Select(x => x.Name));
    Assert.All(sets, x => Assert.Equal(["@id", "@tenant"],
        x.Metadata.Select(m => m.Name)));
    Assert.DoesNotContain("999", JsonSerializer.Serialize(
        sets.Select(x => new { x.Name, x.Metadata, x.ValueHash })));
}
```

Cover duplicate set names, fixed/set collision, duplicate names within set,
different names/types/size/precision/scale across sets, every shared parameter
type, nulls, missing/unreferenced parameters, culture independence, input-order
independence, different values producing different hashes, and no value in
exception text.

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~MeasureParameterSetValidationTests|FullyQualifiedName~TypedParameterHasherTests"
```

Expected: FAIL because validator and hasher are absent.

- [ ] **Step 3: Implement prepared value-bearing and public metadata models**

```csharp
public sealed record MeasureParameterMetadata(string Name, string Type);

internal sealed record PreparedMeasureParameterSet(
    string Name,
    IReadOnlyList<SqlHarnessParameter> Parameters,
    IReadOnlyList<MeasureParameterMetadata> Metadata,
    string ValueHash);
```

Parse through existing `SqlParameterParser`; merge fixed parameters; compare
shapes case-insensitively. Hash a canonical binary representation sorted by
lower-case name and containing type, size, precision, scale, null marker, and
invariant typed value. Return uppercase SHA-256 hex. Validate references for
every effective set.

- [ ] **Step 4: Run validation/hash tests**

Run the Task 2 command.

Expected: PASS.

- [ ] **Step 5: Commit validation and hashing**

```powershell
git add src/SqlHarness.Core/MeasureParameterSets.cs tests/SqlHarness.Tests/MeasureParameterSetValidationTests.cs tests/SqlHarness.Tests/TypedParameterHasherTests.cs
git commit -m "feat: validate and hash measure parameter sets"
```

### Task 3: Extract a reusable benchmark run seam with plan hashes

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Modify: `src/SqlHarness.Core/Artifacts.cs`
- Modify: `tests/SqlHarness.Tests/MeasureTests.cs`
- Create: `tests/SqlHarness.Tests/BenchmarkRunTests.cs`

**Interfaces:**
- Consumes: existing `ExecuteBenchmarkRunAsync` behavior.
- Produces: `BenchmarkRunner.ExecuteAsync(ISqlSession, string, IReadOnlyList<SqlHarnessParameter>, int, int, string, string?, CanonicalResultAccumulator, CancellationToken)`, `CollectedBenchmarkRun`, and `CompareRunArtifact.ParameterSet`.

- [ ] **Step 1: Add characterization and plan-hash tests**

Assert extracted runner preserves STATISTICS enable/disable cleanup, message
capture, timing/reads, result hash, plan XML, warnings, cancellation, and
primary-exception preservation. Add deterministic distinct plan hash assertions
using SHA-256 of exact plan XML.

- [ ] **Step 2: Run focused tests to verify the new seam is absent**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~BenchmarkRunTests|FullyQualifiedName~MeasureTests"
```

Expected: FAIL on missing runner tests while existing measure tests remain green.

- [ ] **Step 3: Extract without changing single-set behavior**

```csharp
internal sealed record CollectedBenchmarkRun(
    CompareRunArtifact Artifact,
    IReadOnlyList<ExecutionPlan> Plans,
    IReadOnlyList<string> PlanHashes);

internal static class BenchmarkRunner
{
    internal static Task<CollectedBenchmarkRun> ExecuteAsync(
        ISqlSession session, string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds, int repetition, string variant,
        string? parameterSet, CanonicalResultAccumulator raw,
        CancellationToken ct);
}
```

Add nullable `ParameterSet` to `CompareRunArtifact` as a trailing optional
property so existing construction remains source-compatible. Single-set measure
and compare pass null and retain exact report/artifact behavior.

- [ ] **Step 4: Run benchmark and existing measure/compare tests**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~BenchmarkRunTests|FullyQualifiedName~MeasureTests|FullyQualifiedName~CompareTests"
```

Expected: PASS.

- [ ] **Step 5: Commit the run seam**

```powershell
git add src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/Artifacts.cs tests/SqlHarness.Tests/MeasureTests.cs tests/SqlHarness.Tests/BenchmarkRunTests.cs
git commit -m "refactor: extract benchmark run execution"
```

### Task 4: Orchestrate one-session rotated multi-set measurement

**Files:**
- Modify: `src/SqlHarness.Core/MeasureParameterSets.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs`
- Create: `tests/SqlHarness.Tests/MeasureParameterSetExecutionTests.cs`

**Interfaces:**
- Consumes: prepared sets, `BenchmarkRunner`, and one verified session.
- Produces: `MeasureParameterSetRunner.ExecuteAsync(ISqlSession, SqlHarnessMeasureOperation, IReadOnlyList<PreparedMeasureParameterSet>, CanonicalResultAccumulator, CancellationToken)` returning `MeasureParameterSetExecution`.

- [ ] **Step 1: Write orchestration sequence tests**

```csharp
[Fact]
public async Task Three_sets_warm_in_input_order_and_rotate_each_round()
{
    var session = RecordingSession.Create();
    await Runner(session).ExecuteAsync(Operation(repeat: 3), Prepared("A", "B", "C"));

    Assert.Equal(["A", "B", "C"], session.WarmupSetNames);
    Assert.Equal(
        ["B", "C", "A", "C", "A", "B", "A", "B", "C"],
        session.MeasuredSetNames);
    Assert.Equal(1, session.SetupCount);
    Assert.Equal(1, session.ConnectionCount);
}
```

Test no setup, every set once per round, repeat bounds, stop on setup/warm-up/
measured failure, no partial success, cancellation, same physical session, and
absence of `DBCC FREEPROCCACHE`, `sp_recompile`, `OPTION(RECOMPILE)`, or cache
control batches introduced by the runner.

- [ ] **Step 2: Run execution tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~MeasureParameterSetExecutionTests
```

Expected: FAIL because the multi-set runner is missing.

- [ ] **Step 3: Implement exact loop semantics**

Define the orchestration result:

```csharp
internal sealed record MeasureParameterSetExecution(
    int SetupExecutionCount,
    IReadOnlyList<string> WarmupOrder,
    IReadOnlyList<CollectedBenchmarkRun> Runs);
```

After setup, execute one warm-up per set with repetition `0`. For measured
rounds:

```csharp
for (var round = 1; round <= repeat; round++)
{
    var start = round % sets.Count;
    for (var offset = 0; offset < sets.Count; offset++)
    {
        var set = sets[(start + offset) % sets.Count];
        // execute once with repetition=round and parameterSet=set.Name
    }
}
```

Use the existing timeout per SQL execution. Do not catch caller cancellation.
Before parsing or executing, add every fixed/set parameter string and every
non-null parsed value to the module's `knownSecrets` collection so all
validation and SQL exception branches pass through existing redaction without
leaking value-bearing input.

- [ ] **Step 4: Run execution and single-set regressions**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~MeasureParameterSetExecutionTests|FullyQualifiedName~MeasureTests"
```

Expected: PASS.

- [ ] **Step 5: Commit orchestration**

```powershell
git add src/SqlHarness.Core/MeasureParameterSets.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/MeasureParameterSetExecutionTests.cs
git commit -m "feat: rotate measure parameter sets"
```

### Task 5: Build per-set and cross-set reports

**Files:**
- Modify: `src/SqlHarness.Core/Artifacts.cs`
- Modify: `src/SqlHarness.Core/MeasureParameterSets.cs`
- Create: `tests/SqlHarness.Tests/MeasureParameterSetReportTests.cs`

**Interfaces:**
- Consumes: grouped measured runs and prepared value-free metadata.
- Produces: `MeasureParameterSetReport`, `MeasureCrossSetSummary`, and `SqlHarnessMeasureSetReport`.

- [ ] **Step 1: Write report semantics and secrecy tests**

Assert stability only within set, unstable hash null, distinct plan hashes,
per-set distributions/table reads/warnings, exact min/max median set labels,
deterministic tie selection by input order, counts, cache warning, and no values
or source paths in serialized report.

- [ ] **Step 2: Run report tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter FullyQualifiedName~MeasureParameterSetReportTests
```

Expected: FAIL because report types/projector are missing.

- [ ] **Step 3: Add distinct multi-set report contracts**

```csharp
public sealed record MeasureParameterSetReport(
    string Name, IReadOnlyList<MeasureParameterMetadata> Parameters,
    string ValueHash, int Repetitions, bool ResultsStable, string? ResultHash,
    CompareVariantReport Metrics, IReadOnlyList<string> PlanHashes);

public sealed record MeasureCrossSetSummary(
    string MinimumMedianElapsedSet, long MinimumMedianElapsedMilliseconds,
    string MaximumMedianElapsedSet, long MaximumMedianElapsedMilliseconds,
    string MinimumMedianCpuSet, long MinimumMedianCpuMilliseconds,
    string MaximumMedianCpuSet, long MaximumMedianCpuMilliseconds,
    string MinimumMedianReadsSet, long MinimumMedianLogicalReads,
    string MaximumMedianReadsSet, long MaximumMedianLogicalReads);

public sealed record SqlHarnessMeasureSetReport(
    SqlHarnessTargetIdentityReport Target, int Repeat, int MeasuredRunCount,
    int SetupExecutionCount, IReadOnlyList<string> WarmupOrder,
    string MeasuredOrderRule, string PlanCacheWarning,
    IReadOnlyList<MeasureParameterSetReport> Sets,
    MeasureCrossSetSummary CrossSetSummary,
    string? ArtifactDirectory);
```

Project each set through existing distribution/operator helpers. Do not compute
any cross-set result equivalence.

- [ ] **Step 4: Run report tests**

Run the Task 5 command.

Expected: PASS.

- [ ] **Step 5: Commit reports**

```powershell
git add src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/MeasureParameterSets.cs tests/SqlHarness.Tests/MeasureParameterSetReportTests.cs
git commit -m "feat: report parameter-sensitive measurements"
```

### Task 6: Extend artifacts without persisting input values

**Files:**
- Modify: `src/SqlHarness.Core/Artifacts.cs`
- Modify: `tests/SqlHarness.Tests/ArtifactWriterTests.cs`
- Create: `tests/SqlHarness.Tests/MeasureParameterSetArtifactTests.cs`

**Interfaces:**
- Consumes: `SqlHarnessMeasureSetReport` and artifacts tagged by set.
- Produces: existing artifact layout with sanitized set names in run metadata and plan filenames.

- [ ] **Step 1: Write artifact compatibility/privacy tests**

Assert `report.json`, `runs.jsonl`, and plan filenames contain set label/hash/
metadata but no values, original strings, or source paths. Assert plan XML is
unchanged, filenames are safe/unique, old compare/measure artifacts retain
their exact shape, and all staged files roll back on failure.

- [ ] **Step 2: Run artifact tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~MeasureParameterSetArtifactTests|FullyQualifiedName~ArtifactWriterTests"
```

Expected: FAIL on multi-set report support while legacy writer tests remain green.

- [ ] **Step 3: Extend writer dispatch and filenames**

Teach `WithArtifactDirectory` to copy `SqlHarnessMeasureSetReport`. Add
`parameterSet` to run JSONL only when non-null. Prefix multi-set plan filenames
with the sanitized set label; retain current filenames when null. Never pass
source paths or parameter input strings to the writer.

- [ ] **Step 4: Run all artifact tests**

Run the Task 6 command.

Expected: PASS.

- [ ] **Step 5: Commit artifact support**

```powershell
git add src/SqlHarness.Core/Artifacts.cs tests/SqlHarness.Tests/ArtifactWriterTests.cs tests/SqlHarness.Tests/MeasureParameterSetArtifactTests.cs
git commit -m "feat: persist measure set artifacts safely"
```

### Task 7: Render, document, and run the full regression gate

**Files:**
- Modify: `src/SqlHarness.Cli/Commands/Renderer.cs`
- Modify: `tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs`
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `skills/sqlharness/SKILL.md`
- Modify: `tests/SqlHarness.Tests/ContractsTests.cs`
- Modify: `tests/SqlHarness.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: `SqlHarnessMeasureSetReport`.
- Produces: compact text/JSON, synchronized guidance, and complete compatibility verification.

- [ ] **Step 1: Write failing output and documentation tests**

Require text to show cache warning, warm-up/rotation semantics, per-set
stability/distributions/plan hashes, cross-set ranges, and artifact path.
Assert values/paths are absent. Require help/docs to distinguish fixed
`--param`, multi-set `--param-set`, and `compare --matrix`.

- [ ] **Step 2: Run focused tests to verify they fail**

```powershell
dotnet test .\tests\SqlHarness.Tests\SqlHarness.Tests.csproj --filter "FullyQualifiedName~MeasureParameterSetCommandTests|FullyQualifiedName~ContractsTests|FullyQualifiedName~SmokeTests"
```

Expected: FAIL because renderer/help/docs are absent.

- [ ] **Step 3: Implement rendering and synchronized examples**

Render no parameter values. Document strict files:

```powershell
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --param tenant:nvarchar=acme --param-set .\sets\small.sqljson --param-set .\sets\large.sqljson --repeat 5 --json
```

Document one session, setup once, rotation, per-set stability, cache warning,
input sensitivity, plan sensitivity, and no cross-set equivalence.

- [ ] **Step 4: Run the complete gate**

```powershell
dotnet format .\SqlHarness.sln --verify-no-changes
dotnet build .\SqlHarness.sln -c Release
dotnet test .\SqlHarness.sln -c Release --no-build
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- measure --help
dotnet run --project .\src\SqlHarness.Cli\SqlHarness.Cli.csproj -c Release --no-build -- compare --help
```

Expected: formatting exit `0`, build succeeds, all tests pass, ordinary measure
help remains valid, and compare matrix help is unchanged.

- [ ] **Step 5: Commit output and docs**

```powershell
git add src/SqlHarness.Cli/Commands/Renderer.cs tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs README.md AGENTS.md skills/sqlharness/SKILL.md tests/SqlHarness.Tests/ContractsTests.cs tests/SqlHarness.Tests/SmokeTests.cs
git commit -m "docs: document measure parameter sets"
```
