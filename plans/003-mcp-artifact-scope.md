# Plan 003: Tie artifact access to the MCP scope

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P1; effort: L; change risk: HIGH.
Coverage: **S2; snapshot namespace**. Dependencies: **001, 002**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree, preserve other people's changes. Writing the plan is not implementation. On completion update plans/README.md along with the evidence; do not push or publish without an assignment.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Mcp/McpScope.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "src/SqlHarness.Core/ArtifactReader.cs" "src/SqlHarness.Core/Artifacts.cs" "src/SqlHarness.Core/Contracts.cs" "src/SqlHarness.Core/SqlHarnessModule.cs" "src/SqlHarness.Core/SnapshotStore.cs" "src/SqlHarness.Core/SnapshotRunner.cs" "tests/SqlHarness.Mcp.Tests/McpScopeTests.cs" "tests/SqlHarness.Mcp.Tests/McpMappingTests.cs" "tests/SqlHarness.Tests/ArtifactReaderTests.cs" "tests/SqlHarness.Tests/SnapshotStoreTests.cs"
```

Predecessor changes are expected: read them and align the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

ReadArtifactSection accepts a scope, but reads the global CompareDir without checking the owner. A synthetic report from another database was returned with isError=false. The manifest has no scope identity; SnapshotDocument does not record the target either.

Reference point from code/command:
```text
return ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, id ?? string.Empty, section);
```

.NET8/C#/xUnit. Match patterns to the tests listed in the scope. Core remains the shared safety locus; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Mcp/McpScope.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `src/SqlHarness.Core/ArtifactReader.cs`
- `src/SqlHarness.Core/Artifacts.cs`
- `src/SqlHarness.Core/Contracts.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Core/SnapshotStore.cs`
- `src/SqlHarness.Core/SnapshotRunner.cs`
- `tests/SqlHarness.Mcp.Tests/McpScopeTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs`
- `tests/SqlHarness.Tests/SnapshotStoreTests.cs`

Additionally: new files named in the steps, the output documents plans/003-*.md and the status in the index. Other files require an explicit scope correction and justification before any change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, globally disabling validation and unrelated refactorings. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as described in the steps.

## Tasks

### 003/T1

- [ ] First write the scope model in plans/003-scope-contract.md: profile + canonical var set + resolved engine/server/database, without passwords. Establish persistence across restart and the owner for matrix/param-set. Chosen variant: versioned owner metadata created through a trusted write path; a missing owner means MCP rejection. A local writer with process permissions is outside the integrity boundary — do not promise resistance to manifest forgery.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 003/T2

- [ ] Add tests for an own and a foreign scope, the same database name on a different server, other vars/profile, and a missing owner in an old artifact. Preserve the existing offline CLI reader; MCP must not grant itself a right to an old artifact on the basis of its name.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 003/T3

- [ ] Move the owner context from the frozen scope through the operation to the publisher and the reader. Check the owner before projecting the report. Preserve summary/metrics/operators, the absence of raw SQL and values, atomic publish, budgets and safe errors.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 003/T4

- [ ] Define snapshot capture/diff separately: MCP requires an owner and rejects a foreign scope, CLI keeps the current name-based, scope-aware behaviour. Do not migrate or automatically overwrite existing files. Test the absence of overwrite and the absence of cell disclosure.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 003/T5

- [ ] Update docs/mcp.md and the MCP specification with the actual scope model and legacy compatibility; add those two files to the scope list during implementation.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

## Tests and completion

A foreign scope and legacy artifacts without an owner are rejected before any data is returned; an own artifact works after restart according to the contract; legacy CLI works; a snapshot does not disclose cells.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behaviour of the correct adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's changes within the scope.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not declare a full PASS then.

## Stop conditions and maintenance

Do not use the database name or the folder name alone as authorization. If context threading requires a different runner, update the plan scope and the justification before the change; do not skip matrix/param-set.

If a step requires a real DB target, secrets, or going beyond authorization, stop that step and record the absence. Complete the independent tasks. Do not mark the whole thing DONE without all required results.

The reviewer checks negative-case safety, compatibility and the truthfulness of the evidence. After changing the parser/SDK/operation, return to these tests. Small commits of the fix/test/docs type, no push. Required characteristic tests must precede a refactoring.
