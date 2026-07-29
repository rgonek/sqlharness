# SQLHarness — repeatable SQL Server optimization for coding agents

Measure, compare, and prove SQL changes.

SQLHarness is a command-line optimization harness for SQL Server and Azure SQL. It gives coding agents and engineers bounded query execution, repeated measurements, baseline/candidate equivalence checks, compact execution-plan distillation, schema inspection, and locally recorded output-savings evidence.

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
sqlharness schema prod-eu --var tenant=acme --var env=uat --json
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --repeat 5 --json
sqlharness plan .\artifacts\orders.sqlplan --json
sqlharness gain --json
```

`plan --json` emits a compact, deterministic plan contract: `statements` preserves input order; each statement has optional `sql`, a `root` node, and optional `missingIndexes`. Nodes retain `physicalOp`, optional `logicalOp`, operator metadata and runtime values, warnings (with their Showplan attributes), and child nodes. Persisted `*.plan.json` files from `compare` and `measure` use this same schema, including `sql`. This output is serialization-only; it is not accepted as plan input.

Use `--json` for automation. `query` reads SQL from exactly one source: `--file` or redirected stdin. Bind runtime values with repeatable `--param name[:type]=value`, never by string interpolation.

Supported `--param` types (culture-invariant; date/time values use ISO 8601):

`nvarchar`, `nvarchar(max)`, `varchar`, `varchar(max)`, `char`, `nchar`, `int`, `bigint`, `smallint`, `tinyint`, `bit`, `decimal`, `decimal(p,s)`, `numeric`, `numeric(p,s)`, `float`, `real`, `money`, `smallmoney`, `date`, `time`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`, `uniqueidentifier`, `varbinary`, `varbinary(max)`, `hierarchyid`, `geography`, `geometry`

Notes:

- Untyped `name=value` is `nvarchar`; strings longer than 4000 characters bind as `nvarchar(max)` (`Size = -1`).
- `uniqueidentifier` accepts any standard GUID format (`D`, `N`, `B`, `P`).
- `varbinary` values are Base64.
- `hierarchyid` / `geography` / `geometry` bind the path or WKT as `nvarchar` (cast in SQL when you need the native type).
- Nulls: `name:null` or typed `name:type:null` (for example `count:int:null`).

```powershell
sqlharness measure prod-eu --var tenant=acme --var env=uat `
  --query .\queries\orders.sql `
  --param asOf:datetime2=2026-07-29T12:00:00 `
  --param amount:decimal(19,4)=1234.5600 `
  --repeat 5 --json
```

## Benchmark setup contract

For `measure` and `compare`, SQLHarness opens one connection per invocation:

1. Runs optional `--setup` SQL **exactly once** on that connection.
2. Runs warm-up(s) on the same connection.
3. Runs all measured repetitions on the same connection.

Session-local `#temp` objects created in setup remain visible to warm-up and measured SQL. A rejected or failed setup stops the benchmark; SQLHarness does not retry by creating persistent objects or weakening safety.

Local temporary objects whose unqualified name starts with exactly one `#` may use session-only work without mutation confirmation: `CREATE TABLE #t` / `SELECT INTO #t`, `#temp` DML, `#temp` indexes and supported constraints (`NULL`/`NOT NULL`, `PRIMARY KEY`, `UNIQUE`), and `DROP TABLE #t`. Persistent objects, `##temp`, dynamic SQL, external access, cross-database references, and transaction control remain denied.

Before measuring, inventory a representative parameter range offline: row count, cardinality distribution (small/median/large/exceptional), ordering ties, missing history, boundary dates, and empty results. Keep ticket-specific benchmark SQL outside the application repository (ticket workspace or isolated temp directory). Treat plans, comparison artifacts, and runtime parameters as locally sensitive.

## Commands

| Command | Purpose |
| --- | --- |
| `query` | Run one bounded, classified SQL batch. |
| `measure` | Collect repeated timing, IO, and plan evidence for one query. |
| `compare` | Compare baseline and candidate queries, including result equivalence. |
| `schema` | Return a compact read-only catalog description. |
| `plan` | Distill a showplan XML file or stdin without connecting to a database. |
| `gain` | Summarize recorded raw-versus-emitted output savings. |

Run `sqlharness <command> --help` for the final option surface.

## Safety contract

| Area | Contract |
| --- | --- |
| Exit codes | `0` success; `2` validation or safety rejection; `3` authentication failure; `4` target mismatch; `5` SQL execution failure; `6` local storage failure. |
| Closed targets | Profiles in `~/.sqlharness/targets.json` define server, database template, variables, and authentication. Missing, extra, or invalid variables are rejected. |
| Direct targets | `--unsafe-direct` deliberately bypasses the closed-profile guardrail and requires `--server`, `--database`, and `--auth`. Do not mix it with a profile or `--var`. |
| Mutations | Read-only is the default. Persistent-object mutation requires fresh, single-use approval for the exact batch and exact resolved database, plus `--allow-mutation --confirm-database <exact-resolved-name>`. |
| Session `#temp` | Local `#temp` setup/DML/indexes are session-only and do not require mutation confirmation; setup runs once per connection and shares that session with warm-up and measured runs. |
| SQL input | Use exactly one query source (`--file` or stdin) and bound `--param` values. |
| Secrets | Tokens and passwords remain only in process memory; do not put them in command arguments, configuration output, logs, reports, or artifacts. |
| Artifacts | Comparison artifacts, `.sqlplan` files, and runtime parameter material are locally sensitive because they can embed SQL text and parameter values. |

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
