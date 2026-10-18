# Plan 010: Remove duplicated watch loops and pure adapter rules

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P3; effort: L; change risk: MED.
Coverage: **D1; interface assessment**. Dependencies: **004, 007, 008, 009**.

## Context and execution instruction

Read the whole plan. Work in an isolated worktree, preserve other people's changes. A plan record is not an implementation. On completion update plans/README.md together with the evidence; do not push or publish without an instruction.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/WatchRunner.cs" "src/SqlHarness.Core/WatchNdjson.cs" "src/SqlHarness.Core/Capabilities.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Cli/Commands/WatchCommand.cs" "src/SqlHarness.Cli/Commands/QueryStoreTopCommand.cs" "tests/SqlHarness.Tests/WatchTests.cs" "tests/SqlHarness.Tests/WatchNdjsonTests.cs" "tests/SqlHarness.Mcp.Tests/McpMappingTests.cs"
```

Predecessor changes are expected: read them and reconcile the plan before coding. Unexplained divergence requires updating the assumptions, not mechanically pasting old code.

WatchRunner.ExecuteAsync and ExecuteNdjsonAsync have two budget/read/condition/delay loops. The MCP mapper repeats the time and bounds parsing, capabilities describes them separately. Two transports and separate writers are not redundant interfaces.

Reference point from code/command:
```text
while (true)
```

.NET8/C#/xUnit. Match the patterns to the tests listed in the scope. Core remains the shared place for safety; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Core/WatchRunner.cs`
- `src/SqlHarness.Core/WatchNdjson.cs`
- `src/SqlHarness.Core/Capabilities.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Cli/Commands/WatchCommand.cs`
- `src/SqlHarness.Cli/Commands/QueryStoreTopCommand.cs`
- `tests/SqlHarness.Tests/WatchTests.cs`
- `tests/SqlHarness.Tests/WatchNdjsonTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`

Additionally: new files named in the steps, result documents plans/010-*.md and the status in the index. Other files require an explicit scope correction and justification before the change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, global disabling of validation and unrelated refactorizations. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as specified in the steps.

## Tasks

### 010/T1

- [ ] Add parity tests for the final watch report/NDJSON decisions with a fake clock: condition, unchanged, timeout connect/read/delay, late result, caller cancellation and error. Keep the absence of history in NDJSON.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 010/T2

- [ ] Extract a single polling engine that emits events to two small sinks. The existing public ExecuteAsync and ExecuteWatchNdjsonAsync remain adapters; one monotonic budget covers the whole operation.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 010/T3

- [ ] Extract only the identical pure bounds/duration rules into a new Core file OperationLimits.cs, with a new OperationLimitsTests.cs test. Transport formats and errors stay in the adapters. Capabilities uses the same constants.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 010/T4

- [ ] Do not merge CLI/MCP, query/measure/compare, param-set/matrix, domain writers or clocks merely because of a shared method. Preserve the ADR object Report and OperationReportContract. Record the decisions to preserve the boundaries in plans/010-interface-decisions.md.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

## Tests and completion

One polling engine, identical terminal outcomes before/after, no growth in NDJSON retention; CLI/MCP bounds consistent.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behavior of the proper adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's own in-scope changes.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not then declare a full PASS.

## Stop conditions and maintenance

Do not unify rules whose units/defaults are deliberately different. Do not turn a refactoring into a public API change.

If a step requires a real DB target, secrets or going beyond authorization, stop that step and record the gap. Complete the independent tasks. Do not mark the whole thing DONE without all the required results.

The reviewer checks the safety of negative cases, compatibility and the truthfulness of the evidence. After a change to the parser/SDK/operations, return to these tests. Small commits of the fix/test/docs type, no push. The required characteristic tests must precede the refactoring.