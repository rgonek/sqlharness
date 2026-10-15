<picture>
  <source media="(prefers-color-scheme: dark)" srcset="src/SqlHarness.Dashboard/ui/public/logo-dark.svg">
  <img src="src/SqlHarness.Dashboard/ui/public/logo.svg" alt="SQLHarness" width="320">
</picture>

# SQLHarness

SQLHarness helps engineers and coding agents measure SQL changes on SQL Server, Azure SQL, and PostgreSQL. Run bounded queries, compare a baseline with a candidate, check their results, and inspect compact execution plans. SQLHarness also records workflow output costs locally. The engine is a property of the locked target profile (omit `engine` for SQL Server; set `"engine": "postgres"` for Postgres).

[Install](#install-a-release-binary) · [Quick start](#quick-start) · [MCP](#mcp-over-local-stdio) · [Commands](#commands) · [Safety](#safety-contract) · [Dashboard](#dashboard) · [Development](#development)

## Install a release binary

SQLHarness releases are distributed only as self-contained, single-file, untrimmed binaries with the .NET 10 runtime included. Download the archive matching your platform (`win-x64`, `linux-x64`, or `osx-arm64`) and the accompanying `SHA256SUMS` from the [GitHub Releases page](https://github.com/rgonek/sqlharness/releases). Verify the archive before extracting it, then put `sqlharness.exe` on `PATH` for Windows or `sqlharness` on `PATH` for Linux/macOS.

Release binaries take their numeric version from the release tag (for example, tag `v0.4.0` produces version `0.4.0`). Local development builds report `0.0.0` with `--version`; their informational `buildId` is `0.0.0-dev` and may include a commit suffix.

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

<!-- docs-sync:example -->
```powershell
sqlharness watch prod-eu --var tenant=acme --var env=uat --file .\queries\progress.sql --param target:int=1000 --until "Imported >= 1000" --interval 30 --max-duration 45m --json
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --json
sqlharness snapshot prod-eu --var tenant=acme --var env=uat --file .\queries\coverage.sql --name before-import --diff --json
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --subject orders-report --scope whole --baseline .\queries\before.sql --candidate .\queries\after.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --subject orders-report --scope whole --baseline .\queries\before.sql --candidate .\queries\after.sql --compare-results multiset --repeat 5 --json-summary
sqlharness plan .\artifacts\orders.sqlplan --json
```

`plan` is offline and returns a compact tree with bounded statement text and predicates unchanged from the caller-supplied plan. Output limits clip long cells and mark truncation.

Compare context identifies the optimization target (`--subject`) and the tested fragment (`--scope`). Use stable short labels; use exactly `whole` for a direct whole-query comparison. The global `compare.requireContext` setting in `~/.sqlharness/config.json` enables context by default, independently of the journal. With it enabled, both labels are required; when disabled, both options may be omitted or supplied together. MCP freezes this setting when the server starts, so changing it requires restarting the server and reconnecting the client.

When disabled, MCP hides and rejects the textual `subject` and `scope` fields; a request-scoped `target` remains required to select the database.

The dashboard assigns `v0`, `v1`, and later labels to distinct SQL hashes within one `(sessionId, subject, scope)` group, in first-seen order. The labels identify SQL content within that group. Choosing a winning version or proving that a fragment is equivalent to the whole query requires separate evidence. Numbering uses the full retained journal history before filtering or paging. Journal retention can remove earlier observations, so version numbers may change after history expires; the dashboard shows retained history only.

`compare` accepts one `--matrix` dimension:

<!-- docs-sync:example -->
```powershell
sqlharness compare prod-eu `
  --var tenant=acme --var env=uat `
  --subject orders-report --scope whole `
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

`watch` polls a bounded read-only query until a stop condition: exactly one of `--until` (predicate on the first row of the first result set) or `--until-unchanged` (stable hash across consecutive polls; default when neither is supplied is `--until-unchanged 3`). Values compare numerically when both sides parse as numbers (exponents allowed); `bit`/`boolean` compare as `1`/`0` (`true`/`false` accepted); quote a string operand to force text comparison; no row or a NULL value means the condition is not met yet.

Defaults: `--interval 30` seconds and `--max-duration 15m` (positive integral `s`/`m`/`h`, max 24h). Exit `0` when the condition is met or results stay unchanged; exit `7` when the max duration elapses without a stop condition. `snapshot` stores a named canonical result under `~/.sqlharness/snapshots` (sensitive result data; treat like comparison artifacts). Capture with `--name`; replace an existing name only with `--force`.

`--diff` compares the live query against the stored snapshot without printing cell values (locations and kinds only): exit `0` when identical, exit `8` when a valid comparison found differences rather than an execution failure. Both accept the same single SQL source and `--param` pipeline as `query` (mutation classification rejects with exit `2`).

`plan --json` emits a compact, deterministic plan contract: `statements` preserves input order; each statement has optional `sql`, a `root` node, and optional `missingIndexes`. Nodes retain `physicalOp`, optional `logicalOp`, operator metadata and runtime values, warnings (with their Showplan attributes), and child nodes. Persisted `*.plan.json` files from `compare` and `measure` use this same schema, including `sql`. This output is serialization-only; it is not accepted as plan input.

Missing indexes (SQL Server): the measured plan's `missingIndexes` (read the artifact `plan` section with `sqlharness artifact <id> --section plan --variant query|candidate`, or run `plan <file> --json` on a saved `.sqlplan`; the `--json-summary` projection does not carry it) names the table, equality, inequality and include columns, and impact for the statement under test. Check the table's existing indexes with a scoped `query` on `sys.indexes` and `sys.index_columns` before proposing one; creating an index is a persistent mutation under the [Safety contract](#safety-contract).

Use `--json` for the full report (automation and artifacts). For agent-sized stdout on `measure` / `compare`, use `--json-summary` instead: a bounded projection (target, classification, distributions, table reads, warnings, at most ten noteworthy operators, equivalence and regression verdict for compare, artifact directory) without full operator/run arrays, plan XML, or result hashes. A command accepts one output flag ([Commands](#commands) lists the modes). `query` reads SQL from exactly one source: `--file` or redirected stdin. SQLHarness sends one batch; `GO` separators are rejected.

Split work into separate invocations; for `measure` and `compare`, put preparation in `--setup` (see [Benchmark setup contract](#benchmark-setup-contract)). Bind runtime values with repeatable `--param name[:type]=value`, never by string interpolation.

<details>
<summary>Parameter types and binding details</summary>

Supported `--param` types (culture-invariant; date/time values use ISO 8601; `sqlharness capabilities <profile> --json` lists the set for that profile's engine):

`nvarchar`, `nvarchar(max)`, `nvarchar(n)`, `varchar`, `varchar(max)`, `varchar(n)`, `char`, `char(n)`, `nchar`, `nchar(n)`, `int`, `bigint`, `smallint`, `tinyint`, `bit`, `decimal`, `decimal(p,s)`, `numeric`, `numeric(p,s)`, `float`, `real`, `money`, `smallmoney`, `date`, `time`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`, `uniqueidentifier`, `varbinary`, `varbinary(max)`, `varbinary(n)`, `binary(n)`, `hierarchyid`, `geography`, `geometry`

`datetime`, `datetime2`, and `smalldatetime` accept offset-less ISO 8601 values; use `datetimeoffset` for instants. On PostgreSQL, `datetimeoffset` values are bound as UTC.

Result hashing supports arrays, PostgreSQL `inet`/`cidr`/`macaddr`/`bit varying`, non-finite floats, and SQL Server `hierarchyid`, `geography`, and `geometry` values. Npgsql 10 `cidr` values use the existing invariant `System.Net.IPNetwork` encoding. Other provider-specific result types fail with a clear `not supported as a result column` error; cast them to text in the query.

Notes:

- Untyped `name=value` is `nvarchar`; strings longer than 4000 characters bind as `nvarchar(max)` (`Size = -1`).
- `uniqueidentifier` accepts any standard GUID format (`D`, `N`, `B`, `P`).
- `varbinary` values are Base64.
- `hierarchyid` binds a path (for example `/1/2/`) as SQL UDT `HierarchyId`.
- `geography` / `geometry` bind WKT as native UDTs (via `Microsoft.SqlServer.Types`). Optional form: `srid;WKT` (defaults: geography `4326`, geometry `0`), for example `4326;POINT(-122.3 47.6)`.
- Spatial types need the package's native `SqlServerSpatial*` runtime on supported Windows RIDs; Linux/macOS spatial UDT binding is not guaranteed.
- Nulls: `name:null` or typed `name:type:null` (for example `count:int:null`).
- On Postgres, reject `money`, `smallmoney`, `smalldatetime`, `hierarchyid`, `geography`, and `geometry` parameters (exit 2). Prefer `decimal` / `numeric`, `datetime` / `datetime2`, and text/binary types.

<!-- docs-sync:example -->
```powershell
sqlharness measure prod-eu --var tenant=acme --var env=uat `
  --query .\queries\orders.sql `
  --param asOf:datetime2=2026-07-29T12:00:00 `
  --param amount:decimal(19,4)=1234.5600 `
  --repeat 5 --json
```

</details>

### PostgreSQL engine notes

- Profiles may set `"engine": "postgres"`. Omitted `engine` means SQL Server. Use `--engine postgres` only with `--unsafe-direct`.
- Postgres auth is `sql` only (`sqlUser` + `passwordEnvVar`). `ad-default` / `azure-cli` / `integrated` are rejected (exit 2). Password values stay in the environment variable.
- Every non-approved PostgreSQL command runs in its own `READ ONLY` transaction. PostgreSQL rejects persistent table and sequence writes there (SQLSTATE `25006`, reported as safety exit `2`); this includes writes reached through user functions. PostgreSQL `TEMP` setup is the exception: it runs outside that wrapper under the existing static visible-effects and temp-target checks, so `nextval` and `setval` remain denied in setup. That does not inspect a user-defined function body: a setup `SELECT` can execute a persistent-writing function. Review setup SQL and use a least-privilege setup role; this is not a guarantee that every setup effect is rejected. Query and measured commands use the per-command read-only transaction. The transaction does not contain every administrative or external effect. SQLHarness explicitly denies known residual calls such as statistics resets, promotion and backup operations, but cannot prove arbitrary user functions harmless; use a least-privilege database role to limit their other effects. PostgreSQL `measure` / `compare` timings use server `EXPLAIN` planning plus execution time, so they do not include the client-side transaction-wrapper cost.
- A session is refused before any classified SQL runs when the server does not report `standard_conforming_strings = on`: `on` in any casing is the only accepted value, and a missing parameter is refused as well. The rejection is a safety exit `2` with the fixed message `PostgreSQL standard_conforming_strings must be on; SQLHarness classifies SQL under standard string rules.` The offline classifier parses SQL under standard string rules (a backslash inside `'...'` is an ordinary character), so a server configured with the parameter off could read one accepted statement as a different statement sequence, past the `READ ONLY` transaction above. The setting is reported by the server on every connection, so the check costs no round trip. There is no profile field that relaxes it; change the role or database default on the server.
- Omitted `sslMode` and `rootCertificate` keep the historical mapping, and a successful report sets `target.transportPolicy` to `legacy`: `trustServerCertificate: true` maps to Npgsql `SslMode=Disable`, `false` maps to `SslMode=Require`. An explicit `sslMode` of `verify-full`, `verify-ca`, `require`, or `disable` is authoritative. A mode that contradicts `trustServerCertificate` is rejected (`true` agrees only with `disable`; `false` agrees with `require` and the verifying modes). `rootCertificate` is accepted only with `verify-full` or `verify-ca` and must be a readable certificate file. SQL Server profiles that set either field are rejected. `--ssl-mode` and `--root-certificate` are direct options (`--unsafe-direct` only). New remote examples use `verify-full`, and a remote server should do so as well: authentication happens during the handshake, before the identity reported below is known, so `require` and `disable` expose the credential and the result data to a man-in-the-middle; `verify-ca` is the fallback when the endpoint name cannot be matched against the certificate. Details: `docs/superpowers/specs/2026-09-26-postgres-transport-policy.md`.
- Both engines report the server identity observed on the connected session (`SERVERPROPERTY('ServerName')` for SQL Server and `inet_server_addr()` for Postgres). That value is informational; it is not compared with the profile host. TLS certificate validation authenticates the endpoint: use `trustServerCertificate: false` for SQL Server and `sslMode: verify-full` for Postgres when hostname verification is required. Both engines still require the observed database name to equal the resolved database with exact ordinal comparison; this is the only connected-target mismatch that returns exit 4.
- `measure` / `compare` time a single EXPLAIN-able statement via `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`. Result equivalence uses an unmeasured sidecar SELECT. `CpuTimeMs` is `0`; `logicalReads` are shared/local buffer hits+reads; `missingIndexes` is empty.

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

## MCP over local stdio

The CLI distribution also includes an MCP server for one local stdio client per process. Fixed-profile mode freezes one profile at startup:

<!-- docs-sync:example -->
```powershell
sqlharness mcp serve sample-country --var tenant=example --var env=test --input-root C:\work\sqlharness-inputs
```

For SQL Server, you can configure an allowlist of profiles and choose a target for each tool call:

<!-- docs-sync:example -->
```powershell
sqlharness mcp serve --request-scope --allow-profile sample-country --allow-profile sample-shared --input-root C:\work\sqlharness-inputs
```

The eight MCP tools mirror the CLI commands of the same names (`capabilities`, `query`, `measure`, `compare`, `watch`, `snapshot`, `plan`, `artifact`); tools removed from one interface are also removed from the other. `capabilities`: MCP returns the server's runtime configuration, including its active compare-context policy, while CLI `capabilities` describes commands, engines, limits and that same policy. CLI `--dry-run` is MCP `dryRun: true`.

Request mode is SQL Server only: `query`, `measure`, `compare`, `watch`, `snapshot`, and `artifact` require a nested `target` with an allowlisted profile and its variables, while `capabilities` and `plan` remain target-free. The process snapshots profiles, input roots, and budgets at startup. Tool calls run concurrently and benchmarks are not serialized ([concurrency](docs/mcp.md#concurrency-deadline-cancellation-and-progress)). SQLHarness does not register or edit client configuration. `sqlharness_capabilities` discloses the configured input roots (`inputRoots`), so callers can find where file inputs are allowed.

File reads require absolute paths lexically under a configured root, reject UNC paths, and honor byte caps; the reader does not resolve links or guard against changes by the same OS user. See [the MCP contract](docs/mcp.md) for both client-entry shapes, scope behavior, and limits.

<a id="agent-startup"></a>

## Check your configuration

Check the profile's engine and limits with capabilities. Use doctor to diagnose local configuration problems. Both commands work without a database connection:

<!-- docs-sync:example -->
```powershell
sqlharness capabilities prod-eu --json
sqlharness doctor --json
```

`capabilities <profile> --json` describes the commands, the output modes each command accepts (`outputModesByCommand`) and the limits, scoped to that profile's engine: its parameter types, session-temp statements, engine-only limits and an `engineGuidance` object (`query`, `setup`; `query` may be empty) with the engine's native safety and setup rules, and the engine's server-identity rule in `limits.targetIdentity.server`. Without a profile it returns engine-neutral discovery only, and command `--help` stays engine-neutral too. Rules that hold for both engines (setup runs once per measured session, setup locals do not carry into measured SQL, comparison semantics, budgets) live in help and tool descriptions; the engine sections below are the complete reference, read only for the engine you use. It never takes `--var`; an unknown or unreadable profile returns a JSON error envelope with exit `2` (`profile_unknown`, `profile_unavailable`). `doctor --json` checks the local installation and omits accounts and environment values.

`--dry-run` on `query`, `measure` and `compare` checks the exact invocation offline; the dry-run rule is in the [Safety contract](#safety-contract).

Queries and benchmarks validate their inputs before connecting, including setup and multiple cases. A separate dry run is optional: use it to check an invocation while preparing it. It cannot predict execution cost or database errors.

Start with compact output (`--output agent`, or benchmark `--json-summary`), then retrieve details
needed for the conclusion. Correct ordinary SQL/argument errors within unchanged read-only scope when
the cause is clear. Stop on safety rejection, authentication/access failure or target mismatch; do not
automatically repeat timeouts or cancellations. A persistent-mutation attempt consumes its approval.
Argument validation and safety rejection can share an exit code and error code: use the detailed
message to identify the cause. Correct only a clear input error; stop on ambiguous rejections.

See [the agent output contract](docs/superpowers/specs/2026-09-26-agent-output-contract.md)
and [the audit roadmap](docs/superpowers/plans/2026-09-26-audit-roadmap.md).

## Benchmark setup contract

For `measure` and for `compare` without `--matrix`, SQLHarness opens one connection per invocation:

1. Runs optional `--setup` SQL **exactly once** on that connection.
2. Runs warm-up(s) on the same connection.
3. Runs all measured repetitions on the same connection.

Session-local `#temp` objects created in setup remain visible to warm-up and measured SQL. A rejected or failed setup stops the benchmark; SQLHarness does not retry by creating persistent objects or weakening safety. `compare --matrix` does not reuse that single connection across values: each value opens a new connection and runs setup once, in the contract next to the quick-start matrix command.

On SQL Server, CPU, elapsed and logical reads come from English `STATISTICS IO/TIME` messages. If the login's language is not English, those metrics are reported as `unavailable` with a warning; set the login's default language to English (`ALTER LOGIN ... WITH DEFAULT_LANGUAGE = us_english`) to get them.

On SQL Server, scalar and table variables declared in setup do not cross into baseline, candidate, or measured batches. Declare a local in each batch that needs one, or pass a supported typed parameter. An unproven table-position variable remains denied; only an earlier inline `DECLARE @t TABLE (...)` proves a table variable in that batch. Named user-defined table types do not prove that shape.

On SQL Server, when `--setup` references any supplied parameter, the batch is split at the first statement that references a parameter. Statements before that point execute in a single parameter-free root-scope command, in their original order; statements from that point onward execute in one parameterized command on the same session. To keep a session-local `#temp` table visible, place every `CREATE TABLE #t` and `SELECT ... INTO #t` before the first statement that references a parameter.

Any temp-creating statement at or after that point is rejected offline with an actionable message. A local scalar or table variable declared before the split cannot be used after it; that shape is also rejected offline, because the parameterized command has a separate local-variable scope. Declare and use the variable within the parameterized batch, or pass a supported typed parameter. Setup that references no parameters runs unchanged without unused bindings.

Postgres `TEMP` tables are not affected by this split because Npgsql keeps temp objects visible across parameterized commands.

Local temporary objects whose unqualified name starts with exactly one `#` may use session-only work without mutation confirmation: `CREATE TABLE #t` / `SELECT INTO #t`, `#temp` DML, `#temp` indexes and supported constraints (`NULL`/`NOT NULL`, `PRIMARY KEY`, `UNIQUE`), `DROP TABLE #t`, `TRUNCATE TABLE #t`, scalar `DECLARE` with analyzed initializers, table variables (`DECLARE @t TABLE (...)` then `INSERT`/`UPDATE`/`DELETE`/`MERGE`/`SELECT` against it when the `DECLARE` comes earlier in the same batch; `OUTPUT INTO @t` only when the statement's primary target is also local; a table variable declared in `--setup` is not visible to the measured SQL, so carry data in `#temp`), and `ALTER TABLE #t` limited to add/drop columns and local `CHECK`/`DEFAULT`/`NULL`/`UNIQUE` constraints.

Plain `SET @v = <expr>` to a scalar local declared earlier in the same batch is accepted in query, measured SQL and `--setup`.

On Postgres the session-only set is native `CREATE TEMP TABLE` / `SELECT INTO TEMP TABLE` (unambiguous single-part name), `TRUNCATE [ONLY]` of proven current-session temps, named unqualified or as `pg_temp.<name>` (no `CASCADE`, `RESTART IDENTITY`, or other schema-qualified target), and the `DROP TABLE` / `CREATE INDEX` temp targets; those four classify as allowed in every phase but execute only in `--setup`, the one read-write phase: in query and measured SQL the server refuses them in the read-only transaction (safety exit `2`).

The set also includes plan-only `EXPLAIN` over a safe `SELECT`, and `EXPLAIN ANALYZE` with full inner-statement effect analysis; canonical `ANALYZE tbl` stays rejected until the parser supports it. The Postgres temp proof, where it still applies, is name-based and needs a quoted or all-ASCII name of at most 63 UTF-8 bytes; temp DML in query and measured SQL needs none, because that command runs read-only.

An unqualified name assumes the default `search_path`, which SQLHarness does not check; when `search_path` may be non-default, write to session temps as `pg_temp.<name>`, which does not depend on it (details in [PostgreSQL temp target proof](#postgresql-temp-target-proof)). Persistent objects, `##temp`, dynamic SQL, external access, cross-database references, and transaction control remain denied.

SQL Server accepts IF, ELSE and BEGIN … END at any nesting depth. WHILE, BREAK, CONTINUE, GOTO, labels, RETURN, TRY … CATCH, WAITFOR, THROW and RAISERROR are denied. The classifier checks every branch, including branches with a constant-false predicate; it does not evaluate predicates. These rules apply only to SQL Server. PostgreSQL control flow is unchanged.

<details>
<summary>PostgreSQL TEMP target rules</summary>

### PostgreSQL temp target proof

Session temps are native `CREATE TEMP TABLE` / `TEMPORARY`; no `#temp` translation. Persistent DML still needs `--allow-mutation --confirm-database`; persistent DDL stays denied.

A `DROP TABLE`, `CREATE INDEX` or `TRUNCATE` target, and every DML target on the approved mutation path, is session-local only when it is a proven current-session temp: an unqualified name recorded from `CREATE TEMP TABLE` / `SELECT INTO TEMP` in this flow, declared without `ON COMMIT DROP`, and spelled quoted or all-ASCII-unquoted in at most 63 UTF-8 bytes both where declared and where used. Any other name is never proven; quote or shorten it.

The proof is name-based and assumes the default `search_path`, where `pg_temp` is searched first: under a `search_path` that puts another schema ahead of `pg_temp`, a proven unqualified name can resolve to a persistent table and SQLHarness does not detect that. This limitation applies to unqualified names only.

When `search_path` may be non-default, write to session temps as `pg_temp.<relation>`, which always addresses the current session's temp schema: for DML, `DROP TABLE` and `CREATE INDEX` the two-part `pg_temp.<relation>` alias is session-local by itself, and `TRUNCATE [ONLY] pg_temp.<relation>` is allowed when `<relation>` is a proven temp. A `pg_temp_` prefix (a relation name or a `pg_temp_<N>` schema) is never proof, and a `CREATE TEMP TABLE` whose name carries any qualifier other than `pg_temp` proves nothing.

`DROP TABLE` revokes the proof of the name it drops; when that name's stored form is unknown offline (unquoted non-ASCII or over 63 bytes, reachable only as `pg_temp.<relation>`), it revokes every proof in the flow until a new `CREATE TEMP TABLE` proves a name again. Query and measured DML needs no such proof: it runs in the per-command `READ ONLY` transaction, where PostgreSQL allows DML on a temp and refuses any persistent target with SQLSTATE `25006` (safety exit `2`).

An unqualified DML target therefore reaches the server and the server decides; a `search_path` that puts another schema ahead of `pg_temp` is resolved by the server rather than by the name. With `--allow-mutation --confirm-database` the batch executes read-write, so there the same unqualified target stays unproven and is an ordinary persistent mutation (`MutationNotAllowed` without approval).

With `--allow-mutation` but a missing or mismatched `--confirm-database` the batch is not approved, so a persistent target is still denied offline (`DatabaseConfirmationRequired` / `DatabaseConfirmationMismatch`) but an unqualified DML target now classifies as session-local and runs read-only: that batch ends in exit `2` from the server's `25006` rather than from the confirmation error.

Whenever the flow declared a TEMP table that recorded no proof, every write denial that remains (`MutationNotAllowed` or `NonTemporaryWrite`, an unproven `TRUNCATE` target in query or setup among them) carries one fixed hint to quote or shorten the name instead of asking for mutation approval. A `pg_temp_<N>` schema, a foreign schema and a three-part name are persistent on the server too, so their DML still needs approval. An unproven `DROP TABLE` / `CREATE INDEX` target is denied.

`TRUNCATE [ONLY] <temp[, ...]>` is allowed only when every target is a proven temp, named unqualified or as `pg_temp.<relation>`; a persistent, mixed, or otherwise schema-qualified target (`public.t`, `pg_temp_<N>.t`, a three-part name, `pg_temp.<unproven>`) is denied as `NonTemporaryWrite`, and `CASCADE` / `RESTART IDENTITY` as `UnsupportedStatement`, with or without mutation approval. Canonical `ANALYZE tbl` stays rejected until the parser supports it (design-only spike: `plans/011-analyze-parser-spike.md`).

Before measuring, inventory a representative parameter range offline: row count, cardinality distribution (small/median/large/exceptional), ordering ties, missing history, boundary dates, and empty results. Keep ticket-specific benchmark SQL outside the application repository (ticket workspace or isolated temp directory). Treat plans, comparison artifacts, and runtime parameters as locally sensitive.

</details>

## Parameter sets

The CLI `measure --param-set` measures one query across at least two named parameter sets. Fixed `--param` values are shared by every set; per-set parameters come from each file and must not reuse a fixed name. A single scenario stays on `--param` alone. MCP `sqlharness_measure` and `sqlharness_compare` share three mutually exclusive inputs: `parameterSets` (inline named sets), `parameterSetFiles` (named `.sqljson` files), or `matrix` (a one-parameter shorthand).

The `parameterSets` and `parameterSetFiles` forms contain 2 to 32 sets; a matrix contains 2 to 32 values. Every set must bind the same parameter names with the same declared type, size, precision and scale. Unsized declarations such as `nvarchar` may bind values with different inferred sizes. Fixed parameters combine with every set, and a set cannot repeat a fixed parameter.

```powershell
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --param tenant:nvarchar=acme --param-set .\sets\small.sqljson --param-set .\sets\large.sqljson --repeat 5 --json
```

Each CLI `--param-set` or MCP `parameterSetFiles` file is JSON containing only `name` and `parameters`, at most 64 KiB. A UTF-8 BOM, JSON comments, and trailing commas are accepted. Those files are locally sensitive. SQLHarness does not copy parameter values or source paths into reports. `.sqlplan` artifacts stay locally sensitive because server plan XML may still embed parameter values; do not publish them without review.

Multi-set measure uses one connection and runs setup once. Setup binds the first parameter set's merged parameters; later sets do not re-run setup. Each set is warmed once in user-supplied order, then measured rounds rotate on that same connection. In one-based round r, measured execution starts at index r modulo setCount and wraps in user-supplied order. Per-set stability is the only stability result; SQLHarness does not claim cross-set result equivalence.

Parameter-set measurements use the observed server plan-cache state; SQLHarness did not clear or isolate the plan cache. Timings are order-dependent because of that shared cache. Compare runs one connection per set and runs setup once on each connection. Compare cell reports include the set name and its bound parameters; one-parameter matrix cells also retain `parameterValue` (JSON null only for a typed NULL). These labels are additive in new artifacts, and older matrix artifacts remain readable.

The CLI `--param-set` file grammar and `compare --matrix` text grammar are unchanged. MCP's `parameterSets`, `parameterSetFiles`, and `matrix` inputs use the same validation for both tools; matrix values remain typed strings or JSON null, so commas, empty text and typed NULL stay distinct.

`--json-summary` for a multi-set measure is a bounded projection: the cache warning, the rotation rule, each set name, parameter type metadata, the value hash, per-set stability, median elapsed/CPU/reads, cross-set labels, and the artifact directory. It omits parameter values, plan XML, and full run arrays. Noteworthy operators, when included, stay capped at ten.

## Result equivalence and compact output

`compare` reports **technical equivalence** under `--compare-results` (default `ordered`):

| Mode | Meaning |
| --- | --- |
| `ordered` | Same schema, same multiset of rows, same row order. |
| `multiset` | Same schema and row multiset; order may differ. |
| `set` | Same schema and distinct-row set; duplicates and order ignored. |
| `off` | Skip result comparison; equivalence fields are null. |

Equivalence compares data and column ordinal/name/type per result set; it ignores column nullability (`allowNull`) by default.

Every report's equivalence object carries `compareNullability` (policy), `nullabilityEquivalent` (`true` when every measured run matches the first baseline measured run, `false` when a difference was observed, `null` when comparison is off, evidence is absent, the provider reported no nullability for a compared column (it is never read as nullable here, although `allowNull` in query and snapshot output still shows `true`), or result shapes/types differ; never inferred `true`) and `mismatchReason` (`"nullability"` only when strict mode alone caused the mismatch, otherwise `null`).

Artifacts written before this change lack these fields: they read `compareNullability` as `false` (even though those comparisons effectively included nullability) and their `nullabilityEquivalent` is unavailable. CLI text output labels strict runs `Technical equivalence (<mode>, strict nullability)`, appends `; nullability: True|False|unavailable` to the equivalence line, and adds `; mismatch reason: nullability` when strict mode alone caused the mismatch. Snapshots (`snapshot --diff`) are unchanged.

Add `--compare-nullability` (MCP: `compareNullability`, default `false`) to require matching nullability as well: Equivalent then needs data equivalence and `nullabilityEquivalent` true. It is rejected with exit `2` under `--compare-results off`.

Every measured run participates; warm-ups do not. Directional counts (`baselineOnlyCount`, `candidateOnlyCount`, and for `ordered` also `differingPositions`) are the maximum over any baseline/candidate measured-run pair. Modes that retain directional fingerprints accept at most **1,000,000** row fingerprints per measured variant run; exceeding that bound fails the operation.

Order-only mismatches: under `ordered`, `differingPositions` can be non-zero while multiset equivalence would still hold; use `--compare-results multiset` when order is not part of the contract. Technical equivalence is not domain/business equivalence: prove ticket-specific semantics separately (for example two-direction `EXCEPT` for set differences, or grouped counts when duplicates matter; check ordering ties and missing-history cases offline).

Regression verdict: `compare` also reports `regression` with a `verdict` of `pass`, `fail` or `inconclusive`, `reasons` codes (`too-few-runs`, `elapsed-unavailable`, `results-not-compared`, `results-not-equivalent`, `unstable-elapsed`, `elapsed-regressed`; every failing gate is listed in that order), `policyVersion: 2`, and the `elapsed`, `logicalReads` and `cpu` diagnostics (each with `status` `regressed`, `not-regressed`, `inconclusive` (elapsed only) or `unavailable`, plus `baselineMedian`, `candidateMedian`, `delta` and `deltaPercent`). `deltaPercent` is null when the baseline median is 0, and medians and deltas are null when a diagnostic is `unavailable`.

When a gate fails, `elapsed.status` is `inconclusive` (medians and deltas are still shown; trust `verdict`). Regression medians are exact (the mean of the two middle samples for even run counts), so they can differ from the whole-ms `median` in the variant distributions. Reads and CPU are cost diagnostics and never change the verdict; a cost diagnostic is `regressed` only when the candidate median is at least 1.10 times the baseline and at least 100 logical reads higher (reads), or at least 1.15 times and at least 5 ms higher (CPU); `not-regressed` means at least one of those two gates did not fire, so read `delta` and `deltaPercent` for the size of the change; PostgreSQL CPU is always `unavailable`.

The verdict is `inconclusive` unless there are at least 5 measured runs per variant, elapsed time is measured on both, results were compared and are equivalent, and each variant's measured elapsed samples have an acceptable spread: a range (max - min) of at most 2 ms, or otherwise (max - min) / median at most 0.25 (a zero median with a range above 2 ms is unstable). Otherwise it is `fail` if and only if the candidate elapsed median is at least 1.10 times the baseline median and at least 5 ms higher (a zero baseline median fails above 5 ms), else `pass`.

Thresholds are fixed: there are no flags and no config file. The verdict appears in `--json`, `--json-summary`, matrix cell summaries (not the matrix text rows), the artifact `summary` and MCP `compare`; the CLI text prints `Regression: fail (elapsed +900 ms, +900 %)`, `Regression: pass (...)` or `Regression: inconclusive (too-few-runs, ...)` after the equivalence line. The exit code stays `0` for a valid compare whatever the verdict. The verdict checks latency; domain equivalence requires a separate check.

Artifacts written before this version have no `regression`. Artifacts keep the `policyVersion` they were written with; version 1 had no 2 ms spread floor.

<!-- docs-sync:example -->
```powershell
sqlharness compare prod-eu --var tenant=acme --var env=uat `
  --subject orders-report --scope whole `
  --baseline .\queries\before.sql --candidate .\queries\after.sql `
  --compare-results multiset --repeat 5 --json-summary

sqlharness compare prod-eu --var tenant=acme --var env=uat `
  --subject orders-report --scope whole `
  --baseline .\queries\before.sql --candidate .\queries\after.sql `
  --compare-results ordered --compare-nullability --repeat 5 --json-summary
```

## Commands

| Command | Purpose |
| --- | --- |
| `capabilities` | Describe commands, engines, limits, output modes and the active compare-context policy, offline. Pass a profile for that engine's parameter types, limits and `engineGuidance`. |
| `doctor` | Check the local installation and profile-file availability, offline. |
| `watch` | Poll a bounded read-only query until `--until` / `--until-unchanged` or max duration. |
| `snapshot` | Capture or `--diff` a named canonical result under `~/.sqlharness/snapshots` (`--force` to replace). |
| `query` | Run one bounded, classified SQL batch. `query`, `measure` and `compare` take `--dry-run`. |
| `measure` | Collect repeated timing, IO, and plan evidence for one query. |
| `compare` | Compare baseline and candidate queries, including result equivalence. |
| `plan` | Distill a showplan XML file or stdin without connecting to a database. |
| `artifact` | Read safe sections of a saved benchmark artifact, offline. |
| `mcp serve` | Serve the Core tools to one MCP client over local stdio ([MCP contract](docs/mcp.md)). |
| `dashboard` | Operator tool: serve the local activity dashboard on 127.0.0.1 ([Dashboard](#dashboard)). Agents leave it and its API alone. |

Run `sqlharness <command> --help` for available options and defaults.

`artifact <id> --section <section>` reads one safe section (`summary`, `metrics`, `operators`, `statements`, `matrix-cells`, `plan`) of a saved benchmark artifact without connecting. `<id>` is the directory name from the report's `artifactDirectory`; `operators`, `statements`, `matrix-cells`, and `plan` continue with `--cursor`. For `--section plan`, select `--variant baseline|candidate|query` (query is a measure artifact), optional zero-based `--statement` (default `0`), `--parameter-set` for a measure-set artifact, or an available measured `--repetition`.

The default is the last measured repetition and the response identifies that selection and lists available repetitions, including measured runs without saved plans. If the selected run has no saved plan, the reader reports that repetition's missing evidence instead of falling back to an earlier run. `--include-text` opts into bounded statement text and predicates. The section reads saved distilled `.plan.json` evidence when present; it never reads or regenerates raw XML.

Plan nodes are flattened in preorder with stable paths and paged so following continuations reconstructs the saved hierarchy. Each response is trimmed to its byte budget; returned node counts vary with content and budget, and continuation advances by nodes returned. `planShapeStable` compares saved operator shape, object/index identity, and predicates, excluding costs, timing, and row counts. It is `null` when fewer than two measured repetitions are available, because there is not enough evidence to compare; in that case `evidenceGaps` is empty. If a multi-repetition comparison has missing or incomplete saved plan evidence, `planShapeStable` is also `null` and `evidenceGaps` describes the gap.

The `operators` section is paged too, but its noteworthy-operator source is capped and does not represent the full plan.

SQL Server per-table read summaries normalize session-suffixed temporary table names before aggregating repetitions: `[schema].[#name<underscores><12 hex>]` becomes `[schema].[#name]`, and `#name<underscores><12 hex>` becomes `#name`. Other table names are unchanged.

Output modes: `--json` writes the full report; `measure` and `compare` add the bounded `--json-summary`. `--output agent` writes schema version 2 (the MCP result shape), shrunk to `--max-output-bytes` (4096..1048576, default 65536) with each cell clipped to `--max-cell-chars` (0..4096, default 2048). The seven MCP tools that return projected output accept the matching `maxCellChars` limit; truncated results report the applied value as `truncation.maxCellChars`.

Query projections retain the resolved database name, use CLR value aliases, omit the rest of the target identity and canonical hash/footprint, and identify every retained clipped cell by result-set-local row and column plus original/returned length; default omission counters and empty messages are left out while zero `recordsAffected` and empty-result columns remain. See [docs/mcp.md](docs/mcp.md#result-envelope-errors-and-budgets) for the shared projection rules.

`watch --output ndjson` streams one JSON record per line: `started`, a `changed` record for every changed result, then exactly one terminal `completed` or `failed` record, with a strictly increasing `sequence`. A command accepts one of `--json`, `--json-summary` and `--output`; `outputModesByCommand` in `capabilities --json` lists which modes each command takes.

<!-- docs-sync:example -->
```powershell
sqlharness artifact 20261012T101500123Z-prod-eu-0f3c2a9d4b6e4f1a8c7d5e3b2a190876 --section summary --json
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --output agent --max-output-bytes 8192 --max-cell-chars 256
sqlharness watch prod-eu --var tenant=acme --var env=uat --file .\queries\progress.sql --until-unchanged 3 --output ndjson
```

## Safety contract

Use a least-privilege reader account for each profile. The classifier and mutation flags help prevent accidental writes; database permissions enforce the account's access. A CLI agent shares the shell's environment: it can read the profile's password variable, call `az`, and run `--unsafe-direct`, so the principal's database permissions are the real boundary.

An MCP client without a shell reaches the database only through the server's tools, which run no explicit persistent mutations; PostgreSQL TEMP setup can still reach a persistent-writing user function, so the least-privilege principal applies here too ([docs/mcp.md](docs/mcp.md#no-persistent-mutations)).

### Exit codes

In text mode, unhandled CLI errors, including command-line parse errors, return `-1` (255 on POSIX). Other output modes, `mcp` and `dashboard` return `2`.

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `2` | Validation or safety rejection. |
| `3` | Authentication failure. |
| `4` | The connected database differs from the resolved database (exact ordinal comparison). |
| `5` | SQL execution failure. |
| `6` | Local storage failure. |
| `7` | `watch` reached its max duration without a stop condition. |
| `8` | `snapshot --diff` found differences in a valid comparison. |

### Closed targets

Profiles in `~/.sqlharness/targets.json` define server, database template, variables, authentication, and optional `engine` (`sqlserver` default, or `postgres`). Missing, extra, or invalid variables are rejected. A different profile or variable set needs a new explicit user request. In request-scope MCP the operator's `--allow-profile` list approves switching to a listed profile; a different variable set still needs a new explicit user request ([docs/mcp.md](docs/mcp.md#scope-lifetime)).

### Direct targets

`--unsafe-direct` deliberately bypasses the closed-profile guardrail and requires `--server`, `--database`, and `--auth`. Do not mix it with a profile or `--var`. `--engine` is valid only with `--unsafe-direct` (never with a named profile).

### Mutations

Read-only is the default. Explicit persistent-object mutation requires fresh, single-use user approval for the exact batch and exact resolved database. After that approval the agent supplies `--allow-mutation --confirm-database <exact-resolved-name>`: a deliberate-act check that records the approval, not a prompt to the user. Exception: read-write PostgreSQL TEMP setup can statically accept a setup `SELECT` that invokes an opaque user-defined function which writes persistently, without approval; review setup SQL (see PostgreSQL execution).

### PostgreSQL execution

Each non-approved command runs in a per-command `READ ONLY` transaction; the server rejects persistent table and sequence writes (SQLSTATE `25006`, safety exit `2`). Allowed TEMP setup is the exception and runs outside that wrapper under static visible-effects and temp-target checks; `nextval` and `setval` remain denied in setup. A setup `SELECT` can still execute a user-defined function that writes persistently because static validation does not inspect its body.

Review setup SQL and use a least-privilege role, but do not treat validation or that role choice as proof that every effect is rejected. `READ ONLY` also does not contain every administrative or external effect, so SQLHarness denies known residual calls. PostgreSQL benchmark timings use server `EXPLAIN` planning and execution time and exclude client wrapper cost.

### Session `#temp` / `TEMP`

SQL Server: local `#temp` setup/DML/indexes are session-only and do not require mutation confirmation, plus scalar `DECLARE`, table variables declared earlier in the same batch, `TRUNCATE TABLE #t`, and `ALTER TABLE #t` add/drop columns with local constraints; plain `SET @v = <expr>` to a same-batch scalar local in query, measured SQL and setup.

Postgres: use native `CREATE TEMP TABLE` / `TEMPORARY` (no `#temp` translation), `SELECT INTO TEMP TABLE` with an unambiguous name, `TRUNCATE [ONLY]` of proven current-session temps, plan-only `EXPLAIN` over a safe `SELECT`, and `EXPLAIN ANALYZE` with full effect analysis. Setup runs once per connection and shares that session with warm-up and measured runs.

### SQL input

Use exactly one query source (`--file` or stdin) and bound `--param` values. Parameter names accept an optional single leading `@` and reports print the canonical `@Name`. Unsized `nvarchar`/`varchar` values bind with size 4000 when the value is at most 4000 characters (including NULL), and as `max` when longer, matching Dapper so a benchmark compiles the plan the application compiles.

Fixed-width `nchar`/`char` have no `max`: they use size 4000 through 4000 characters (including NULL); `char` values of 4001 to 8000 characters use their actual length, and values beyond the type limit are rejected (`nchar` over 4000, `char` over 8000). Explicit sizes are unchanged. Parsed syntax inside a top-level `SELECT` is accepted without a fragment allowlist; independent safety checks still reject cross-database access, external or stateful sources, dynamic SQL, and writes outside the approved local-`#temp` or mutation contracts.

### Dry run

`query`, `measure`, and `compare` accept a dry run (CLI `--dry-run`, MCP `dryRun: true`) of the exact invocation, setup, matrix values and parameter-set contents included: offline argument/bounds checks, safety classification, parameter binding/references, setup-shape checks and matrix/parameter-set binding, then a return before connection or artifact storage. Success exits `0` with a `SqlHarnessDryRunReport` (`command`, `classification`, `parameters` with canonical name, type, size, precision and scale, and `parameterSets`); a rejection exits `2`, and a referenced parameter without a value is `missing_parameters` with its names.

It creates no raw-token emission receipt. It cannot check runtime artifact storage, authentication, connection identity, catalog contents or database permissions, so success does not guarantee execution, and it never authorizes a later mutation. It does not check parameter references inside `--setup`. The first executed result's `database` is the verified target: every connection compares it with the resolved database (exit `4`). Safety rejection details identify the ScriptDom statement type and starting line, such as `IfStatement at line 14`.

### Secrets

Known credentials (the profile's password and the Azure token) are replaced in every output. Other text, including SQL quoted in server errors, is shown as is. Credential-redacted operation errors are stored in the activity journal by default.

### Artifacts

Comparison artifacts, `.sqlplan` files, named snapshots under `~/.sqlharness/snapshots`, and runtime parameter material are locally sensitive because they can embed SQL text, parameter values, and result data. `snapshot --diff` never prints cell values.

Keep passwords and tokens out of command-line arguments, configuration output, logs, reports, and artifacts. Store profile passwords in the environment variables named by the profile.

A safety rejection is a stop: narrow the operation or obtain explicit approval. A rejected setup stops execution.

## Activity journal and workflow output cost

The dashboard's Statistics aggregate measured output bytes from `~/.sqlharness/data/activity.db`. Each output response contributes its emitted bytes once; its displayed token count uses `ceil(UTF-8 bytes / 4)` and is an estimate, not provider-tokenizer output. Input tokens, tool discovery, capabilities discovery, and final chat are unmeasured. Totals cover only operations the journal still holds; retention trims them. Raw means SQLHarness's canonical internal representation, not the response from an alternative tool.

For newly referenced workflows, artifact reads join to their producer by exact operation reference. Each top-level invocation receives an opaque `operation_ref` before journaling or artifact publication; matrix roots and their cell artifacts share that producer reference, and each artifact read gets a new reference with `parent_operation_ref` set to the exact producer. References are lineage metadata, never access credentials or scope grants.

The `single-measured-execution-output` baseline includes the first measured result/messages for each variant and parameter set in the producer; it excludes warm-ups, later repeats, and plan XML. The dashboard reports output overhead in bytes and an estimated output-token delta. Stored byte counts are authoritative; the `utf8-bytes-div-4` estimate is `ceil(UTF-8 bytes / 4)`, not a provider-tokenizer measurement. This is an output-volume comparison, not actual token savings or avoided agent cost.

Failed operations, dry-run roots, missing baselines, and rows without measured baseline bytes have no comparison. Dry-run responses contribute to output bytes and call counts while remaining excluded from database-execution counts. Input tokens, tool discovery (including `capabilities`), and final chat are outside coverage. Historical rows without operation references appear separately as `legacy-artifact-volume` and are excluded from comparable workflow totals. Missing provenance is never inferred from artifact paths or timestamps.

### Activity journal

Every operation that reaches the SQLHarness module (`query`, `measure`, `compare`, `watch`, `snapshot`, `artifact`, `plan`; CLI or MCP) is recorded in a local activity journal (`~/.sqlharness/data/activity.db`, owner-only on Unix): operation, scope, target identity, status, timings, measured raw/emitted bytes and their estimates, opaque operation references, dry-run state, and a SHA-256 hash of the SQL. Schema v7 adds nullable `operation_ref` and `parent_operation_ref` plus `dry_run` (`NOT NULL DEFAULT 0`). It reuses the `raw_bytes` and `emitted_bytes` columns introduced in v3; stored byte values remain intact.

Existing rows have null references and `dry_run = 0`, while existing token columns remain readable; no historical provenance is inferred or backfilled. Not recorded: `doctor`, `capabilities`, `--help`, and rejections before the module runs. Rows of removed commands (`ping`, `schema`, `counts`, `space`, `qstop`, `indexes`) stay readable in the dashboard. The `capabilities` command is outside reported workflow-cost coverage. Session identity is implicit (MCP `clientInfo`, or the CLI's nearest `claude`/`codex` ancestor process); agents send nothing extra.

Credential-redacted error messages are stored by default, truncated to the journal limit; they can still quote caller-supplied SQL or data values. SQL text (including measure/compare setup SQL from schema v12) is stored only with `journal.storeSensitive: true` in `~/.sqlharness/config.json`, as are full plans; plans may embed statement text and parameter values, including `--param-set` and matrix values (`ParameterCompiledValue` / `ParameterRuntimeValue`, or literals in Postgres plans). Parameter values, secrets, and result cells are never stored as journal fields.

An invalid `config.json` falls back to defaults (hash-only SQL and plans, with redacted error messages). Journal failures never change output or exit codes. Treat `activity.db` as locally sensitive.

For successful `measure` and `compare` runs the journal also stores per-variant medians, memory grant, DOP, compile, spill and wait diagnostics, per-table `STATISTICS IO` reads (SQL Server only: scan count and logical, physical, page-server, read-ahead, and LOB logical/physical/read-ahead reads; page-server read-ahead and LOB page-server counters are not kept; Postgres rows store relation buffer reads as logical reads and leave the detail counters null), per-statement SQL hashes and plan metrics, and plan identity hashes; metric rows identify matrix cells by index only, never by value.

Metrics a run marks unavailable are stored as null, and `STATISTICS IO` lines without logical reads (localized output, columnstore segment lines) are not recorded. Per-statement rows contain hashes and plan details without SQL text; CPU and elapsed values come from SQL Server `QueryTimeStats`. A stored plan is the first actual plan observed for its shape (`PlanIdentity`); per-run metrics are kept separately, so later runs with the same shape do not store another copy.

Schema v9 adds operator annotations of sessions (title, note, star, archive). Only the dashboard writes them; titles and notes are stored as plain text regardless of `journal.storeSensitive`; retention deletes them with their session, and a star does not protect a session from retention. Schema v10 adds `operation_phases` aggregates (`operation_id`, `phase`, `count`, `total_ms`, `max_ms`) to every journaled operation; phases are `connect`, `setup`, `execute`, `artifact`, and `other`, whose total reconciles to the operation wall time.

When `diagnostics.enabled` is true, schema v10 also stores bounded `operation_events` for connection attempts and artifact writes. Its fields are `operation_id`, `seq`, `offset_ms`, `phase`, `duration_ms`, `outcome`, `error_number`, and `detail_json`; details are limited to 512 characters and contain safe metadata such as a client connection ID, authentication strategy, attempt number, or artifact file count. Event capture is off by default and capped at 256 events per operation. Neither phase aggregates nor diagnostic events store SQL text, parameter or result values, credentials, or tokens.

Schema v11 adds the dashboard-written derived tables `agent_conversations`, `agent_tool_calls`, `operation_agent_links`, and `transcript_cursors` for opt-in agent transcript matching. These derived records do not replace operation or operator-annotation fields. Retention removes links with their operations and removes conversations that are no longer referenced. **Clear matched data** empties all four transcript tables; it keeps session titles, notes, stars, and archive state. Operator titles and notes are stored as plain text regardless of `journal.storeSensitive`; a star does not protect a session from retention.

`sqlharness doctor --json` reports whether the config file is present and valid, the effective journal settings, and whether the journal file exists.

```json
{
  "journal": { "enabled": true, "storeSensitive": false }
}
```

`dashboard.port` sets the dashboard's preferred port. Optional behavior in `~/.sqlharness/config.json`:

```json
{
  "journal": { "retention": { "enabled": true, "maxAgeDays": 30, "maxSizeMb": 500 } },
  "diagnostics": { "enabled": false },
  "dashboard": { "autoStart": true, "idleShutdownHours": 8 }
}
```

`journal.retention` (off by default; needs `journal.enabled`) deletes operations older than `maxAgeDays` (1..3650, default 30), then unreferenced plans and empty sessions last seen before that age, then the oldest operations while the database's used size exceeds `maxSizeMb` (10..102400, default 500), and finally returns free pages with `PRAGMA incremental_vacuum` (skipped for a database whose `auto_vacuum` is not `INCREMENTAL`; every journal SQLHarness creates is).

It never deletes a running operation whose host process is alive (or whose liveness cannot be determined); a running row whose host process is gone (`abandoned`) is deleted like a finished one. On Linux an unreadable `/proc` entry counts as a gone process. Deletes run in short transactions of at most 500 rows, so concurrent journal writes are not starved. It runs when the dashboard starts, every hour while it runs, and once in the background when `mcp serve` starts.

`dashboard.autoStart` (off by default; an invalid `config.json` never enables it) makes every `mcp serve` start `sqlharness dashboard --background` when no dashboard is running for this home. The launch happens after startup validation and before the MCP transport starts; it is detached from the server's standard streams (closed redirected stdio on Unix, ShellExecute with a hidden window on Windows) with the SQLHarness home as working directory, so MCP stdout stays protocol-only and the client still sees EOF when the server exits.

A failed launch writes one line to stderr. The background dashboard exits after `idleShutdownHours` (1..168, default 8) without journal writes (other than its own retention), requests, or open browser tabs; the next `mcp serve` starts it again. A dashboard started by hand never idle-exits. Open the running dashboard with `sqlharness dashboard`.

Known limitation: the autostarted dashboard is not placed in a new session or process group, so Ctrl+C or SIGHUP delivered to the MCP client's process group (or closing its console on Windows) also stops it; the next `mcp serve` relaunches it.

### Dashboard

<!-- docs-sync:example -->
```powershell
sqlharness dashboard            # serve on 127.0.0.1 and open the browser
sqlharness dashboard --no-open  # print the URL only
sqlharness dashboard --stop     # stop this SQLHarness home's dashboard
sqlharness dashboard --restart  # replace it with this installed version
sqlharness dashboard --restart --no-open  # restart and print the URL only
```

`--stop` and `--restart` apply only to the effective SQLHarness home (`SQLHARNESS_HOME`, or the default home). Stop is successful when no dashboard is running. Restart stops the current instance, then starts this installed version in background mode; it opens the browser by default and follows the background dashboard's `idleShutdownHours` behavior. Use `--no-open` to print its URL without opening a browser. CLI help lists the option details.

To bring a running dashboard onto a newly published version, either stop it, publish, then start it again (`sqlharness dashboard --stop`, `just publish`, `sqlharness dashboard`), or publish and restart in one step (`just publish`, `sqlharness dashboard --restart`). A dashboard started before guarded shutdown was added cannot be stopped by the new CLI; during that first upgrade, stop the older process manually once, then publish and start the new version. Later upgrades can use `--restart`.

Stopping a dashboard does not immediately start another one. MCP autostart runs when an MCP server starts, and an MCP process that was already running keeps its installed version until its client restarts it.

One dashboard runs per SQLHarness home; a second invocation opens the running one. It listens only on `127.0.0.1` (default port `47800`, then the next free port, then an ephemeral port when those are taken), and the printed URL carries the dashboard token, which is exchanged for an HttpOnly SameSite=Strict cookie valid for 400 days.

The token persists in an owner-only `dashboard-token` file in the SQLHarness home (delete it to rotate), so the URL is bookmarkable; open tabs show a reconnect banner and recover after a restart. The browser is opened through an owner-only `dashboard-open.html` redirect page in the SQLHarness home, so the token never appears in process arguments; the page is deleted when the dashboard stops.

The page shows live activity (running operations, active sessions, recent operations), session history, per-operation detail (key metrics, per-table IO, waits, memory grant, baseline-versus-candidate comparison, stored plans with an operator view when `journal.storeSensitive` is on), and statistics over 24 hours, 7 days, 30 days, or all time. For `measure` and `compare` operations, the detail page displays saved setup SQL with a copy control (in **SQL** for measure and **SQL changes** for compare). Schema v12 adds `operations.setup_sql_text`, stored only when `journal.storeSensitive` is enabled. Unlike the older bounded `compare_conditions_json` field, this setup column has no 64 KiB conditions limit. Historical compare records may still have setup only in `compare_conditions_json`; historical measure records cannot be backfilled. Replay setup and the measured query or chosen baseline/candidate query in the same database session, with the original parameters. The API under `/api` serves the same data and has five guarded writes: settings, the price refresh, session annotations, clearing matched transcript data, and shutdown. Running operations whose process ended show as `abandoned`. The data it serves is the activity journal and is locally sensitive.

The Sessions page searches title, note, directory, session key and client title, switches between Active and Archived sessions, and pins starred sessions at the top. On a session page click the title (or the pencil) to rename it, star or archive the session, and keep a note. Archiving only hides a session from the Sessions list; Live, Operations and Stats still count it. The dashboard refuses these edits when the journal schema differs from this sqlharness and never migrates it.

Browsers do not isolate cookies by port, so the session cookie is also sent to other services on `127.0.0.1`/`localhost` opened in the same browser; use the dashboard on a single-user workstation. Press Ctrl+C (or send SIGTERM) to stop it; it exits `0`. It exits `6` when another dashboard holds the lock without publishing its address, when it cannot bind a loopback port, or when the activity journal schema does not match this sqlharness (newer, or older and not upgraded).

Snap-confined browsers (for example Firefox or Chromium from a snap on Ubuntu) may not be allowed to open the redirect page under the hidden SQLHarness home directory; open the printed URL instead.

Settings retains the optional model-price reference refresh (`POST /api/prices/refresh`); it is separate from workflow output measurements and does not produce savings estimates. The Settings page edits the operator settings in `config.json` (journal, retention, connection diagnostics, dashboard auto-start and idle shutdown, and agent transcript matching) and autosaves (there is no Save button; numbers apply on blur or Enter); `dashboard.port` is not editable, and enabling `journal.storeSensitive`, enabling retention, or lowering retention limits while retention is on asks for confirmation first.

Agent transcript matching is off by default. Enable `dashboard.agentTranscripts.enabled` in `~/.sqlharness/config.json` to let each dashboard housekeeping pass read local Claude Code and Codex metadata, with a maximum of 32 MiB read per pass. Claude files are read from `CLAUDE_CONFIG_DIR` (or `~/.claude`), under `projects/<encoded cwd>/`, including subagent JSONL files. Codex titles are read from `$CODEX_HOME/session_index.jsonl` (default `~/.codex/session_index.jsonl`). Matching is exact: Claude tool-use ids must equal the operation's `client_call_id` in a Claude session; Codex conversation ids must equal `agent_session_id` in a Codex session. No time or working-directory guesses are used.

Only conversation ids, titles, tool-use ids, model names, and effort values are retained from transcripts; each value is capped at 256 characters, and no other transcript text is stored. The dashboard uses the operator's session title first, then the agent conversation title, then the session key; for an operation it uses the model reported by the call first, then the transcript model. Disabling matching stops new reads but keeps matched data. Use **Clear matched data** in Settings to remove all matched conversation, title, model, and effort data; it does not remove operator session titles or notes. Normal journal retention removes transcript links with their operations and unreferenced conversation data.

Connection diagnostics records bounded connection and artifact metadata in the journal; it excludes credentials and target names and never stores SQL, parameter values, or tokens.

Saved settings apply to new processes, so restart running `mcp serve` sessions. The Profiles page lists the `targets.json` profiles read-only and shows password variable names, never values. A failed or rejected operation shows its credential-redacted error in a tooltip on the status badge and in a details dialog; legacy rows without a message show that no message is available. Error messages may quote SQL and data values.

The theme menu switches between system, light, and dark and is remembered per browser.

<details>
<summary>Build and develop the dashboard UI</summary>

#### Building the dashboard UI

The UI lives in `src/SqlHarness.Dashboard/ui` (React, Vite, shadcn/ui on Base UI) and is embedded into the binary by `dotnet build`, which runs `npm ci` (when `node_modules` is missing) and `npm run build`. Node is pinned in `.nvmrc`. Build without Node with `dotnet build -p:SkipDashboardUi=true` (a placeholder page is served).

For UI development run `sqlharness dashboard --no-open`, then in `ui/` run `SQLHARNESS_DASHBOARD_TOKEN=<t value> npm run dev` (bash) or `$env:SQLHARNESS_DASHBOARD_TOKEN='<t value>'; npm run dev` (PowerShell); the Vite dev server proxies `/api` to the running dashboard (set `SQLHARNESS_DASHBOARD_URL` when it is not on the default `http://127.0.0.1:47800`).

Add shadcn components with `npx shadcn@latest add <component>` from `ui/`; the shadcn CLI is deliberately not a project dependency (its file-globbing dependencies carry an unpatched `braces` advisory), and the one stylesheet the UI needs from it is vendored as `ui/src/styles/shadcn-tailwind.css`. If the CLI re-adds `@import "shadcn/tailwind.css"` to `src/index.css`, point it back at the vendored file.

</details>

### Measured examples

Examples of measured raw and emitted bytes are planned for this section, using real runs.

## Local data

- Target profiles: `~/.sqlharness/targets.json`
- Activity journal: `~/.sqlharness/data/activity.db` (locally sensitive)
- Comparison artifacts: `~/.sqlharness/compare/`
- Named snapshots: `~/.sqlharness/snapshots/` (sensitive result data; replace only with `snapshot --force`)

Set `SQLHARNESS_HOME` to relocate these paths, for example in an isolated test environment.

On Unix, SQLHarness creates its home with mode 0700; `doctor` warns when an existing home is broader.

## Roadmap after v1

- Windows approval tray for one-time local approval of persistent DML requested over MCP: designed, not implemented; deferred ([plan](docs/superpowers/plans/2026-09-26-audit-08-windows-approval-tray.md)).

The local MCP server over stdio is implemented: see [docs/mcp.md](docs/mcp.md).

## Development

Before marking implementation complete, run `just verify`. It runs the same four stages (restore, build, test, format) as CI, with the local non-integration test filter. A change is not done until it is green. The .NET SDK version comes from `global.json` (currently `10.0.303`, `rollForward: latestPatch`). Re-release after .NET runtime security patches.

`just verify-linux` still runs the identical stages inside WSL on a case-sensitive ext4 filesystem to check Linux-specific behaviour. It is not required for completion while CI runs on the local machine, and comes back into the completion criteria when CI moves to GitHub Actions. Provision it once with `pwsh ./scripts/setup-linux-gate.ps1` (installs the `global.json` SDK in the distro and creates a disposable clone under `~/src`; the gate clone never lives under `/mnt/d`). It syncs committed code only: it verifies the branch tip, not uncommitted work.

Neither gate replaces CI, which runs on ubuntu-latest and windows-latest.

The gate prints one line per stage (`==> build ok 45.5s`) and a final `verify: OK stages=6 2m03s`;
stage output goes to a log file, and a failed stage prints its exit code, the tail of the log and the
log path. Add `-Stream` (`just verify -Stream`) to watch a run instead. Logs default to a directory
under the temp path, overridable with `-LogDirectory`.

The format stage is scoped to the C# files a change touched, because `dotnet format` spends nearly all of its time loading the MSBuild workspace: a full-solution check took 129s here against 34s scoped to what changed. `just verify` checks the working tree; `just verify-linux` checks what the branch changed relative to `origin/main`, because the gate clone is reset to the branch tip and a working-tree diff there is always empty.

CI still runs `dotnet format --no-restore --verify-no-changes` over the whole solution. The Linux gate syncs committed code only: it verifies the branch tip, not uncommitted work in the working tree. `just fix` rewrites the formatting of the same file set the gates check.

`just publish` tests, builds a self-contained binary and installs it where `sqlharness` is on PATH
(override with `SQLHARNESS_INSTALL_DIR`). On Windows, when the system host cannot resolve the
pinned SDK, it tries the user-local installation under `%LOCALAPPDATA%/Microsoft/dotnet`.
The publishing process uses `%SystemDrive%/t` for Windows test temporary files so bounded-path
tests do not depend on the length of the user's normal temp directory. These environment changes
apply only to the publishing process; no terminal configuration is needed.

```powershell
just verify
just verify-linux
just fix
dotnet test
dotnet run --project src\SqlHarness.Cli -- --help
```

The test suite uses fake adapters and fixtures; it does not connect to a real database.

<details>
<summary>SQL Server development playground</summary>

### Optional local AdventureWorks playground

SQLHarness does not require a database to build, install, start, or run offline commands such as `plan`. The local AdventureWorks environment exists only for development, manual experiments, and opt-in SQL Server integration tests. Docker resources and secrets remain local to your machine.

Docker stores the SQL Server bootstrap password in local container metadata, so the playground password must be development-only and unique. Do not reuse production credentials.

#### Setup

```powershell
$env:SQLHARNESS_PLAYGROUND_PASSWORD = Read-Host 'Local playground password'
.\scripts\setup-local-adventureworks.ps1
```

The script creates or reuses fixed local Docker resources (`sqlharness-sql` on host port `14335`, volume `sqlharness-sql-data`) and restores `AdventureWorks2022` when that database is absent. It never edits `~/.sqlharness/targets.json`.

#### Profile merge (manual CLI)

Add this entry to the existing profiles in `~/.sqlharness/targets.json`:

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

Create the local smoke query outside tracked repository paths:

```powershell
$smokeQuery = Join-Path $env:TEMP 'sqlharness-playground-smoke.sql'
@'
SELECT TOP (10) p.ProductID, p.Name, p.ListPrice
FROM Production.Product AS p
WHERE p.ListPrice > @minimumPrice
ORDER BY p.ListPrice DESC;
'@ | Set-Content $smokeQuery -Encoding utf8

sqlharness query local-playground --file $smokeQuery --param minimumPrice:decimal=100 --json

sqlharness measure local-playground `
    --query $smokeQuery `
    --param minimumPrice:decimal=100 `
    --repeat 2 `
    --json
```

Expected: both commands exit `0`; the query result names `AdventureWorks2022` as its database, and measure returns bounded JSON output.

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

Stop, start or restart the container with:

```powershell
docker stop sqlharness-sql
docker start sqlharness-sql
docker restart sqlharness-sql
```

Deleting the container or volume destroys local playground state and must be a separate, explicit user action. Automated removal is intentionally not part of the setup path.

</details>

<details>
<summary>PostgreSQL development playground</summary>

### Optional local Postgres (Pagila) playground

The optional Postgres playground uses fixed Docker resources, like AdventureWorks, and never edits `~/.sqlharness/targets.json`.

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

The TLS proof test reads only `SQLHARNESS_PG_TLS_PROOF`; it does not read user profiles or `~/.sqlharness/targets.json`.

</details>
