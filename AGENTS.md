# SQLHarness agent contract

SQLHarness is a repeatable SQL Server and PostgreSQL optimization harness for coding agents. It provides bounded query execution, measurements, baseline/candidate comparison with equivalence checks, compact plan distillation, schema inspection, and gain reporting. Engine is a property of the locked profile (`engine: postgres` or omitted/`sqlserver`); `--engine` is valid only with `--unsafe-direct`.

## Start safely

```powershell
Get-Command sqlharness
sqlharness --help
```

For an offline agent startup path, use `sqlharness capabilities --json` and
`sqlharness doctor --json`, then classify a SQL file with
`sqlharness validate <profile> --file .\query.sql --json`. Add `--var` and
`--param` values only when the closed profile and SQL require them. Validation
never connects or executes SQL, reports object existence and database
permissions as unknown, and does not authorize a later mutation. See [the agent output contract](docs/superpowers/specs/2026-09-26-agent-output-contract.md)
and [audit roadmap](docs/superpowers/plans/2026-09-26-audit-roadmap.md).

Prefer `--json` for full reports, or `--json-summary` on `measure`/`compare` for a bounded agent-sized projection (mutually exclusive with `--json`). For a database request, lock one profile and one variable set per invocation; a different profile or variables needs a new explicit user request. Prefer closed named profiles to direct targets.

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

## Measure parameter sets

`measure --param-set` measures one query across at least two named parameter sets. Fixed `--param` values are shared by every set; per-set parameters come from each file and must not reuse a fixed name. A single scenario stays on `--param` alone.

```powershell
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --param tenant:nvarchar=acme --param-set .\sets\small.sqljson --param-set .\sets\large.sqljson --repeat 5 --json
```

Each `--param-set` file is strict JSON containing only `name` and `parameters`, at most 64 KiB, with no BOM, comments, or trailing commas. Those files are locally sensitive and remain the only SQLHarness input that contains those values. SQLHarness does not copy parameter values or source paths into reports. `.sqlplan` artifacts stay locally sensitive because server plan XML may still embed parameter values; do not publish them without review.

Multi-set measure uses one session and setup once. Setup runs once and binds the first parameter set's merged parameters. Later sets do not re-run setup. Each set is warmed once in user-supplied order, then measured rounds rotate on that same session. In one-based round r, measured execution starts at index r modulo setCount and wraps in user-supplied order. Per-set stability is the only stability result; SQLHarness does not claim cross-set result equivalence. Parameter-set measurements use the observed server plan-cache state; SQLHarness did not clear or isolate the plan cache. Timings are order-dependent because of that shared cache.

This is not `compare --matrix`. Matrix mode varies one parameter across two query variants and opens a fresh connection per value. `--param-set` measures one query on one connection.

`--json-summary` for a multi-set measure is a bounded projection: the cache warning, the rotation rule, each set name, parameter type metadata, the value hash, per-set stability, median elapsed/CPU/reads, cross-set labels, and the artifact directory. It omits parameter values, plan XML, and full run arrays. Noteworthy operators, when included, stay capped at ten.

Use parameters instead of SQL interpolation. Supported types: `nvarchar`, `nvarchar(max)`, `varchar`, `varchar(max)`, `char`, `nchar`, `int`, `bigint`, `smallint`, `tinyint`, `bit`, `decimal`, `decimal(p,s)`, `numeric`, `numeric(p,s)`, `float`, `real`, `money`, `smallmoney`, `date`, `time`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`, `uniqueidentifier`, `varbinary`, `varbinary(max)`, `hierarchyid`, `geography`, `geometry`. Values are culture-invariant; date/time use ISO 8601 (for example `--param asOf:datetime2=2026-07-29T12:00:00` and `--param amount:decimal(19,4)=1234.5600`). GUIDs accept any standard format; `varbinary` is Base64; long strings and `nvarchar(max)` use `Size = -1`; `hierarchyid`/`geography`/`geometry` bind as true SQL UDTs (`Microsoft.SqlServer.Types`; spatial WKT may use `srid;WKT`); nulls are `name:null` or `name:type:null`.

`query` accepts exactly one SQL source: `--file` or redirected stdin. `schema` is read-only catalog inspection (`--object` selects exactly one table or view). `ping` and `counts` are fixed internal probes with no user SQL: `counts` defaults to partition estimates and uses `--exact` for `COUNT_BIG(*)`. `space` is read-only DMV storage diagnosis (`--top` default 25; `--object` for one exact table with per-index detail): files, aggregate allocation, and top tables by reserved space. It diagnoses storage only and never performs shrink, recovery-model change, compression change, or index mutation; any mutation still requires a separately approved `query --allow-mutation` batch. None of `ping`, `counts`, `schema`, `space`, or `qstop` accepts arbitrary SQL or mutations. `watch` polls a bounded read-only query (same SQL/`--param` pipeline as `query`; mutations reject with exit `2`) until exactly one of `--until` (predicate on the first row of the first result set) or `--until-unchanged` (default when neither is supplied: `--until-unchanged 3`). Defaults: `--interval 30`, `--max-duration 15m`. Exit `0` for condition-met/unchanged; exit `7` when max duration elapses without a stop condition. `snapshot` stores a named canonical result under `~/.sqlharness/snapshots` (sensitive result data—treat as locally sensitive). Capture with `--name`; `--force` is required to replace an existing name. `--diff` compares live results to the stored snapshot and never prints cell values (locations/kinds only): exit `0` when identical, exit `8` when a valid comparison found differences rather than an execution failure.

`qstop` ranks SQL Server Query Store consumers by total duration, then total CPU, then executions, then `query_id`. It is read-only, accepts no user SQL, and has no mutation flags. Defaults: `--top 20`, `--window 24h` (1440 minutes), and `--timeout 30`. `--window` is a positive integer with an `m`, `h`, or `d` suffix totaling 1..44640 minutes; `--top` is 1..500; `--timeout` is 1..300. Target scope is the usual profile, `--var`, or `--unsafe-direct` options. One item aggregates all plans and runtime intervals into that one `query_id`. An empty result from a readable window exits 0; unavailable Query Store (`OFF`, `ERROR`, or malformed results) and Postgres exit 5; an artifact write failure exits 6. Stdout has ids, hashes, and metrics only and has no SQL text. SQL text is only in `artifactDirectory/queries.jsonl`. That artifact is locally sensitive and must not be pasted or published without explicit review. A high rank is a lead to `measure`, `compare`, and `plan`, not proof the query is defective.

`plan` is offline, needs no target or scope lock, and accepts a showplan XML file or stdin:

```powershell
sqlharness plan .\artifacts\orders.sqlplan --json
sqlharness gain --json
```

For agent-authored SQL, every ScriptDom-parsable construct inside a top-level `SELECT` is accepted without per-fragment registration. This includes CTEs, scalar functions, table/index hints, optimizer hints, windowing, and derived/apply syntax. Independent safety checks still reject unsupported top-level statements, cross-database access, external or stateful sources, dynamic SQL, and writes outside the approved local-`#temp` or mutation contracts. Do not fall back to `sqlcmd` merely because a safe nested `SELECT` construct is unfamiliar to the parser AST. SQLHarness sends one batch; `GO` separators are rejected. Split work into separate invocations or use `--setup`.

## PostgreSQL engine notes

- Profiles may set `"engine": "postgres"`; omitted means SQL Server. Prefer closed named profiles (for example `local-pg`). `--engine` only with `--unsafe-direct` (never with a profile or `--var`).
- Postgres auth is `sql` only (`sqlUser` + `passwordEnvVar`). Password values stay in that environment variable. Error text must not contain the password; a missing-password error may still name the variable.
- Omitted `sslMode` and `rootCertificate` keep today's mapping and the outcome's `target.transportPolicy` is `legacy` (`trustServerCertificate: true` → `SslMode=Disable`, `false` → `SslMode=Require`). Explicit `sslMode` (`verify-full`, `verify-ca`, `require`, `disable`) is authoritative. A combination that contradicts `trustServerCertificate` is rejected. `rootCertificate` is accepted only with `verify-full` or `verify-ca`. SQL Server profiles that contain the Postgres fields are rejected. New remote examples use `verify-full`. `--ssl-mode` and `--root-certificate` exist only on `--unsafe-direct`. This is not the plan 03 agent envelope.
- Postgres endpoint identity always checks the database. It is the connection that was established and, for `verify-full` / `verify-ca`, authenticated — not text equality between a DNS name and `inet_server_addr()`. Loopback still checks only the database. `require` and `disable` do not authenticate a DNS name. A proxy address the name does not resolve to fails. DNS by itself is not authentication. Policy: `docs/superpowers/specs/2026-09-26-postgres-transport-policy.md`.
- Session temps are native `CREATE TEMP TABLE` / `TEMPORARY` — no `#temp` translation. Persistent DML still needs `--allow-mutation --confirm-database`; persistent DDL stays denied. A write or DDL target (`INSERT`/`UPDATE`/`DELETE`/`MERGE`, `DROP TABLE`, `CREATE INDEX`, `TRUNCATE`) is session-local only when it is a proven current-session temp: an unqualified name recorded from `CREATE TEMP TABLE` / `SELECT INTO TEMP` in this flow, declared without `ON COMMIT DROP`, and spelled quoted or all-ASCII-unquoted in at most 63 UTF-8 bytes both where declared and where used. Any other name is never proven; quote or shorten it. The proof is name-based and assumes the default `search_path`, where `pg_temp` is searched first: under a `search_path` that puts another schema ahead of `pg_temp`, a proven unqualified name can resolve to a persistent table and SQLHarness does not detect that. This limitation applies to unqualified names only. When `search_path` may be non-default, write to session temps as `pg_temp.<relation>`, which always addresses the current session's temp schema: for DML, `DROP TABLE` and `CREATE INDEX` the two-part `pg_temp.<relation>` alias is session-local by itself, and `TRUNCATE [ONLY] pg_temp.<relation>` is allowed when `<relation>` is a proven temp. A `pg_temp_` prefix (a relation name or a `pg_temp_<N>` schema) is never proof, and a `CREATE TEMP TABLE` whose name carries any qualifier other than `pg_temp` proves nothing. `DROP TABLE` revokes the proof of the name it drops; when that name's stored form is unknown offline (unquoted non-ASCII or over 63 bytes, reachable only as `pg_temp.<relation>`), it revokes every proof in the flow until a new `CREATE TEMP TABLE` proves a name again. An unproven DML target is an ordinary persistent mutation (`MutationNotAllowed` without approval); when the flow declared a TEMP table that recorded no proof, that denial adds a hint to quote or shorten the name instead of asking for mutation approval. An unproven `DROP TABLE` / `CREATE INDEX` target is denied. `TRUNCATE [ONLY] <temp[, ...]>` is allowed only when every target is a proven temp, named unqualified or as `pg_temp.<relation>`; a persistent, mixed, or otherwise schema-qualified target (`public.t`, `pg_temp_<N>.t`, a three-part name, `pg_temp.<unproven>`) is denied as `NonTemporaryWrite`, and `CASCADE` / `RESTART IDENTITY` as `UnsupportedStatement`, with or without mutation approval. Canonical `ANALYZE tbl` stays rejected until the parser supports it (design-only spike: `plans/011-analyze-parser-spike.md`).
- Rejected `--param` types on Postgres: `money`, `smallmoney`, `smalldatetime`, `hierarchyid`, `geography`, `geometry`.
- `measure` / `compare` time one EXPLAIN-able statement with `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`; result equivalence uses an unmeasured sidecar. `CpuTimeMs` is `0`; `logicalReads` are buffer hits+reads; `missingIndexes` is empty.
- `counts` / `schema` / `space` / `--like` / `--filter` / `--object` match identifiers case-sensitively as stored (`Contracts` ≠ `contracts`).
- `space` analogs: Files = one `DATA` row (`pg_database_size`); Allocation Reserved vs Used/Data need not sum; Tables by `pg_total_relation_size` + `reltuples`; index `Type` = access method, `Compression` = null.
- `qstop` reads SQL Server Query Store only and returns exit 5 on Postgres.
- Optional playground: `.\scripts\setup-local-postgres.ps1` with `SQLHARNESS_PG_PLAYGROUND_PASSWORD` → container `sqlharness-pg`, port `5433`, volume `sqlharness-pg-data`, image `postgres:16`, database `pagila`. Never writes `targets.json`. Opt-in live tests use `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING` (not the SQL Server integration variable). The TLS proof uses `SQLHARNESS_PG_TLS_PROOF` only and does not read user profiles, `~/.sqlharness/targets.json`, or `CIVICLENS_POSTGRES_PASSWORD`.

## Benchmark setup contract

- `measure` / `compare`: setup runs exactly once per connection; warm-up and all measured repetitions reuse that same session (so session-local `#temp` / Postgres `TEMP` from setup is visible).
- `compare --matrix` replaces that single invocation connection with a new connection and one setup per value (one dimension, at least two typed values, sequential user-supplied order). The first failure stops the run; completed cell artifacts remain. Ticket SQL stays outside the application repository.
- A rejected or failed setup stops the run; never work around safety by creating persistent objects.
- SQL Server: local `#temp` only (unqualified name starts with exactly one `#`): allowed for setup/DML/indexes/supported constraints without mutation confirmation, plus scalar `DECLARE` with analyzed initializers, `DECLARE @t TABLE (...)` then `INSERT`/`UPDATE`/`DELETE`/`MERGE`/`SELECT` against that table variable when its `DECLARE` comes earlier in the same batch (a table variable declared in `--setup` is not visible to the measured SQL, which is a separate batch; use `#temp` to carry data out of setup), `TRUNCATE TABLE #temp`, and `ALTER TABLE #temp` limited to add/drop columns and local `CHECK`/`DEFAULT`/`NULL`/`UNIQUE` constraints. `OUTPUT INTO @t` is session-local only when the statement's primary target is also proven local (`#temp` or a table variable); with a persistent primary target the statement is a persistent mutation (`MutationNotAllowed` without approval, `NonTemporaryWrite` in setup). Undeclared, cross-batch, use-before-`DECLARE`, and scalar-as-table targets, aliased DML targets (`UPDATE x ... FROM @t AS x`), and user-defined table types stay denied. Plain `SET @v = <expr>` to a scalar local declared earlier in the same batch is allowed in query and measured SQL only, and `--setup` denies it; the RHS goes through the same external/stateful/cross-database checks, and compound `SET` (`+=`), cursor `SET`, a member or method target (`SET @v.Member = ...`), session/transaction option `SET`, undeclared or cross-batch targets, and use before `DECLARE` stay denied. Postgres: native `TEMP` / `TEMPORARY` only — no `#temp` rewrite — plus unambiguous `SELECT INTO TEMP TABLE`, `TRUNCATE [ONLY]` of proven current-session temps only, named unqualified or as `pg_temp.<relation>` (persistent, mixed, `CASCADE`, `RESTART IDENTITY`, and other schema-qualified targets stay denied; the proof rules, including the name and `search_path` limits, are in the PostgreSQL engine notes), plan-only `EXPLAIN` over a safe `SELECT`, and `EXPLAIN ANALYZE` with full inner-statement effect analysis. Canonical `ANALYZE tbl` stays rejected until the parser supports it. Persistent objects, `##temp`, dynamic SQL, external access, cross-database references, and transaction control remain denied.
- Before measuring, inventory representative cases offline: row count, cardinality distribution, ordering ties, missing history, boundary dates, and empty results.
- Keep ticket-specific benchmark SQL outside the application repository. Treat plans, artifacts, and runtime parameters as locally sensitive.

## Technical equivalence and summaries

- `compare --compare-results` modes: `ordered` (default), `multiset`, `set`, `off`. Every measured run participates; warm-ups do not. Directional counts are max over baseline/candidate measured pairs. Fingerprint comparison retains at most 1,000,000 rows per measured variant run.
- Technical equivalence is not domain equivalence. For set-style domain checks use two-direction `EXCEPT`; when duplicates matter use grouped counts. Still inventory ordering ties and missing history offline.
- `--json` = full report; `--json-summary` = bounded projection (≤10 noteworthy operators; no full run/operator arrays, plan XML, or result hashes). Choosing both fails with exit 2: `Choose only one of --json or --json-summary.`

## Safety contract

- Read-only is the default. A persistent mutation requires fresh, single-use user approval for the exact batch and resolved database, then both `--allow-mutation` and `--confirm-database <exact-resolved-name>`.
- Never work around a safety rejection. Report it and ask for an explicit, narrower request or approval.
- `--unsafe-direct` bypasses closed profiles. Use it only with an explicit request and complete `--server`, `--database`, and `--auth`; do not combine it with a profile or `--var`. Optional `--engine` is allowed only on that direct path.
- Treat `.sqlplan`, comparison artifacts, named snapshots under `~/.sqlharness/snapshots` (sensitive result data; replace only with `--force`), `qstop` artifacts (`artifactDirectory/queries.jsonl`), and runtime parameters as locally sensitive. `snapshot --diff` never prints cell values. Do not paste or publish `queries.jsonl` without explicit review. Secrets, passwords, and tokens stay only in process memory.
- Every operation that reaches the SQLHarness module (`query`, `measure`, `compare`, `watch`, `snapshot`, `schema`, `counts`, `space`, `ping`, `qstop`, `indexes`, `plan`, `gain`; CLI or MCP) is recorded in a local activity journal (`~/.sqlharness/data/activity.db`, owner-only on Unix): operation, scope, target identity, status, timings, token estimates, and a SHA-256 hash of the SQL. Not recorded: `validate`, `doctor`, `capabilities`, MCP `artifact`, `--help`, and rejections before the module runs. CLI rows and newly completed MCP tool results carry raw and emitted token estimates; historical MCP rows that have no emitted count remain unavailable to gain and Statistics. MCP emitted estimates use the SDK-serialized UTF-8 bytes of the complete budgeted `CallToolResult` and the `ceil(bytes / 4)` estimator; they estimate the tool result representation, not actual model tokenization or the full JSON-RPC frame. Session identity is implicit (MCP `clientInfo`, or the CLI's nearest `claude`/`codex` ancestor process); agents send nothing extra. SQL text is stored only with `journal.storeSensitive: true` in `~/.sqlharness/config.json`; the full error message of a failed or rejected operation is stored under the same flag (truncated to 4096 characters); parameter values, secrets, and result cells are never stored as journal fields (only an opt-in stored plan or stored error message can embed parameter or data values; error messages can also quote SQL; see below). An invalid `config.json` falls back to defaults (hash-only). Journal failures never change output or exit codes. Treat `activity.db` as locally sensitive. For successful `measure` and `compare` runs the journal also stores per-variant medians, memory grant, DOP, compile, spill and wait diagnostics, per-table `STATISTICS IO` reads (SQL Server only: scan count and logical, physical, page-server, read-ahead, and LOB logical/physical/read-ahead reads; page-server read-ahead and LOB page-server counters are not kept; Postgres rows store relation buffer reads as logical reads and leave the detail counters null), and plan identity hashes; metric rows identify matrix cells by index only, never by value. Metrics a run marks unavailable are stored as null, and `STATISTICS IO` lines without logical reads (localized output, columnstore segment lines) are not recorded. Full plans are stored (gzip, deduplicated by plan shape) only with `journal.storeSensitive: true`; they embed statement text and may embed parameter values, including `--param-set` and matrix values (`ParameterCompiledValue` / `ParameterRuntimeValue`, or literals in Postgres plans). A stored plan is the first actual plan observed for its shape. `gain` and Statistics aggregate only operations with paired raw/emitted footprints; new MCP records are included automatically, while historical raw-only MCP rows stay unavailable and are not backfilled. Totals cover only operations the journal still holds (retention trims them); `gain.jsonl` is no longer written or read and its data is not migrated. With `journal.enabled: false`, `gain` reports zeros with `journalEnabled: false`; a gain storage failure never changes an exit code. Retention is opt-in (`journal.retention.{enabled,maxAgeDays,maxSizeMb}`, default off/30/500): at dashboard start, hourly in the dashboard, and once at `mcp serve` start, it deletes operations older than `maxAgeDays`, unreferenced plans, and empty sessions, then the oldest operations while the used size exceeds `maxSizeMb`, in ≤500-row transactions, then `incremental_vacuum`. It never deletes a running operation whose host process is alive or cannot be checked (on Linux an unreadable `/proc` entry counts as gone); abandoned running rows (host process gone) are deleted like finished ones.
- `sqlharness dashboard` is an operator tool, not an agent command: agents do not start it or call its API. It serves the activity journal read-only on `127.0.0.1` behind a per-start token cookie, shows `targets.json` profiles read-only (password variable names only, never values), and can edit operator settings in `config.json` through its one write endpoint (`PUT /api/settings`, which also requires the `X-SqlHarness-Dashboard` header and a same-origin `Origin`; `dashboard.port` is never changed there). Saved settings apply to new processes; restart running `mcp serve` sessions. With `dashboard.autoStart` (default `false`; an invalid `config.json` never enables it), `mcp serve` launches it detached (`--background`) after startup validation without touching MCP stdout; agents still never call its API. Only that background instance idle-exits, after `dashboard.idleShutdownHours` (default 8) without journal writes, requests, or open streams. It shares the MCP client's process group, so a Ctrl+C/SIGHUP to that group (or closing its Windows console) also stops it; the next `mcp serve` relaunches it.
- Exit codes: `0` success; `2` validation/safety; `3` authentication; `4` target mismatch; `5` SQL execution; `6` local storage; `7` `watch` max duration without a stop condition; `8` `snapshot --diff` found differences (valid comparison, not an execution failure).

## Verify changes

```powershell
pwsh ./scripts/verify.ps1
pwsh ./scripts/verify-linux.ps1
```

> CI runs the same four stages on ubuntu-latest; tests that pass only on Windows are not done.
> `verify-linux.ps1` runs those stages inside WSL on case-sensitive ext4 (provision once with
> `pwsh ./scripts/setup-linux-gate.ps1`); a change is not done until both gates are green.

## MCP server (local stdio)

One `sqlharness` process serves one MCP client over local stdio. It supports fixed-profile mode and SQL Server-only request-scoped mode. Full contract and client configuration examples: [docs/mcp.md](docs/mcp.md).

```powershell
sqlharness mcp serve sample-country --var tenant=example --var env=test --input-root C:\work\sqlharness-inputs
sqlharness mcp serve --request-scope --allow-profile sample-country --allow-profile sample-shared --input-root C:\work\sqlharness-inputs
```

- Fixed mode freezes one profile and its `--var` values at startup. Request mode freezes a nonempty explicit allowlist of SQL Server profiles and resolves a required nested `scope` on each target-dependent call. Neither mode accepts `--unsafe-direct` (exit `2`).
- Exactly 11 tools: `sqlharness_capabilities`, `sqlharness_inspect` (`ping`/`schema`/`counts`/`space`/`qstop`/`indexes`; `qstop`/`indexes` are SQL Server only), `sqlharness_validate` (offline), `sqlharness_query`, `sqlharness_measure`, `sqlharness_compare`, `sqlharness_watch`, `sqlharness_snapshot`, `sqlharness_plan` (offline, sanitized), `sqlharness_artifact` (`summary`/`metrics`/`operators`), `sqlharness_gain`.
- Request scope is required on inspect, validate, query, measure, compare, watch, snapshot, and artifact. Capabilities, plan, and gain stay target-free.
- File inputs only under absolute `--input-root` directories (empty by default means no file inputs); inline payloads above 1 MiB must arrive as files. Parameter-set files stay strict `.sqljson` (64 KiB, only `name` and `parameters`).
- No persistent mutations: query runs with `AllowMutation: false` and no confirmation database; snapshot capture never overwrites and has no force flag.
- Budgets are enforced on wire bytes: one `CallToolResult` defaults to 16384 B (range 4096..1048576), tools/list to 32768 B, cells to 512 characters (max 4096). One database operation per process across all request scopes; a second concurrent call gets a stable `busy` rejection whose hint names the running tool; wait for it to return before the next call. Controlled outcomes `watch_max_duration` (exit `7`) and `snapshot_differences` (exit `8`) are valid results with `isError=false`.
- Stdout carries only protocol frames; logs go to stderr without arguments, SQL, values, or secrets. Tested SDK `ModelContextProtocol` 2.2.0 with protocol revisions `2025-06-18`, `2025-11-25` and `2026-07-28` (negotiated; older `initialize` requests get `2025-11-25`); stderr carries SDK warnings and errors only. Register the client entry manually; SQLHarness never edits client configuration. Byte counts are not token counts.
