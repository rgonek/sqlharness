# Plan 007: Build the shared query/setup/benchmark preflight

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P2; effort: L; change risk: MED.
Coverage: **A2; preflight function**. Dependencies: **005**.

## Context and execution instruction

Read the whole plan. Work in an isolated worktree, preserve other people's changes. A plan record is not an implementation. On completion update plans/README.md together with the evidence; do not push or publish without an instruction.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/SqlValidation.cs" "src/SqlHarness.Core/CompareCellRunner.cs" "src/SqlHarness.Core/SqlHarnessModule.cs" "src/SqlHarness.Core/Dialect/ISqlDialect.cs" "src/SqlHarness.Cli/Commands/ValidateCommand.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs" "tests/SqlHarness.Tests/DialectAnalysisConsistencyTests.cs" "tests/SqlHarness.Mcp.Tests/McpMappingTests.cs"
```

Predecessor changes are expected: read them and reconcile the plan before coding. Unexplained divergence requires updating the assumptions, not mechanically pasting old code.

MapValidateAsync accepts usage, but always delegates to SqlValidation.Validate which uses SqlUsage.Query. PG two SELECTs with usage=benchmark received allowed=true, although PostgresBenchmark requires a single statement.

Reference point from code/command:
```text
return SqlValidation.Validate(scope.TargetRequest, sqlText, declarations, scope.Profiles);
```

.NET8/C#/xUnit. Match the patterns to the tests listed in the scope. Core remains the shared place for safety; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Core/SqlValidation.cs`
- `src/SqlHarness.Core/CompareCellRunner.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Core/Dialect/ISqlDialect.cs`
- `src/SqlHarness.Cli/Commands/ValidateCommand.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs`
- `tests/SqlHarness.Tests/DialectAnalysisConsistencyTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`

Additionally: new files named in the steps, result documents plans/007-*.md and the status in the index. Other files require an explicit scope correction and justification before the change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, global disabling of validation and unrelated refactorizations. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as specified in the steps.

## Tasks

### 007/T1

- [ ] Add the shared query/setup/benchmark ValidationUsage model and the optional setup context. Keep the existing signature as a query delegation. Share the classification, the parameter analysis and ValidateMeasuredBatch with a real preparer; do not copy the blacklist.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 007/T2

- [ ] Test the PG rejection of multiple statements in benchmark, query acceptance, local temp with setup, missing parameters and the absence of authorization for a persistent mutation. The lack of complete context must be explicit; do not fake executability evidence.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 007/T3

- [ ] Add the CLI --usage (default query) and a setup input consistent with the existing file reading; the MCP usage must reach the model. Keep both engines and offline: zero connect/execute in tests.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 007/T4

- [ ] Report safe reason/location and the scope of the checks performed. Do not print SQL or values in errors. Update README.md, docs/mcp.md and capabilities after adding the feature; these documents are an allowed scope extension.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

## Tests and completion

CLI and MCP return consistent decisions for the same operation; PG benchmark does not accept an impermissible shape; validate never opens a session.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behavior of the proper adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's own in-scope changes.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not then declare a full PASS.

## Stop conditions and maintenance

Do not check the catalog/permissions through the DB in the offline tool. The validate evidence does not replace re-validation of execution.

If a step requires a real DB target, secrets or going beyond authorization, stop that step and record the gap. Complete the independent tasks. Do not mark the whole thing DONE without all the required results.

The reviewer checks the safety of negative cases, compatibility and the truthfulness of the evidence. After a change to the parser/SDK/operations, return to these tests. Small commits of the fix/test/docs type, no push. The required characteristic tests must precede the refactoring.
