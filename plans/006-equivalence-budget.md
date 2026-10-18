# Plan 006: Limit the memory and time of result comparison

Status: **TODO**
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P1; effort: L; change risk: HIGH.
Coverage: **R2**. Dependencies: **005**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree, preserve other people's changes. Writing the plan is not implementation. On completion update plans/README.md along with the evidence; do not push or publish without an assignment.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/ResultEquivalence.cs" "src/SqlHarness.Core/CompareCellRunner.cs" "src/SqlHarness.Core/BenchmarkCollector.cs" "src/SqlHarness.Core/Postgres/PostgresBenchmark.cs" "src/SqlHarness.Core/BenchmarkRunner.cs" "src/SqlHarness.Core/Capabilities.cs" "tests/SqlHarness.Tests/ResultEquivalenceTests.cs" "tests/SqlHarness.Tests/CompareTests.cs" "tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs"
```

Predecessor changes are expected: read them and align the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

CompareCellRunner keeps all fingerprints; ResultComparer computes baseline×candidate without a token. The 1M limit is per run, repeat up to 100. 200M fingerprints and 10k pairs are the allowed upper range, not a measured load.

Reference point from code/command:
```text
foreach (var left in baseline)
```

.NET8/C#/xUnit. Match patterns to the tests listed in the scope. Core remains the shared safety locus; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Core/ResultEquivalence.cs`
- `src/SqlHarness.Core/CompareCellRunner.cs`
- `src/SqlHarness.Core/BenchmarkCollector.cs`
- `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs`
- `src/SqlHarness.Core/BenchmarkRunner.cs`
- `src/SqlHarness.Core/Capabilities.cs`
- `tests/SqlHarness.Tests/ResultEquivalenceTests.cs`
- `tests/SqlHarness.Tests/CompareTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs`

Additionally: new files named in the steps, the output documents plans/006-*.md and the status in the index. Other files require an explicit scope correction and justification before any change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, globally disabling validation and unrelated refactorings. Preserve codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as described in the steps.

## Tasks

### 006/T1

- [ ] Add small characteristic tests for all modes: ordering, duplicates, different schemas, empty results, unstable repetitions, the maximum number of directional differences across all pairs. Keep a simple reference implementation only in the tests.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 006/T2

- [ ] Define in plans/006-resource-contract.md an explicit budget for the whole operation, how fingerprints/histograms are accounted for, and rejection without truncating data. Preserve the per-run limit and do not mix the presentation MaxRows with equivalence. Cover the matrix budget reset per cell and the shared deadline.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 006/T3

- [ ] Pass the CancellationToken to the CPU phases and check it at bounded work intervals. Introduce a compact representation and a histogram cache per unique result; identical runs may share the representation. Do not change SHA/hash semantics without versioning.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 006/T4

- [ ] Verify the new algorithm against an oracle on deterministic small sets; add limit-boundary attempts with a small injected budget and cancellation of an already started comparison. Measure allocations on a bounded synthetic set, without attempting 200M rows.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

### 006/T5

- [ ] Preserve the artifacts of completed matrix cells and controlled errors. Extend capabilities with the budget that is actually enforced.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 after the change. For a regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and checking the paths.

## Tests and completion

Oracle and new code agree for all modes; the total budget is never exceeded; cancellation of the CPU phase is observed; never equivalent=true on truncated data.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped.
- [ ] Tests cover the behaviour of the correct adapter/runner, not just a list of names or a constant.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's changes within the scope.
- [ ] The index contains status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not declare a full PASS then.

## Stop conditions and maintenance

If the exact maximum counts cannot be preserved by the proposed optimization, preserve the semantics and bound the effort with a limit; do not replace them with a comparison of only the first pair.

If a step requires a real DB target, secrets, or going beyond authorization, stop that step and record the absence. Complete the independent tasks. Do not mark the whole thing DONE without all required results.

The reviewer checks negative-case safety, compatibility and the truthfulness of the evidence. After changing the parser/SDK/operation, return to these tests. Small commits of the fix/test/docs type, no push. Required characteristic tests must precede a refactoring.
