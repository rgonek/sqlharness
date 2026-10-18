# Plan 011: Plan and add narrow safe-syntax extensions

Status: **DONE — code and documentation complete; NOT a full PASS** (branch `feat/plan-011-safe-sql-extensions`; scope `0fad2a8..` up to and including the commit carrying the final version of the evidence — the commit cannot state its own hash, it is preceded by `d9b7479`, the last commit changing code or tests is `9f07deb`). The MCP test project (`SqlHarness.Mcp.Tests`) is unstable (flaky) both at the tip of the branch and at base `0fad2a8`; no commit on this branch changes the MCP project file. This gate is an independent failure whose owner is plan 005. Additionally in `SqlHarness.Tests` one test (`ProcessRunnerTests.RunAsync_CancellationTerminatesEntireProcessTree`, untouched by this branch) failed in 2 of 10 runs of the final wave. T5 ANALYZE: DESIGN COMPLETE, not IMPLEMENTED. Evidence and exact counts: [011-safe-sql-extensions-proof](011-safe-sql-extensions-proof.md).
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P2; effort: L; change risk: HIGH.
Coverage: **over-blocking of SET, table variables, PG TEMP TRUNCATE and ANALYZE**. Dependencies: **007, 009**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree, preserve other people's changes. A plan document is not an implementation. On completion update plans/README.md together with the evidence; do not push or publish without an order.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/SqlSafety.cs" "src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs" "src/SqlHarness.Core/Postgres/PostgresDocument.cs" "src/SqlHarness.Core/SqlValidation.cs" "src/SqlHarness.Core/Capabilities.cs" "tests/SqlHarness.Tests/SqlSafetyTests.cs" "tests/SqlHarness.Tests/SqlParameterReferenceValidatorTests.cs" "tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs" "AGENTS.md"
```

Predecessor changes are expected: read them and align the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

Offline: scalar DECLARE works, setting a variable and a table variable are unsupported. PG CREATE TEMP + TRUNCATE is unsupported; ANALYZE gives parse_error. Ordinary CTE SELECTs work. Table variable and ANALYZE are known limits, not accidental regressions.

Reference point from the code/command:
```text
case DeclareVariableStatement declare:
```

.NET8/C#/xUnit. Match patterns to the tests listed in the scope. Core remains the shared place for safety; adapters do not create their own classifier. Use the existing fake reader/session/module and the synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `src/SqlHarness.Core/SqlSafety.cs`
- `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs`
- `src/SqlHarness.Core/Postgres/PostgresDocument.cs`
- `src/SqlHarness.Core/SqlValidation.cs`
- `src/SqlHarness.Core/Capabilities.cs`
- `tests/SqlHarness.Tests/SqlSafetyTests.cs`
- `tests/SqlHarness.Tests/SqlParameterReferenceValidatorTests.cs`
- `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`
- `AGENTS.md`

Additionally: new files named in the steps, resulting documents plans/011-*.md and the status in the index. Other files require an explicit scope correction and justification before the change.

### Scope corrections

Added in the final fix wave (2026-10-01). The files below lie outside the list above and were changed with the controller's consent; no new source or test file was created.

- `README.md` — two passages mentioning the „session-only" instructions described the state from before plan 011. Leaving them would be false documentation for the user (T6 round 1; final wave: the `pg_temp.<name>` spelling, the `search_path` restriction, the table variable with `--setup`).
- `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md`, `docs/superpowers/specs/2026-09-10-postgres-engine-design.md`, `docs/superpowers/plans/2026-09-10-postgres-engine.md`, `plans/009-strict-profile-assessment.md` — four older documents still gave the prefix `pg_temp_` as evidence of a session-local target. Two specs were corrected in place with a note „changed by plan 011", two historical plans got a dated note (T6).
- `tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json` — the exact byte pin read by `AgentWorkflowTests`. It grows together with the capabilities text: 5710 → 6219 → 6887 → 7100 UTF-8 bytes. The 8192 ceiling was not raised. The `AgentWorkflowTests.cs` file itself was not changed.
- `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs` — tests binding every sentence of `sessionTempStatements` to the verdict of the real classifier (T6, final wave).
- `tests/SqlHarness.Tests/QueryTests.cs`, `tests/SqlHarness.Tests/MeasureTests.cs`, `tests/SqlHarness.Tests/CompareTests.cs`, `tests/SqlHarness.Tests/Postgres/PostgresQueryTests.cs`, `tests/SqlHarness.Tests/Postgres/PostgresBenchmarkTests.cs` — end-to-end tests through `SqlHarnessModule` (final wave, M1). Added to existing classes because their fake session/reader are private to the class; no new test infrastructure was created.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, global disabling of validation and unrelated refactorings. Preserve exit codes 0/2/3/4/5/6/7/8, legacy JSON and both engines. Additive fields only as specified by the steps.

## Tasks

### 011/T1

- [x] In plans/011-syntax-contract.md record the matrix of allowed ASTs and negative cases before changing the classifier. Split the implementation into independent subtasks; each has its own regression test.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

### 011/T2

- [x] T-SQL SET: admit only a recognized assignment to a local scalar variable, analyzing the RHS through the existing external/stateful/cross-db checks. Do not enable SET of session/transaction options.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

### 011/T3

- [x] T-SQL table variables: design local-target identification for DECLARE/SELECT/DML/OUTPUT, batch scope and parameter references. Only a complete target analysis may allow DML; do not treat an arbitrary @name as local.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

### 011/T4

- [x] PG TRUNCATE: admit only targets all proven to belong to the current session; reject persistent/mixed targets and CASCADE, settle RESTART IDENTITY in the matrix instead of allowing it by default. Do not trust an arbitrary pg_temp_ prefix as evidence of the current session.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

### 011/T5

- [x] PG ANALYZE: first check the real offline AST of the parser. If support is still missing, deliver plans/011-analyze-parser-spike.md with the scope of the parser change and acceptance tests; the status of that subtask is DESIGN COMPLETE, not IMPLEMENTED. No regex bypass and no dependency update without a regression assessment.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

### 011/T6

- [x] Update capabilities and AGENTS only for actually supported constructs. Preserve the rejections of dynamic SQL, persistent DDL, cross-database access and function effects.

**Verification:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 after the change. For the regression test, first confirm the expected failure of the old code. Documentation-only step: `git diff --check` and a path check.

## Tests and completion

Every implemented extension has a positive and a negative test; all existing rejections of significant effects remain. ANALYZE has an explicit outcome: implementation or a dependency design.

- [x] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0.
- [x] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; record passed/failed/skipped. **Unmet as a gate — not a full PASS.** On `7ceed54` the run gave exit 0 (SqlHarness.Tests 2712/0/0, SqlHarness.Mcp.Tests 193/0/4), on `d9b7479` exit 1 (SqlHarness.Tests 2711/1/0 — `ProcessRunnerTests.RunAsync_CancellationTerminatesEntireProcessTree`; five repetitions of the project gave 2712/0/0). SqlHarness.Mcp.Tests fails irregularly: on `8abaa15` 1 of 6 controller runs gave 192/1/4 (`McpStdioProcessTests.Inprocess_host_returns_zero_on_immediate_eof_without_stdout_bytes`), the others 193/0/4; at base `0fad2a8` 1 of 5 runs gave 192/1/4 (`McpSecretRedactionTests.Transport_failure_logs_generic_text_without_exception_content`). Both runs of the MCP project in the final wave gave 193/0/4, which does not prove stability. Independent gate failure, owner: plan 005. Counts and sources: evidence, section „Test evidence".
- [x] Tests cover the behavior of the proper adapter/runner, not just a list of names or a constant. (Final wave, M1: tests through `SqlHarnessModule` for `query`, `measure`, `compare` on both engines; `watch`, `snapshot` and the MCP tools have no test of their own for the new constructs — they use the same Core classifier.)
- [x] `git diff --check` → exit 0.
- [x] `git status --short` shows only the author's changes within the scope.
- [x] The index contains the status, commit and evidence; local paths exist or are marked as new.
- [x] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review once the independent gate failure is documented; do not announce a full PASS then.

## Stop conditions and maintenance

If the parser does not represent the effects needed to settle the question, keep the rejection and describe the dependency. Do not extend the allowlist on the basis of a keyword alone.

If a step requires a real DB target, secrets or going beyond authorization, stop that step and record the gap. Finish the independent tasks. Do not mark the whole thing DONE without all required results.

The reviewer checks the safety of the negative cases, compatibility and the truthfulness of the evidence. After a parser/SDK/operation change return to these tests. Small commits of the fix/test/docs type, no push. The required characteristic tests must precede the refactoring.
