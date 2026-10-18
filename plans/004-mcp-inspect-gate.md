# Plan 004: Cover inspect with the shared DB-operation gate

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P1; effort: S; change risk: MED.
Coverage: **R1**. Dependencies: **001**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree, preserve other people's changes. Writing the plan is not implementation. On completion update plans/README.md along with the evidence; do not push or publish without an assignment.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "src/SqlHarness.Mcp/McpExecutionGate.cs" "tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs" "tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs" "docs/mcp.md"
```

Predecessor changes are expected: read them and align the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

InspectAsync uses RunAsync without the gate, although the mapper creates real DB operations. RunDbAsync takes the gate. A test entrenches the wrong split of five tools and treats inspect as discovery.

Reference point from code/command:
```text
_gate.TryEnterDb()
```

.NET8/C#/xUnit. Match patterns to the tests listed in the scope. Core remains the shared safety locus; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `src/SqlHarness.Mcp/McpExecutionGate.cs`
- `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs`
- `docs/mcp.md`

Additionally: new files named in the steps, the output documents plans/004-*.md and the status in the index. Other files require an explicit scope correction and justification before any change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, globally disabling validation and unrelated refactorings. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as described in the steps.

## Tasks

### 004/T1

- [ ] Add deterministic tests with RecordingModule and TaskCompletionSource: an active query blocks inspect, inspect blocks a query and a second inspect; capabilities/validate remain available. No sleeps and no DB.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 004/T2

- [ ] Route all inspect kinds through RunDbAsync and include inspect in IsDbTool. Preserve the timeouts per kind and the existing process-level limit; new per-call arguments only if needed, and then with a schema test.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 004/T3

- [ ] Check slot release after a mapping rejection, a Core error, a deadline, cancellation and EOF. Update the documentation and the classification test; do not increase the number of tools.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

## Tests and completion

At most one DB call reaches the module concurrently; the second receives busy/exit2/isError=true; after completion the slot is available.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behaviour of the correct adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's changes within the scope.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not declare a full PASS then.

## Stop conditions and maintenance

Do not replace busy with a queue and do not serialize offline operations unnecessarily.

If a step requires a real DB target, secrets, or going beyond authorization, stop that step and record the absence. Complete the independent tasks. Do not mark the whole thing DONE without all required results.

The reviewer checks negative-case safety, compatibility and the truthfulness of the evidence. After changing the parser/SDK/operation, return to these tests. Small commits of the fix/test/docs type, no push. Required characteristic tests must precede a refactoring.
