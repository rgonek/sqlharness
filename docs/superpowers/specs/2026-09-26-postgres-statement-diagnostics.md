# PostgreSQL statement diagnostics policy (plan 06/T5, spec v1)

Status: specification only. No production code, no stubs, no exit-code changes,
no live testing. SQL Server `qstop` keeps its current contract unchanged.
Decisions here are a reviewable contract for a future implementation task.

## 1. Scope and non-goals

- This policy defines a new read-only PostgreSQL diagnostic over the
  `pg_stat_statements` extension: top statements by cumulative execution time
  in the current database, with a value-free stdout report and SQL text kept
  only in a locally sensitive artifact (mirroring the Query Store contract).
- Target floor is PostgreSQL 14+. Servers below 14 are out of scope and must
  be rejected explicitly, never probed with version-dependent SQL.
- Non-goals: enabling or installing the extension (`CREATE EXTENSION`,
  `ALTER SYSTEM`, reload/restart); changing `shared_preload_libraries`,
  `compute_query_id`, `pg_stat_statements.*` GUCs, or calling
  `pg_stat_statements_reset()` from this diagnostic; recommending indexes;
  proving plan regression; capturing statements the extension was not
  configured to track; any time-window semantics (section 7).
- Query Store semantics are never merged with `pg_stat_statements` semantics
  under one identical report. Differences (windowed intervals vs cumulative
  counters, `query_id` vs `queryid`, per-entry timestamps vs none) are stated
  in this spec, not hidden behind shared field names.

## 2. Availability: extension present or explicitly unavailable

Official behavior (PostgreSQL 14+ `pg_stat_statements` docs; see section 11):

- The module must be loaded via `shared_preload_libraries` (server restart
  required) and query-identifier calculation must be active
  (`compute_query_id` `auto`/`on`, or a third-party query-id module).
- The views (`pg_stat_statements`, `pg_stat_statements_info`) and functions
  (`pg_stat_statements_reset`, `pg_stat_statements`) exist only in databases
  where `CREATE EXTENSION pg_stat_statements` was run. Tracking itself is
  cluster-wide; visibility objects are per-database.
- Absent extension therefore surfaces as a missing relation/function error
  (SQLSTATE `42P01` / `42883`). The implementation maps that to exit `5`
  with a bounded message naming only the cause: extension not installed in
  the resolved database. It never runs `CREATE EXTENSION`, never suggests
  connection-string edits, and never retries against another database.
- Permission denial on the view (`42501`) is likewise exit `5`: telemetry
  unreadable, not an empty result (section 9).

## 3. Column versions (14 vs 15+)

- PG14 view columns used by this spec (stable subset; types per official
  docs): `userid oid`, `dbid oid`, `toplevel bool`, `queryid bigint`,
  `query text` (nullable), `plans bigint`, `total/min/max/mean/stddev_plan_time
  double precision`, `calls bigint`, `total/min/max/mean/stddev_exec_time
  double precision`, `rows bigint`, `shared_blks_hit/read/dirtied/written`,
  `local_blks_hit/read/dirtied/written`, `temp_blks_read/written` (all
  `bigint`), `blk_read_time/blk_write_time double precision`,
  `wal_records/wal_fpi bigint`, `wal_bytes numeric`.
- `toplevel` and the `pg_stat_statements_info` view (`dealloc bigint`,
  `stats_reset timestamptz`, single row) exist on 14+. PG15 adds eight
  `jit_*` columns (`jit_functions`, `jit_generation_time`,
  `jit_inlining_time`, `jit_optimization_time`, `jit_emission_time`,
  `jit_inlining_count`, `jit_optimization_count`, `jit_emission_count`).
- The fixed diagnostic query selects the section-3 subset by name (never
  `SELECT *`) so extra columns on newer servers are ignored. A row missing
  any subset column, or carrying an unexpected type, is a malformed shape
  (exit `5`), never silently coerced.
- Zero-means-zero columns (not missing data): plan times are `0` unless
  `pg_stat_statements.track_planning` is on; `blk_read_time/blk_write_time`
  are `0` unless `track_io_timing` is on; `toplevel` is always true when
  `pg_stat_statements.track` is `top` (the default). The report carries
  these GUC values as observed context so a reader can tell "untracked"
  from "fast".
- Tracking scope GUCs, for the record: `track` (`top` default; `all` also
  counts nested statements such as function bodies; `none` disables),
  `track_utility` (default `on`; non-`SELECT/INSERT/UPDATE/DELETE`),
  `track_planning` (default `off`), `max` (default `5000` entries;
  settable only at server start; overflow discards least-executed entries
  and increments `info.dealloc`).

## 4. Identity, ranking, metrics

- One output item represents one `queryid` in the current database
  (`dbid = current database`, resolved server-side). Rows for other
  databases are excluded by the fixed query; rows across `userid` values
  are aggregated into that one item.
- `queryid` stability limits (official docs): derived from the
  post-parse-analysis tree, so a dropped-and-recreated table splits one
  apparent query into two entries; sensitive to platform/architecture;
  not stable across major versions; not comparable across logical
  replicas. The spec therefore treats `queryid` as a within-server,
  within-version clustering key, never as a cross-server identifier.
- Displayed query text is representative, not canonical: constants are
  normalized to `$n` parameters (numbering starts after the highest `$n`
  in the original text; PL/pgSQL hidden parameters can shift numbering),
  and the text shown is the first query observed for that `queryid`.
  `query` may be null (texts discarded under memory pressure; statistics
  preserved) — null text is tolerated, never an error.
- Aggregation per `queryid`: `calls = SUM(calls)`,
  `total_exec = SUM(total_exec_time)`, read/write/buffer/row/WAL counters
  summed; `avg_exec = total_exec / SUM(calls)`; `max_exec = MAX(max_exec_time)`;
  same pattern for plan times. `toplevelOnly` is true iff every contributing
  row has `toplevel = true`.
- Ranking (no ties to Query Store fields): `total_exec` descending, then
  `calls` descending, then `shared_blks_hit + shared_blks_read` descending,
  then `queryid` ascending. The first `--top` rows are returned.
- The view has no per-entry timestamp: there is no `lastExecutionAt`
  equivalent. The report must not contain recency fields and must not imply
  them. (Contrast: Query Store ranks windowed intervals per `query_id` with
  `last_execution_time`; see section 8.)

## 5. Report and sensitive artifact

- Each stdout item contains only metrics and identifiers:

```text
queryId                  pg_stat_statements queryid (int64)
toplevelOnly             bool
executionCount           SUM(calls)
totalDurationMs          SUM(total_exec_time)
averageDurationMs        total / calls
maximumDurationMs        MAX(max_exec_time)
totalPlanMs / averagePlanMs / maximumPlanMs
rowsReturned             SUM(rows)
sharedBlocksHit/Read     SUM(...)
localBlocksRead/Written  SUM(...)
tempBlocksRead/Written   SUM(...)
walBytes                 SUM(wal_bytes)
blockReadMs/blockWriteMs SUM(blk_read_time/blk_write_time)
```

- Stdout additionally carries observed context: server version string,
  `track`/`track_utility`/`track_planning` settings, `stats_reset`,
  `dealloc`, and the artifact directory. No query text, no parameter
  values, no connection strings, no credentials, no internal SQL.
- Every successful invocation atomically writes one local artifact
  directory `pg-statements/<timestamp>-<database>-<random>/` with
  `report.json` (the same value-free report) and `queries.jsonl`
  (one line per returned item: `queryId` + verbatim `query` text, null
  where the server returned null). Text is stored verbatim because
  reliable literal redaction is impossible.
- The future implementation should use the `showtext := false` function
  form for the ranking read and fetch texts only for the returned top-N
  artifact lines, reducing repeated transfer of indeterminate-length text.

## 6. Confidentiality contract (Query Store parity)

- Stdout, stderr, safe errors, filenames, and gain records contain metrics
  and identifiers only — never SQL text. Same rule as `qstop`
  (`AGENTS.md`): SQL text lives exclusively in the locally sensitive
  `queries.jsonl`, which must not be pasted or published without explicit
  review, and is never copied into gain records or errors.
- Non-superusers see statistics for all rows but SQL text/`queryid` only
  for their own queries (official access rule: text and `queryid` of other
  users' queries require superuser or `pg_read_all_stats` membership).
  The report therefore treats `query`/`queryId` as sensitive even when the
  caller is privileged: redaction policy does not depend on who asked.
- Artifact write failure is exit `6` with no report emitted, mirroring
  `qstop`.

## 7. Snapshot/delta and the no-window rule

- Counters are cumulative since collection start, not windowed. The command
  offers no `--window` parameter, and the report never labels a delta with
  a duration. A delta is "change between snapshot A and snapshot B",
  labeled with both snapshots' `stats_reset` values — never "last 24h".
- Snapshot record (stored offline by the future implementation, outside
  this spec's files): per-`queryid` counters from section 5 plus continuity
  markers: `stats_reset`, `dealloc`, and `pg_postmaster_start_time()`.
- A delta is comparable only if all three markers are equal across both
  snapshots. Any `stats_reset` change (manual `pg_stat_statements_reset`,
  full or selective), any `dealloc` increase (entry eviction under
  `pg_stat_statements.max` pressure — per-entry deltas after eviction are
  silently partial), or any postmaster-start change with
  `pg_stat_statements.save = off` (statistics lost at shutdown; default
  `save = on` preserves them across clean shutdowns) invalidates the delta:
  the implementation reports `comparable: false` and refuses per-query
  rate/interval arithmetic instead of emitting misleading numbers.
- Review focus for this task: reset handling and the absent window promise.
  No future reviewer may reintroduce a 24h-equivalent claim on top of
  cumulative counters.

## 8. Explicit non-equivalence with Query Store top

| Aspect | SQL Server `qstop` (unchanged) | PG diagnostic (this spec) |
|---|---|---|
| Window | `--window`, default 24h, interval-overlap filter | none; cumulative counters + explicit snapshot/delta |
| Identity | `query_id` (+ `queryHash`), all plans aggregated | `queryid` per current database, user rows aggregated |
| Recency | `lastExecutionAt` per item | unavailable by design |
| CPU | total/avg/max CPU ms | no CPU metric (consistent with `measure`/`compare`: `CpuTimeMs` is `0` on PG) |
| Reads | logical reads (pages) | shared/local/temp buffer counters + WAL bytes |
| Unavailable | `OFF`/`ERROR` state, PG engine → exit `5` | extension missing, `42501`, malformed shape → exit `5` |
| Empty | readable + no rows in window → exit `0`, empty list | readable + no rows → exit `0`, empty list + artifact |

## 9. Errors and exits (existing contract, no changes)

- `0`: success, including readable-but-empty (writes artifact with empty list).
- `2`: CLI validation (`--top` 1..500, `--timeout` 1..300; unknown server
  < 14 rejected before connect).
- `3`: authentication; `4`: target mismatch (both before any extension probe).
- `5`: extension missing (`42P01`/`42883`), permission denied (`42501`),
  SQL timeout, malformed shape, or execution failure. The message names
  only the cause class, never SQL text.
- `6`: artifact persistence failure. Exits `7`/`8` are inapplicable and
  untouched. No new exit code is introduced.

## 10. Fixtures (offline; no live target required)

The future implementation tests must cover at least these shapes, in the
`FakeReader` style of `QueryStoreTopTests` (state/metric/text result sets,
no connection):

1. `no-extension`: catalog probe raises `42P01` → exit `5`, message names
   extension absence, zero artifact writes, no `CREATE EXTENSION` issued
   (assert the single executed batch contains no DDL).
2. `permission-denied`: view read raises `42501` → exit `5`, safe error
   contains no SQL text and no relation internals beyond the view name.
3. `empty-readable`: `pg_stat_statements_info` readable, zero entry rows →
   exit `0`, empty list, artifact still written with empty `queries.jsonl`.
4. `reset-between-snapshots`: two snapshots with different `stats_reset`
   (and one with increased `dealloc`) → delta `comparable: false`, no
   per-query arithmetic emitted.
5. `server-14-shape`: 35-column row without any `jit_*` column, including
   a null-`query` entry → accepted, null text tolerated, ranking by
   `total_exec_time` then `calls`.
6. `server-15plus-shape`: same rows plus the eight `jit_*` columns (and a
   second `userid` row for an aggregated `queryid`) → extra columns ignored,
   user rows aggregated into one item, identical ranking outcome as fixture 5.

## 11. Implementation plan (future files; not created by this task)

1. `src/SqlHarness.Core/Postgres/PgStatementTopQuery.cs` — fixed
   parameterized batch: extension-presence probe, `info` marker read
   (`dealloc`, `stats_reset`), context GUCs + `pg_postmaster_start_time()`,
   ranked subset-column select filtered to current database.
2. `src/SqlHarness.Core/Postgres/PgStatementTop.cs` — pure aggregation,
   ranking, delta-comparability check, value-free report type.
3. `src/SqlHarness.Cli/Commands/PgStatementTopCommand.cs` — proposed
   command name `pgstop` (product review may rename); `--top`, `--timeout`,
   `--json`/`--json-summary`; no `--window`, no mutation flags; PG engine
   only (SQL Server profile → exit `5`, mirroring `qstop` on PG).
4. `src/SqlHarness.Core/PgStatementArtifactWriter.cs` — atomic
   `pg-statements/<timestamp>-<database>-<random>/` publish
   (`report.json` + `queries.jsonl`), sanitized database directory name.
5. `tests/SqlHarness.Tests/PgStatementTopTests.cs` — one test per fixture
   in section 10, plus bounds validation, secret redaction, and
   no-SQL-in-report serialization tests.
6. `Capabilities.cs` + docs (`AGENTS.md`, `README.md`, skill) — advertise
   `pgstop` only after real implementation lands (same rule as 06/T4:
   no capability mapping from a spec).

Review focus for the implementer: reset/dealloc/postmaster continuity
(section 7) must yield `comparable: false`, never a fabricated windowed
rate; null `query` text must never fail the ranking read.

## 12. Sources

- PostgreSQL 14, F.30 `pg_stat_statements` (availability, `toplevel`,
  `pg_stat_statements_info`, reset semantics, security rule, `save` GUC):
  [docs/14/pgstatstatements](https://www.postgresql.org/docs/14/pgstatstatements.html)
- PostgreSQL 15, F.32 (adds `jit_*` columns):
  [docs/15/pgstatstatements](https://www.postgresql.org/docs/15/pgstatstatements.html)
- PostgreSQL 16, F.32 (buffer/IO-timing column set, text-discard behavior,
  `showtext := false` form):
  [docs/16/pgstatstatements](https://www.postgresql.org/docs/16/pgstatstatements.html)
- Repo contracts: `AGENTS.md` (`qstop` report/artifact/exit rules, PG engine
  notes); `docs/superpowers/specs/2026-07-29-query-store-top-consumers-design.md`;
  `tests/SqlHarness.Tests/QueryStoreTopTests.cs` (fixture style to follow).
