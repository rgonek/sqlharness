---
name: sqlharness
description: Use when a coding agent needs safe, repeatable SQL Server, Azure SQL, or PostgreSQL query evidence, performance comparison, execution-plan distillation, compact schema inspection, readiness probes, row-count inventory, read-only database storage (space) diagnosis, bounded progress polling (watch), or named result snapshots.
---

# SQLHarness

SQLHarness is a repeatable SQL Server and PostgreSQL optimization harness: it measures, compares, and proves SQL changes while keeping target resolution and SQL execution guarded. Engine lives on the locked profile (`"engine": "postgres"` or omitted for SQL Server); `--engine` is valid only with `--unsafe-direct`.

## Readiness and scope

Before a database operation, confirm the executable is available:

```powershell
Get-Command sqlharness
sqlharness --help
```

Lock one profile + one variable set per invocation; a different profile/vars requires a new explicit user request. Use the named profile and its required variables rather than direct connection details. Prefer read-only helpers before authoring ad-hoc SQL:

```powershell
sqlharness ping prod-eu --var tenant=acme --var env=uat --json
sqlharness counts prod-eu --var tenant=acme --var env=uat --table Contracts --table SourceFiles --exact --json
sqlharness counts prod-eu --var tenant=acme --var env=uat --like "%Sync%" --json
sqlharness schema prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
sqlharness schema prod-eu --var tenant=acme --var env=uat --json
sqlharness space prod-eu --var tenant=acme --var env=uat --top 25 --json
sqlharness space prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
sqlharness watch prod-eu --var tenant=acme --var env=uat --file .\queries\progress.sql --param target:int=1000 --until "Imported >= 1000" --interval 30 --max-duration 45m --json
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --json
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --diff --json
```

`ping`, `counts`, `schema`, and `space` never accept arbitrary user SQL or mutations. `counts` defaults to approximate partition estimates; use `--exact` when the agent needs `COUNT_BIG(*)`. `space` diagnoses storage only via fixed read-only DMV/catalog SQL (database files, reserved/used/data MB, top tables by reserved space; `--object` adds per-index detail for one exact table). Default `--top` is 25 (bounds 1..500). It never performs shrink, recovery-model change, compression change, or index mutation—any mutation still requires a separately approved `query --allow-mutation` batch. Prefer `--json` for full reports. On `measure` / `compare`, prefer `--json-summary` when the agent only needs the bounded projection (target, classification, distributions, table reads, warnings, ≤10 noteworthy operators, equivalence for compare, artifact directory). Do not pass both `--json` and `--json-summary`. Pass SQL through `--file` or stdin exactly as the command requires, and use repeatable `--param name[:type]=value` parameters instead of interpolating values into SQL.

`watch` and `snapshot` accept the same single SQL source and `--param` pipeline as `query` (read-only by default; mutation classification rejects with exit `2`). Use `watch` to poll progress instead of re-running ad-hoc queries: exactly one of `--until` (predicate on the first row of the first result set) or `--until-unchanged` (stable hash across consecutive polls; default when neither is supplied is `--until-unchanged 3`). Defaults: `--interval 30` seconds and `--max-duration 15m`. Exit `0` when the condition is met or results stay unchanged; exit `7` when max duration elapses without a stop condition. Use `snapshot` to store a named canonical result under `~/.sqlharness/snapshots` (sensitive result data—treat as locally sensitive). Capture with `--name`; `--force` is required to replace an existing name. `--diff` compares the live query to the stored snapshot and never prints cell values (locations and kinds only): exit `0` when identical, exit `8` when a valid comparison found differences rather than an execution failure.

Supported types: `nvarchar`, `nvarchar(max)`, `varchar`, `varchar(max)`, `char`, `nchar`, `int`, `bigint`, `smallint`, `tinyint`, `bit`, `decimal`, `decimal(p,s)`, `numeric`, `numeric(p,s)`, `float`, `real`, `money`, `smallmoney`, `date`, `time`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`, `uniqueidentifier`, `varbinary`, `varbinary(max)`, `hierarchyid`, `geography`, `geometry`. Parsing is culture-invariant; date/time values use ISO 8601. GUIDs accept any standard format; `varbinary` is Base64; `hierarchyid`/`geography`/`geometry` bind as true SQL UDTs (`path`, WKT, optional `srid;WKT`); nulls are `name:null` or `name:type:null`.

```powershell
--param customerId:int=42
--param asOf:datetime2=2026-07-29T12:00:00
--param amount:decimal(19,4)=1234.5600
--param when:time=14:30:00
--param id:uniqueidentifier=0f8fad5bd9cb469fa16570867728950e
--param path:hierarchyid=/1/2/
--param loc:geography=4326;POINT(-122.34900 47.65100)
```

## Safe workflow

1. Inventory representative cases offline before measuring: row count, cardinality distribution, ordering ties, missing history, boundary dates, and empty results. Keep ticket SQL outside the application repository.
2. Confirm readiness with `ping --json` when the target may still be starting.
3. Inventory tables with `counts` (partition estimates by default; `--exact` when needed) instead of hand-built `COUNT(*)` batches.
4. Inspect with `schema` / `schema --object` when object shape is unknown.
5. Diagnose storage with `space` / `space --object` (read-only DMVs only; no shrink/recovery/compression/index mutation).
6. Use `watch` to poll a progress query until `--until` / `--until-unchanged` instead of looping ad-hoc `query` calls.
7. Use `snapshot` / `snapshot --diff` for named before/after result comparisons without replaying full result dumps (`--force` only to replace).
8. Use `query` for a bounded read-only result.
9. Use `measure` for repeated timing, IO, and plan evidence for one query.
10. Use `compare` for baseline/candidate evidence and technical result equivalence.
11. Use `gain --json` to inspect recorded output savings.

```powershell
sqlharness watch prod-eu --var tenant=acme --var env=uat --file .\queries\progress.sql --param target:int=1000 --until "Imported >= 1000" --interval 30 --max-duration 45m --json
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --json
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --diff --json
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --setup .\queries\setup.sql --repeat 5 --json-summary
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --setup .\queries\setup.sql --repeat 5 --json-summary
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --compare-results multiset --repeat 5 --json-summary
sqlharness gain --json
```

### PostgreSQL engine notes

- Prefer a closed `engine: postgres` profile (example name `local-pg`). Do not pass `--engine` with a profile.
- Auth is `sql` only; `trustServerCertificate: true` → `SslMode=Disable`, `false` → `SslMode=Require`.
- Use native `CREATE TEMP TABLE` / `TEMPORARY` in setup — there is no `#temp` translation.
- Rejected params: `money`, `smallmoney`, `smalldatetime`, `hierarchyid`, `geography`, `geometry`.
- Measure/compare: single statement timed via `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`; equivalence via unmeasured sidecar. `CpuTimeMs` is 0; `logicalReads` are buffer hits+reads; `missingIndexes` empty.
- Catalog/`--like`/`--object` matching is case-sensitive as stored. `space` uses `pg_database_size` / relation sizes (Files DATA row; Allocation Reserved may exceed Used/Data; index Type = AM, Compression null).
- Playground: `scripts/setup-local-postgres.ps1` + `SQLHARNESS_PG_PLAYGROUND_PASSWORD` → `sqlharness-pg` / port `5433` / `pagila`. Never writes `targets.json`. Live opt-in: `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING`.

### Benchmark session contract

For `measure` and `compare`, setup runs exactly once per connection; warm-up and all measured repetitions reuse that same session. Session-local `#temp` (SQL Server) or `TEMP` (Postgres) tables created in setup are visible to measured SQL. A rejected or failed setup stops the run—do not retry via persistent objects.

SQL Server local `#temp` only (name starts with exactly one `#`): setup may create/index/DML/`DROP` session temps with supported constraints without mutation confirmation. Postgres uses native `TEMP` / `TEMPORARY` only. Persistent objects, `##temp`, dynamic SQL, external access, cross-database references, and transaction control remain denied.

### Technical vs domain equivalence

`compare --compare-results` defaults to `ordered`. Modes: `ordered` (schema + multiset + row order), `multiset` (schema + multiset; order free), `set` (schema + distinct rows), `off` (no comparison; null equivalence fields). Every measured run participates; warm-ups do not. Directional counts are the max over any baseline/candidate measured pair. Fingerprint retention is capped at 1,000,000 rows per measured variant run.

Technical equivalence is not domain/business equivalence. When the ticket needs set semantics, verify with two-direction `EXCEPT`; when duplicates matter, use grouped counts. Inventory ordering ties and missing-history cases offline before treating a run as representative.

Agent-authored SQL may use every ScriptDom-parsable construct inside a top-level `SELECT`, including CTEs, scalar functions, table/index hints, optimizer hints, windowing, and derived/apply syntax. SQLHarness classifies safety by the top-level statement and explicit prohibited behaviors, not by a nested-fragment allowlist. A rejection must not be bypassed, but an unfamiliar safe nested fragment should be reported as a SQLHarness compatibility bug rather than worked around with `sqlcmd`.

## Mutation gate

The default is read-only. Session-local `#temp` work is classified separately, but persistent-object mutation requires a fresh, single-use user approval for the exact SQL batch and exact resolved database.

Only after that approval, use both flags:

```powershell
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\batches\approved.sql --allow-mutation --confirm-database contoso-acme-uat --json
```

Do not reuse an approval for changed SQL, a different database, a different profile, or different variables. Never work around a safety rejection; report the safe error and request a narrower or explicitly approved operation.

`--unsafe-direct` bypasses the closed-profile guardrail. Use it only when the user explicitly requests direct/ad-hoc access and supplies the complete target and authentication strategy; never combine it with a profile or `--var`. Optional `--engine` is allowed only on that direct path.

## Offline plans and schema

`plan` is offline and does not need a scope lock or database access. It reads a showplan XML file (or stdin), returns a compact plan tree, and can emit JSON:

```powershell
sqlharness plan .\artifacts\orders.sqlplan --json
```

`.sqlplan` artifacts, comparison artifacts, named snapshots under `~/.sqlharness/snapshots` (sensitive result data; replace only with `--force`), and runtime parameters are locally sensitive: they can contain batch text, parameter values, and result data. `snapshot --diff` never prints cell values. Do not paste or publish them without explicit review.

`schema` uses only internal catalog queries (no arbitrary SQL) and returns compact tables, views, columns, indexes, and foreign keys. Prefer `--object` for exactly one table or view; use `--filter` for a LIKE catalog walk:

```powershell
sqlharness schema prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
sqlharness schema prod-eu --var tenant=acme --var env=uat --filter "%Order%" --max-objects 50 --json
```

`space` uses only fixed internal DMV/catalog SQL (no arbitrary SQL) and diagnoses storage only: database files, aggregate reserved/used/data MB, and top tables by reserved space (default `--top 25`). Pass `--object name` or `--object schema.name` for optional per-index detail on one exact table. It never shrinks files, changes recovery model or compression, or mutates indexes; any mutation still requires a separately approved `query --allow-mutation` batch:

```powershell
sqlharness space prod-eu --var tenant=acme --var env=uat --top 25 --json
sqlharness space prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
```

## Outcomes

Exit codes are stable: `0` success, `2` validation or safety rejection, `3` authentication, `4` target mismatch, `5` SQL execution failure, `6` local storage failure, `7` `watch` max duration elapsed without a stop condition, and `8` `snapshot --diff` found differences (a valid comparison, not an execution failure). Secrets, access tokens, and passwords must remain only in process memory; do not put them in arguments, files, logs, reports, or artifacts.
