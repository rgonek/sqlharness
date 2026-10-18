# Plan 008: Expose a versioned MCP response schema

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P2; effort: M; change risk: LOW.
Coverage: **A3**. Dependencies: **005, 007**.

## Context and execution instruction

Read the whole plan. Work in an isolated worktree, preserve other people's changes. A plan record is not an implementation. On completion update plans/README.md together with the evidence; do not push or publish without an instruction.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Mcp/McpResultAdapter.cs" "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs" "tests/SqlHarness.Mcp.Tests/McpProtocolTests.cs" "tests/SqlHarness.Mcp.Tests/McpOutputTests.cs" "tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs"
```

Predecessor changes are expected: read them and reconcile the plan before coding. Unexplained divergence requires updating the assumptions, not mechanically pasting old code.

OutputSchema exists in the adapter, but tools/list of all 11 tools returned no outputSchema. Registering McpServerTool.Create does not attach the schema. This is not evidence of a protocol inconsistency, but a lack of a discoverable contract.

Reference point from code/command:
```text
public static JsonDocument OutputSchema { get; }
```

.NET8/C#/xUnit. Match the patterns to the tests listed in the scope. Core remains the shared place for safety; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Mcp/McpResultAdapter.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpProtocolTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`

Additionally: new files named in the steps, result documents plans/008-*.md and the status in the index. Other files require an explicit scope correction and justification before the change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, global disabling of validation and unrelated refactorizations. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as specified in the steps.

## Tasks

### 008/T1

- [ ] Check the local API of the installed SDK2.2.0 and wire the existing envelope schema to every tool; do not update the SDK along the way.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 008/T2

- [ ] Test the real tools/list through the transport, not only the static field. Each tool has schemaVersion1, required fields and a consistent schema for structuredContent. Cover error, partial, watch7 and snapshot8.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 008/T3

- [ ] Check the whole 32768B catalog limit and the 16384B default CallToolResult. If repeating the schema exceeds the catalog, reduce redundant descriptions/schema without removing required semantics; do not silently increase the limit.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 008/T4

- [ ] Treat narrowing the result per tool as a further step only if it fits within the budget and has a conformance test; an envelope with a free-form result is the minimal scope of this plan.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

## Tests and completion

11/11 tools publish outputSchema; the serialized catalog fits within the limit; controlled outcomes do not become isError.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behavior of the proper adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's own in-scope changes.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not then declare a full PASS.

## Stop conditions and maintenance

If the SDK does not allow a schema with the current registration, document a verified alternative before rebuilding the host.

If a step requires a real DB target, secrets or going beyond authorization, stop that step and record the gap. Complete the independent tasks. Do not mark the whole thing DONE without all the required results.

The reviewer checks the safety of negative cases, compatibility and the truthfulness of the evidence. After a change to the parser/SDK/operations, return to these tests. Small commits of the fix/test/docs type, no push. The required characteristic tests must precede the refactoring.