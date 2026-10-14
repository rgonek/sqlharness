# Plan 047 step 1 — READ ONLY proof on a disposable PostgreSQL

## Environment

- Server: PostgreSQL **18.6** on x64-pc-windows-msvc, `initdb` cluster in `D:\temp\opencode\pg47\data`, trust auth, `localhost:5434`, database `pg47proof`. Disposable: it holds only proof objects (`p47_*`) and the cluster directory is deleted after the plan closes.
- Why not the Docker playground: `sqlharness-pg` (host port 5433, `scripts/setup-local-postgres.ps1`) was not reachable — Docker Desktop was not serving the Linux engine API during this run. The plan's STOP condition asks for an *authorized disposable* PostgreSQL, not specifically the container; the throwaway cluster is local, holds no real data, and is on a newer major version than the playground image `postgres:16`.
- Scripts: `D:\temp\opencode\pg47\proof2.sql` (semantics) and `proof3.sql` (timings); captured output in the matching `-output.txt` files.
- Client: `psql` 18.6 with `lc_messages = 'C'` (English server messages) and `client_encoding = 'UTF8'`.

## Documentation basis

- PostgreSQL 18, **8.1. Read-only Transactions**: "The SQL standard defines a read-only transaction ... If a statement in a read-only transaction would modify ... the database ... it is rejected with error `25006` (`read_only_sql_transaction`)"; and "a read-only transaction ... `INSERT`, `UPDATE`, `DELETE`, `MERGE` ... *on temporary tables* are allowed."
- PostgreSQL 18, **16.3. SET TRANSACTION**: `READ ONLY` / `READ WRITE` per transaction; `SET SESSION CHARACTERISTICS` sets the default for all subsequent transactions of a session.
- PostgreSQL 18, **23.1. Temporary Tables** and **5.11.2. A Function Definition**: `CREATE TEMP TABLE`, `CREATE TABLE ... AS`, `SELECT INTO`, `CREATE INDEX`, `TRUNCATE`, `DROP` are all DDL and are rejected in a read-only transaction (temporary tables are not exempt from the DDL ban, only from the DML ban).

## Results

Each row is the verbatim server outcome, with the proof script section.

| # | Case | Result | Section |
|---|---|---|---|
| 1 | `BEGIN READ ONLY`, `INSERT`/`UPDATE`/`DELETE`/`MERGE` on a **temp** table | **allowed** — `INSERT 0 1`, `UPDATE 1`, `DELETE 1`, `MERGE 1`; final temp contents `{1:ro, 3:ro, 4:m}` | B |
| 2 | `BEGIN READ ONLY`, the same four on a **persistent** table | **rejected** — `ERROR: cannot execute INSERT in a read-only transaction` (SQLSTATE 25006), one statement per transaction so all four were observed | C |
| 3 | `SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY` (`SHOW transaction_read_only` = `on`), DML on a temp table | **allowed** — same four command tags and same resulting rows as #1 | D |
| 4 | same session-level read-only, DML on a persistent table, and `SELECT * INTO TEMP` | **rejected** — `cannot execute INSERT/UPDATE/DELETE/MERGE in a read-only transaction`, `cannot execute SELECT INTO in a read-only transaction` | E |
| 5 | setup DDL under the session-level read-only: `CREATE TEMP TABLE`, `CREATE TABLE ... AS TEMP`, `SELECT INTO TEMP`, `CREATE INDEX` ×3, `TRUNCATE` ×3, `DROP TABLE` ×3 | **all rejected** — `cannot execute CREATE TABLE / CREATE TABLE AS / SELECT INTO / CREATE INDEX / TRUNCATE TABLE / DROP TABLE in a read-only transaction` | F |
| 6 | unqualified name with a persistent table of the same name in `public` and a temp table of the same name, `SET search_path TO public, pg_temp`, session read-only | **rejected** — the unqualified `INSERT` resolves to the *persistent* `public.p47_shadow` and fails with 25006. Catalog check confirms both relations exist (`public.p47_shadow`, `pg_temp_5.p47_shadow`). With `SET search_path TO pg_temp, public` the same unqualified `INSERT` is **allowed** (`INSERT 0 1`) and lands in the temp table; `public.p47_shadow` stays empty | G |
| 7 | volatile `plpgsql` function that inserts into a persistent table | **rejected** — `cannot execute INSERT in a read-only transaction`, `CONTEXT: PL/pgSQL function p47_volatile_write() line 1 at SQL statement` | H |
| 8 | `nextval('p47_seq')` on a persistent sequence | **rejected** — `cannot execute nextval() in a read-only transaction` | H |
| 9 | `pg_advisory_lock(4711)` / `pg_advisory_unlock` | **allowed** (`t`, `t`) — advisory locks are not writes; this is the known residual, mitigated by connection lifetime | H |
| 10 | `pg_sleep(0)` | **allowed** | H |
| 11 | `SET transaction_read_only = off` inside a read-only session | accepted syntactically but **ineffective** — `SHOW transaction_read_only` still `on` and the following `INSERT` is rejected. Only `SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE` restores write ability (`SHOW` = `off`, `INSERT 0 1`). The classifier denies `SET` offline in any case | I |
| 12 | `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) UPDATE` on a temp table inside `BEGIN READ ONLY` | **works** — full ModifyTable plan, `Actual Rows: 50000`, `Local Written Blocks: 1227`, `Execution Time: 257.748` | K |
| 13 | `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) UPDATE` on a *persistent* table inside `BEGIN READ ONLY` | **rejected** — `cannot execute UPDATE in a read-only transaction` (the analyzer executes the statement) | L |
| 14 | reads under session read-only: temp table created *before* the switch, and a persistent table | **allowed** — 200000 temp rows, 3 persistent rows; the persistent table is unchanged afterwards | M |

## Timing check (plan STOP condition)

Same statement five times in a row against a 200 000-row temp table with `max_parallel_workers_per_gather = 0` and `synchronous_commit = off`, `UPDATE ... WHERE id <= 50000`:

| Phase | Repeats (ms) | Median |
|---|---|---|
| read-write | 834.3, 787.0, 885.4, 861.7, 830.9 | **834.3** |
| read-only | 866.3, 770.7, 1178.1, 676.8, 585.6 | **770.7** |

The ranges overlap almost completely and the read-only median is the *lower* of the two. No material timing change is attributable to the read-only transaction. This is a Windows host with a single sample set, so it shows the absence of a systematic penalty, not a precise ratio.

## Decision (plan step 2)

The proof holds: **persistent writes are rejected with 25006 and temporary DML is allowed**, in both `BEGIN READ ONLY` and session-default read-only.

1. No session-level `SET SESSION CHARACTERISTICS` change is needed. Plan 052 already wraps every non-setup, non-approved-mutation execution in `SET TRANSACTION READ ONLY` (`NpgsqlSessionFactory.NpgsqlSession.ExecuteReaderAsync`), and `PostgresReadOnlyFailure.Map` already turns 25006 into `SqlHarnessSafetyException` (exit 2). Cases 2, 4, 6, 7, 8, 11 and 13 are exactly that path.
2. Setup DDL stays where it is: session-setup executions run read-write (case 5 shows the server rejects it otherwise), so `CREATE TEMP`, `CREATE INDEX`, `TRUNCATE` and `DROP` of proven temps keep their name proof.
3. Classifier relaxation is safe for read-only phases only. An unqualified DML target no longer needs a name proof: if it resolves to the session temp, the write succeeds (cases 1, 3); if it resolves to a persistent table, the server rejects it with 25006 and the caller sees a safety rejection (cases 2, 6). The approved mutation path still runs read-write, so it keeps the name proof.
4. Residual after the change, stated for the record: advisory locks (`pg_advisory_lock`) and other non-DDL side effects still succeed under read-only (case 9). Least-privilege roles remain the operator's control.

## Unverified / limits

- One server major version (18.6), one platform. The DML/DDL split for temporary tables is documented behaviour and is unchanged across 14–18, but the proof itself is 18.6.
- The Docker playground was not used; if the maintainers want the numbers on `postgres:16`, re-run the two scripts there and diff this file.
