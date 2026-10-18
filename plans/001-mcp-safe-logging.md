# Plan 001: Close the SQL and parameter leak through stderr

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P1; effort: S; change risk: LOW.
Coverage: **S1**. Dependencies: **—**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree, preserve other people's changes. Writing the plan is not implementation. On completion update plans/README.md along with the evidence; do not push or publish without an assignment.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Mcp/McpHost.cs" "tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs" "tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs"
```

Predecessor changes are expected: read them and align the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

McpHost.cs:131,143 enables all logging levels and passes the SDK formatter to stderr. A process trial with synthetic SQL and a parameter showed both values present in stderr, also when arguments were rejected.

Reference point from code/command:
```text
public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
```

.NET8/C#/xUnit. Match patterns to the tests listed in the scope. Core remains the shared safety locus; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Mcp/McpHost.cs`
- `tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs`

Additionally: new files named in the steps, the output documents plans/001-*.md and the status in the index. Other files require an explicit scope correction and justification before any change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, globally disabling validation and unrelated refactorings. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as described in the steps.

## Tasks

### 001/T1

- [ ] Add process-level regression tests modelled on McpStdioProcessTests: handshake, valid validate, rejected arguments and a malformed frame. Use different artificial markers in the SQL, the parameter, the path and the exception text. Check stdout and stderr; do not start a DB.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpSecretRedactionTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 001/T2

- [ ] Replace arbitrary SDK log formatting with constant messages or an explicit list of safe events. Do not invoke the formatter or ToString on an unknown state/exception. Limiting the log level by itself is not a protection.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpSecretRedactionTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 001/T3

- [ ] Keep stdout exclusively for MCP frames, safe start and completion messages, and error-category diagnostics. Check normal EOF and cancellation.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpSecretRedactionTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

## Tests and completion

The process test finds no synthetic marker on stderr; the validate response stays a valid frame. Startup errors stay concise.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpSecretRedactionTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behaviour of the correct adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's changes within the scope.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not declare a full PASS then.

## Stop conditions and maintenance

Do not add regex redaction of whole payloads as the primary protection. Do not change the SQL classifier or the confidentiality level of the query result.

If a step requires a real DB target, secrets, or going beyond authorization, stop that step and record the absence. Complete the independent tasks. Do not mark the whole thing DONE without all required results.

The reviewer checks negative-case safety, compatibility and the truthfulness of the evidence. After changing the parser/SDK/operation, return to these tests. Small commits of the fix/test/docs type, no push. Required characteristic tests must precede a refactoring.
