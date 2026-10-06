# Plan 023: T-SQL text containing `GO` batch separators is rejected instead of being classified as batches the server never sees

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/SqlValidation.cs tests/SqlHarness.Tests/SqlSafetyTests.cs tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs AGENTS.md README.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md; land after plans 017/018 (same file)
- **Category**: bug
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

ScriptDom splits a script on `GO` lines into several `TSqlBatch` objects, and the classifier (and `validate`) reasons per batch: scope of `DECLARE`d variables, table variables, parameter references. But **nothing in the execution path splits batches**. The whole text, `GO` lines included, is sent to SQL Server as one `CommandText`. `GO` is a client-tool (sqlcmd/SSMS) convention, not T-SQL. So `validate` and `query`/`measure` may approve a script whose meaning on the server differs from what was classified. `GO` can become an ordinary identifier (a column or table alias), silently changing result shape and hashes, or the batch can fail with a syntax error after connecting. The audit found no shape where this widens a safety decision, because per-batch scope is stricter. It is a correctness and contract bug that makes the offline preflight lie.

Rejecting multi-batch text is simpler and more honest than splitting execution. Splitting would interact with the "setup runs once per session" benchmark contract and with per-run message capture.

## Current state

- `src/SqlHarness.Core/SqlSafety.cs:194-233` (`Classify`): parses, inspects, then:

  ```csharp
          if (script.Batches.All(batch => batch.Statements.Count == 0))
          {
              return Denied(SqlSafetyReason.UnsupportedStatement);
          }

          return usage switch
          {
              SqlUsage.Query => ClassifyQuery(script.Batches, inspection, database, allowMutation, confirmDatabase),
              SqlUsage.CompareSetup => ClassifyCompareSetup(script.Batches, inspection),
              _ => Denied(SqlSafetyReason.UnsupportedStatement),
          };
  ```

  `Denied` has an optional `detail` (`private static SqlSafetyDecision Denied(SqlSafetyReason reason, string? detail = null)` near `:823`). Read how `Detail` is rendered (`grep -n "Detail" src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/SqlValidation.cs`) before using it.
- Execution sends text unchanged: `src/SqlHarness.Core/SqlExecution.cs:339` (`BindCommand`, `CommandText = command.Sql`). `grep -rn "\.Batches" src/SqlHarness.Core` shows only `SqlSafety.cs` and `SqlValidation.cs`. No splitter exists.
- Existing tests that **already expect denial** for cross-batch scripts and must keep passing (reason `UnsupportedStatement`), e.g. `SqlSafetyTests.cs` ~761-771:

  ```csharp
      [InlineData("DECLARE @v int;\nGO\nSET @v = 1; SELECT @v")]
      [InlineData("DECLARE @v int = 1;\nGO\nSET @v = 2")]
      public void T2_Query_denies_SET_to_scalar_declared_in_previous_batch(string sql)
  ```

  and ~925-937 (`T3_Query_denies_table_variable_across_batches`).
- `src/SqlHarness.Core/SqlValidation.cs:254-275`: the parameter-reference collector closes scope per batch (`ExplicitVisit(TSqlBatch)`). It can stay as is; after this plan, multi-batch input never reaches execution.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Find multi-batch tests | `grep -rn '\\nGO\\n\|\nGO\n' tests --include=*.cs` | lists every test with GO |
| Focused tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlSafety|FullyQualifiedName~Validate|FullyQualifiedName~SqlParameterReference"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `src/SqlHarness.Core/SqlSafety.cs` (`Classify` only), `tests/SqlHarness.Tests/SqlSafetyTests.cs`, `tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs` (one CLI-level test), `AGENTS.md`, `README.md` (one sentence each).

**Out of scope**: splitting batches in execution; `SqlValidation.cs` reference logic; PostgreSQL (no `GO` concept; multiple statements there are real server statements).

## Git workflow

Branch `fix/plan-023-go-batches`. Commits `test(023): ...`, `fix(023): ...`, `docs(023): ...`. Do NOT push.

## Steps

### Step 1: Inventory existing multi-batch expectations

Run the grep from the table. For every test whose SQL contains a `GO` line, note whether it expects **allowed** or **denied**. Tests expecting denied stay correct. Tests expecting **allowed** will change verdict. List them in your working notes; if there are more than 5 such tests outside `SqlSafetyTests.cs`, STOP and report (the feature may be relied on more widely than expected).

### Step 2: RED test

Add a theory `Multi_batch_scripts_are_rejected` in `SqlSafetyTests.cs`:
- `SELECT 1\nGO\nSELECT 2`
- `CREATE TABLE #t (Id int)\nGO\nINSERT #t VALUES (1)`
- `SELECT 1\nGO`. A trailing `GO` gives a second, empty batch. Decide by ScriptDom's behavior: if `Batches.Count(b => b.Statements.Count > 0) == 1`, this row must be **allowed** (only one batch has statements). Write the assertion accordingly and keep it as documentation.

For rows with at least two non-empty batches: `Allowed == false`, `Reason == UnsupportedStatement`, in both `Query` and `CompareSetup`.

**Verify**: these rows fail today (they are allowed).

### Step 3: Reject in `Classify`

After the empty-script check, add:

```csharp
        // 023: GO is a client-tool separator; SQLHarness sends the text as one
        // batch, so classifying it as several batches would describe SQL the
        // server never runs.
        if (script.Batches.Count(batch => batch.Statements.Count > 0) > 1)
        {
            return Denied(SqlSafetyReason.UnsupportedStatement, "Batch separators (GO) are not supported; send a single batch.");
        }
```

If `detail` text flows into user-visible output, confirm it contains no SQL text. It is a constant, so it does not. If passing a detail breaks a pinned message test, drop the detail and use the plain `Denied(SqlSafetyReason.UnsupportedStatement)`.

Update the tests from Step 1 that expected **allowed** for multi-batch input: change them to expect denial **only if** the test's purpose was batch handling itself (011/T3b scope tests). If a test's purpose was something else, rewrite its SQL as a single batch so it keeps testing that thing. Record each changed test in the commit message.

**Verify**: focused tests pass.

### Step 4: CLI-level proof and docs

Add one test in `tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs`, following the file's existing pattern of running `validate <profile> --file <tmp> --json` with an offline profile. Assert that a two-batch file returns `allowed: false` with the `UnsupportedStatement` rule.

Docs: `AGENTS.md` (Safety/agent-authored SQL paragraph) and `README.md` (query section): "SQLHarness sends one batch; `GO` separators are rejected. Split work into separate invocations or use `--setup`."

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

- New: `Multi_batch_scripts_are_rejected` (3 rows × 2 usages) and 1 CLI validate test.
- Changed: only the multi-batch-allowed tests found in Step 1, each listed in the commit.

## Done criteria

- [ ] Focused tests and full gate pass
- [ ] `grep -n "Batch separators (GO)" src/SqlHarness.Core/SqlSafety.cs` → match (or the plain-denial variant with a comment `// 023:`)
- [ ] `AGENTS.md` and `README.md` mention that `GO` is rejected
- [ ] `plans/README.md` row updated

## STOP conditions

- More than 5 tests outside `SqlSafetyTests.cs` expect multi-batch input to be allowed (Step 1).
- Any MCP test (`tests/SqlHarness.Mcp.Tests`) fails. MCP validate may expose batch locations; report it.
- ScriptDom treats `GO` inside a string or comment as a separator. Add a test row `SELECT 'a\nGO\nb'` and expect it **allowed**; if it is denied, STOP.

## Maintenance notes

- If batch splitting is ever wanted, execution must run each `TSqlBatch` fragment by its exact text span on the same session, and measure/compare must define which batch is "measured". That is a design task, not a tweak to this rule.
- The per-batch scope machinery (011/T3b) stays useful for `--setup` text, which is also a single batch after this plan; it remains as defense in depth.
