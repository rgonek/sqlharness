# Session control flow in read-only SQL (safety stage) — design

Date: 2026-10-16. Plan: 081 (to be written from this design). Sits between stage A/B (plans 079/080) and stage C
(per-statement IO). Origin: UAT spike of 2026-10-16 and an offline dry-run survey of 20 constructs against the
current classifier; owner's decisions recorded below.

## Intent

Real application SQL (stored procedure bodies, inline Dapper batches) routinely starts with session options and uses
control flow and transactions. Today the SQL Server classifier denies all of these on every read-only path
(`query`, `measure`, `compare`, `watch`, `snapshot`, setup), so an agent cannot measure a procedure body pasted as a
batch — it fails on its first line (`SET NOCOUNT ON`). Measured on the owner's five application repositories, the
blocked constructs are common: `SET XACT_ABORT ON` (Wind 52 `.sql` files), `BEGIN TRAN` (Wind 82, PartnerZone 58),
`RETURN` (PartnerZone 102), `WHILE` (Wind 26, PartnerZone 24), `SET TRANSACTION ISOLATION LEVEL` (dozens of `.cs`).

This stage admits constructs whose effects stay inside the session (or are pure control flow) while keeping the
persistent-mutation classification exactly as strict as today.

## Owner's decisions

- Admit the "should pass" group (below), with automatic rollback of any transaction left open.
- `EXEC` (procedures and `sp_executesql`) stays denied on read-only paths (option b): agents paste the procedure body
  instead. Mutation approval (plan 073) is a separate tool and does not change this.

## Goals

Admit on SQL Server read-only paths, classifying every nested statement with the existing rules (a construct is
admitted only when everything inside it is admitted):

1. **Session options** (`PredicateSetStatement` and siblings), allowlist only: `NOCOUNT`, `XACT_ABORT`, `ARITHABORT`,
   `ANSI_NULLS`, `ANSI_WARNINGS`, `ANSI_PADDING`, `QUOTED_IDENTIFIER`, `CONCAT_NULL_YIELDS_NULL`,
   `NUMERIC_ROUNDABORT`, `DATEFIRST`, `DATEFORMAT`, `LOCK_TIMEOUT`, `DEADLOCK_PRIORITY`, and
   `SET TRANSACTION ISOLATION LEVEL …`.
2. **Control flow**: `WHILE` (with `BREAK`/`CONTINUE`), `BEGIN TRY … END TRY BEGIN CATCH … END CATCH`, `RETURN`
   (without a value at batch level), `THROW`, `RAISERROR` (without `WITH LOG`), `PRINT`.
3. **Transactions**: `BEGIN TRAN[SACTION]`, `COMMIT`, `ROLLBACK`, `SAVE TRAN`. After every executed command on a
   read-only path (setup, warm-up, measured run, query, watch poll, snapshot), SQLHarness checks `@@TRANCOUNT` and
   rolls back if it is greater than zero, reporting a warning (`open transaction rolled back`).
4. **Cursors** over admitted `SELECT`s: `DECLARE … CURSOR` (local or global), `OPEN`, `FETCH` (into admitted local
   variables), `CLOSE`, `DEALLOCATE`. `WHERE CURRENT OF` updates/deletes follow the normal DML rules (persistent
   targets stay denied).
5. **Bug fix**: DML through a CTE whose base is a same-batch table variable or local `#temp`
   (`WITH x AS (SELECT … FROM @t) UPDATE x …`) is session-local, not `MutationNotAllowed`.

## Non-goals

- `EXEC` of any kind on read-only paths (owner's decision b).
- PostgreSQL equivalents (`DO` blocks, PL/pgSQL); this stage is SQL Server only.
- Measuring persistent writes (with or without rollback) — a later, separate decision.
- Per-statement IO for loops and many-plan batches — stage C (it must now handle them).

## Anti-goals

- Any path by which an admitted construct reaches a persistent effect that the current classifier would deny.
- Session options that change measurement or output semantics: `SET STATISTICS IO|TIME|XML|PROFILE`, `SHOWPLAN_*`,
  `NOEXEC`, `PARSEONLY`, `FMTONLY`, `ROWCOUNT`, `IDENTITY_INSERT`, `CONTEXT_INFO`, `LANGUAGE`, `FORCEPLAN`,
  `IMPLICIT_TRANSACTIONS`, `REMOTE_PROC_TRANSACTIONS`, `OFFSETS` — they stay denied.
- `WAITFOR`, `GOTO`, `RAISERROR … WITH LOG` stay denied.
- Leaving a transaction or its locks open on the shared UAT server.

## Constraints

- AGENTS.md: protect persistent-mutation classification; interface meaning in tool descriptions/schemas, runtime
  policy in capabilities; README "Safety contract" is the human contract and must list the new admitted constructs,
  the denied options and the rollback rule.
- Existing timeouts bound loops; no new loop limit.
- A security review of the classifier diff is part of verification (repo `/security-review` or equivalent).
- Interaction with plan 079: `PRINT`/`RAISERROR` messages share the message stream; plan 079 Task 5 filters
  statistics by message number (see "Calls settled", item 3). Plan 081 depends on 079.
- Interaction with plan 073: the mutation path's classification of these constructs must not loosen; admitted
  constructs inside a mutation request keep being classified by the mutation rules.

## Dashboard notifications (owner's addition)

Admitted-but-notable events raise a non-blocking dashboard notification (toast) in addition to the report warning:
an automatic rollback of an open transaction, `SERIALIZABLE`/`REPEATABLE READ` isolation, a loop or batch that hit
its timeout, a connection discarded after a failed cleanup. Notifications are **opt-in**: a Settings toggle
(`dashboard.safetyNotifications`, default `false`). They reuse the dashboard's live feed and toast styling (plan 073
UI) and never block or change the operation's outcome; report warnings exist regardless of the toggle.

Approval-gated `EXEC` was considered and **dropped by the owner**; `EXEC` stays denied on read-only paths.

## Calls settled after the builder check (my calls, fail closed)

1. **Mutation path unchanged.** New admissions apply only to read-only usages (query without mutation, measure,
   compare, watch, snapshot, setup). The mutation tool and the query `allowMutation` path classify exactly as
   today; the CTE fix also applies only to read-only usages. No tier changes for `yolo`/approval.
2. **Rollback failure dooms the connection.** After a timeout, cancellation, broken command, or a failed `ROLLBACK`,
   the target connection is removed from the pool (`SqlConnection.ClearPool` for that connection) and the operation
   reports `open transaction could not be rolled back; connection discarded`. A failed rollback fails the
   operation.
3. **Measurement integrity against user messages.** `PRINT`/`RAISERROR` can imitate `STATISTICS IO/TIME` text.
   The session keeps each message's error number; IO/time parsers consume only engine statistics messages (IO 3615,
   time 3612/3613 and their siblings as verified by the builder). This changes the `ISqlSession.Messages` contract and
   is implemented in **plan 079 Task 5** (streaming IO), which plan 081 depends on.
4. **Session options do not leak between variants or runs.** Options set by setup persist for that case's session
   (intended). Options set by measured SQL are restored after each measured command to the state captured right
   after setup (`@@OPTIONS`, isolation level, `LOCK_TIMEOUT`, `DATEFIRST`, `DATEFORMAT`, `DEADLOCK_PRIORITY`), so
   baseline and candidate start from the same state.
5. **Value limits.** `DEADLOCK_PRIORITY` only `LOW`, `NORMAL` or numeric ≤ 0; `LOCK_TIMEOUT` any value (the command
   timeout bounds it). Cursor options `SCROLL_LOCKS`, `FOR UPDATE` and cursor variables (`SET @c = CURSOR …`) stay
   denied. `BEGIN DISTRIBUTED TRAN` and `BEGIN TRAN … WITH MARK` stay denied.
6. **Rollback granularity is the logical user batch** (all commands `SetupSqlExecution.PrepareCommands` produces for
   one setup; one measured run). In parameterized setup, transaction control and `RETURN` are denied in the
   parameter-free prefix so control flow cannot cross the split.
7. **CTE fix scope:** DML through a CTE chain is session-local only when the chain resolves to exactly one base
   source that is a same-batch table variable or local `#temp`, with no joins; anything else keeps today's
   classification.

## Left to the builder

- Exact ScriptDom statement types for each construct and how nested classification is composed.
- Where the `@@TRANCOUNT` check lives (session executor) and how the warning reaches reports and the journal.
- Dry-run fixtures and characterization tests mirroring the 20-construct survey (admitted ↔ denied).
