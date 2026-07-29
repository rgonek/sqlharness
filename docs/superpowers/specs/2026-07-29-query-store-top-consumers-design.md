# Query Store Top Consumers Design

**Date:** 2026-07-29
**Status:** Draft for review

## Purpose

SQLHarness should identify the logical queries with the largest cumulative
execution-time impact in a recent Query Store window without requiring an agent
to author DMV joins or expose SQL text in stdout.

The command is diagnostic and read-only. It discovers expensive work; existing
`measure`, `compare`, and `plan` commands remain responsible for proving an
optimization.

## Command

```text
sqlharness qstop <profile> [--var key=value ...]
    [--top <count>=20]
    [--window <duration>=24h]
    [--timeout <seconds>=30]
    [--json]
```

`--top` accepts `1..500`. `--timeout` accepts `1..300`. `--window` accepts a
positive integral value followed by `m`, `h`, or `d`, with a minimum of one
minute and a maximum of 31 days. All values and target scope are validated
before authentication.

The command supports the same closed-profile and explicit `--unsafe-direct`
target modes as other database commands. It accepts no user SQL, mutation
approval, or parameters.

## Goals

- Rank logical queries by total elapsed execution time in the selected window.
- Aggregate all observed plans and Query Store runtime intervals per
  `query_id`.
- Return compact, value-free JSON suitable for an agent context window.
- Preserve full SQL text only in a locally sensitive artifact.
- Distinguish unavailable Query Store telemetry from a valid empty result.
- Expose enough supporting metrics to choose a query for subsequent
  `measure`/`compare` work.

## Non-goals

- Ranking each `plan_id` as an independent query.
- Recommending or applying indexes.
- Detecting parameter sniffing or proving plan regression.
- Capturing live requests that have not reached Query Store.
- Changing Query Store configuration, retention, capture policy, or cleanup.
- Returning execution plans or runtime parameter values.
- Claiming that high cumulative duration alone proves a query is defective.

## Query identity and aggregation

One output item represents one Query Store `query_id`. All matching
`plan_id` and runtime-stat rows in the selected window contribute to that item.

The fixed internal query joins:

- `sys.database_query_store_options`;
- `sys.query_store_query`;
- `sys.query_store_query_text`;
- `sys.query_store_plan`;
- `sys.query_store_runtime_stats`;
- `sys.query_store_runtime_stats_interval`;
- `sys.objects` and `sys.schemas` for optional object context.

Runtime rows participate when their interval overlaps
`SYSUTCDATETIME() - @windowMinutes` and their execution type is regular
successful execution. The query binds `@windowMinutes` and `@top`.

Query Store duration and CPU averages are weighted by `count_executions`:

```text
total duration = SUM(avg_duration * count_executions)
total CPU      = SUM(avg_cpu_time * count_executions)
total reads    = SUM(avg_logical_io_reads * count_executions)
```

Average metrics divide the weighted total by the summed execution count.
Maximum duration, CPU, and logical reads use Query Store's maximum values over
the included runtime rows. Duration and CPU are converted from microseconds to
milliseconds. Logical reads remain page counts.

`planCount` is the number of distinct plans contributing runtime data in the
window. `lastExecutionAt` is the greatest contributing
`last_execution_time`. `queryHash` is the uppercase hexadecimal Query Store
hash, without a `0x` prefix.

Results sort by:

1. total duration descending;
2. total CPU descending;
3. execution count descending;
4. `query_id` ascending.

The first `--top` rows are returned after aggregation.

## Compact report

The public report contains target identity, the normalized window in minutes,
the requested top bound, artifact directory, and ordered query items.

Each item contains:

```text
queryId
queryHash
objectName                 nullable schema-qualified module/object context
executionCount
planCount
totalDurationMilliseconds
averageDurationMilliseconds
maximumDurationMilliseconds
totalCpuMilliseconds
averageCpuMilliseconds
maximumCpuMilliseconds
totalLogicalReads
averageLogicalReads
maximumLogicalReads
lastExecutionAt
```

The report does not contain query text, plan XML, parameter values, runtime
values, connection strings, credentials, or Query Store internal SQL handles.
Human-readable output presents the same fields in a bounded table and does not
add text snippets.

An empty query list is a valid successful report when Query Store is enabled
and readable but no successful runtime statistics overlap the window.

## Sensitive artifact

Every successful invocation atomically writes one local artifact directory
under the existing SQLHarness data root:

```text
query-store/<timestamp>-<database>-<random>/
    report.json
    queries.jsonl
```

`report.json` is the same value-free public report with its final artifact
directory. `queries.jsonl` contains one line per returned item:

```json
{"queryId":42,"queryHash":"A1B2","querySqlText":"SELECT Id FROM dbo.Contracts"}
```

The artifact contains only the top returned queries, not every query examined.
SQL text is stored verbatim because reliable literal redaction is impossible.
The artifact is locally sensitive under the same policy as `.sqlplan` and
comparison artifacts. Artifact contents are never copied into gain records or
errors.

Writes use a staging directory followed by an atomic directory move. A partial
write is cleaned up. Artifact write failure returns exit `6` and does not emit
a successful report.

## Query Store availability

The fixed batch returns Query Store actual state before returning ranked rows.

- Query Store state `READ_WRITE` or `READ_ONLY` is readable; with no matching
  runtime data it returns exit `0` and an empty list.
- Query Store state `OFF`, `ERROR`, or another state that cannot supply runtime
  statistics returns exit `5` with a bounded message naming only the state.
- Missing permission or unsupported Query Store catalog access returns exit
  `5` through the existing SQL error mapping.
- The command never attempts `ALTER DATABASE ... SET QUERY_STORE`.

The error message must not imply that an empty list means Query Store is
disabled.

## Data flow

1. Validate target arguments, `--top`, `--window`, and timeout.
2. Resolve the requested target.
3. Authenticate and open one connection.
4. Verify observed server/database identity.
5. Execute one fixed parameterized Query Store batch.
6. Validate Query Store state and parse ranked rows plus SQL text.
7. Construct the value-free public report.
8. Atomically write `report.json` and `queries.jsonl`.
9. Render text or JSON.
10. Complete deferred gain accounting using the raw DMV footprint and actual
    emitted footprint.

## Safety and privacy

- The command is read-only and exposes no mutation flags.
- SQL is fixed inside SQLHarness; only numeric bounds are parameters.
- SQL text never appears in stdout, stderr, safe errors, filenames, or gain
  records.
- The database name used in an artifact directory is sanitized with the same
  allowlist as existing comparison artifact targets.
- Query Store SQL text may contain literals or sensitive business data, so the
  artifact directory receives an explicit sensitivity warning in README,
  `AGENTS.md`, and the SQLHarness skill.
- A target mismatch stops before the Query Store batch.

## Error handling

- CLI validation and safety failures return `2`.
- Authentication failures return `3`.
- Target mismatch returns `4`.
- Query Store unavailable, catalog permission failure, SQL timeout, malformed
  result shape, or execution failure returns `5`.
- Artifact persistence failure returns `6`.
- Successful empty and non-empty reports return `0`.
- All errors use existing secret redaction and remain bounded.

## Testing strategy

### CLI tests

- defaults and explicit `--top`, `--window`, and timeout;
- valid `m`, `h`, and `d` durations;
- rejected zero, fractional, suffix-free, overflow, and over-31-day windows;
- top and timeout bounds;
- profile variables and direct target options;
- no user SQL or mutation options.

### Query and reader tests

- one fixed read-only batch with bound `@windowMinutes` and `@top`;
- weighted duration/CPU/read formulas;
- aggregation per `query_id` across multiple plans and intervals;
- deterministic ordering and top bound;
- exclusion of non-regular execution types;
- enabled empty result;
- disabled/error Query Store state;
- optional object context;
- invariant numeric and UTC timestamp parsing;
- no SQL text in public report serialization.

### Artifact tests

- `report.json` contains no query text;
- `queries.jsonl` contains only returned top queries and exact SQL text;
- atomic successful publication;
- cleanup after partial failure;
- safe database-derived directory name;
- storage failure exit `6`;
- no query text in failure output.

### Module and rendering tests

- success, empty success, auth, mismatch, SQL, and storage exit mapping;
- a single verified SQL session and a single Query Store batch;
- compact deterministic text;
- JSON shape and field names;
- deferred gain accounting under command name `qstop`;
- legacy gain logs remain readable.

An opt-in SQL Server integration test may verify catalog compatibility against
an isolated database with Query Store enabled. It must not enable Query Store
on an existing user database.

## Documentation

CLI help, README, `AGENTS.md`, and `skills/sqlharness/SKILL.md` document:

- the default 24-hour total-duration ranking;
- aggregation per logical `query_id`;
- the distinction between empty and unavailable telemetry;
- absence of SQL text in stdout;
- local sensitivity of the artifact;
- the handoff from `qstop` discovery to `measure`, `compare`, and `plan`.

## Acceptance criteria

- The command ranks by weighted total elapsed duration per `query_id`.
- Multiple plans contribute to one logical-query item and `planCount`.
- Stdout and safe errors contain no SQL text.
- The artifact contains exact SQL only for returned top queries.
- Enabled Query Store with no data returns exit `0` and an empty list.
- Disabled, errored, unreadable, or malformed Query Store returns exit `5`.
- Artifact failure returns exit `6`.
- Fixed SQL, deterministic ordering, compact rendering, redaction, gain
  accounting, and synchronized documentation are covered by tests.
