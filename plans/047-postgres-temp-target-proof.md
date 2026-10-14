# Plan 047: Let PostgreSQL enforce "no persistent writes" before simplifying the temp-name proof

> **Narrowed 2026-10-11:** the READ ONLY transaction for every non-approved command moved to [052](052-postgres-read-only-transactions.md) (P1). This plan now covers only what remains after 052: simplifying the name-based proof for setup DDL (CREATE TEMP, CREATE INDEX, TRUNCATE, DROP) and unqualified temp DML in setup. Execute after 052 and reuse its proof file.

> Read current source before editing. Inspect drift since `cadde67` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition. Revised 2026-10-11: the earlier version required `pg_temp.` qualification for every temp write (a breaking change). This version first tests whether the server can enforce the boundary.

## Status

- Status: IN PROGRESS — user-confirmed 2026-10-13; execute the whole plan.
- Priority: P2 (PostgreSQL queue)
- Effort: M
- Risk: MED
- Depends on: 052 (DONE first); a disposable PostgreSQL for step 1
- Planned at: `729d88f`; revised at `cadde67`, 2026-10-11

## Why this matters

The PostgreSQL classifier decides offline whether a write target is a current-session temp table by tracking names: unqualified names recorded from `CREATE TEMP TABLE`, quoting and 63-byte limits, `pg_temp_<N>` exclusions, `DROP TABLE` revoking proofs, and a known `search_path` hole for unqualified names. TRUNCATE uses a stricter rule than DML. The AGENTS.md paragraph describing this is the longest rule in the file, and agents still trip on it.

PostgreSQL itself refuses writes to non-temporary tables inside a `READ ONLY` transaction, while allowing DML on temporary tables. If that holds for the statement shapes SQLHarness runs, the server enforces the persistent-write boundary independently of name resolution and `search_path`, and the name proof can shrink to the DDL that a read-only transaction rejects.

This is **unverified**. Step 1 is the proof; the design depends on it.

## Current state

- `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs`: `IsProvenSessionTemp` (TRUNCATE) and `IsSessionLocal` (DML/DROP/CREATE INDEX) implement the name proof.
- `src/SqlHarness.Core/Postgres/NpgsqlSessionFactory.cs`: `BindClassifiedCommand` sends classified text with "no transaction wrapper, role change, privilege change, or search_path edit".
- Measured statements run under `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`; equivalence uses an unmeasured sidecar.
- Docs: AGENTS.md PostgreSQL engine notes; `plans/011-syntax-contract.md`; `plans/009-strict-profile-assessment.md`.

## Scope

In scope: the classifier, `NpgsqlSessionFactory.cs`, `PostgresQuery`/`PostgresBenchmark` execution paths, PostgreSQL safety and opt-in integration tests, AGENTS.md PostgreSQL notes, `docs/mcp.md`, `Capabilities.cs`, `plans/011-syntax-contract.md`.

Out of scope: SQL Server, persistent DML approval path (`--allow-mutation --confirm-database` keeps running read-write).

## Steps

1. **Proof on the playground (disposable temp tables only).** Record each result in `plans/047-readonly-proof.md` with the PostgreSQL version and the PostgreSQL documentation section for `SET TRANSACTION`:
   - inside `BEGIN READ ONLY` / `SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY`: `INSERT`/`UPDATE`/`DELETE`/`MERGE` on a temp table; the same on a persistent table; an unqualified temp name shadowed by a persistent table under a `search_path` that puts `public` before `pg_temp`;
   - `CREATE TEMP TABLE`, `SELECT INTO TEMP`, `CREATE INDEX` on a temp table, `TRUNCATE` temp, `DROP TABLE` temp;
   - `EXPLAIN ANALYZE` of DML on a temp table;
   - a volatile function that writes a persistent table, `nextval` on a persistent sequence, `pg_advisory_lock`;
   - whether `SET transaction_read_only = off` inside the session is reachable (the classifier denies `SET`; record the server behaviour anyway).
2. **Decide.** If persistent writes are rejected and temp DML is allowed: run setup DDL (`CREATE TEMP`, `CREATE INDEX`, `TRUNCATE`, `DROP` of proven temps) as today, then switch the session to read-only (`SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY`) before query and measured SQL. Read-only paths with no setup run read-only from the start. If the proof fails for DML, keep the current rules, record why, and close the plan with the `search_path` limitation documented (no breaking qualification requirement).
3. **Simplify the classifier** for read-only phases: an unqualified DML target no longer needs a name proof; the server rejects a persistent target with an execution error mapped to a safety exit (2) with a clear message. Keep the name proof only for DDL that runs before the read-only switch. Keep denials of `CASCADE`, `RESTART IDENTITY`, `pg_temp_<N>` and foreign schemas.
4. **Shorten docs** to the new rule: "Query and measured SQL run in a read-only transaction; PostgreSQL itself rejects writes to persistent tables. Setup may create, index, truncate and drop TEMP tables."

## Verification

- `dotnet build SqlHarness.sln -warnaserror` — 0 warnings.
- `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~PostgresSafety|FullyQualifiedName~PostgresQuery|FullyQualifiedName~PostgresBenchmark"` — pass.
- Opt-in: `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING` set to the playground, `dotnet test tests/SqlHarness.Tests --filter "Category=PostgresIntegration"` — pass. A skipped integration run is not proof for step 2.
- Final `scripts/verify.ps1` → `verify: OK`; `scripts/verify-linux.ps1` → `verify-linux: OK`.

## Done criteria

- [ ] `plans/047-readonly-proof.md` records every step-1 case with server output.
- [ ] Either the read-only session is in place with integration tests, or the plan records why it is not and leaves rules unchanged.
- [ ] No existing valid temp batch becomes a rejection without a documented reason.
- [ ] Both gates pass; `plans/README.md` row updated.

## STOP conditions

- No authorized disposable PostgreSQL is available for step 1. Stop after documenting the proof script; do not change rules on assumptions.
- `EXPLAIN (ANALYZE)` timings change materially inside a read-only transaction (compare the playground numbers); report before adopting.
