# Plan 012: Replace the textual MCP bridge with a typed parameter model

Status: **DONE** (branch `feat/plan-012-typed-matrix`, `3d56a81..da73e14`; evidence: [012-typed-matrix-proof](012-typed-matrix-proof.md); merge/push awaits the decision)
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P2; effort: L; change risk: MED.
Coverage: **matrix with commas/empty values; typed-input function**. Dependencies: **007, 009**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree, preserve other people's changes. A plan document is not an implementation. On completion update plans/README.md together with the evidence; do not push or publish without an order.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/Contracts.cs" "src/SqlHarness.Core/SqlParameterMatrix.cs" "src/SqlHarness.Core/SqlSafety.cs" "src/SqlHarness.Core/Postgres/PostgresParameters.cs" "src/SqlHarness.Core/CompareMatrixRunner.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Mcp/Tools/McpToolArguments.cs" "tests/SqlHarness.Tests/SqlParameterMatrixTests.cs" "tests/SqlHarness.Tests/CompareMatrixTests.cs" "tests/SqlHarness.Mcp.Tests/McpMappingTests.cs" "tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs"
```

Predecessor changes are expected: read them and align the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

MCP has the JSON array Values, but FormatMatrix reassembles it into name:type=v1,v2 and rejects commas and empty strings. Parameters also fall back to textual declarations. That is a declared limited representation, not a safety gap.

Reference point from the code/command:
```text
return $"{matrix.Name}:{matrix.Type}={string.Join(",", matrix.Values)}";
```

.NET8/C#/xUnit. Match patterns to the tests listed in the scope. Core remains the shared place for safety; adapters do not create their own classifier. Use the existing fake reader/session/module and the synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Core/Contracts.cs`
- `src/SqlHarness.Core/SqlParameterMatrix.cs`
- `src/SqlHarness.Core/SqlSafety.cs`
- `src/SqlHarness.Core/Postgres/PostgresParameters.cs`
- `src/SqlHarness.Core/CompareMatrixRunner.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Mcp/Tools/McpToolArguments.cs`
- `tests/SqlHarness.Tests/SqlParameterMatrixTests.cs`
- `tests/SqlHarness.Tests/CompareMatrixTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs`

Additionally: new files named in the steps, resulting documents plans/012-*.md and the status in the index. Other files require an explicit scope correction and justification before the change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, global disabling of validation and unrelated refactorings. Preserve exit codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as specified by the steps.

## Tasks

### 012/T1

- [x] Add an additive Core model for name, type and value with explicit null. The CLI declaration parser must produce this model; preserve the existing public constructors through a compatibility adapter.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

### 012/T2

- [x] Move the shared type validation and binding into a single path; MCP passes the structure without join/split. Define empty strings as text values, null as null, and the comma as a value character; still one dimension and at least two elements.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

### 012/T3

- [x] Test commas, equality, colon, Unicode, empty string, null, decimal(p,s), an incorrect count, duplicate names and both engines. The legacy CLI keeps the current meaning of the comma; the new CLI representation requires a separate explicit argument/file, without a silent parser change.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

### 012/T4

- [x] Preserve the new per-cell session/setup, the order, first-failure and the existing confidentiality contract of matrix versus param-set. The redaction test covers the new typed errors. Do not copy param-set values/paths into the report.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

## Tests and completion

MCP handles legitimate text values containing commas and empty strings without changing the legacy CLI; the same binder and the same type rejections for both paths.

- [x] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0 (SqlHarness.Tests 134/0/0, SqlHarness.Mcp.Tests 73/0/0).
- [x] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0 (0 Warning(s), 0 Error(s)).
- [x] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; SqlHarness.Tests 2808/0/0, SqlHarness.Mcp.Tests 212/0/4 skipped (2 live opt-in + 2 foreign-RID smoke).
- [x] Tests cover the behavior of the proper adapter/runner, not just a list of names or a constant (see the T4 evidence: `CompareMatrixRunner`/`McpResultAdapter` through temporary mutation + reversal).
- [x] `git diff --check` → exit 0.
- [x] `git status --short` shows only the author's changes within the scope.
- [x] The index (`plans/README.md`) contains the status, commit and evidence; local paths exist.
- [x] The absence of live/platform evidence is explicit — see [012-typed-matrix-proof](012-typed-matrix-proof.md), section "No live-DB / platform evidence".

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review once the independent gate failure is documented; do not announce a full PASS then.

## Stop conditions and maintenance

Do not encode values through your own CSV escaping and do not add a second MCP binder.

If a step requires a real DB target, secrets or going beyond authorization, stop that step and record the gap. Finish the independent tasks. Do not mark the whole thing DONE without all required results.

The reviewer checks the safety of the negative cases, compatibility and the truthfulness of the evidence. After a parser/SDK/operation change return to these tests. Small commits of the fix/test/docs type, no push. The required characteristic tests must precede the refactoring.
