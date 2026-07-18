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

Use `--json` for automation. `query` reads SQL from exactly one source: `--file` or redirected stdin. Bind runtime values with repeatable `--param name[:type]=value`, never by string interpolation.

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
| SQL input | Use exactly one query source (`--file` or stdin) and bound `--param` values. |
| Secrets | Tokens and passwords remain only in process memory; do not put them in command arguments, configuration output, logs, reports, or artifacts. |
| Artifacts | Comparison artifacts and `.sqlplan` files are locally sensitive because they can embed SQL text and parameter values. |

Never work around a safety rejection. Narrow the operation or obtain explicit approval instead.

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
