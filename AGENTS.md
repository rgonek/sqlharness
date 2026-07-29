# SQLHarness agent contract

SQLHarness is a repeatable SQL Server optimization harness for coding agents. It provides bounded query execution, measurements, baseline/candidate comparison with equivalence checks, compact plan distillation, schema inspection, and gain reporting.

## Start safely

```powershell
Get-Command sqlharness
sqlharness --help
```

Prefer `--json` for full reports, or `--json-summary` on `measure`/`compare` for a bounded agent-sized projection (mutually exclusive with `--json`). For a database request, lock one profile and one variable set per invocation; a different profile or variables needs a new explicit user request. Prefer closed named profiles to direct targets.

```powershell
sqlharness ping prod-eu --var tenant=acme --var env=uat --json
sqlharness counts prod-eu --var tenant=acme --var env=uat --table Contracts --table SourceFiles --exact --json
sqlharness counts prod-eu --var tenant=acme --var env=uat --like "%Sync%" --json
sqlharness schema prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
sqlharness schema prod-eu --var tenant=acme --var env=uat --json
sqlharness space prod-eu --var tenant=acme --var env=uat --top 25 --json
sqlharness space prod-eu --var tenant=acme --var env=uat --object dbo.Contracts --json
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --compare-results multiset --repeat 5 --json-summary
```

Use parameters instead of SQL interpolation. Supported types: `nvarchar`, `nvarchar(max)`, `varchar`, `varchar(max)`, `char`, `nchar`, `int`, `bigint`, `smallint`, `tinyint`, `bit`, `decimal`, `decimal(p,s)`, `numeric`, `numeric(p,s)`, `float`, `real`, `money`, `smallmoney`, `date`, `time`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`, `uniqueidentifier`, `varbinary`, `varbinary(max)`, `hierarchyid`, `geography`, `geometry`. Values are culture-invariant; date/time use ISO 8601 (for example `--param asOf:datetime2=2026-07-29T12:00:00` and `--param amount:decimal(19,4)=1234.5600`). GUIDs accept any standard format; `varbinary` is Base64; long strings and `nvarchar(max)` use `Size = -1`; `hierarchyid`/`geography`/`geometry` bind as true SQL UDTs (`Microsoft.SqlServer.Types`; spatial WKT may use `srid;WKT`); nulls are `name:null` or `name:type:null`.

`query` accepts exactly one SQL source: `--file` or redirected stdin. `schema` is read-only catalog inspection (`--object` selects exactly one table or view). `ping` and `counts` are fixed internal probes with no user SQL: `counts` defaults to partition estimates and uses `--exact` for `COUNT_BIG(*)`. `space` is read-only DMV storage diagnosis (`--top` default 25; `--object` for one exact table/view with per-index detail): files, aggregate allocation, and top tables by reserved space. It diagnoses storage only and never performs shrink, recovery-model change, compression change, or index mutation; any mutation still requires a separately approved `query --allow-mutation` batch. None of `ping`, `counts`, `schema`, or `space` accepts arbitrary SQL or mutations. `plan` is offline, needs no target or scope lock, and accepts a showplan XML file or stdin:

```powershell
sqlharness plan .\artifacts\orders.sqlplan --json
sqlharness gain --json
```

## Benchmark setup contract

- `measure` / `compare`: setup runs exactly once per connection; warm-up and all measured repetitions reuse that same session (so session-local `#temp` from setup is visible).
- A rejected or failed setup stops the run; never work around safety by creating persistent objects.
- Local `#temp` only (unqualified name starts with exactly one `#`): allowed for setup/DML/indexes/supported constraints without mutation confirmation. Persistent objects, `##temp`, dynamic SQL, external access, cross-database references, and transaction control remain denied.
- Before measuring, inventory representative cases offline: row count, cardinality distribution, ordering ties, missing history, boundary dates, and empty results.
- Keep ticket-specific benchmark SQL outside the application repository. Treat plans, artifacts, and runtime parameters as locally sensitive.

## Technical equivalence and summaries

- `compare --compare-results` modes: `ordered` (default), `multiset`, `set`, `off`. Every measured run participates; warm-ups do not. Directional counts are max over baseline/candidate measured pairs. Fingerprint comparison retains at most 1,000,000 rows per measured variant run.
- Technical equivalence is not domain equivalence. For set-style domain checks use two-direction `EXCEPT`; when duplicates matter use grouped counts. Still inventory ordering ties and missing history offline.
- `--json` = full report; `--json-summary` = bounded projection (≤10 noteworthy operators; no full run/operator arrays, plan XML, or result hashes). Choosing both fails with exit 2: `Choose only one of --json or --json-summary.`

## Safety contract

- Read-only is the default. A persistent mutation requires fresh, single-use user approval for the exact batch and resolved database, then both `--allow-mutation` and `--confirm-database <exact-resolved-name>`.
- Never work around a safety rejection. Report it and ask for an explicit, narrower request or approval.
- `--unsafe-direct` bypasses closed profiles. Use it only with an explicit request and complete `--server`, `--database`, and `--auth`; do not combine it with a profile or `--var`.
- Treat `.sqlplan`, comparison artifacts, and runtime parameters as locally sensitive. Secrets, passwords, and tokens stay only in process memory.
- Exit codes: `0` success; `2` validation/safety; `3` authentication; `4` target mismatch; `5` SQL execution; `6` local storage.
