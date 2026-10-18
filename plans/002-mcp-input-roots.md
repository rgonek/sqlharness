# Plan 002: Fix the scope and path normalization of input paths

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P1; effort: M; change risk: MED.
Coverage: **S3, A1; file identity assessment**. Dependencies: **—**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree, preserve other people's changes. Writing the plan is not implementation. On completion update plans/README.md along with the evidence; do not push or publish without an assignment.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Mcp/McpInputReader.cs" "src/SqlHarness.Mcp/McpScope.cs" "tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs" "tests/SqlHarness.Mcp.Tests/McpStartupTests.cs"
```

Predecessor changes are expected: read them and align the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

McpInputReader.cs:235–240 applies OrdinalIgnoreCase on every platform and appends a separator without checking its presence. McpScope.cs:161 keeps a trailing separator. The Windows trial rejected a legal file under such a root; the case-sensitivity hazard follows from the code, without a Linux trial.

Reference point from code/command:
```text
full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
```

.NET8/C#/xUnit. Match patterns to the tests listed in the scope. Core remains the shared safety locus; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Mcp/McpInputReader.cs`
- `src/SqlHarness.Mcp/McpScope.cs`
- `tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpStartupTests.cs`

Additionally: new files named in the steps, the output documents plans/002-*.md and the status in the index. Other files require an explicit scope correction and justification before any change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, globally disabling validation and unrelated refactorings. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as described in the steps.

## Tasks

### 002/T1

- [ ] Add regressions for a root with/without a separator, a filesystem root, a similar prefix of a sibling directory, an empty set of roots, UNC/ADS, a file link and a parent link. Create the case-sensitive test on a filesystem that supports it; report the lack of such an environment as missing evidence, not as a test pass.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpInputReaderTests|FullyQualifiedName~McpStartupTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 002/T2

- [ ] Extract the common root normalization and the containment check. Keep the root directory; remove only the redundant separator. Adopt conservative case semantics on non-Windows systems; explicitly describe the restriction of Windows directories with individual case sensitivity.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpInputReaderTests|FullyQualifiedName~McpStartupTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 002/T3

- [ ] Preserve the limit on bytes read and the rejection of reparse/link. Test file substitution. Record in plans/002-file-identity-assessment.md whether a length/mtime check gives only accidental-change detection, or whether opening relative to a root handle is required. Do not claim protection against a race without evidence.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpInputReaderTests|FullyQualifiedName~McpStartupTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

## Tests and completion

A legal file works for both root notations; a directory out of scope is rejected; the existing traversal/link cases are still rejected. Linux evidence marked as performed or not performed.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpInputReaderTests|FullyQualifiedName~McpStartupTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behaviour of the correct adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's changes within the scope.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not declare a full PASS then.

## Stop conditions and maintenance

Do not allow links as a platform bypass. If full identity requires a system API, separate the design of such protection from the simple normalization fix.

If a step requires a real DB target, secrets, or going beyond authorization, stop that step and record the absence. Complete the independent tasks. Do not mark the whole thing DONE without all required results.

The reviewer checks negative-case safety, compatibility and the truthfulness of the evidence. After changing the parser/SDK/operation, return to these tests. Small commits of the fix/test/docs type, no push. Required characteristic tests must precede a refactoring.
