# Plan 081: Session control flow in read-only SQL Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** plan 079 must be merged (message numbers in `ISqlSession.Messages`, streaming IO). `git diff --stat 1737091..HEAD -- src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/SqlMutationStatementMap.cs src/SqlHarness.Core/SqlExecution.cs src/SqlHarness.Core/SetupSqlExecution.cs src/SqlHarness.Core/SqlHarnessConfig.cs src/SqlHarness.Dashboard README.md`.

**Goal:** Admit session options, control flow, transactions (with automatic rollback) and read-only cursors on SQL Server read-only paths without loosening persistent-mutation classification, with opt-in dashboard notifications for notable events.

**Architecture:** The classifier gains usage-gated admissions (read-only usages only) that classify nested statements recursively with existing rules; the mutation path is untouched. The SQL Server session executor enforces post-batch rollback, discards connections whose cleanup fails, and restores session options between measured commands. Notable events become report warnings, journaled, and optionally toasted by the dashboard.

**Tech Stack:** .NET 10, Microsoft.SqlServer.TransactSql.ScriptDom, Microsoft.Data.SqlClient, xUnit; React + Vitest.

**Spec:** [docs/superpowers/specs/2026-10-16-session-control-flow-safety-design.md](../docs/superpowers/specs/2026-10-16-session-control-flow-safety-design.md)

## Review amendments (2026-10-16, cross-plan review) — these override task text below

1. **Threading the read-only gate (Task 1).** `ClassifyProvenStatement` has no usage today and the mutation path reuses it. Thread a `bool readOnlyAdmissions` through `ClassifyStatements` → `ClassifyStatement` → `ClassifyProvenStatement` → `ClassifyNestedStatement`: true only for `SqlUsage.CompareSetup` and for `SqlUsage.Query` with `allowMutation == false`; `ClassifyMutationNestedStatement` and the mutation entry always pass `false`. The characterization test stays as planned.
2. **Cursors.** Deny `GLOBAL` cursors (spec amended to LOCAL only); `EndLogicalBatchAsync` additionally deallocates any session cursors defensively. Fix the sample: `DECLARE @a int; DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT a FROM dbo.T; OPEN c; FETCH NEXT FROM c INTO @a; CLOSE c; DEALLOCATE c`.
3. **Cleanup placement (Task 2).** `EndLogicalBatchAsync` runs after `SET STATISTICS … OFF` and after the measured window's messages are consumed, so its messages never enter a measured window; it is a no-op for PostgreSQL.
4. **Message numbers** come from 079 amendment 5 (`SessionMessage(int Number, string Text)`); fakes emit numbered messages.
5. **Warnings storage (Task 4).** `has_warnings` is the plan-warning flag on `operation_metrics`, not operation warnings. Add `operations.warnings_json` in a new journal step (`VersionN`, next free; pin in `JournalContractTests`; follows 079's `FirstRetainedVersion` rule). Add MCP output tests that the new report warnings pass through 077's v3 projection.
6. **Missing tests.** `batch reached its timeout` notice; `RETURN 1` at batch level denied; `SET DEADLOCK_PRIORITY @v` denied (variables not allowed for this option).
7. **Toast.** Reuse the styling/mounting pattern of `ui/src/mutations/ApprovalToast.tsx`.
8. **Per-case sequence shared with 083:** connect → materialize TVP/list sources (083) → setup → capture session state (this plan) → measured runs.

## Status and dependencies

- IMPLEMENTED on `plan-081`; final verification pending (not DONE). See [verification evidence](081-verification-evidence.md). Planning base `1737091`. Depends on 079 (message numbers). Independent of 080. Stage C becomes plan 082, stage D plan 083.
- Priority P1; effort M; risk HIGH (safety classifier) — security review required before completion.

## Global Constraints

- Admissions apply only to read-only usages: `SqlUsage.Query` with `allowMutation == false` and `SqlUsage.CompareSetup`. `SqlUsage.Mutation` and query with `allowMutation == true` classify exactly as today (characterization tests must stay green unchanged).
- `EXEC`/`EXECUTE` of any kind stays denied on read-only paths.
- Allowed `SET` options (exact list): `NOCOUNT`, `XACT_ABORT`, `ARITHABORT`, `ANSI_NULLS`, `ANSI_WARNINGS`, `ANSI_PADDING`, `QUOTED_IDENTIFIER`, `CONCAT_NULL_YIELDS_NULL`, `NUMERIC_ROUNDABORT`, `DATEFIRST`, `DATEFORMAT`, `LOCK_TIMEOUT`, `DEADLOCK_PRIORITY` (`LOW`, `NORMAL` or numeric ≤ 0), `TRANSACTION ISOLATION LEVEL`. Everything else stays denied, explicitly including `STATISTICS *`, `SHOWPLAN_*`, `NOEXEC`, `PARSEONLY`, `FMTONLY`, `ROWCOUNT`, `IDENTITY_INSERT`, `CONTEXT_INFO`, `LANGUAGE`, `FORCEPLAN`, `IMPLICIT_TRANSACTIONS`, `REMOTE_PROC_TRANSACTIONS`, `OFFSETS`.
- Still denied: `WAITFOR`, `GOTO`, `RAISERROR … WITH LOG`, `BEGIN DISTRIBUTED TRAN`, `BEGIN TRAN … WITH MARK`, cursor `SCROLL_LOCKS`/`FOR UPDATE`, cursor variables.
- Rollback granularity = logical user batch; failed rollback or cleanup after timeout/cancel ⇒ `SqlConnection.ClearPool(connection)` and the operation fails with `open transaction could not be rolled back; connection discarded`.
- Warning texts (exact): `open transaction rolled back`, `isolation level SERIALIZABLE in effect` / `isolation level REPEATABLE READ in effect`, `batch reached its timeout`, `connection discarded after failed cleanup`.
- Notifications opt-in: `dashboard.safetyNotifications` (default `false`).
- No auto-formatting; commits `feat(081): …`; completion requires `just verify` and a security review of the classifier diff.

## Review Focus

1. A `WHILE` or `TRY` whose body contains persistent DML must be denied on read-only paths → Task 1 `Loop_around_persistent_dml_is_denied`, `Try_around_persistent_dml_is_denied`.
2. The same new constructs on the mutation path must classify exactly as today → Task 1 `Mutation_path_unchanged_for_new_constructs`.
3. A batch that times out inside `BEGIN TRAN` must not return a connection with an open transaction to the pool → Task 2 `Timeout_inside_transaction_discards_connection`.
4. `SET ANSI_NULLS OFF` in the baseline must not affect the candidate → Task 3 `Measured_options_do_not_leak_between_variants`.
5. `PRINT` imitating statistics must not change measured IO/time (relies on 079) → Task 2 `Print_imitating_statistics_is_ignored` (integration through the executor).

---

### Task 0: Drift check and baseline

- [x] Confirm 079 merged; run the drift check; run baseline `just verify`; record base commit and result here. 079 is an ancestor (`13b468a`); drift checked against `1737091`. Baseline gate exited 1 because the in-progress Task 1 RED tests were compiled into the test run; build and UI passed, all existing tests passed, and 29 expected new RED failures were reported. This is not a baseline gate pass; see [verification evidence](081-verification-evidence.md).

### Task 1: Classifier admissions (read-only usages)

**Files:**
- Modify: `src/SqlHarness.Core/SqlSafety.cs` (`ClassifyProvenStatement` ~`:470-620`, statement dispatch ~`:180-220`, `IsReadOnlyUsage` helper), `src/SqlHarness.Core/SqlMutationStatementMap.cs` (only if a shared list must exclude new types)
- Test: `tests/SqlHarness.Tests/SqlSafetyTests.cs`, `SqlSafetyNodeCoverageTests.cs`, `SqlSafetyTierTests.cs`; create `tests/SqlHarness.Tests/SqlSafetyControlFlowTests.cs`

**Interfaces:**
- Produces: `StatementClassification` unchanged in shape; new admitted statements classify `ReadOnly` or `SessionLocal` (transactions, cursors, options: `SessionLocal`); `SqlSafetyDecision.Notices` (new `IReadOnlyList<string>`, e.g. `isolation level SERIALIZABLE in effect`) consumed by Task 2.

- [x] **Step 1: Write failing tests** (`[Theory]` over SQL text; usage `Query`, `allowMutation=false` unless stated):
  - Admitted: each allowed `SET` option; `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`; `DECLARE @i int = 0; WHILE @i < 3 BEGIN SET @i += 1; IF @i = 2 CONTINUE; IF @i = 3 BREAK; END`; `BEGIN TRY SELECT 1 END TRY BEGIN CATCH SELECT ERROR_NUMBER() END CATCH`; `IF 1=0 RETURN; SELECT 1`; `THROW 50000, 'x', 1`; `RAISERROR('x', 10, 1) WITH NOWAIT`; `PRINT 'x'`; `BEGIN TRAN; INSERT #t VALUES (1); COMMIT`; `SAVE TRAN s; ROLLBACK TRAN s`; `DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT a FROM dbo.T; OPEN c; FETCH NEXT FROM c INTO @a; CLOSE c; DEALLOCATE c`; `WITH x AS (SELECT a FROM @t) UPDATE x SET a = 2` (table variable declared earlier).
  - Denied: `SET STATISTICS IO ON`, `SET SHOWPLAN_XML ON`, `SET ROWCOUNT 10`, `SET IDENTITY_INSERT #t ON`, `SET DEADLOCK_PRIORITY HIGH`, `SET DEADLOCK_PRIORITY 5`, `WAITFOR DELAY '00:00:01'`, `GOTO x; x: SELECT 1`, `RAISERROR('x', 10, 1) WITH LOG`, `BEGIN DISTRIBUTED TRAN`, `BEGIN TRAN t WITH MARK`, `DECLARE c CURSOR SCROLL_LOCKS FOR SELECT a FROM dbo.T`, `DECLARE c CURSOR FOR SELECT a FROM dbo.T FOR UPDATE`, `DECLARE @c CURSOR; SET @c = CURSOR FOR SELECT 1`, `EXEC dbo.P`, `EXEC sp_executesql N'SELECT 1'`, `WITH x AS (SELECT t.a FROM @t t JOIN dbo.T d ON d.a = t.a) UPDATE x SET a = 2`.
  - `Loop_around_persistent_dml_is_denied`: `WHILE 1=0 UPDATE dbo.T SET a = 1` → denied (`NonTemporaryWrite`/`MutationNotAllowed` as today for the bare statement). `Try_around_persistent_dml_is_denied` likewise.
  - `Mutation_path_unchanged_for_new_constructs`: for every admitted sample, `SqlUsage.Mutation` and `Query` with `allowMutation=true` return the same decision as at the planning base (record expected values by running the base first).
  - Parameterized setup (`SqlUsage.CompareSetup` with parameters): `BEGIN TRAN` or `RETURN` in the parameter-free prefix → denied.
  - Notices: `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE` → notice `isolation level SERIALIZABLE in effect`.
- [x] **Step 2:** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety"` → new tests FAIL, existing PASS.
- [x] **Step 3:** Implement admissions in `ClassifyProvenStatement` behind `IsReadOnlyUsage`; recurse into `WhileStatement`, `TryCatchStatement` like `BeginEndBlockStatement`; cursor `SELECT` classified by existing rules; CTE fix per spec "Calls settled" item 7. Extend `SqlSafetyNodeCoverageTests` for new node types.
- [x] **Step 4:** Focused safety suite passed (595 tests after review fixes).
- [x] **Step 5:** Committed `feat(081): admit session control flow on read-only paths` (implementation commit chain `c8a449b..abbd601`). Review clean of Critical/Important findings.

### Task 2: Transaction cleanup and connection discard

**Files:**
- Modify: `src/SqlHarness.Core/SqlExecution.cs:179-370` (`SqlClientSessionFactory`, `SqlClientSession`), `src/SqlHarness.Core/SetupSqlExecution.cs:~120` (logical batch boundary), report warnings in `Contracts.cs` (`Warnings` on query/measure/compare reports, existing pattern)
- Test: `tests/SqlHarness.Tests/SqlExecutionTests.cs` (create if absent) using the existing fake session patterns; `DialectMessageBudgetTests.cs`

**Interfaces:**
- Produces: `ISqlSession.EndLogicalBatchAsync(CancellationToken ct) : Task<IReadOnlyList<string>>` — runs `IF @@TRANCOUNT > 0 ROLLBACK;` and returns warnings (`open transaction rolled back`); on failure or after a timeout/cancel it calls `SqlConnection.ClearPool`, marks the session unusable and throws `SessionCleanupException("open transaction could not be rolled back; connection discarded")`. All runners (query, measure, compare, watch, snapshot, setup) call it after each logical batch.

- [x] **Step 1: Failing tests:** `Open_transaction_is_rolled_back_with_warning`; `Timeout_inside_transaction_discards_connection` (fake throws timeout → `ClearPool` called, operation error text exact); `Failed_rollback_fails_operation`; `Setup_split_rolls_back_once_after_whole_setup`; `Print_imitating_statistics_is_ignored` (fake session emits message number 0 with IO-like text; measured IO unchanged).
- [x] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS (focused cleanup/setup/message suite 64 passed after review fixes).
- [x] **Step 5:** Committed `feat(081): roll back open transactions and discard unclean connections` (implementation commit chain `2232730..fd25a55`). Review clean of Critical/Important findings.

### Task 3: Session option isolation between measured commands

**Files:**
- Modify: `src/SqlHarness.Core/CompareCellRunner.cs:186-218`, `src/SqlHarness.Core/MeasureParameterSets.cs` (079's per-case runner), `src/SqlHarness.Core/Dialect/SqlServerDialect.cs`
- Test: `tests/SqlHarness.Tests/CompareTests.cs`, `MeasureTests.cs`

**Interfaces:**
- Produces: `SqlServerDialect.CaptureSessionStateAsync(ISqlSession s, CancellationToken ct) : Task<SessionState>` (`@@OPTIONS`, isolation level from `sys.dm_exec_sessions`, `@@LOCK_TIMEOUT`, `@@DATEFIRST`, `DATEFORMAT`, deadlock priority) and `RestoreSessionStateAsync(ISqlSession s, SessionState state, CancellationToken ct)`; runners capture after setup and restore after every measured command.

- [x] **Step 1: Failing tests:** `Measured_options_do_not_leak_between_variants` (baseline sets `ANSI_NULLS OFF`, candidate observes the post-setup state); `Setup_options_persist_for_the_case` (setup `SET NOCOUNT ON` visible in measured runs).
- [x] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS (Compare/Measure focused suite 96 passed).
- [x] **Step 5:** Committed `feat(081): isolate session options between measured commands` (`4eb2315`). One deferred minor review note: no dedicated per-parameter-set capture/restore regression. Review found no Critical/Important issues.

### Task 4: Notices, journal and opt-in dashboard notifications

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessConfig.cs` (`DashboardConfig.SafetyNotifications`, default `false`), operation warning journal storage, MCP query warning projection, `src/SqlHarness.Dashboard/JournalReader.cs` and `DashboardModels.cs`, settings UI and safety-notice toast. `LiveFeed.cs` and `DashboardSettings.cs` needed no changes: existing generic warning/config pass-through already supports the behavior, as confirmed in Task 4 review.
- Test: `tests/SqlHarness.Tests/Journal/SqlHarnessConfigTests.cs`, `Dashboard/LiveFeedTests.cs`, `DashboardSettingsTests.cs`; `SettingsPage.test.tsx`; create `SafetyNoticeToast.test.tsx`

- [x] **Step 1: Failing tests:** config default `false` and round-trip; live feed operation event includes notices from Tasks 1–3; toast renders notice text and links to the operation only when the setting is `true`; never rendered when `false`; toast does not block or alter anything else.
- [x] **Step 2:** → FAIL. **Step 3:** Implement. **Step 4:** → PASS (scoped backend, MCP, and UI tests; see Task 4 report).
- [x] **Step 5:** Commit `feat(081): opt-in dashboard safety notifications`.

### Task 5: Contracts, documentation, security review, verification

- [x] **Step 1:** README "Safety contract" documents admitted constructs, explicit denied options/statements, rollback/discard rules, option isolation and notifications; SQL Server engine guidance/capabilities and MCP tool docs carry the interface contract. The user skill does not name rejected constructs and needed no change.
- [ ] **Step 2:** Security review of the classifier and executor diff (repo `/security-review` or a fresh reviewer focused on persistent-effect paths); fix findings.
- [ ] **Step 3:** `just verify` → exit 0; record revision and result.
- [ ] **Step 4 (operator-authorized, optional):** UAT dry-run of the 20-construct survey and one measured procedure body with `SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRAN … COMMIT`; record outcomes without business data.
- [ ] **Step 5:** Queue → history with evidence; commit `docs(081): record plan 081 evidence`.
