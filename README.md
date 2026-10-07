# SQLHarness — repeatable SQL Server optimization for coding agents

Measure, compare, and prove SQL changes.

SQLHarness is a command-line optimization harness for SQL Server, Azure SQL, and PostgreSQL. It gives coding agents and engineers bounded query execution, repeated measurements, baseline/candidate equivalence checks, compact execution-plan distillation, schema inspection, and locally recorded output-savings evidence. The engine is a property of the locked target profile (omit `engine` for SQL Server; set `"engine": "postgres"` for Postgres).

## Agent startup

Use `sqlharness capabilities --json` to inspect commands and engine support,
then `sqlharness doctor --json` to check the local installation. Both commands
are offline and omit profile names, accounts, and environment values. Before
running a SQL file, `sqlharness validate <profile> --file query.sql --param id:int=1 --json`
classifies it with the selected closed profile and reports required parameters;
it does not connect or execute the SQL. Database permissions and referenced
object existence remain unknown, and its result does not authorize a later
mutation.

`validate` accepts a caller intent: `--usage query|setup|benchmark` (default `query`). `query` applies the
engine query rules; `setup` classifies the batch as session-local preparation; `benchmark` adds the
measured-batch shape check of the execution path (`benchmark_batch_not_supported`: on Postgres a
multi-statement batch is rejected, on SQL Server the check is a no-op). `--setup <PATH>` supplies setup-SQL
session-temp context for `query`/`benchmark` validation and is ignored for `--usage setup`. The JSON report
carries the decision (`allowed`, safe `reason` codes such as `missing_parameters` or `mutation_not_allowed`,
statement-level `astLocations` on SQL Server) and the explicit `checkedConditions` scope for the usage. It
never prints SQL or parameter values; `objectAndPermissionStatus` stays `"unknown"` and `executed` stays
`false` because offline validation never connects.

See [the agent output contract](docs/superpowers/specs/2026-09-26-agent-output-contract.md)
and [the audit roadmap](docs/superpowers/plans/2026-09-26-audit-roadmap.md).

## Install a release binary

SQLHarness is distributed only as self-contained, single-file, untrimmed GitHub Release binaries. Download the archive matching your platform (`win-x64`, `linux-x64`, or `osx-arm64`) and the accompanying `SHA256SUMS` from the [GitHub Releases page](https://github.com/rgonek/sqlharness/releases). Verify the archive before extracting it, then put `sqlharness.exe` on `PATH` for Windows or `sqlharness` on `PATH` for Linux/macOS.

Example for Windows PowerShell:

```powershell
$version = "v0.1.0" # replace with the chosen release tag
$base = "https://github.com/rgonek/sqlharness/releases/download/$version"
Invoke-WebRequest "$base/sqlharness-win-x64.zip" -OutFile sqlharness-win-x64.zip
Invoke-WebRequest "$base/SHA256SUMS" -OutFile SHA256SUMS
$expected = (Select-String 'sqlharness-win-x64.zip' SHA256SUMS).Line.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)[0]
if ((Get-FileHash sqlharness-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected.ToLowerInvariant()) { throw "Checksum mismatch." }
Expand-Archive sqlharness-win-x64.zip -DestinationPath "$HOME\bin\sqlharness" -Force
$env:Path += ";$HOME\bin\sqlharness"
sqlharness --help
```

For Linux/macOS, verify the downloaded archive with the checksum utility available on the host, extract it, make `sqlharness` executable, and add its directory to `PATH`.

## MCP over local stdio

The CLI distribution also includes an MCP server for one local stdio client per process. Fixed-profile mode freezes one profile at startup:

```powershell
sqlharness mcp serve sample-country --var tenant=example --var env=test --input-root C:\work\sqlharness-inputs
```

SQL Server users can instead freeze an explicit allowlist and supply a target scope for each target-dependent tool call:

```powershell
sqlharness mcp serve --request-scope --allow-profile sample-country --allow-profile sample-shared --input-root C:\work\sqlharness-inputs
```

Request mode is SQL Server only. It keeps the existing eleven tool names; `inspect`, `validate`, `query`, `measure`, `compare`, `watch`, `snapshot`, and `artifact` require a nested `scope` with an allowlisted profile and its variables. `capabilities`, `plan`, and `gain` remain target-free. The process snapshots profiles, input roots, and budgets at startup, and one process-wide gate allows one database operation at a time across all request scopes. SQLHarness does not register or edit client configuration. See [the MCP contract](docs/mcp.md) for both client-entry shapes, scope behavior, and limits.

## Quick start

Create `~/.sqlharness/targets.json` from [docs/example-targets.json](docs/example-targets.json), replacing the example server and database template with your own closed target definition. The example uses the `prod-eu` profile with required `tenant` and `env` variables.

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
sqlharness qstop prod-eu --var tenant=acme --var env=uat --top 20 --window 24h --json
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --compare-results multiset --repeat 5 --json-summary
sqlharness plan .\artifacts\orders.sqlplan --json
sqlharness gain --json
```

`compare` accepts one `--matrix` dimension:

```powershell
sqlharness compare prod-eu `
  --var tenant=acme --var env=uat `
  --baseline .\before.sql `
  --candidate .\after.sql `
  --matrix BatchSize:int=1,20,100 `
  --param AsOfDate:datetime2=2026-07-29T12:00:00 `
  --repeat 7 `
  --compare-results ordered `
  --json-summary
```

- one matrix dimension only;
- at least two typed values;
- sequential user-supplied order;
- a new connection and one setup per value;
- first failure stops the run;
- completed cell artifacts remain;
- ticket SQL stays outside the application repository.

Read-only database helpers (`ping`, `counts`, `schema --object`, and `space`) use fixed internal catalog/probe/DMV SQL only: they never accept an arbitrary user SQL batch or mutations. `counts` defaults to approximate row counts from partition statistics; pass `--exact` for `COUNT_BIG(*)`. `space` diagnoses storage only (files, aggregate allocation, top tables by reserved space, optional per-index detail for `--object`); it never performs shrink, recovery-model change, compression change, or index mutation—any mutation still requires a separately approved `query --allow-mutation` batch. Prefer `--json` for machine-readable reports.

`watch` polls a bounded read-only query until a stop condition: exactly one of `--until` (predicate on the first row of the first result set) or `--until-unchanged` (stable hash across consecutive polls; default when neither is supplied is `--until-unchanged 3`). Defaults: `--interval 30` seconds and `--max-duration 15m` (positive integral `s`/`m`/`h`, max 24h). Exit `0` when the condition is met or results stay unchanged; exit `7` when the max duration elapses without a stop condition. `snapshot` stores a named canonical result under `~/.sqlharness/snapshots` (sensitive result data—treat like comparison artifacts). Capture with `--name`; replace an existing name only with `--force`. `--diff` compares the live query against the stored snapshot without printing cell values (locations and kinds only): exit `0` when identical, exit `8` when a valid comparison found differences rather than an execution failure. Both accept the same single SQL source and `--param` pipeline as `query` (mutation classification rejects with exit `2`).

`qstop` ranks SQL Server Query Store consumers by total duration, then total CPU, then executions, then `query_id`. It is read-only, accepts no user SQL, and has no mutation flags. Defaults: `--top 20`, `--window 24h` (1440 minutes), and `--timeout 30`. `--window` is a positive integer with an `m`, `h`, or `d` suffix totaling 1..44640 minutes; `--top` is 1..500; `--timeout` is 1..300. Target scope is the usual profile, `--var`, or `--unsafe-direct` options. One item aggregates all plans and runtime intervals into that one `query_id`. An empty result from a readable window exits 0; unavailable Query Store (`OFF`, `ERROR`, or malformed results) and Postgres exit 5; an artifact write failure exits 6. Stdout has ids, hashes, and metrics only and has no SQL text. SQL text is only in `artifactDirectory/queries.jsonl`. That artifact is locally sensitive and must not be pasted or published without explicit review. A high rank is a lead to `measure`, `compare`, and `plan`, not proof the query is defective.

`plan --json` emits a compact, deterministic plan contract: `statements` preserves input order; each statement has optional `sql`, a `root` node, and optional `missingIndexes`. Nodes retain `physicalOp`, optional `logicalOp`, operator metadata and runtime values, warnings (with their Showplan attributes), and child nodes. Persisted `*.plan.json` files from `compare` and `measure` use this same schema, including `sql`. This output is serialization-only; it is not accepted as plan input.

Use `--json` for the full report (automation and artifacts). For agent-sized stdout on `measure` / `compare`, use `--json-summary` instead: a bounded projection (target, classification, distributions, table reads, warnings, at most ten noteworthy operators, equivalence for compare, artifact directory) without full operator/run arrays, plan XML, or result hashes. `--json` and `--json-summary` are mutually exclusive (`Choose only one of --json or --json-summary.`). `query` reads SQL from exactly one source: `--file` or redirected stdin. Bind runtime values with repeatable `--param name[:type]=value`, never by string interpolation.

Supported `--param` types (culture-invariant; date/time values use ISO 8601):

`nvarchar`, `nvarchar(max)`, `varchar`, `varchar(max)`, `char`, `nchar`, `int`, `bigint`, `smallint`, `tinyint`, `bit`, `decimal`, `decimal(p,s)`, `numeric`, `numeric(p,s)`, `float`, `real`, `money`, `smallmoney`, `date`, `time`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`, `uniqueidentifier`, `varbinary`, `varbinary(max)`, `hierarchyid`, `geography`, `geometry`

Notes:

- Untyped `name=value` is `nvarchar`; strings longer than 4000 characters bind as `nvarchar(max)` (`Size = -1`).
- `uniqueidentifier` accepts any standard GUID format (`D`, `N`, `B`, `P`).
- `varbinary` values are Base64.
- `hierarchyid` binds a path (for example `/1/2/`) as SQL UDT `HierarchyId`.
- `geography` / `geometry` bind WKT as native UDTs (via `Microsoft.SqlServer.Types`). Optional form: `srid;WKT` (defaults: geography `4326`, geometry `0`), for example `4326;POINT(-122.3 47.6)`.
- Spatial types need the package's native `SqlServerSpatial*` runtime on supported Windows RIDs; Linux/macOS spatial UDT binding is not guaranteed.
- Nulls: `name:null` or typed `name:type:null` (for example `count:int:null`).
- On Postgres, reject `money`, `smallmoney`, `smalldatetime`, `hierarchyid`, `geography`, and `geometry` parameters (exit 2). Prefer `decimal` / `numeric`, `datetime` / `datetime2`, and text/binary types.

```powershell
sqlharness measure prod-eu --var tenant=acme --var env=uat `
  --query .\queries\orders.sql `
  --param asOf:datetime2=2026-07-29T12:00:00 `
  --param amount:decimal(19,4)=1234.5600 `
  --repeat 5 --json
```

### PostgreSQL engine notes

- Profiles may set `"engine": "postgres"`. Omitted `engine` means SQL Server. Use `--engine postgres` only with `--unsafe-direct`.
- Postgres auth is `sql` only (`sqlUser` + `passwordEnvVar`). `ad-default` / `azure-cli` / `integrated` are rejected (exit 2). Password values stay in the environment variable.
- Omitted `sslMode` and `rootCertificate` keep the historical mapping, and a successful report sets `target.transportPolicy` to `legacy`: `trustServerCertificate: true` → Npgsql `SslMode=Disable`, `false` → `SslMode=Require`. An explicit `sslMode` of `verify-full`, `verify-ca`, `require`, or `disable` is authoritative. A mode that contradicts `trustServerCertificate` is rejected (`true` agrees only with `disable`; `false` agrees with `require` and the verifying modes). `rootCertificate` is accepted only with `verify-full` or `verify-ca` and must be a readable certificate file. SQL Server profiles that set either field are rejected. `--ssl-mode` and `--root-certificate` are direct options (`--unsafe-direct` only). New remote examples use `verify-full`. Details: `docs/superpowers/specs/2026-09-26-postgres-transport-policy.md`.
- Postgres always checks `current_database()`. Endpoint identity is the connection that was established, not a text comparison of a DNS name with `inet_server_addr()`. Loopback checks the database only. Non-loopback `verify-full` and `verify-ca` also require the authenticated host and a connected address returned for that name. `require` and `disable` do not authenticate a DNS name. DNS by itself is not authentication.
- `measure` / `compare` time a single EXPLAIN-able statement via `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`. Result equivalence uses an unmeasured sidecar SELECT. `CpuTimeMs` is `0`; `logicalReads` are shared/local buffer hits+reads; `missingIndexes` is empty.
- Catalog helpers (`counts`, `schema`, `space`) use fixed `pg_catalog` SQL. Unquoted identifiers and `--like` / `--filter` patterns are case-sensitive as stored (`Contracts` does not match `contracts`).
- `space` field analogs: Files = one `DATA` row from `pg_database_size` / `data_directory`; Allocation `ReservedMb` = database size while `UsedMb`/`DataMb` sum user relation sizes (they need not equal Reserved); Tables = top N by `pg_total_relation_size` with `Rows` from `reltuples`; `--object` indexes use access-method `Type` and null `Compression`.

```json
{
  "local-pg": {
    "engine": "postgres",
    "server": "localhost,5433",
    "database": "pagila",
    "vars": {},
    "auth": "sql",
    "sqlUser": "postgres",
    "passwordEnvVar": "SQLHARNESS_PG_PLAYGROUND_PASSWORD",
    "trustServerCertificate": true
  },
  "remote-pg": {
    "engine": "postgres",
    "server": "db.example.test,5432",
    "database": "appdb",
    "vars": {},
    "auth": "sql",
    "sqlUser": "sqlharness",
    "passwordEnvVar": "SQLHARNESS_PG_PASSWORD",
    "trustServerCertificate": false,
    "sslMode": "verify-full"
  }
}
```

`local-pg` leaves the new fields absent, so it stays on the legacy `Disable` mapping. `remote-pg` uses `verify-full` and omits `rootCertificate`, so the example file loads without a certificate on disk and Npgsql uses its default trust store. Add `rootCertificate` only when that file exists; a missing path rejects the whole profile file.

## Benchmark setup contract

For `measure` and for `compare` without `--matrix`, SQLHarness opens one connection per invocation:

1. Runs optional `--setup` SQL **exactly once** on that connection.
2. Runs warm-up(s) on the same connection.
3. Runs all measured repetitions on the same connection.

Session-local `#temp` objects created in setup remain visible to warm-up and measured SQL. A rejected or failed setup stops the benchmark; SQLHarness does not retry by creating persistent objects or weakening safety. `compare --matrix` does not reuse that single connection across values: each value opens a new connection and runs setup once, in the contract next to the quick-start matrix command.

Local temporary objects whose unqualified name starts with exactly one `#` may use session-only work without mutation confirmation: `CREATE TABLE #t` / `SELECT INTO #t`, `#temp` DML, `#temp` indexes and supported constraints (`NULL`/`NOT NULL`, `PRIMARY KEY`, `UNIQUE`), `DROP TABLE #t`, `TRUNCATE TABLE #t`, scalar `DECLARE` with analyzed initializers, table variables (`DECLARE @t TABLE (...)` then `INSERT`/`UPDATE`/`DELETE`/`MERGE`/`SELECT` against it when the `DECLARE` comes earlier in the same batch; `OUTPUT INTO @t` only when the statement's primary target is also local; a table variable declared in `--setup` is not visible to the measured SQL, so carry data in `#temp`), and `ALTER TABLE #t` limited to add/drop columns and local `CHECK`/`DEFAULT`/`NULL`/`UNIQUE` constraints. Plain `SET @v = <expr>` to a scalar local declared earlier in the same batch is accepted in query and measured SQL, not in `--setup`. On Postgres the session-only set is native `CREATE TEMP TABLE` / `SELECT INTO TEMP TABLE` (unambiguous single-part name), `TRUNCATE [ONLY]` of proven current-session temps, named unqualified or as `pg_temp.<name>` (no `CASCADE`, `RESTART IDENTITY`, or other schema-qualified target), plan-only `EXPLAIN` over a safe `SELECT`, and `EXPLAIN ANALYZE` with full inner-statement effect analysis; canonical `ANALYZE tbl` stays rejected until the parser supports it. The Postgres temp proof is name-based and needs a quoted or all-ASCII name of at most 63 UTF-8 bytes. An unqualified name assumes the default `search_path`, which SQLHarness does not check; when `search_path` may be non-default, write to session temps as `pg_temp.<name>`, which does not depend on it (details in `AGENTS.md`). Persistent objects, `##temp`, dynamic SQL, external access, cross-database references, and transaction control remain denied.

Before measuring, inventory a representative parameter range offline: row count, cardinality distribution (small/median/large/exceptional), ordering ties, missing history, boundary dates, and empty results. Keep ticket-specific benchmark SQL outside the application repository (ticket workspace or isolated temp directory). Treat plans, comparison artifacts, and runtime parameters as locally sensitive.

## Measure parameter sets

`measure --param-set` measures one query across at least two named parameter sets. Fixed `--param` values are shared by every set; per-set parameters come from each file and must not reuse a fixed name. A single scenario stays on `--param` alone.

```powershell
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --param tenant:nvarchar=acme --param-set .\sets\small.sqljson --param-set .\sets\large.sqljson --repeat 5 --json
```

Each `--param-set` file is strict JSON containing only `name` and `parameters`, at most 64 KiB, with no BOM, comments, or trailing commas. Those files are locally sensitive and remain the only SQLHarness input that contains those values. SQLHarness does not copy parameter values or source paths into reports. `.sqlplan` artifacts stay locally sensitive because server plan XML may still embed parameter values; do not publish them without review.

Multi-set measure uses one session and setup once. Setup runs once and binds the first parameter set's merged parameters. Later sets do not re-run setup. Each set is warmed once in user-supplied order, then measured rounds rotate on that same session. In one-based round r, measured execution starts at index r modulo setCount and wraps in user-supplied order. Per-set stability is the only stability result; SQLHarness does not claim cross-set result equivalence. Parameter-set measurements use the observed server plan-cache state; SQLHarness did not clear or isolate the plan cache. Timings are order-dependent because of that shared cache.

This is not `compare --matrix`. Matrix mode varies one parameter across two query variants and opens a fresh connection per value. `--param-set` measures one query on one connection.

`--json-summary` for a multi-set measure is a bounded projection: the cache warning, the rotation rule, each set name, parameter type metadata, the value hash, per-set stability, median elapsed/CPU/reads, cross-set labels, and the artifact directory. It omits parameter values, plan XML, and full run arrays. Noteworthy operators, when included, stay capped at ten.

## Result equivalence and compact output

`compare` reports **technical equivalence** under `--compare-results` (default `ordered`):

| Mode | Meaning |
| --- | --- |
| `ordered` | Same schema, same multiset of rows, same row order. |
| `multiset` | Same schema and row multiset; order may differ. |
| `set` | Same schema and distinct-row set; duplicates and order ignored. |
| `off` | Skip result comparison; equivalence fields are null. |

Every measured run participates; warm-ups do not. Directional counts (`baselineOnlyCount`, `candidateOnlyCount`, and for `ordered` also `differingPositions`) are the maximum over any baseline/candidate measured-run pair. Modes that retain directional fingerprints accept at most **1,000,000** row fingerprints per measured variant run; exceeding that bound fails the operation.

Order-only mismatches: under `ordered`, `differingPositions` can be non-zero while multiset equivalence would still hold—use `--compare-results multiset` when order is not part of the contract. Technical equivalence is not domain/business equivalence: prove ticket-specific semantics separately (for example two-direction `EXCEPT` for set differences, or grouped counts when duplicates matter; check ordering ties and missing-history cases offline).

```powershell
sqlharness compare prod-eu --var tenant=acme --var env=uat `
  --baseline .\queries\before.sql --candidate .\queries\after.sql `
  --compare-results multiset --repeat 5 --json-summary
```

## Commands

| Command | Purpose |
| --- | --- |
| `ping` | Verify connection readiness with a fixed internal probe (no user SQL). |
| `counts` | Inventory table row counts (partition estimates by default; `--exact` for `COUNT_BIG(*)`). |
| `space` | Diagnose storage via read-only DMVs (files, allocation, top tables; `--object` for per-index detail). |
| `qstop` | Rank SQL Server Query Store consumers by total duration (read-only; one `query_id` per item). |
| `watch` | Poll a bounded read-only query until `--until` / `--until-unchanged` or max duration. |
| `snapshot` | Capture or `--diff` a named canonical result under `~/.sqlharness/snapshots` (`--force` to replace). |
| `query` | Run one bounded, classified SQL batch. |
| `measure` | Collect repeated timing, IO, and plan evidence for one query. |
| `compare` | Compare baseline and candidate queries, including result equivalence. |
| `schema` | Return a compact read-only catalog description (`--object` for one table/view). |
| `plan` | Distill a showplan XML file or stdin without connecting to a database. |
| `gain` | Summarize recorded raw-versus-emitted output savings. |

Run `sqlharness <command> --help` for the final option surface.

## Safety contract

| Area | Contract |
| --- | --- |
| Exit codes | `0` success; `2` validation or safety rejection; `3` authentication failure; `4` target mismatch; `5` SQL execution failure; `6` local storage failure; `7` `watch` max duration elapsed without a stop condition; `8` `snapshot --diff` found differences (valid comparison, not an execution failure). |
| Closed targets | Profiles in `~/.sqlharness/targets.json` define server, database template, variables, authentication, and optional `engine` (`sqlserver` default, or `postgres`). Missing, extra, or invalid variables are rejected. |
| Direct targets | `--unsafe-direct` deliberately bypasses the closed-profile guardrail and requires `--server`, `--database`, and `--auth`. Do not mix it with a profile or `--var`. `--engine` is valid only with `--unsafe-direct` (never with a named profile). |
| Mutations | Read-only is the default. Persistent-object mutation requires fresh, single-use approval for the exact batch and exact resolved database, plus `--allow-mutation --confirm-database <exact-resolved-name>`. |
| Session `#temp` / `TEMP` | SQL Server: local `#temp` setup/DML/indexes are session-only and do not require mutation confirmation, plus scalar `DECLARE`, table variables declared earlier in the same batch, `TRUNCATE TABLE #t`, and `ALTER TABLE #t` add/drop columns with local constraints; plain `SET @v = <expr>` to a same-batch scalar local in query and measured SQL only. Postgres: use native `CREATE TEMP TABLE` / `TEMPORARY` (no `#temp` translation), `SELECT INTO TEMP TABLE` with an unambiguous name, `TRUNCATE [ONLY]` of proven current-session temps, plan-only `EXPLAIN` over a safe `SELECT`, and `EXPLAIN ANALYZE` with full effect analysis. Setup runs once per connection and shares that session with warm-up and measured runs. |
| SQL input | Use exactly one query source (`--file` or stdin) and bound `--param` values. Parsed syntax inside a top-level `SELECT` is accepted without a fragment allowlist; independent safety checks still reject cross-database access, external or stateful sources, dynamic SQL, and writes outside the approved local-`#temp` or mutation contracts. Helpers (`ping`, `counts`, `schema`, `space`) never accept arbitrary user SQL. `space` is read-only DMV diagnosis only—no shrink, recovery-model, compression, or index mutation; mutations need a separately approved `query --allow-mutation` batch. |
| Secrets | Tokens and passwords remain only in process memory; do not put them in command arguments, configuration output, logs, reports, or artifacts. |
| Artifacts | Comparison artifacts, `.sqlplan` files, named snapshots under `~/.sqlharness/snapshots`, Query Store text in `queries.jsonl`, and runtime parameter material are locally sensitive because they can embed SQL text, parameter values, and result data. `snapshot --diff` never prints cell values. Do not paste or publish `queries.jsonl` without explicit review. |

Never work around a safety rejection. Narrow the operation or obtain explicit approval instead. A rejected setup stops execution.

## Gain accounting

Every command except `gain` records metadata-only raw and emitted byte counts in `~/.sqlharness/data/gain.jsonl`; SQL text, result values, plans, messages, and secrets are not recorded there. The `gain` command estimates tokens as `ceil(UTF-8 bytes / 4)` using the `utf8-bytes-div-4` heuristic. `savedEstimatedTokens` remains the historical nonnegative gross field; `netEstimatedTokens` is the signed raw-minus-emitted delta, and savings percentage uses that signed net. Raw means SQLHarness's canonical internal representation, not the response from an alternative tool. This is a model-independent output-size estimate, not a tokenizer measurement or a claim about LLM cost.

### Activity journal

Every operation that reaches the SQLHarness module (`query`, `measure`, `compare`, `watch`, `snapshot`, `schema`, `counts`, `space`, `ping`, `qstop`, `indexes`, `plan`, `gain`; CLI or MCP) is recorded in a local activity journal (`~/.sqlharness/data/activity.db`, owner-only on Unix): operation, scope, target identity, status, timings, token estimates, and a SHA-256 hash of the SQL. Not recorded: `validate`, `doctor`, `capabilities`, MCP `artifact`, `--help`, and rejections before the module runs. MCP rows carry raw but not emitted token counts. Session identity is implicit (MCP `clientInfo`, or the CLI's nearest `claude`/`codex` ancestor process); agents send nothing extra. SQL text is stored only with `journal.storeSensitive: true` in `~/.sqlharness/config.json`; parameter values, secrets, and result cells are never stored as journal fields (only an opt-in stored plan can embed parameter values; see below). An invalid `config.json` falls back to defaults (hash-only). Journal failures never change output or exit codes. Treat `activity.db` as locally sensitive. For successful `measure` and `compare` runs the journal also stores per-variant medians, memory grant, DOP, compile, spill and wait diagnostics, per-table `STATISTICS IO` reads (SQL Server only: scan count and logical, physical, page-server, read-ahead, and LOB logical/physical/read-ahead reads; page-server read-ahead and LOB page-server counters are not kept; Postgres rows store relation buffer reads as logical reads and leave the detail counters null), and plan identity hashes; metric rows identify matrix cells by index only, never by value. Metrics a run marks unavailable are stored as null, and `STATISTICS IO` lines without logical reads (localized output, columnstore segment lines) are not recorded. Full plans are stored (gzip, deduplicated by plan shape) only with `journal.storeSensitive: true`; they embed statement text and may embed parameter values, including `--param-set` and matrix values (`ParameterCompiledValue` / `ParameterRuntimeValue`, or literals in Postgres plans). A stored plan is the first actual plan observed for its shape (`PlanIdentity`); per-run metrics are kept separately, so later runs with the same shape do not store another copy. `sqlharness doctor --json` reports whether the config file is present and valid, the effective journal settings, and whether the journal file exists.

```json
{
  "journal": { "enabled": true, "storeSensitive": false }
}
```

`dashboard.port` sets the dashboard's preferred port. The `retention` key and the other `dashboard` keys are accepted but have no effect until later releases.

### Dashboard

```powershell
sqlharness dashboard            # serve on 127.0.0.1 and open the browser
sqlharness dashboard --no-open  # print the URL only
```

One dashboard runs per SQLHarness home; a second invocation opens the running one. It listens only on `127.0.0.1` (default port `47800`, then the next free port, then an ephemeral port when those are taken), and the printed URL carries a one-time token that becomes an HttpOnly cookie. The browser is opened through an owner-only `dashboard-open.html` redirect page in the SQLHarness home, so the token never appears in process arguments; the page is deleted when the dashboard stops. The page shows live activity (running operations, active sessions, recent operations), session history, per-operation detail (key metrics, per-table IO, waits, memory grant, baseline-versus-candidate comparison, stored plans with an operator view when `journal.storeSensitive` is on), and statistics over 24 hours, 7 days, 30 days, or all time. The read-only API under `/api` serves the same data. Running operations whose process ended show as `abandoned`. The data it serves is the activity journal and is locally sensitive. Browsers do not isolate cookies by port, so the session cookie is also sent to other services on `127.0.0.1`/`localhost` opened in the same browser; use the dashboard on a single-user workstation. Press Ctrl+C (or send SIGTERM) to stop it; it exits `0`. It exits `6` when another dashboard holds the lock without publishing its address, when it cannot bind a loopback port, or when the activity journal schema does not match this sqlharness (newer, or older and not upgraded). Snap-confined browsers (for example Firefox or Chromium from a snap on Ubuntu) may not be allowed to open the redirect page under the hidden SQLHarness home directory; open the printed URL instead.

#### Building the dashboard UI

The UI lives in `src/SqlHarness.Dashboard/ui` (React, Vite, shadcn/ui on Base UI) and is embedded into the binary by `dotnet build`, which runs `npm ci` (when `node_modules` is missing) and `npm run build`. Node is pinned in `.nvmrc`. Build without Node with `dotnet build -p:SkipDashboardUi=true` (a placeholder page is served). For UI development run `sqlharness dashboard --no-open`, then in `ui/` run `SQLHARNESS_DASHBOARD_TOKEN=<t value> npm run dev`; the Vite dev server proxies `/api` to the running dashboard (set `SQLHARNESS_DASHBOARD_URL` when it is not on the default `http://127.0.0.1:47800`).

### Results to fill from real runs before publishing

| Scenario | Raw bytes | Emitted bytes | Net estimated token delta | Evidence |
| --- | ---: | ---: | ---: | --- |
| Public sample: `compare` | TBD | TBD | TBD | Add a reproducible run and artifact-free summary. |
| Public sample: `plan` | TBD | TBD | TBD | Add a reproducible run and source plan provenance. |
| Public sample: `schema` | TBD | TBD | TBD | Add a reproducible run and target shape. |

## Local data

- Target profiles: `~/.sqlharness/targets.json`
- Gain records: `~/.sqlharness/data/gain.jsonl`
- Activity journal: `~/.sqlharness/data/activity.db` (locally sensitive)
- Comparison artifacts: `~/.sqlharness/compare/`
- Named snapshots: `~/.sqlharness/snapshots/` (sensitive result data; replace only with `snapshot --force`)
- Query Store artifacts: `~/.sqlharness/query-store/` (`artifactDirectory/queries.jsonl` holds SQL text and is locally sensitive)

Set `SQLHARNESS_HOME` to relocate these paths, for example in an isolated test environment.

## Roadmap after v1

- Missing-index overlap analysis against existing indexes.

The local MCP server over stdio is implemented: see [docs/mcp.md](docs/mcp.md).

## Development

Run both gates before committing; they run the same four stages (restore, build, test, format) that
CI runs, with the local non-integration test filter. `verify.ps1` covers Windows; `verify-linux.ps1`
runs the identical stages inside WSL on a case-sensitive ext4 filesystem and is the only way to see
Linux-only behaviour before pushing — provision it once with `pwsh ./scripts/setup-linux-gate.ps1`
(installs the `global.json` SDK in the distro and creates a disposable clone under `~/src`; the gate
clone never lives under `/mnt/d`). Neither gate replaces CI, and CI itself currently runs
only ubuntu-latest with an unfiltered test step. A change is not done until both gates are green.
The .NET SDK version comes from `global.json` (currently `9.0.316`, `rollForward: latestPatch`).

```powershell
pwsh ./scripts/verify.ps1
pwsh ./scripts/verify-linux.ps1
dotnet test
dotnet run --project src\SqlHarness.Cli -- --help
```

The test suite uses fake adapters and fixtures; it does not connect to a real database.

### Optional local AdventureWorks playground

SQLHarness does not require a database to build, install, start, or run offline commands such as `plan` and `gain`. The local AdventureWorks environment exists only for development, manual experiments, and opt-in SQL Server integration tests. Docker resources and secrets remain local to your machine.

Docker stores the SQL Server bootstrap password in local container metadata, so the playground password must be development-only and unique. Do not reuse production credentials.

#### Setup

```powershell
$env:SQLHARNESS_PLAYGROUND_PASSWORD = Read-Host 'Local playground password'
.\scripts\setup-local-adventureworks.ps1
```

The script creates or reuses fixed local Docker resources (`sqlharness-sql` on host port `14335`, volume `sqlharness-sql-data`) and restores `AdventureWorks2022` when that database is absent. It never edits `~/.sqlharness/targets.json`.

#### Profile merge (manual CLI)

Merge—do not replace—this entry into `~/.sqlharness/targets.json`:

```json
{
  "local-playground": {
    "server": "localhost,14335",
    "database": "AdventureWorks2022",
    "vars": {},
    "auth": "sql",
    "sqlUser": "sa",
    "passwordEnvVar": "SQLHARNESS_PLAYGROUND_PASSWORD",
    "trustServerCertificate": true
  }
}
```

If an existing `local-playground` entry has different values, review it manually before changing anything. The bootstrap script never writes this file.

Keep `SQLHARNESS_PLAYGROUND_PASSWORD` set in the process that runs `sqlharness` against this profile.

#### Read-only CLI smoke checks

Use the installed command first:

```powershell
sqlharness schema local-playground --json
```

Create the local smoke query outside tracked repository paths:

```powershell
$smokeQuery = Join-Path $env:TEMP 'sqlharness-playground-smoke.sql'
@'
SELECT TOP (10) p.ProductID, p.Name, p.ListPrice
FROM Production.Product AS p
WHERE p.ListPrice > @minimumPrice
ORDER BY p.ListPrice DESC;
'@ | Set-Content $smokeQuery -Encoding utf8

sqlharness measure local-playground `
    --query $smokeQuery `
    --param minimumPrice:decimal=100 `
    --repeat 2 `
    --json
```

Expected: both commands exit `0`; schema identifies `AdventureWorks2022`, and measure returns bounded JSON output.

#### Opt-in SQL Server integration tests

`SQLHARNESS_INTEGRATION_CONNECTION_STRING` must reference an isolated test database. Integration tests never load `~/.sqlharness/targets.json`. Construct the connection string only in the current process:

```powershell
$env:SQLHARNESS_INTEGRATION_CONNECTION_STRING = `
    "Server=localhost,14335;Database=AdventureWorks2022;User ID=sa;Password=$($env:SQLHARNESS_PLAYGROUND_PASSWORD);TrustServerCertificate=True"

dotnet test tests/SqlHarness.Tests --filter Category=SqlServerIntegration --no-restore
```

Clean skip check (variable unset):

```powershell
Remove-Item Env:SQLHARNESS_INTEGRATION_CONNECTION_STRING -ErrorAction SilentlyContinue
dotnet test tests/SqlHarness.Tests --filter Category=SqlServerIntegration --no-restore
```

Expected: configured tests PASS (including `#temp` setup session-scope proof); unconfigured tests report skipped tests and zero failures.

#### Container lifecycle

Non-destructive stop, start, and restart only:

```powershell
docker stop sqlharness-sql
docker start sqlharness-sql
docker restart sqlharness-sql
```

Deleting the container or volume destroys local playground state and must be a separate, explicit user action. Automated removal is intentionally not part of the setup path.

### Optional local Postgres (Pagila) playground

Parallel to AdventureWorks, an optional Postgres playground uses fixed Docker resources and never edits `~/.sqlharness/targets.json`.

```powershell
$env:SQLHARNESS_PG_PLAYGROUND_PASSWORD = Read-Host 'Local Postgres playground password'
.\scripts\setup-local-postgres.ps1
```

The script creates or reuses `sqlharness-pg` on host port `5433` (volume `sqlharness-pg-data`, image `postgres:16`) and restores Pagila into database `pagila` when absent. It prints a `local-pg` profile JSON block for manual merge and never prints the password.

Keep `SQLHARNESS_PG_PLAYGROUND_PASSWORD` set when running against `local-pg`.

#### Opt-in Postgres integration tests

`SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING` is separate from the SQL Server integration variable. Construct it only in the current process:

```powershell
$env:SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING = `
    "Host=localhost;Port=5433;Database=pagila;Username=postgres;Password=$($env:SQLHARNESS_PG_PLAYGROUND_PASSWORD);SSL Mode=Disable"

dotnet test tests/SqlHarness.Tests --filter Category=PostgresIntegration --no-restore
```

Clean skip check (variable unset):

```powershell
Remove-Item Env:SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING -ErrorAction SilentlyContinue
dotnet test tests/SqlHarness.Tests --filter Category=PostgresIntegration --no-restore
```

Expected: configured tests PASS; unconfigured tests are skipped with zero failures and zero live connections.