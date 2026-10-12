# Plan 052: PostgreSQL runs every non-approved command inside a READ ONLY transaction

> Read current source before editing. Inspect drift since `bab7618` in the scope below and adapt implementation details within this intent. A changed file alone is not a STOP condition. Split out of plan 047 on 2026-10-11 after the rule review; it closes a real hole and comes first in the PostgreSQL queue.

## Status

- Status: BLOCKED (step 1 requires an authorized disposable PostgreSQL target)
- Priority: **P1** (security)
- Effort: M
- Risk: MED
- Depends on: a disposable PostgreSQL (playground `scripts/setup-local-postgres.ps1`) for the proof step
- Planned at: `bab7618`, 2026-10-11

## Why this matters

The PostgreSQL classifier treats a `SELECT` with no write target as read-only (`PostgresSafetyClassifier.ClassifySelect` → `FromWrites`) and the session runs it without any transaction wrapper (`PostgresSessionScope.BindClassifiedCommand`: "No transaction wrapper"). Function calls are checked only against a name denylist with prefixes. So these run without mutation approval, including over MCP, whose contract says "no persistent mutations":

- `SELECT app.purge_old_orders();` / `SELECT * FROM app.archive()` — any user function that writes;
- `EXPLAIN ANALYZE SELECT app.f()`;
- `SELECT pg_stat_statements_reset()` (the denied prefix is `pg_stat_reset`);
- `SELECT pg_promote()`, `SELECT pg_backup_start('x')`.

A name denylist can never be complete. PostgreSQL itself enforces "no writes to persistent relations" inside a `READ ONLY` transaction, and it does so after function inlining, triggers and dynamic SQL inside functions.

## Design

- Every classified command that does **not** carry approved mutation (`--allow-mutation --confirm-database`) runs as an explicit transaction: `BEGIN TRANSACTION READ ONLY` → command → `COMMIT` (`ROLLBACK` on error or cancellation). Use Npgsql `BeginTransaction` plus a `SET TRANSACTION READ ONLY` as the first statement, or the equivalent; the classified text itself is unchanged.
- Per command, **not** a session default. A function can call `set_config('default_transaction_read_only','off', false)` inside a read-only transaction; a session default would then be off for the next command on the same session (measure/compare reuse one session for setup, warm-up and every repetition).
- Applies to: `query` (read-only path), `watch`, `snapshot`, catalog helpers, measured and warm-up runs, the equivalence sidecar, and `EXPLAIN ANALYZE`.
- Setup: setup DDL that creates, indexes, truncates or drops TEMP tables cannot run in a read-only transaction. Setup stays on the current name-proof rules (plan 047 owns their simplification) and runs read-write; everything after setup runs read-only.
- Temp DML (`INSERT`/`UPDATE`/`DELETE` into a TEMP table) in query or measured SQL is allowed by PostgreSQL inside a read-only transaction; confirm in the proof.
- A server rejection (`SQLSTATE 25006 read_only_sql_transaction`) maps to exit `2` with "PostgreSQL rejected a write in a read-only transaction; persistent writes need an approved mutation."

## Steps

1. **Proof on the playground** (disposable objects only), recorded in `plans/052-readonly-proof.md` with the PostgreSQL version and the `SET TRANSACTION` documentation section:
   - persistent-table write through a plpgsql function → rejected;
   - `pg_stat_statements_reset()`, `nextval` on a persistent sequence, `pg_promote()` (expect a privilege or read-only error) → record each outcome;
   - `set_config('default_transaction_read_only','off',false)` inside the transaction, then a write in the next per-command transaction → still rejected;
   - temp-table `INSERT`/`UPDATE`/`DELETE` → allowed; `EXPLAIN ANALYZE` of both;
   - timing overhead of the wrapper on a trivial `SELECT 1` (100 repetitions) to confirm measurements are not distorted.
   - Prepared runner: `pwsh ./scripts/prove-postgres-readonly.ps1 -ConfirmDisposableDatabase <exact-database>`. It requires `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING`, creates only a uniquely named schema plus a session-local temp table, and refuses to overwrite a completed proof report. Current report is explicitly pending because no disposable target is authorized or available.
2. Implement the per-command wrapper in the PostgreSQL session for every non-mutation path. Keep the approved-mutation path read-write and unchanged.
3. Map `25006` to the safety exit with the message above. Tests with a fake session for the mapping; integration tests (`Category=PostgresIntegration`) for steps 1's cases.
4. Shrink the PostgreSQL function denylist to effects a transaction cannot contain: backend terminate/cancel, `pg_reload_conf`, advisory locks, `pg_sleep`, `dblink*`, server file reads, `set_config`. Keep the external-access checks. Record in the classifier comment that persistent writes are enforced by the server, and that a least-privilege role is the operator's control for the rest.
5. Docs (README PostgreSQL engine notes, Safety contract, docs/mcp.md, Capabilities): "Non-approved PostgreSQL commands run in a READ ONLY transaction; the server rejects persistent writes."

## Verification

- `dotnet build SqlHarness.sln -warnaserror` — 0 warnings.
- `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Postgres"` — pass.
- Integration: `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING` pointing at the playground, `dotnet test tests/SqlHarness.Tests --filter "Category=PostgresIntegration"` — pass. A skipped integration run is not proof.
- Final `scripts/verify.ps1` → `verify: OK`; `scripts/verify-linux.ps1` → `verify-linux: OK`.

## Done criteria

- [ ] Every example in "Why this matters" is rejected (or, for privilege-gated functions, fails on privileges) without approved mutation, proven by integration tests.
- [ ] The wrapper is per command; the `set_config` bypass test passes.
- [ ] Measured timings show no material wrapper overhead (recorded numbers).
- [ ] Docs and capabilities describe the server-enforced rule.
- [ ] Both gates pass; `plans/README.md` row updated.

## STOP conditions

- No authorized disposable PostgreSQL is available. Stop after writing the proof script.
- A temp-table write is rejected inside `READ ONLY` on the playground version. Report; the design then needs setup-style handling for temp DML.

Current step-1 state: proof harness prepared, but no database proof run. Do not treat the pending report as evidence or proceed to runtime implementation until the disposable PostgreSQL prerequisite is available and authorized.
