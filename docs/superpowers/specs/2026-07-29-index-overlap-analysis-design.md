# Index Overlap Analysis Design

**Date:** 2026-07-29
**Status:** Draft for review

## Purpose

SQLHarness should turn SQL Server missing-index telemetry into a bounded,
read-only diagnostic report that compares each candidate with existing indexes.
The command helps an agent decide what deserves plan-level investigation; it
does not generate or apply index DDL.

## Command

```text
sqlharness indexes <profile> [--var key=value ...]
    [--top <count>=20]
    [--object <name|schema.name>]
    [--timeout <seconds>=30]
    [--json]
```

`--top` accepts `1..500`; timeout accepts `1..300`. Without `--object`, the
command ranks candidates across the current database. With `--object`, it
resolves exactly one user table and limits both candidates and existing-index
metadata to that table.

All arguments and target scope are validated before authentication. The command
accepts no arbitrary SQL, mutation approval, or DDL output option.

## Goals

- Rank missing-index candidates by their cumulative estimated impact.
- Show the raw DMV evidence behind that ranking.
- Compare every candidate with the best matching existing index.
- Classify overlap as `covered`, `include-gap`, `partial-key`, or `new-shape`.
- Make the telemetry observation window and volatility explicit.
- Keep filter predicates and other sensitive definition details out of stdout.
- Preserve complete compared metadata in a locally sensitive artifact.

## Non-goals

- Generating `CREATE INDEX`, `DROP INDEX`, or `ALTER INDEX` statements.
- Claiming that a missing-index DMV row is a safe recommendation.
- Automatically merging, replacing, disabling, or deleting indexes.
- Proving that a candidate improves a workload.
- Estimating write amplification, storage cost, lock impact, or maintenance
  duration.
- Reconstructing telemetry lost to restart, failover, DDL, or DMV reset.
- Analyzing heaps as proposed index definitions.

## Candidate ranking

The fixed internal query reads:

- `sys.dm_db_missing_index_details`;
- `sys.dm_db_missing_index_groups`;
- `sys.dm_db_missing_index_group_stats`;
- `sys.dm_os_sys_info` for SQL Server start time;
- `sys.tables`, `sys.schemas`, and `sys.objects`;
- `sys.indexes`, `sys.index_columns`, `sys.columns`, and `sys.partitions`.

Only candidates for the observed current database and user tables participate.
The cumulative score is:

```text
(user_seeks + user_scans) * avg_total_user_cost * (avg_user_impact / 100)
```

The report also retains the formula inputs:

- `userSeeks`;
- `userScans`;
- `averageTotalUserCost`;
- `averageUserImpactPercent`;
- `lastUserSeek`;
- `lastUserScan`.

Candidates sort by score descending, seeks plus scans descending, object name
ascending, and stable candidate ID ascending. `TOP (@top)` is applied after
database/object filtering.

The score is an estimated cumulative opportunity, not measured elapsed-time
savings.

## Observation window

Missing-index DMVs are cumulative and volatile. The report includes:

- `observationSince`, based on `sqlserver_start_time`;
- `observedAt`, captured once with `SYSUTCDATETIME()`;
- a fixed warning that restart, failover, index DDL, or DMV clearing can shorten
  or reset the evidence.

There is no configurable time window because SQL Server does not retain the
history required to reconstruct one from these DMVs.

## Candidate shape

Each candidate preserves ordered, normalized column lists:

- equality key columns;
- inequality key columns;
- included columns.

The fixed batch returns the bracketed DMV strings plus the complete catalog
column list for every selected table. A pure C# parser recognizes comma
separators outside brackets, unescapes doubled `]]`, and resolves every parsed
name against that table's catalog columns. This avoids treating a comma inside
a bracketed column name as a separator. A malformed, duplicate, unknown, or
ambiguous column reference makes the operation fail with exit `5`; the command
does not silently discard a column.

The proposed key shape for comparison is equality columns followed by
inequality columns. The command does not claim this is the final optimal key
order.

## Existing-index model

Existing index metadata contains:

- schema and table;
- index ID and name;
- index type;
- ordered key columns and sort direction;
- included columns;
- uniqueness;
- primary-key and unique-constraint flags;
- enabled/disabled state;
- `hasFilter`;
- SHA-256 hash of the exact filter definition when present;
- exact filter definition only in the sensitive artifact;
- partition compression descriptions, collapsed to one value or `MIXED`.

Disabled indexes participate in comparison but are visibly marked. Heaps are
reported in table context but do not compete as matching indexes.

## Overlap classification

A pure classifier evaluates each candidate against every non-heap index on the
same table and selects the best match deterministically.

Column identity comparison is case-insensitive, matching SQL Server's usual
identifier behavior. Equality candidate columns may match the leading index-key
segment in any order because they are equality predicates. Inequality columns
must then match the following key segment in candidate order. Existing trailing
key columns do not invalidate a match.

Coverage columns are the union of existing key and INCLUDE columns.

Classifications, from strongest to weakest:

### `covered`

- the existing leading key segment contains every equality candidate column;
- the following key segment contains every inequality candidate column;
- every candidate INCLUDE column exists in the coverage columns.

### `include-gap`

- the complete candidate key shape matches as for `covered`;
- one or more candidate INCLUDE columns are absent from coverage.

The report lists the missing include-column names.

### `partial-key`

- the index does not satisfy the complete unfiltered key shape and shares at
  least one leading equality column or a non-empty leading candidate-key
  prefix; or
- the index is filtered and structurally satisfies the complete candidate
  key, because the command cannot prove filter implication.

The report includes matched key-column count and candidate key-column count.

### `new-shape`

No existing index has a qualifying key overlap.

Selection prefers classification strength, then matched key count, then fewer
missing include columns, then enabled over disabled, then lower index ID.

Filtered indexes never produce `covered` because filter implication cannot be
proved without query predicates. A structurally covered filtered index is
reported as `partial-key`, with `hasFilter=true` and its filter hash.

## Public report

The public report contains:

```text
target
observationSince
observedAt
top
objectFilter                 nullable normalized schema.table
warnings
artifactDirectory
candidates[]
```

Each candidate contains:

```text
candidateId
schema
table
equalityColumns[]
inequalityColumns[]
includeColumns[]
userSeeks
userScans
averageTotalUserCost
averageUserImpactPercent
cumulativeImpactScore
lastUserSeek
lastUserScan
classification
bestExistingIndex            nullable
matchedKeyColumnCount
candidateKeyColumnCount
missingIncludeColumns[]
existingIndexDisabled
existingIndexHasFilter
existingIndexFilterHash      nullable
```

No report field contains filter predicates, query text, DDL, connection
strings, credentials, runtime parameter values, or application row data.

An enabled target with no missing-index rows returns exit `0` and an empty
candidate list.

## Sensitive artifact

Each successful invocation atomically writes:

```text
index-analysis/<timestamp>-<database>-<random>/
    report.json
    candidates.jsonl
    existing-indexes.jsonl
```

`report.json` is the value-free public report with its final artifact path.
`candidates.jsonl` contains the complete normalized candidate shapes and raw DMV
metrics. `existing-indexes.jsonl` contains every existing index used in
comparison, including exact filter definitions.

The artifact contains no generated DDL or query text. It is locally sensitive
because filter predicates and object metadata can reveal business rules.
Publication uses a staging directory followed by an atomic directory move;
partial writes are cleaned up. Persistence failure returns exit `6`.

## Data flow

1. Validate target, top, timeout, and optional object syntax.
2. Resolve the target and authenticate.
3. Verify observed target identity.
4. Execute one fixed parameterized catalog/DMV batch.
5. Validate object resolution and parse candidate/existing-index models.
6. Classify every candidate against same-table indexes in memory.
7. Construct the value-free public report.
8. Atomically persist the public report and sensitive metadata.
9. Render compact text or JSON.
10. Complete deferred gain accounting under command name `indexes`.

## Safety and error handling

- The command is fixed-query and read-only.
- Object schema/name and top are bound parameters.
- Invalid bounds or object syntax return `2` before authentication.
- Missing or ambiguous `--object` returns `2`.
- Authentication failure returns `3`.
- Target mismatch returns `4`.
- DMV/catalog permission failure, SQL timeout, malformed candidate columns,
  malformed result shape, or SQL execution failure returns `5`.
- Artifact failure returns `6`.
- Empty valid telemetry returns `0`.
- Errors are bounded and use existing redaction.
- Filter definitions never enter public reports, errors, filenames, or gain
  records.

## Testing strategy

### CLI tests

- defaults and explicit top/timeout;
- database-wide and exact-object modes;
- invalid bounds and multipart object names;
- profile variables and direct targets;
- absence of user-SQL, mutation, or DDL options.

### Fixed-query and reader tests

- every user input is parameterized;
- current-database and user-table restrictions;
- exact ranking formula and deterministic ordering;
- observation timestamps;
- candidate column ordering and validation;
- existing key direction, INCLUDE, uniqueness, constraints, disabled state,
  filters, hashes, and compression;
- missing/ambiguous object;
- empty telemetry;
- malformed or incomplete result sets;
- invariant numeric and timestamp parsing.

### Classifier tests

- equality columns in different orders;
- ordered inequality matching;
- trailing existing key columns;
- INCLUDE coverage and gaps;
- partial leading overlap;
- unrelated indexes;
- filtered indexes never classified `covered`;
- disabled-index tie breaking;
- deterministic best-match selection;
- case-insensitive identifiers;
- duplicate candidate columns rejected.

### Artifact and privacy tests

- public report and stdout contain no filter definition;
- full filter exists only in `existing-indexes.jsonl`;
- no DDL or query text in any artifact;
- atomic success, unique safe path, and rollback on each write failure;
- storage failure exit `6`;
- sensitive values absent from errors.

### Module, rendering, and gain tests

- success, empty success, validation, auth, mismatch, SQL, and storage mapping;
- one verified session and one fixed batch;
- compact deterministic text and JSON;
- observation-window warning always visible;
- deferred `indexes` gain accounting;
- legacy gain logs remain readable.

## Documentation

CLI help, README, `AGENTS.md`, and `skills/sqlharness/SKILL.md` document:

- database-wide top-20 default and exact `--object`;
- the cumulative impact formula and since-restart evidence window;
- all four overlap classifications;
- lack of DDL generation or mutation;
- sensitive filter metadata in the artifact;
- the requirement to validate candidates with workload knowledge,
  `measure`/`compare`, and migration review.

## Acceptance criteria

- Database-wide and exact-object analysis use one fixed read-only batch.
- Candidates rank by the documented cumulative impact score.
- Classification is deterministic and covered by pure unit tests.
- `covered` candidates are not presented as new-index proposals.
- Filtered indexes never yield `covered`.
- No DDL, query text, or filter predicate appears in stdout or safe errors.
- Exact filter definitions exist only in the local sensitive artifact.
- Empty telemetry succeeds; safety, auth, mismatch, SQL, and storage failures
  retain their documented exit codes.
- Help, public docs, agent guidance, gain accounting, and full tests are
  synchronized.
