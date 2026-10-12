<!-- pending-proof-template: safe to replace after an authorized run -->
# Plan 052 PostgreSQL read-only proof

**Evidence status: PENDING.** No PostgreSQL connection was made for this work. The required disposable target is unavailable and unauthorized, so this file contains no live proof or measured result.

Run the prepared proof only after a disposable PostgreSQL target is explicitly available and authorized:

```powershell
$env:SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING = '<connection string held only in this process environment>'
pwsh ./scripts/prove-postgres-readonly.ps1 -ConfirmDisposableDatabase '<exact database name>'
```

The runner records the server version, SQLSTATE and outcome for each case, 100-repetition `SELECT 1` timings, cleanup status, and the relevant PostgreSQL documentation section. It does not print the connection string or password. Review every outcome before treating step 1 as proven.

| Case | Outcome | SQLSTATE | Detail |
|---|---|---|---|
| Persistent write through PL/pgSQL function outside a read-only transaction | PENDING | — | Must demonstrate the uncontained function effect. |
| Same function inside a `READ ONLY` transaction | PENDING | — | Expect SQLSTATE `25006`. |
| Persistent table after rejected function write | PENDING | — | Must remain unchanged from the uncontained baseline. |
| `pg_stat_statements_reset()` inside a `READ ONLY` transaction | PENDING | — | Record read-only rejection, privilege failure, or unavailable function exactly. |
| `nextval()` on a persistent sequence inside a `READ ONLY` transaction | PENDING | — | Record server outcome and SQLSTATE. |
| `pg_promote()` inside a `READ ONLY` transaction | PENDING | — | Record read-only rejection or privilege failure. |
| Next per-command transaction after `set_config('default_transaction_read_only', 'off', false)` | PENDING | — | Expect SQLSTATE `25006`. |
| `INSERT`, `UPDATE`, and `DELETE` against a TEMP table in read-only transactions | PENDING | — | Each must succeed. |
| `EXPLAIN ANALYZE` for TEMP DML | PENDING | — | INSERT, UPDATE, and DELETE are each measured. |
| `EXPLAIN ANALYZE` for function persistent write | PENDING | — | Expect SQLSTATE `25006`. |

## Wrapper timing

PENDING: timing has not been run. The prepared measurement uses 10 alternating warm-up pairs, then 100 measured pairs with balanced order (50 bare-first, 50 wrapper-first), and reports paired totals plus per-run and paired-delta median/p95 distributions.

## Cleanup

PENDING: no proof schema or session-local objects were created.

## Documentation reference

[PostgreSQL `SET TRANSACTION`](https://www.postgresql.org/docs/current/sql-set-transaction.html), section `SET TRANSACTION`.
