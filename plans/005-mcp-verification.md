# Plan 005: Establish the causes of the MCP test timeouts

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P2; effort: M; change risk: LOW.
Coverage: **T1**. Dependencies: **001, 004**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree, preserve other people's changes. Writing the plan is not implementation. On completion update plans/README.md along with the evidence; do not push or publish without an assignment.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs" "tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs" "tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj" "src/SqlHarness.Mcp/McpExecutionGate.cs" "src/SqlHarness.Mcp/McpEofShutdown.cs" ".github/workflows/ci.yml"
```

Predecessor changes are expected: read them and align the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

Full audit: Core/CLI 1895 pass; MCP 122 pass, 2 timeouts, 4 skip. Progress timeout 15 s in WaitForProgressCountAsync; in isolation that test passed. Publish smoke got stuck in PublishAsync after about 6 minutes, before launching the binary.

Reference point from code/command:
```text
await WaitForProgressCountAsync(collector, 1, cts.Token)
```

.NET8/C#/xUnit. Match patterns to the tests listed in the scope. Core remains the shared safety locus; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs`
- `tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj`
- `src/SqlHarness.Mcp/McpExecutionGate.cs`
- `src/SqlHarness.Mcp/McpEofShutdown.cs`
- `.github/workflows/ci.yml`

Additionally: new files named in the steps, the output documents plans/005-*.md and the status in the index. Other files require an explicit scope correction and justification before any change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, globally disabling validation and unrelated refactorings. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as described in the steps.

## Tasks

### 005/T1

- [ ] Reproduce progress and publish separately on the current HEAD, recording stage, time, exit and the reason for cancellation without SQL/secrets. If the problem does not occur, run one full set; do not treat a single pass as an explanation.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 005/T2

- [ ] Separate the operation token, the client lifetime and the progress wait; check receiver registration before the response arrives. Fix only the established cause; do not add blind retries.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 005/T3

- [ ] In PublishAsync ensure concurrent draining of stdout/stderr, exit checking, distinguishing restore/publish/launch, and termination of its own process tree after a timeout. Do not stop processes from other sessions.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 005/T4

- [ ] Run the full set and record the actual results in plans/005-verification-evidence.md. Report an environment timeout separately from an application error. Do not raise limits without a measurement justifying the change.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

## Tests and completion

The full set passes in the target environment; the tests have evidence of correct cleanup and stage diagnostics. If infrastructure blocks it, status BLOCKED with evidence, never DONE.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behaviour of the correct adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's changes within the scope.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not declare a full PASS then.

## Stop conditions and maintenance

Do not mark unperformed RIDs as verified. Changing the production lifecycle requires reproducing the failure, not just the test timeout.

If a step requires a real DB target, secrets, or going beyond authorization, stop that step and record the absence. Complete the independent tasks. Do not mark the whole thing DONE without all required results.

The reviewer checks negative-case safety, compatibility and the truthfulness of the evidence. After changing the parser/SDK/operation, return to these tests. Small commits of the fix/test/docs type, no push. Required characteristic tests must precede a refactoring.
