# Plan 009: Make the static safety boundary visible to clients

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P2; effort: M; change risk: LOW.
Coverage: **A4; PG function limitations known to be in effect**. Dependencies: **007, 008**.

## Context and execution instruction

Read the whole plan. Work in an isolated worktree, preserve other people's changes. A plan record is not an implementation. On completion update plans/README.md together with the evidence; do not push or publish without an instruction.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/SqlValidation.cs" "src/SqlHarness.Core/Capabilities.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs" "tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs" "tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs" "README.md" "AGENTS.md" "docs/mcp.md" "docs/superpowers/specs/2026-09-26-postgres-safety-policy.md"
```

Predecessor changes are expected: read them and reconcile the plan before coding. Unexplained divergence requires updating the assumptions, not mechanically pasting old code.

The PG policy describes the effects visible in the text and the absence of a READ ONLY transaction; functions/views/operators are outside AST evidence. The MCP query description may suggest a guarantee of no mutation. This is a known execution boundary, not a new classifier gap.

Reference point from code/command:
```text
ObjectAndPermissionStatus = "unknown"
```

.NET8/C#/xUnit. Match the patterns to the tests listed in the scope. Core remains the shared place for safety; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Core/SqlValidation.cs`
- `src/SqlHarness.Core/Capabilities.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs`
- `tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`
- `README.md`
- `AGENTS.md`
- `docs/mcp.md`
- `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md`

Additionally: new files named in the steps, result documents plans/009-*.md and the status in the index. Other files require an explicit scope correction and justification before the change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, global disabling of validation and unrelated refactorizations. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as specified in the steps.

## Tasks

### 009/T1

- [ ] Add additive, versioned capabilities/validate metadata: analysis kind static-visible-effects, object/permissions unknown, hidden effects not verified. Keep the existing classification and exit codes.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 009/T2

- [ ] Change the query/validate descriptions to match the actual check. Point to the role of restricted DB permissions without claiming that SQLHarness creates or verifies it.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 009/T3

- [ ] Test field stability in CLI/MCP and the absence of a connection. Keep the function denylist and the absence of side effects from the preflight itself.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

### 009/T4

- [ ] Record the stricter profile option in plans/009-strict-profile-assessment.md as a design: TEMP restrictions, SECURITY DEFINER functions, operators and external effects. No READ ONLY implementation or role switching in this plan.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a check of the paths.

## Tests and completion

The client can distinguish syntactically allowed from a permissions check; descriptions and fields are consistent; the existing classifications are preserved.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behavior of the proper adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's own in-scope changes.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not then declare a full PASS.

## Stop conditions and maintenance

Do not narrow the lo_/dblink/pg_advisory_ prefixes without new evidence of function identity. Do not treat the catalog check as a complete solution for hidden effects.

If a step requires a real DB target, secrets or going beyond authorization, stop that step and record the gap. Complete the independent tasks. Do not mark the whole thing DONE without all the required results.

The reviewer checks the safety of negative cases, compatibility and the truthfulness of the evidence. After a change to the parser/SDK/operations, return to these tests. Small commits of the fix/test/docs type, no push. The required characteristic tests must precede the refactoring.