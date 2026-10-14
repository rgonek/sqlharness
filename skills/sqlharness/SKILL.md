---
name: sqlharness
description: Use when a coding agent needs SQL Server, Azure SQL, or PostgreSQL evidence through SQLHarness — offline validate and capabilities, bounded queries, measure/compare benchmarks with result equivalence, plan distillation, schema, ping, counts, space, watch, snapshot, qstop, missing indexes, saved artifact sections, or the MCP server.
---

# SQLHarness

SQLHarness runs bounded SQL Server and PostgreSQL work for coding agents and guards target resolution and SQL execution. [README.md](../../README.md) is the reference for every option and rule, [AGENTS.md](../../AGENTS.md) is the contract, and this skill is the order of work. Look up options and defaults with `sqlharness <command> --help`; the command list is [README Commands](../../README.md#commands).

Release binaries derive their numeric version from the release tag (for example, `v0.4.0` produces `0.4.0`). Local development builds report `0.0.0` with `--version`; the informational `buildId` is `0.0.0-dev` and may include a commit suffix.

## Start offline

<!-- docs-sync:example -->
```powershell
sqlharness capabilities prod-eu --json
sqlharness doctor --json
sqlharness validate prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
```

None of these connects. `capabilities <profile>` adds that profile's engine, parameter types and limits; without a profile it returns engine-neutral discovery only. `validate` classifies the SQL for the profile and reports required parameters: classify setup with `--usage setup` and a measured batch with `--usage benchmark --setup <file>`. A passing `validate` never authorizes a later mutation. Details: [README Agent startup](../../README.md#agent-startup).

## Scope lock

Lock one closed named profile and one `--var` set per invocation; a different profile or different variables needs a new explicit user request. The engine comes from the profile (`"engine": "postgres"`, or omitted for SQL Server). `--unsafe-direct` serves only an explicit user request for direct access, with complete `--server`, `--database` and `--auth`, and never with a profile or `--var`; `--engine` is valid only there.

## Workflow

1. **Inventory** representative cases offline before measuring: row count, cardinality distribution, ordering ties, missing history, boundary dates, empty results. Keep ticket SQL outside the application repository.
2. **Helpers** come before ad-hoc SQL: `ping` (readiness), `counts` (approximate partition estimates; `--exact` for `COUNT_BIG(*)`), `schema` (`--object` for one table or view), `space` (storage). They run fixed internal SQL and never accept arbitrary SQL.
3. **`query`** returns one bounded read-only batch. Bind values as typed `--param name[:type]=value`; names may include one leading `@`, and reports use `@Name`. Unsized variable-width strings bind at size 4000 through 4000 characters and `max` above that, matching Dapper so the benchmark compiles the application's plan. Fixed-width `char`/`nchar` cannot use `max`: `char` accepts through 8000 (using actual length above 4000), `nchar` through 4000, and both use size 4000 up to that boundary, including NULL. Explicit sizes are unchanged. Types: [README Quick start](../../README.md#quick-start), or `capabilities <profile>`.
4. **`measure`** collects repeated timing, IO and plan evidence for one query; **`compare`** adds baseline/candidate result equivalence and a latency `regression.verdict` (on `inconclusive`, read `reasons`; the verdict never changes the exit code). For both, put preparation in `--setup` (runs once on the measured session). One query across several parameter sets: `measure --param-set` ([README Measure parameter sets](../../README.md#measure-parameter-sets)).
5. **`plan`** distills a saved showplan offline, with no scope lock.

<!-- docs-sync:example -->
```powershell
sqlharness ping prod-eu --var tenant=acme --var env=uat --json
sqlharness counts prod-eu --var tenant=acme --var env=uat --table Contracts --table SourceFiles --exact --json
sqlharness schema prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
sqlharness space prod-eu --var tenant=acme --var env=uat --top 25 --json
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --setup .\queries\setup.sql --repeat 5 --json-summary
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --compare-results multiset --repeat 5 --json-summary
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\before.sql --candidate .\after.sql --matrix BatchSize:int=1,20,100 --repeat 7 --json-summary
sqlharness plan .\artifacts\orders.sqlplan --json
```

`space` diagnoses storage only through read-only DMVs and `--object dbo.Contracts` adds per-index detail; shrink, recovery-model, compression and index changes go through an approved `query --allow-mutation` batch. `compare --matrix` varies one parameter, opening a new connection and running setup once per value; its rules sit next to the matrix example in [README Quick start](../../README.md#quick-start).

**Output.** Prefer `--json`; on `measure` and `compare`, `--json-summary` is the bounded projection. `--output agent` and MCP use one compact schema version 2 JSON text block. Both default to 65536 UTF-8 bytes per report; MCP `--max-result-bytes` sets a process ceiling of 262144 bytes, and a call may raise or lower its budget up to that ceiling. Every report uses the detail ladder `unbounded, 128, 64, 32, 16, 8, 4, 2, 1, 0`. Query projections identify CLR value types and every clipped retained cell by row, column, original length and returned length. Compare matrices retain their case inventory before optional details; per-table reads are ordered by median reads. See [README Commands](../../README.md#commands) and [MCP output contract](../../docs/mcp.md#result-envelope-errors-and-budgets).

**MCP dry runs.** Set `dryRun: true` on query, measure or compare to validate the exact offline arguments (including setup, matrix values and `.sqljson` sets) without connecting or writing artifacts. Success returns `command`, `classification`, canonical typed `parameters` with size/precision/scale, and `parameterSets`; it has no raw-token receipt and does not check runtime storage, authentication, connection identity, catalog contents or permissions. There is no CLI switch. Safety denials name the ScriptDom statement type and starting line, such as `IfStatement at line 14`. For exact operation arguments this supersedes `sqlharness_validate`; that MCP tool stays until plan 060 removes it. See [MCP parameters and dry runs](../../docs/mcp.md#parameters-and-dry-runs).

**Polling.** `watch` replaces a loop of ad-hoc `query` calls: it stops on `--until <predicate>` or `--until-unchanged <polls>`, and exits `7` when `--max-duration` elapses first. `watch --output ndjson` streams a `started` event, a `changed` event per change, and one terminal `completed` or `failed` record ([README Commands](../../README.md#commands)).

**Before/after results.** `snapshot --name` stores a canonical result under `~/.sqlharness/snapshots` (`--force` to replace a name). `snapshot --diff` reports locations and kinds without cell values: exit `8` means a valid comparison found differences rather than an execution failure ([README Commands](../../README.md#commands)).

**Query Store** (SQL Server). `qstop` ranks consumers by total duration; one item aggregates all plans and runtime intervals of one `query_id`. A high rank is a lead to `measure`, `compare` and `plan`, not proof the query is defective. An empty readable window exits `0`; unavailable Query Store (`OFF`, `ERROR`) exits `5`. Stdout has no SQL text; the text sits in `artifactDirectory/queries.jsonl` ([README Commands](../../README.md#commands)).

**Missing indexes** (SQL Server): `indexes` before proposing an index; creating one goes through the mutation gate ([README Commands](../../README.md#commands)).

**Saved benchmarks**: `artifact <id> --section <section>` reads `summary`, `metrics`, `operators`, `statements`, `matrix-cells`, or `plan` offline. Paged sections use `--cursor`. For `plan`, select `--variant baseline|candidate|query`, optional zero-based `--statement`, `--parameter-set`, and available measured `--repetition`; by default the last measured repetition is identified in the response. Available repetitions include measured runs with no saved plan; selecting such a run reports the missing evidence instead of falling back to an earlier run. `--include-text` opts into bounded statement text and predicates. Saved nodes continue in preorder pages; `planShapeStable` describes comparable saved operator/object/index/predicate shape, never timing or row-count stability, and reports unavailable evidence as `null`. The noteworthy `operators` list is capped even though it is paged ([README Commands](../../README.md#commands)). SQL Server per-table read keys normalize session-suffixed temp-table names before aggregation.

**MCP.** `sqlharness mcp serve` exposes the tools to one local stdio client and runs no explicit persistent mutations; PostgreSQL TEMP setup can still reach a persistent-writing user function, so use a least-privilege principal ([details](../../docs/mcp.md#no-persistent-mutations)). In request scope the operator's `--allow-profile` list approves profile switches; different variables still need a new user request. Start, scope and limits: [docs/mcp.md](../../docs/mcp.md).

<!-- docs-sync:example -->
```powershell
sqlharness watch prod-eu --var tenant=acme --var env=uat --file .\queries\progress.sql --param target:int=1000 --until "Imported >= 1000" --interval 30 --max-duration 45m --json
sqlharness watch prod-eu --var tenant=acme --var env=uat --file .\queries\progress.sql --until-unchanged 3 --output ndjson
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --json
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --diff --json
sqlharness qstop prod-eu --var tenant=acme --var env=uat --top 20 --window 24h --json
sqlharness indexes prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
sqlharness artifact <id> --section summary --json
```

### Measure parameter sets

`measure --param-set` measures one query across at least two named parameter sets. Fixed `--param` values are shared by every set; per-set parameters come from each file and must not reuse a fixed name. A single scenario stays on `--param` alone.

```powershell
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --param tenant:nvarchar=acme --param-set .\sets\small.sqljson --param-set .\sets\large.sqljson --repeat 5 --json
```

Each `--param-set` file is JSON containing only `name` and `parameters`, at most 64 KiB. A UTF-8 BOM, JSON comments, and trailing commas are accepted. Those files are locally sensitive and remain the only SQLHarness input that contains those values. SQLHarness does not copy parameter values or source paths into reports. `.sqlplan` artifacts stay locally sensitive because server plan XML may still embed parameter values; do not publish them without review.

Multi-set measure uses one session and setup once. Setup runs once and binds the first parameter set's merged parameters. Later sets do not re-run setup. Each set is warmed once in user-supplied order, then measured rounds rotate on that same session. In one-based round r, measured execution starts at index r modulo setCount and wraps in user-supplied order. Per-set stability is the only stability result; SQLHarness does not claim cross-set result equivalence. Parameter-set measurements use the observed server plan-cache state; SQLHarness did not clear or isolate the plan cache. Timings are order-dependent because of that shared cache.

This is not `compare --matrix`. Matrix mode varies one parameter across two query variants and opens a fresh connection per value. `--param-set` measures one query on one connection.

`--json-summary` for a multi-set measure is a bounded projection: the cache warning, the rotation rule, each set name, parameter type metadata, the value hash, per-set stability, median elapsed/CPU/reads, cross-set labels, and the artifact directory. It omits parameter values, plan XML, and full run arrays. Noteworthy operators, when included, stay capped at ten.

## Session contract

For `measure` and for `compare` without `--matrix`, `--setup` runs once on the measured session, and warm-up and measured runs reuse that session, so session temps created in setup are visible to the measured SQL. Session-local work needs no approval: SQL Server `#temp` tables, table variables and `SET @v`; PostgreSQL native `CREATE TEMP TABLE`. Setup locals and table variables stay inside setup's batch, so carry data to the measured SQL in a `#temp` table. On PostgreSQL, temp DML in query and measured SQL needs no temp-name proof: that command runs in a read-only transaction, and the server refuses a persistent target itself (exit `2`). Setup DDL (`CREATE TEMP`, `DROP TABLE`, `CREATE INDEX`, `TRUNCATE`) classifies as allowed in every phase but runs read-write in `--setup` only, so the read-only server refuses it elsewhere (exit `2`); it keeps the name proof, and writes there should use `pg_temp.<name>` when `search_path` may be non-default. A rejected or failed setup stops the run. Syntax and proof limits: [Benchmark setup contract](../../README.md#benchmark-setup-contract).

Any ScriptDom-parsable construct inside a top-level `SELECT` is accepted, so SQLHarness handles unfamiliar nested syntax; report a safe nested fragment it rejects as a SQLHarness compatibility bug. `sqlcmd` is not a fallback.

SQL Server control flow: Allowed control flow: IF, ELSE, BEGIN … END (any nesting). Still denied: WHILE, BREAK, CONTINUE, GOTO, labels, RETURN, TRY … CATCH, WAITFOR, THROW, RAISERROR. A branch is classified even when its predicate is constant false: classifier never evaluates predicates. SQL Server only. PostgreSQL unchanged.

## Equivalence

`compare --compare-results` (default `ordered`; also `multiset`, `set`, `off`) checks observed rows: technical equivalence, not domain equivalence. For a domain check use two-direction `EXCEPT`, or grouped counts when duplicates matter. Modes, nullability and limits: [README Result equivalence](../../README.md#result-equivalence-and-compact-output).

## PostgreSQL

- **Remote profiles** set `"sslMode": "verify-full"` explicitly. An omitted `sslMode` keeps the legacy mapping, where `trustServerCertificate: false` encrypts without verifying the certificate; that legacy mapping and `require` / `disable` give a man-in-the-middle the credential and the result data, because authentication completes before any identity check. Combinations, `rootCertificate` and legacy behaviour: [README PostgreSQL engine notes](../../README.md#postgresql-engine-notes).
- **Session lexing**: a session that does not report `standard_conforming_strings = on` is refused with exit `2` before any classified SQL runs, because the offline classifier and the server must agree on where string literals end. There is no profile opt-out; fix the role or database default on the server.
- Each non-approved command runs in its own `READ ONLY` transaction. TEMP setup runs outside it under static checks, and a setup `SELECT` can still call a user function that writes persistently: review setup SQL.
- Benchmarks time one statement with `EXPLAIN (ANALYZE, BUFFERS)`: `CpuTimeMs` is `0`, `logicalReads` are buffer hits plus reads, and `missingIndexes` is empty. `qstop` and `indexes` are SQL Server only.
- Catalog matching (`--like`, `--filter`, `--object`) is case-sensitive as stored.
- **Top statements**: rank `pg_stat_statements` with a `query`: save the SQL below as `.\queries\pg-top-statements.sql`. The extension must be installed on the server; the column names are PostgreSQL 13+.

```sql
SELECT queryid, calls, total_exec_time, mean_exec_time,
       shared_blks_hit + shared_blks_read AS shared_blks
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 20;
```

<!-- docs-sync:example -->
```powershell
sqlharness query local-pg --file .\queries\pg-top-statements.sql --json
```

## Mutation gate

Read-only is the default. A persistent mutation needs fresh, single-use user approval for the exact batch and the resolved database; then record that approval with both flags:

<!-- docs-sync:example -->
```powershell
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\batches\approved.sql --allow-mutation --confirm-database contoso-acme-uat --json
```

An approval covers that batch, database, profile and variable set only. A safety rejection is a stop: report it and ask the user for a narrower request or an approval; persistent helper objects are never the way around it. The classifier and the mutation flags prevent accidents. For enforcement, point profiles at a least-privilege (reader) database principal.

MCP file inputs require an absolute path lexically under a configured `--input-root`; UNC paths are rejected and file reads are byte-bounded. The reader does not resolve links, reject alternate data streams, or detect same-user replacement during a read. Treat input roots as caller path scoping, not a boundary against the OS user running SQLHarness ([MCP contract](../../docs/mcp.md#input-roots-and-file-inputs)).

## Local outputs and exit codes

Locally sensitive outputs are pasted, attached or published only after the user's explicit review: `.sqlplan` files, measure/compare artifacts, snapshots under `~/.sqlharness/snapshots` (result data), `qstop`'s `queries.jsonl` (SQL text), `--param-set` files and `~/.sqlharness/data/activity.db`. Secrets live in the environment variables the profile names, and in process memory only.

Exit codes, redaction and artifact rules: [README Safety contract](../../README.md#safety-contract). Every output replaces the profile password and Azure SQL token fetched by Azure CLI for the current operation; other text, including caller SQL quoted in server errors, stays readable. `2` is a validation or safety stop, `5` a SQL execution failure.
The activity journal stores credential-redacted error messages by default; SQL text and full plans require `journal.storeSensitive` ([Activity journal](../../README.md#activity-journal-and-workflow-output-cost)).

Dashboard workflow cost uses persisted output bytes as authoritative measurements and labels `utf8-bytes-div-4` token values as estimates. The `single-measured-execution-output` baseline includes the first measured result/messages for each variant and parameter set in the producer; it excludes warm-ups, later repeats, and plan XML. The comparison describes output volume, not actual token savings. Dry-run responses add output bytes and call counts but not database-execution counts, and cannot be baselines. Input, discovery and chat are unmeasured. Artifact reads link to producers through exact operation references; unreferenced history stays `legacy-artifact-volume`, with no inferred lineage. Schema v7 adds nullable `operation_ref` and `parent_operation_ref` plus `dry_run` (`NOT NULL DEFAULT 0`). It reuses the `raw_bytes` and `emitted_bytes` columns introduced in v3; stored byte values remain intact, and legacy token columns remain readable. See [workflow output cost](../../README.md#activity-journal-and-workflow-output-cost).
