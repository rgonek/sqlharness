# Plan 052 PostgreSQL read-only proof

> Evidence captured by an explicitly confirmed disposable-target run. Review each outcome before treating step 1 as proven.

- Evidence status: EXECUTED; interpreted 2026-10-12
- PostgreSQL target: `127.0.0.1:55452/sqlharness_plan052`
- PostgreSQL version: `PostgreSQL 18.6 on x86_64-windows, compiled by msvc-19.44.35228, 64-bit`
- Documentation: PostgreSQL 18.6 [`SET TRANSACTION`](https://www.postgresql.org/docs/18/sql-set-transaction.html), section `SET TRANSACTION`.

| Case | Outcome | SQLSTATE | Detail |
|---|---|---|---|
| Function write outside READ ONLY transaction | PASS | none | executed without a transaction wrapper |
| Uncontained persistent effect observed | PASS | none | rows=1 |
| Function persistent write inside READ ONLY transaction | REJECTED | 25006 | PostgresException |
| Persistent table unchanged after read-only rejection | PASS | none | rows=1 |
| pg_stat_statements_reset() inside READ ONLY transaction | PASS | none | returned timestamp: 2026-10-12 08:52:19 |
| nextval() on persistent sequence inside READ ONLY transaction | REJECTED | 25006 | PostgresException |
| pg_promote() inside READ ONLY transaction | REJECTED | 55000 | PostgresException |
| Next per-command READ ONLY transaction after default changed to off | REJECTED | 25006 | PostgresException |
| INSERT INTO pg_temp.sqlharness_readonly_temp VALUES (1) | PASS | none | affected rows: 1 |
| UPDATE pg_temp.sqlharness_readonly_temp SET id = 2 WHERE id = 1 | PASS | none | affected rows: 1 |
| DELETE FROM pg_temp.sqlharness_readonly_temp WHERE id = 2 | PASS | none | affected rows: 1 |
| EXPLAIN (ANALYZE, COSTS OFF) INSERT INTO pg_temp.sqlharness_readonly_temp VALUES (3) | PASS | none | READ ONLY transaction committed |
| EXPLAIN (ANALYZE, COSTS OFF) UPDATE pg_temp.sqlharness_readonly_temp SET id = 4 WHERE id = 3 | PASS | none | READ ONLY transaction committed |
| EXPLAIN (ANALYZE, COSTS OFF) DELETE FROM pg_temp.sqlharness_readonly_temp WHERE id = 4 | PASS | none | READ ONLY transaction committed |
| EXPLAIN ANALYZE function persistent write inside READ ONLY transaction | REJECTED | 25006 | PostgresException |

## Interpretation

- The uncontained PL/pgSQL function inserted one persistent row. The same function and `EXPLAIN ANALYZE` form were rejected inside `READ ONLY` with SQLSTATE `25006`; the row count remained one. Persistent-sequence `nextval` and the next per-command transaction after changing the session default also returned `25006`.
- TEMP `INSERT`, `UPDATE`, and `DELETE` committed inside read-only transactions, and all three TEMP `EXPLAIN ANALYZE` DML cases succeeded. This proof covers commands inside READ ONLY; it does not test or prove containment for `--setup`, which remains read-write under existing static visible-effects and temp-target checks. A setup SELECT may invoke a persistent-writing user-defined function. Review setup SQL and use a least-privilege role; the classifier cannot promise every such effect is rejected. Any stricter setup policy belongs in follow-up hardening, not this plan.
- `pg_stat_statements_reset()` succeeded inside the read-only transaction and returned a timestamp. Its effect is not contained by this transaction mode; retain an explicit function denial until an approved alternative protects that effect.
- `pg_promote()` returned SQLSTATE `55000` on this primary server because it was not in recovery. This records a server-state rejection and does not demonstrate read-only enforcement. Keep promotion and backup operations explicitly denied. The proof did not test `pg_backup_start()`.

## Wrapper timing

10 warm-up pairs; 100 measured pairs, balanced alternating order (bare→wrapped 50, wrapped→bare 50); paired totals: bare=24,491 ms, READ ONLY wrapper=56,540 ms, delta=32,050 ms (130,9%); per-run median/p95: bare=0,198/0,342 ms, wrapped=0,524/0,812 ms; paired delta median/p95=0,323/0,452 ms.

The wrapped timer includes `BeginTransaction`, `SET TRANSACTION READ ONLY`, `SELECT 1`, and `COMMIT`; the bare timer includes only `SELECT 1`. This is an end-to-end command-cost comparison, not an isolated server execution comparison. Do not claim no overhead: this local trivial query measured a 130.9% aggregate increase and a 0.323 ms median paired delta. SQLHarness PostgreSQL benchmark timings come from server-side `EXPLAIN` `Planning Time + Execution Time` in [`PostgresBenchmark.ParseStats`](../src/SqlHarness.Core/Postgres/PostgresBenchmark.cs), so this client-side wrapper cost is not included in that metric.

## Cleanup

PASS: proof schema dropped; temporary table removed with session
