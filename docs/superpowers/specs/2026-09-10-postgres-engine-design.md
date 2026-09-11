# PostgreSQL Engine Design

**Date:** 2026-09-10
**Status:** Approved

## Purpose

SQLHarness is a SQL Server and Azure SQL optimization harness. This design adds
PostgreSQL as a second engine behind the same CLI, the same command set, and the
same safety and output contracts, so an agent can `ping` / `query` / `measure` /
`compare` / `schema` / `counts` / `space` / `watch` / `snapshot` / `plan` against
a closed Postgres profile without a second binary or a lowered safety bar.

The first release targets local and self-hosted Postgres with password
authentication. Full command parity is the product goal; delivery is staged so
each slice is independently testable and does not change SQL Server behavior.

## Goals

- One `sqlharness` binary. Engine is a property of the locked target, not a
  per-command mode flag.
- Existing `targets.json` profiles remain valid. Omitted `engine` means
  SQL Server.
- Semantic safety parity: read-only default, session-local `TEMP` without
  mutation confirmation, persistent DML only with `--allow-mutation
  --confirm-database`, fail-closed parse.
- Native Postgres SQL in user batches and setup. No `#temp` translation.
- Compact reports keep today’s JSON shape. Postgres may add optional `engine`
  and must document field meanings that cannot be identical (logical reads,
  `CpuTimeMs`).
- Catalog helpers stay fixed internal SQL. They never accept user SQL.

## Non-goals

- Azure Database for PostgreSQL Entra ID / `ad-default` on Postgres.
- A dedicated `sslMode` profile field.
- Translating T-SQL `#temp` into `CREATE TEMP TABLE`.
- A `pg_stat_statements` analog of Query Store `qstop`.
- Missing-index overlap analysis on Postgres.
- PostGIS / `geography` / `geometry` parameter binding.
- Rewriting the SQL Server classifier, session, or catalog SQL onto a
  lowest-common-denominator abstraction before Postgres works.
- Changing exit codes, `--json` / `--json-summary` exclusivity, closed-profile
  locking, or the mutation confirmation ritual.

## Key decisions

| Decision | Choice | Rationale |
| --- | --- | --- |
| Product shape | One CLI, `engine` on the profile | Agents keep one skill and one command surface. |
| Default engine | Omitted `engine` = `sqlserver` | No breaking change to existing profiles. |
| First hosts | Local / self-hosted, `auth: sql` | Avoids Entra and cloud SSL matrix in v1. |
| Safety | Same contract, native TEMP, no `#temp` rewrite | Fail-closed parity without a dialect translator. |
| Parser | SqlParserCS, PostgreSQL dialect | Managed, fits single-file RIDs; parse failure denies. |
| Measure | `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` | Honest runtime evidence; SQL Server STATISTICS is not emulated. |
| Result compare | Unmeasured sidecar query per repetition | EXPLAIN does not return rows; equivalence still uses every measured run. |
| Plan contract | Same `DistilledPlan` JSON | Agents keep one compact plan schema. |
| Catalog JSON | Same report records, dialect SQL | Helpers stay drop-in for agents; storage fields are documented analogs. |
| Runtime floor | PostgreSQL 14+; playground 16 | INCLUDE indexes exist; MERGE may fail at runtime on 14. |

## Architecture

After a profile (or `--unsafe-direct`) resolves, SQLHarness selects an internal
**dialect pack**. `SqlHarnessModule` remains the operation orchestrator and does
not embed T-SQL or `pg_catalog`. The SQL Server pack wraps the current code
unchanged. The Postgres pack is new.

A dialect pack supplies:

- session factory (`ISqlSession` over Npgsql);
- safety classifier;
- parameter binder;
- identity probe and target-match rule;
- fixed ping / counts / schema / space SQL and readers;
- benchmark statistics collection;
- plan distillation from the engine’s native plan document.

There is no plugin loader and no extra assembly. Dispatch is an internal
switch on `ResolvedTarget.Engine`.

Notices from Npgsql map to `ISqlSession.Messages`, matching SQL Server
info-message capture.

Package: **Npgsql**, version-pinned in `Directory.Packages.props`.

## Profiles and targeting

`TargetProfile` gains an optional `engine` string: `sqlserver` or `postgres`.
Unknown values fail at load/resolve with exit 2. `ProfileStore` keeps
`UnmappedMemberHandling.Disallow`. Adding an optional property does not
invalidate existing documents.

```json
{
  "local-pg": {
    "engine": "postgres",
    "server": "localhost,5432",
    "database": "appdb",
    "vars": {},
    "auth": "sql",
    "sqlUser": "sqlharness",
    "passwordEnvVar": "SQLHARNESS_PG_PASSWORD",
    "trustServerCertificate": true
  }
}
```

`server` is `host[,port]` for both engines. Postgres default port is **5432**.

Postgres v1 accepts only `auth: sql` (`sqlUser` + `passwordEnvVar`).
`ad-default` and any token auth on a Postgres target fail with exit 2 and a
message that does not echo secrets.

SSL mapping (v1, no `sslMode` field):

- `trustServerCertificate: true` → Npgsql `SslMode=Disable` (local Docker);
- `trustServerCertificate: false` → `SslMode=Require`.

`--unsafe-direct` on Postgres requires `--engine postgres` in addition to
`--server`, `--database`, and `--auth`. `--engine` combined with a profile
name is rejected (exit 2), the same class of error as mixing a profile with
`--unsafe-direct`. Omitted `--engine` on `--unsafe-direct` remains `sqlserver`.

Commands, closed-profile locking (one profile and one variable set per
invocation), and `--var` validation do not change.

## Identity and ping

After connect, the dialect runs an identity query. Database identity is
authoritative: `current_database()` must equal the resolved database with
`StringComparison.Ordinal` (exit 4 on mismatch). Server matching reuses the
SQL Server loopback exception: for `localhost` / `127.0.0.1` / `::1` only the
database is checked. Non-loopback hosts compare the identity host to the
profile host with the existing ordinal-ignore-case normalization (no Azure
SQL suffix stripping on Postgres).

Postgres identity SQL:

```sql
SELECT current_database() AS DatabaseName,
       COALESCE(inet_server_addr()::text, 'localhost') AS ServerName;
```

Postgres ping SQL (fixed, no user SQL):

```sql
SELECT CAST(1 AS int) AS ok,
       current_database() AS db,
       COALESCE(inet_server_addr()::text, 'localhost') AS server,
       current_user AS login;
```

The ping JSON record is unchanged: `Target`, `Server`, `Database`, `Login`,
`DurationMilliseconds`. Optional `engine` may appear on the target identity
report.

## Safety

Postgres uses a separate classifier with the same `SqlSafetyReason` outcomes
and the same rejection rule: messages must not echo SQL text or parameter
values.

### Parser

Classify with **SqlParserCS** in the PostgreSQL dialect.

Classification has three stages, matching the SQL Server SELECT-syntax
contract:

1. Parse the complete batch. Parse errors → `ParseError` (deny).
2. Classify every top-level statement for `SqlUsage.Query` or
   `CompareSetup`. Unknown top-level types → `UnsupportedStatement`.
3. Inspect the full AST for prohibited behavior (external access, dynamic
   SQL, transaction control, sequence writes, FDW/`dblink`, and writes
   outside TEMP / approved DML). Nested constructs inside a top-level
   `SELECT` (CTEs, windows, joins, subqueries) are accepted without a
   per-fragment allowlist. A writable CTE is a write: TEMP target is
   session-local; otherwise it is persistent DML.

There is no “run it anyway” fallback. Legal Postgres that SqlParserCS
cannot parse is denied. That is fail-closed, not a hole.

### Session-local work (no mutation flags)

Allowed without `--allow-mutation`:

- `CREATE TEMP TABLE` / `CREATE TEMPORARY TABLE` / `CREATE TEMP TABLE AS
  SELECT`;
- `INSERT` / `UPDATE` / `DELETE` / `MERGE` and writable CTEs targeting those
  tables;
- `CREATE INDEX` / `DROP TABLE` on those tables.

Temp identity is **not** a name prefix. A table is session-local when:

1. this batch created it as `TEMP` / `TEMPORARY`; or
2. `--setup` on this invocation created it as TEMP and the name is passed
   into query classification; or
3. the target is qualified as `pg_temp` (or `pg_temp_*`).

Unquoted identifiers are folded to lowercase when recorded and looked up,
matching Postgres. Quoted identifiers keep their case.

`UNLOGGED` tables are persistent. `CREATE TABLE` without `TEMP` is not
session-local.

`--setup` (`SqlUsage.CompareSetup`) allows `SELECT` plus session-local TEMP
work only. Persistent writes in setup → `NonTemporaryWrite`. Setup runs once
per connection; warmup and measured SQL reuse that Npgsql session, so TEMP
from setup is visible.

### Persistent DML vs DDL

`--allow-mutation --confirm-database <exact-resolved-name>` unlocks persistent
**DML only**: `INSERT` / `UPDATE` / `DELETE` / `MERGE` and writable CTEs whose
target is not session-local. Confirmation compares to `current_database()`
with ordinal equality, same as today.

Persistent **DDL** (`CREATE TABLE` without TEMP, `ALTER`, `DROP` of non-temp,
`TRUNCATE`, `CREATE INDEX` on a persistent table, `CREATE EXTENSION`,
`VACUUM`, …) remains `UnsupportedStatement` and cannot be unlocked with the
mutation flags.

Postgres `SELECT INTO` creates a persistent table. It is rejected as
`SelectIntoNotAllowed`. Use `CREATE TEMP TABLE AS` or explicit DML.

### Always denied

`BEGIN` / `COMMIT` / `ROLLBACK` / `SAVEPOINT` / `RELEASE`; `PREPARE` /
`EXECUTE` / `DEALLOCATE`; `DO`; `CALL`; `COPY`; `SET` / `RESET` (including
`search_path`); `LISTEN` / `NOTIFY`; `LOAD`; `GRANT` / `REVOKE`; `dblink` and
FDW / `file_fdw`; `pg_read_file` / `pg_ls_dir` / `lo_import`; `nextval` /
`setval`; `pg_sleep`.

Cross-**schema** (`schema.table`) is allowed. Postgres has no SQL Server-style
three-part database qualifier; cross-database access is blocked by denying
FDW / `dblink`, not by rejecting `schema.table`.

`MERGE` is classified as DML when the parser accepts it. On PostgreSQL 14 the
server may reject `MERGE` at execution (exit 5). That is a runtime engine
limit, not a classifier hole.

## Parameters

CLI `--param name[:type]=value` is unchanged. The dialect binds or rejects.

| CLI type | Postgres binding |
| --- | --- |
| untyped, `nvarchar`, `nvarchar(max)` | `text` |
| `varchar`, `varchar(max)` | `varchar` / `text` |
| `char`, `nchar` | `char` |
| `int`, `bigint`, `smallint` | same |
| `tinyint` | `smallint`, value must be 0–255 |
| `bit` | `boolean` |
| `decimal` / `numeric` `(p,s)` | `numeric` |
| `float` / `real` | `double precision` / `real` |
| `date` / `time` | `date` / `time` |
| `datetime`, `datetime2` | `timestamp` |
| `datetimeoffset` | `timestamptz` |
| `uniqueidentifier` | `uuid` |
| `varbinary`, `varbinary(max)` | `bytea` (Base64, same as today) |

Rejected on Postgres (exit 2): `money`, `smallmoney`, `smalldatetime`,
`hierarchyid`, `geography`, `geometry`. Nulls stay `name:null` and
`name:type:null`. Values remain culture-invariant; date/time remain ISO 8601.

Long strings use the provider’s unbounded size, analogous to `Size = -1`.

## Measure, compare, and plan

SQL Server collects rows, time, IO, and showplan XML from one execution.
Postgres `EXPLAIN (ANALYZE, …)` executes the statement and returns a plan
document, not a result set. The harness does not pretend those are the same
mechanism.

### Measured execution

The measured statement is the user SQL wrapped as:

```sql
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) <user statement>
```

From the JSON:

| Report field | Postgres source |
| --- | --- |
| `ElapsedTimeMs` | `Planning Time` + `Execution Time` |
| `CpuTimeMs` | always `0` (no STATISTICS TIME analog; field kept for JSON stability) |
| `LogicalReads` | sum of shared and local `hit` + `read` blocks (8 KB pages) |
| `Tables` | per `Relation Name`, same block sum |
| Plan document | raw EXPLAIN JSON, then distilled |

`measure` and `compare --compare-results off` run only this wrap. Warm-up uses
the same wrap. The harness does not issue user-level `SET`; EXPLAIN options
are passed in the EXPLAIN command.

Postgres `measure` / `compare` require a **single** statement that EXPLAIN can
wrap: `SELECT`, `WITH … SELECT`, or `VALUES`. Multi-statement measured SQL
fails with exit 2. `--setup` may still be a multi-statement TEMP batch.

Reports may include optional `engine: "postgres"`. `logicalReads` on Postgres
means buffer pages touched (hit + read), not SQL Server logical reads. No
separate IO field is added in v1.

### Result equivalence sidecar

When `--compare-results` is `ordered`, `multiset`, or `set`, each measured
repetition is followed by an **unmeasured** execution of the bare user SQL to
build fingerprints. Every measured run still participates. The sidecar does
not enter time/IO distributions.

Order: EXPLAIN ANALYZE first (the measurement), sidecar second (warm cache).
That is two server executions per repetition when comparing rows, and one
when not. Document this in command help and README.

Fingerprint caps, directional counts, and the four comparison modes are
unchanged.

### DistilledPlan

Both engines emit the existing `DistilledPlan` model (`statements` with
`sql`, `root`, `missingIndexes`). Postgres mapping:

| `PlanNode` / statement | EXPLAIN JSON |
| --- | --- |
| `PhysicalOp` | `Node Type` |
| `LogicalOp` | `Join Type` when present, else omitted |
| `ObjectName` | `Relation Name` |
| `IndexName` | `Index Name` |
| `EstimatedRows` | `Plan Rows` |
| `ActualRows` | `Actual Rows` |
| `Executions` | `Actual Loops` |
| `CostFraction` | node `Total Cost` / root `Total Cost` |
| `Predicate` | first present among `Filter`, `Index Cond`, `Hash Cond`, `Recheck Cond`; truncated to 200 characters |
| `Warnings` | node warnings when present |
| `Children` | `Plans` |
| `missingIndexes` | always empty (EXPLAIN has no missing-index list) |

`plan` stays offline and needs no target. Input is a file or stdin. XML that
matches SQL Server showplan → existing distiller. JSON that matches EXPLAIN
`FORMAT JSON` → Postgres distiller. Anything else → exit 2. Persisted
`*.plan.json` remains serialization-only and is not accepted as input.

Compare/measure artifacts store raw `*.explain.json` plus distilled
`*.plan.json` (same distilled schema, including optional `sql`).

`--json-summary` is unchanged: noteworthy operators come from `DistilledPlan`,
so the projection works for both engines.

## Catalog helpers

All four helpers remain read-only, accept no user SQL, and emit the existing
report records.

### counts

User tables from `pg_class` + `pg_namespace`, excluding `pg_catalog`,
`information_schema`, toast, and temp schemas.

- Default: `reltuples` (negative treated as 0).
- `--exact`: `COUNT(*)` (bigint in Postgres).
- `--table schema.name` and `--like` flags unchanged.
- Unqualified `--table` matches every schema; more than one hit is ambiguous,
  same as today.
- Postgres `LIKE` is **case-sensitive**. The harness does not rewrite to
  `ILIKE`.

### schema

Tables and views. `--object` is `name` or `schema.name`, matched exactly to
`relname` / `nspname` as stored (unquoted Postgres names are lowercase;
`Contracts` does not match `contracts`). Columns, primary keys, indexes
(including `INCLUDE` and partial `WHERE`), and foreign keys. `--filter` and
`--max-objects` unchanged. Missing or ambiguous object: existing messages.

### space

Diagnosis only. No vacuum, no fillfactor/AM change, no shrink.

| Report part | Postgres source |
| --- | --- |
| Files | One `DATA` row: size = `pg_database_size`; `PhysicalName` = `data_directory` when the role can read that setting, else null. No WAL row (often superuser). |
| Allocation | `ReservedMb` = `pg_database_size` (whole database). `UsedMb` / `DataMb` = sum of `pg_total_relation_size` / `pg_relation_size` for user relations only. They will not sum to `ReservedMb`. |
| Tables | Top N by `pg_total_relation_size`; `Rows` from `reltuples` |
| `--object` indexes | `pg_index` plus relation size; `Type` = access method (`btree`, …); `Compression` = null |

`--top` default remains 25. Missing or ambiguous `--object`: existing
messages.

### watch and snapshot

Same pipeline as `query` (Postgres classifier, `--param`, mutation → exit 2).
`--until` / `--until-unchanged`, hashes, exit `0` / `7` / `8`, and
`snapshot --diff` without cell values are unchanged. Snapshot names are not
keyed by engine; comparing a Postgres live result to a SQL Server snapshot
under the same name is a user error, reported as differences (exit 8) or
schema mismatch kinds, not as a special engine error.

## Playground

Opt-in local Docker, parallel to AdventureWorks:

- script: `scripts/setup-local-postgres.ps1`;
- container: `sqlharness-pg`;
- host port: **5433**;
- volume: `sqlharness-pg-data`;
- image: `postgres:16`;
- password only in `SQLHARNESS_PG_PLAYGROUND_PASSWORD`;
- **does not** write `~/.sqlharness/targets.json`.

Sample database: **Pagila**. Manual profile merge (documented, not applied by
the script): `local-pg`, `engine: postgres`,
`trustServerCertificate: true`.

Runtime support floor: PostgreSQL **14+**. Playground is 16.

## Documentation and agent surface

README, `AGENTS.md`, and `skills/sqlharness/SKILL.md` document:

- `engine` on profiles and `--engine` only with `--unsafe-direct`;
- Postgres auth/SSL mapping;
- TEMP vs `#temp`;
- parameter types rejected on Postgres;
- EXPLAIN-based measure and the compare-results sidecar;
- case-sensitive `LIKE` and identifier matching;
- space field analogs;
- playground setup.

SQL Server examples in those files stay valid. Postgres examples use a
distinct profile name.

## Testing

- Existing SQL Server unit and (opt-in) integration tests must keep passing
  with no behavior change.
- New unit tests on fake sessions: profile `engine`, reject `ad-default` on
  Postgres, TEMP tracking and setup-name passing, denied statement classes,
  `--param` mapping and rejected types, EXPLAIN distiller fixtures
  (`*.explain.json`), ping/counts/schema/space Postgres SQL contracts.
- Opt-in integration: `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING` and a
  dedicated fact attribute. Do not reuse `SQLHARNESS_INTEGRATION_CONNECTION_STRING`.
- `dotnet test` without those variables opens no database connections.

## Delivery slices

Each slice is releasable and keeps SQL Server green.

1. **Target + session + ping** — `engine` on profiles, Npgsql session,
   identity match, `ping`.
2. **Safety + query pipeline** — classifier, `--param`, `query`, `watch`,
   `snapshot`, `--setup` TEMP visibility.
3. **Evidence** — `measure`, `compare`, `plan` EXPLAIN JSON distillation.
4. **Catalog** — `counts`, `schema`, `space`.
5. **Playground + docs** — Docker Pagila, README / AGENTS.md / skill,
   opt-in integration tests.

## Error handling

Exit codes are unchanged. New validation failures use exit 2 (unknown engine,
`ad-default` on Postgres, `--engine` mixed with a profile, multi-statement
measure SQL, rejected parameter types, unparseable plan input). Identity
mismatch remains exit 4. Postgres execution errors remain exit 5. Artifact
and snapshot storage remain exit 6.

## Alternatives considered

**Separate `pgharness` binary.** Isolates SQL Server risk but duplicates the
CLI, skill, and contracts. Agents would have to choose a tool. Rejected.

**Abstract every collaborator first, then add Postgres.** Delays the first
working command and risks SQL Server regressions. Rejected in favor of a
dialect pack around the current SQL Server code.

**libpg_query for classification.** Closer to the server parser, but native
libraries conflict with the self-contained `win-x64` / `linux-x64` /
`osx-arm64` release story. SqlParserCS is the v1 choice; switching parsers
later is allowed if it stays fail-closed and managed-or-RID-complete.

**Emulating SQL Server STATISTICS IO/TIME as if they existed on Postgres.**
Would invent CPU and logical-read numbers. Rejected in favor of documented
EXPLAIN mapping and `CpuTimeMs = 0`.
