# SQLHarness — repeatable SQL Server optimization for coding agents

Measure, compare, and prove SQL changes.

SQLHarness is a command-line optimization harness for SQL Server, Azure SQL, and PostgreSQL. It gives coding agents and engineers bounded query execution, repeated measurements, baseline/candidate equivalence checks, compact execution-plan distillation, schema inspection, and locally recorded output-savings evidence. The engine is a property of the locked target profile (omit `engine` for SQL Server; set `"engine": "postgres"` for Postgres).

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
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --compare-results multiset --repeat 5 --json-summary
sqlharness plan .\artifacts\orders.sqlplan --json
sqlharness gain --json
```

Read-only database helpers (`ping`, `counts`, `schema --object`, and `space`) use fixed internal catalog/probe/DMV SQL only: they never accept an arbitrary user SQL batch or mutations. `counts` defaults to approximate row counts from partition statistics; pass `--exact` for `COUNT_BIG(*)`. `space` diagnoses storage only (files, aggregate allocation, top tables by reserved space, optional per-index detail for `--object`); it never performs shrink, recovery-model change, compression change, or index mutation—any mutation still requires a separately approved `query --allow-mutation` batch. Prefer `--json` for machine-readable reports.

`watch` polls a bounded read-only query until a stop condition: exactly one of `--until` (predicate on the first row of the first result set) or `--until-unchanged` (stable hash across consecutive polls; default when neither is supplied is `--until-unchanged 3`). Defaults: `--interval 30` seconds and `--max-duration 15m` (positive integral `s`/`m`/`h`, max 24h). Exit `0` when the condition is met or results stay unchanged; exit `7` when the max duration elapses without a stop condition. `snapshot` stores a named canonical result under `~/.sqlharness/snapshots` (sensitive result data—treat like comparison artifacts). Capture with `--name`; replace an existing name only with `--force`. `--diff` compares the live query against the stored snapshot without printing cell values (locations and kinds only): exit `0` when identical, exit `8` when a valid comparison found differences rather than an execution failure. Both accept the same single SQL source and `--param` pipeline as `query` (mutation classification rejects with exit `2`).

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
- Postgres auth is `sql` only (`sqlUser` + `passwordEnvVar`). `ad-default` / `azure-cli` / `integrated` are rejected (exit 2).
- `trustServerCertificate: true` maps to Npgsql `SslMode=Disable` (local Docker); `false` maps to `SslMode=Require`. There is no separate `sslMode` profile field.
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
  }
}
```

## Benchmark setup contract

For `measure` and `compare`, SQLHarness opens one connection per invocation:

1. Runs optional `--setup` SQL **exactly once** on that connection.
2. Runs warm-up(s) on the same connection.
3. Runs all measured repetitions on the same connection.

Session-local `#temp` objects created in setup remain visible to warm-up and measured SQL. A rejected or failed setup stops the benchmark; SQLHarness does not retry by creating persistent objects or weakening safety.

Local temporary objects whose unqualified name starts with exactly one `#` may use session-only work without mutation confirmation: `CREATE TABLE #t` / `SELECT INTO #t`, `#temp` DML, `#temp` indexes and supported constraints (`NULL`/`NOT NULL`, `PRIMARY KEY`, `UNIQUE`), and `DROP TABLE #t`. Persistent objects, `##temp`, dynamic SQL, external access, cross-database references, and transaction control remain denied.

Before measuring, inventory a representative parameter range offline: row count, cardinality distribution (small/median/large/exceptional), ordering ties, missing history, boundary dates, and empty results. Keep ticket-specific benchmark SQL outside the application repository (ticket workspace or isolated temp directory). Treat plans, comparison artifacts, and runtime parameters as locally sensitive.

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
| Session `#temp` / `TEMP` | SQL Server: local `#temp` setup/DML/indexes are session-only and do not require mutation confirmation. Postgres: use native `CREATE TEMP TABLE` / `TEMPORARY` (no `#temp` translation). Setup runs once per connection and shares that session with warm-up and measured runs. |
| SQL input | Use exactly one query source (`--file` or stdin) and bound `--param` values. Parsed syntax inside a top-level `SELECT` is accepted without a fragment allowlist; independent safety checks still reject cross-database access, external or stateful sources, dynamic SQL, and writes outside the approved local-`#temp` or mutation contracts. Helpers (`ping`, `counts`, `schema`, `space`) never accept arbitrary user SQL. `space` is read-only DMV diagnosis only—no shrink, recovery-model, compression, or index mutation; mutations need a separately approved `query --allow-mutation` batch. |
| Secrets | Tokens and passwords remain only in process memory; do not put them in command arguments, configuration output, logs, reports, or artifacts. |
| Artifacts | Comparison artifacts, `.sqlplan` files, named snapshots under `~/.sqlharness/snapshots`, and runtime parameter material are locally sensitive because they can embed SQL text, parameter values, and result data. `snapshot --diff` never prints cell values. |

Never work around a safety rejection. Narrow the operation or obtain explicit approval instead. A rejected setup stops execution.

## Gain accounting

Every command except `gain` records metadata-only raw and emitted byte counts in `~/.sqlharness/data/gain.jsonl`; SQL text, result values, plans, messages, and secrets are not recorded there. The `gain` command derives estimated tokens as `bytes / 4` (integer byte/4 accounting). This is a model-independent heuristic for comparing output sizes, not a tokenizer measurement or a guarantee of model cost.

### Results to fill from real runs before publishing

| Scenario | Raw bytes | Emitted bytes | Estimated token savings | Evidence |
| --- | ---: | ---: | ---: | --- |
| Public sample: `compare` | TBD | TBD | TBD | Add a reproducible run and artifact-free summary. |
| Public sample: `plan` | TBD | TBD | TBD | Add a reproducible run and source plan provenance. |
| Public sample: `schema` | TBD | TBD | TBD | Add a reproducible run and target shape. |

## Local data

- Target profiles: `~/.sqlharness/targets.json`
- Gain records: `~/.sqlharness/data/gain.jsonl`
- Comparison artifacts: `~/.sqlharness/compare/`
- Named snapshots: `~/.sqlharness/snapshots/` (sensitive result data; replace only with `snapshot --force`)

Set `SQLHARNESS_HOME` to relocate these paths, for example in an isolated test environment.

## Roadmap after v1

- Missing-index overlap analysis against existing indexes.
- A Query Store `top` command.
- Parameter-sniffing checks in `measure` across multiple parameter sets.
- A thin MCP facade over `SqlHarness.Core`.

## Development

```powershell
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
