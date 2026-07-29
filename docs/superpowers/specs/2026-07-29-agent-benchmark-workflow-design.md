# Agent Benchmark Workflow Design

**Date:** 2026-07-29

## Purpose

SQLHarness should support realistic, agent-operated SQL Server optimization
workflows without weakening its safety boundary. The workflow must support
session-local temporary setup, common SQL parameter types, compact agent output,
explicit result-equivalence semantics, and repeatable comparisons across a
single parameter matrix.

The work is delivered in three independently testable stages:

1. session, SQL-safety, and parameter correctness;
2. compact reporting and explicit result equivalence;
3. single-parameter comparison matrices.

Existing behavior that already satisfies the contract is retained and protected
with regression or integration tests instead of being redesigned.

## Goals

- Prove that setup and measured SQL share one physical SQL Server session.
- Allow safe definitions and indexing of local `#temp` tables without mutation
  confirmation.
- Keep persistent mutations and unsafe SQL fail-closed.
- Support common date/time and precision-sensitive decimal parameters.
- Offer a compact JSON projection suitable for agent context windows.
- Make technical result-equivalence semantics explicit and auditable.
- Compare one varying parameter across several values in a single command.
- Document how technical equivalence differs from domain equivalence.
- Document a representative-sample inventory that precedes benchmarking.

## Non-goals

- Multiple matrix dimensions or Cartesian products.
- Parallel matrix execution.
- Inferring domain equivalence from query results.
- Replacing a rejected local temporary table with a persistent object.
- Relaxing safeguards for persistent DDL, persistent DML, global temporary
  tables, dynamic SQL, external data access, or cross-database references.
- Printing result-row values in equivalence diagnostics.
- Moving full plans or full operator arrays into compact stdout.

## Delivery boundaries

The design maps to three implementation plans.

### Stage 1: Session, safety, and parameters

This stage proves the existing single-session execution contract against a real
SQL Server, expands the safe AST allowlist for local temporary-table definitions,
improves rejection diagnostics, adds safe window syntax, completes parameter
types, and synchronizes command help and documentation.

### Stage 2: Summary and equivalence

This stage separates result comparison from measurement collection, adds four
explicit comparison modes, adds directional difference counts, and projects the
full report into bounded `--json-summary` output.

### Stage 3: Parameter matrix

This stage adds one sequential matrix dimension to `compare`. Each matrix value
uses a fresh connection and a fresh setup while reusing the established
single-comparison execution path.

Each stage must produce useful, releasable software and pass its own focused
verification before the next stage starts.

## Component design

### SQL safety classifier

The classifier remains fail-closed. It reports the unsupported top-level
statement types and nested AST fragment types when a parsed batch is rejected.
Diagnostics must not include SQL text or parameter values.

The query and setup paths allow session-only work on local temporary tables whose
unqualified names begin with exactly one `#`. Allowed operations include:

- `SELECT ... INTO #temp`;
- `CREATE TABLE #temp`;
- `INSERT`, `UPDATE`, `DELETE`, and `MERGE` targeting `#temp`;
- `CREATE INDEX` targeting `#temp`;
- `DROP TABLE #temp`.

A local temporary-table definition may contain:

- `NULL` and `NOT NULL`;
- `PRIMARY KEY`;
- `UNIQUE`;
- the AST fragments required by the supported column and table constraints.

The classifier also allows parsed window-function syntax, including `OVER`,
partitioning, ordering, and bounded window frames, provided every other fragment
and statement remains allowlisted.

These additions do not permit:

- persistent or global temporary destinations;
- persistent DDL without an existing approved mutation contract;
- stored procedure execution or dynamic SQL;
- `INSERT ... EXEC`;
- external rowsets or data sources;
- three- or four-part object references;
- transaction-control batches;
- stateful expressions already prohibited by the safety contract.

### Parameter specification

`--param` and `--matrix` share one parser and one typed parameter
representation. Supported types are:

**Strings**

- implicit or explicit `nvarchar` (bounded size from the value length; values
  longer than 4000 characters bind as `nvarchar(max)` with `Size = -1`);
- `nvarchar(max)`, `varchar`, `varchar(max)`, `char`, `nchar`;
- optional fixed/max sizes where applicable (`nvarchar(n)`, `varchar(n)`,
  `char(n)`, `nchar(n)`, `varbinary(n)`, and the `(max)` forms).

**Integers and boolean**

- `int`, `bigint`, `smallint`, `tinyint`, `bit`.

**Exact and approximate numeric**

- `decimal`, `decimal(p,s)`, `numeric`, `numeric(p,s)` (`numeric` is an alias of
  `decimal`);
- `float`, `real`;
- `money`, `smallmoney`.

**Temporal**

- `date`, `time`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`.

**Identity, binary, and UDT**

- `uniqueidentifier` (any standard GUID format: `D`, `N`, `B`, `P`);
- `varbinary`, `varbinary(max)`, `binary(n)` (values are Base64);
- `hierarchyid`, `geography`, `geometry` as true SQL UDTs via
  `Microsoft.SqlServer.Types` (`SqlDbType.Udt` with the matching `UdtTypeName`).

**Nulls**

- `name:null` binds `DBNull` as `nvarchar`;
- `name:type:null` binds typed `DBNull` for any supported type (including UDT
  types).

Parsing is culture-invariant. Date/time values use ISO 8601 input (with
`time` as an invariant time-of-day) and must fit the range of the selected SQL
Server type. `datetimeoffset` requires an explicit `Z` or signed offset so a
machine-local timezone cannot change the bound value.

`decimal(p,s)` / `numeric(p,s)` validate SQL Server precision and scale bounds
(`1 <= p <= 38`, `0 <= s <= p`), validate that the supplied value fits, and bind
`Precision` and `Scale` on `SqlParameter`. The unqualified `decimal` / `numeric`
forms retain provider-inferred precision and scale: SQLHarness sets the decimal
value but does not set `Precision` or `Scale`.

`hierarchyid` values are hierarchy paths (for example `/1/2/`).
`geography` and `geometry` values are WKT, optionally prefixed with
`srid;WKT` (defaults: geography SRID `4326`, geometry SRID `0`). Spatial UDT
binding depends on the package's native spatial runtime on supported Windows
RIDs; Linux/macOS spatial UDT binding is not guaranteed.

This behavior is documented and is identical across `--param` and `--matrix`.

Command help, README, `AGENTS.md`, and the SQLHarness skill list every supported
type and show at least one date/time, one `decimal(p,s)`, and representative
GUID / UDT examples.

### Result comparison

Result comparison is a distinct component that consumes canonical result sets.
It does not collect timings, plans, SQL messages, or artifacts.

`compare` accepts:

```text
--compare-results ordered|multiset|set|off
```

The default is `ordered`.

- `ordered` compares result sets, rows, duplicates, and row order. It reports
  the number of differing positions. It also reports multiset-based
  `baselineOnlyCount` and `candidateOnlyCount`; zero directional counts with a
  failed ordered comparison means that only order differs.
- `multiset` ignores row order and preserves duplicate counts. Directional
  counts include duplicate multiplicity.
- `set` ignores row order and duplicates. Directional counts count unique rows.
- `off` skips equivalence work. `equivalent` and difference counts are `null`,
  not successful defaults.

Comparison diagnostics never contain row values. Existing canonicalization,
type distinctions, result-set boundaries, and secrecy limits remain in force.

Every measured run participates in the selected comparison. Technical
equivalence is true only when all measured results are mutually equivalent under
that mode, including repeated runs of the same variant. Reported difference
counts are the maximum observed for any baseline/candidate repetition pair.
Warm-up results do not affect equivalence.

Modes that require directional counts retain only canonical row fingerprints and
counts, never row values. They accept at most 1,000,000 rows per measured
variant run. Exceeding that bound fails the operation with a bounded message
rather than silently omitting or approximating equivalence.

The report calls this result **technical equivalence**. It does not claim
business or domain equivalence.

### Full report and compact projection

The full report remains the source of truth and remains available through
`--json` and the artifact directory. `--json-summary` is a separate,
mutually-exclusive stdout mode. Invocations specifying both flags fail
validation before authentication.

The compact compare projection contains:

- resolved and observed target identity;
- safety classification without SQL text;
- repeat count, selected equivalence mode, and non-secret parameter metadata;
- the matrix parameter's canonical display value for matrix output, because the
  user supplied that value expressly as a report dimension;
- technical equivalence and its difference counts;
- baseline and candidate min/median/max CPU time;
- baseline and candidate min/median/max elapsed time;
- baseline and candidate min/median/max logical reads;
- per-table min/median/max logical reads for each variant;
- warnings, spills, and implicit-conversion indicators;
- at most ten operators that occur on only one side or carry a warning;
- artifact directory.

The compact measure projection contains the same applicable target, execution,
distribution, table-read, warning, operator, and artifact fields, but no
baseline/candidate equivalence fields.

Full run arrays, complete operator arrays, raw SQL messages, plan XML, and
result-row values do not appear in compact stdout. Result-row values are never
written to comparison artifacts. Existing artifacts remain subject to the
current secrecy contract.

### Matrix orchestration

Only `compare` accepts:

```text
--matrix Name:type=value1,value2,value3
```

Version 1 accepts exactly one `--matrix`. Fixed values continue to use repeated
`--param`. The matrix parameter name must not also appear in `--param`, all
values must parse under the shared parameter specification, and the matrix must
contain at least two values. Empty values and duplicate values after typed
normalization are rejected.

All command input, SQL, target scope, fixed parameters, matrix values, and output
mode are validated before the first authentication attempt.

Matrix cells execute sequentially in user-supplied order. Each cell:

1. opens a new SQL Server connection;
2. verifies resolved and observed target identity;
3. executes setup exactly once;
4. executes baseline and candidate warm-ups;
5. executes alternating measured baseline/candidate repetitions;
6. evaluates technical equivalence using the selected mode;
7. writes full cell artifacts;
8. contributes one row to the matrix summary.

Setup, warm-ups, and measured repetitions within one cell use the same physical
connection. No temporary object or session state is shared between cells.

The first failed cell stops execution. The command identifies the failed matrix
value and returns the normal failure exit code. Artifacts already committed for
completed cells remain available, but stdout must not represent the partial
matrix as a successful complete report.

Matrix execution is not parallelized because concurrent workloads would distort
the measurements being compared.

## Execution semantics

For a non-matrix `measure` or `compare`, SQLHarness:

1. validates the operation, complete SQL input, target request, parameters,
   equivalence mode, and output mode;
2. resolves the target;
3. authenticates and opens one connection;
4. verifies target identity;
5. executes setup exactly once, if present;
6. executes warm-up queries;
7. executes all measured repetitions on the same connection;
8. writes complete artifacts;
9. renders human text, full JSON, or compact JSON.

Setup is not included as a measured repetition. A setup failure stops the
benchmark. SQLHarness reports the bounded, redacted runner error and does not
retry by creating any persistent object.

## Technical and domain equivalence

Technical equivalence is the selected automatic comparison over canonical SQL
results. It is necessary but not always sufficient.

Before accepting an optimization whose semantics depend on business rules, the
operator should also run a domain-equivalence batch tailored to the ticket. That
batch should normally:

- compare baseline-only and candidate-only rows in both directions;
- preserve duplicate semantics where duplicates matter;
- verify ordering ties when order or `TOP` affects the contract;
- cover missing-history and boundary-date behavior;
- report both directional difference counts.

For simple row sets, two directional `EXCEPT` checks may be appropriate. When
duplicates matter, grouped counts or another multiplicity-preserving check is
required because `EXCEPT` alone has set semantics.

The final optimization report records technical equivalence and domain
equivalence separately.

## Representative-sample procedure

Measurement begins only after read-only inventory identifies a representative
parameter range. The inventory should record:

- total relevant row count;
- distribution of batch or grouping cardinality;
- small, median, large, and exceptional parameter cases;
- frequency and shape of ordering ties;
- cases with missing or incomplete history;
- boundary dates and empty-result cases.

The selected sample and rationale belong to the ticket or benchmark workspace,
not to runtime parameter logs. Measurements should cover representative and
pathological cases rather than only one convenient identifier.

## Artifact and workspace guidance

Ticket-specific benchmark SQL should live outside the application repository,
for example in a ticket-owned `.sdd/sql-perf` directory, a ticket workspace, or
an isolated temporary directory. It must not be silently added to an application
checkout.

Showplans, report artifacts, and runtime parameter material remain locally
sensitive. SQLHarness continues to redact emitted errors and keeps secrets,
passwords, and tokens in process memory only.

## Error handling

- CLI validation and safety failures return exit code `2` before authentication.
- Authentication failures return `3`.
- resolved/observed target mismatches return `4`.
- SQL execution and timeout failures return `5`.
- artifact storage failures return `6`.
- Unsupported AST diagnostics remain bounded and name types only.
- A rejected setup stops execution; safety rejections are never retried through
  a weaker path.
- Summary rendering must not turn a failed operation into a successful result.
- A matrix failure identifies the cell by zero-based index and parameter name.
  It does not echo the failed value in an error message.

## Testing strategy

### Unit and module tests

Tests cover:

- accepted local `#temp` constraints and indexes;
- rejection of the same constructs on persistent and global temporary objects;
- safe window-function syntax and continued rejection of unsupported syntax;
- bounded unsupported statement and fragment diagnostics;
- each supported parameter type (including string sizes, small integers, float
  family, money, time/smalldatetime, numeric alias, varbinary Base64, GUID
  formats, and UDT hierarchyid/geography/geometry), invalid format, overflow,
  precision, scale, and culture independence;
- consistent binding of precision and scale into `SqlParameter`, `Size = -1` for
  max strings/binaries, and `UdtTypeName` for UDT parameters;
- typed and untyped null declarations (`name:null`, `name:type:null`);
- `--file` remaining authoritative when the process exposes redirected stdin;
- each comparison mode, duplicates, ordering-only changes, result-set
  boundaries, and directional counts;
- `off` returning nullable equivalence fields;
- mutual exclusion of `--json` and `--json-summary`;
- bounded summary fields and operator limits;
- matrix parsing, fixed-parameter conflicts, typed duplicates, and invalid
  values;
- one session per matrix value, one setup per session, sequential ordering,
  alternating variants, and stop-on-first-failure behavior.

### SQL Server integration test

At least one opt-in integration test runs against an isolated test SQL Server
and executes:

```sql
CREATE TABLE #Req (Id int NOT NULL PRIMARY KEY);
INSERT #Req VALUES (1);
```

Baseline and candidate both select from `#Req`. The test proves that setup,
warm-up, and measured runs share one physical session. A matrix integration test
or an extended scenario proves that a later cell receives a new session and
cannot observe temporary state from an earlier cell.

The integration harness must use an explicit test target and database. It does
not read user profiles and does not connect to unrelated local databases.

## Documentation

The following surfaces stay synchronized:

- CLI help;
- `README.md`;
- `AGENTS.md`;
- `skills/sqlharness/SKILL.md`.

They document setup frequency, session scope, supported parameter types, summary
output, equivalence modes, matrix limitations, representative inventory,
technical versus domain equivalence, stop behavior for rejected setup, and
off-repository handling of ticket SQL and sensitive artifacts.

## Acceptance criteria

Stage 1 is complete when:

- a real SQL Server integration test proves `#temp` setup visibility;
- supported local temporary constraints and indexes pass safety tests;
- equivalent persistent/global constructs remain denied;
- safe window syntax and bounded AST diagnostics work;
- all documented parameter types bind correctly and help is synchronized.

Stage 2 is complete when:

- `ordered` remains the default;
- all four modes produce the specified result and directional diagnostics;
- `--json` remains compatible;
- `--json-summary` is bounded, mutually exclusive with `--json`, and contains
  the specified metrics without full operator/run arrays or row values;
- technical and domain equivalence are documented separately.

Stage 3 is complete when:

- exactly one matrix dimension is accepted;
- each value uses a fresh session and one setup;
- cells run sequentially and stop on the first failure;
- the matrix output compares all completed successful cells compactly;
- no Cartesian-product or parallel-execution behavior is introduced.
