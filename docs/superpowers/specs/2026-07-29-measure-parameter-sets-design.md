# Measure Parameter Sets Design

**Date:** 2026-07-29
**Status:** Draft for review

## Purpose

SQLHarness should measure one query across several named, typed parameter sets
in one session so agents can expose parameter-sensitive performance without
copying values into argv, reports, or artifacts.

The feature extends `measure`; it does not replace `compare --matrix`.
`compare --matrix` varies one explicit dimension to compare two query variants.
`measure --param-set` measures one query for several multi-parameter scenarios.

## Command

```text
sqlharness measure <profile> --query <file>
    [--setup <file>]
    [--param name[:type]=value ...]
    --param-set <file.sqljson>
    --param-set <file.sqljson>
    [--repeat <count>=5]
    [--timeout <seconds>=30]
    [--json]
```

Multi-set mode requires at least two `--param-set` files. Existing single-set
`measure` behavior remains unchanged when none are supplied. Supplying exactly
one set fails validation and directs the user to ordinary `--param`.

## Parameter-set format

Each UTF-8 JSON file has exactly this version-1 shape:

```json
{
  "name": "large-customer",
  "parameters": [
    "customerId:int=42",
    "asOf:date=2026-06-30"
  ]
}
```

Unknown properties, duplicate properties, a byte-order mark, trailing JSON
content, comments, and trailing commas are rejected. Maximum file size is
64 KiB. `name` must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` and be unique
case-insensitively across the invocation.

`parameters` contains one or more strings accepted by the shared
`SqlParameterParser`. Parameter names are unique case-insensitively within a
set. Every set must define the same parameter names and SQL types, although
values may differ.

Repeated `--param` values are fixed parameters shared by all sets. A fixed
parameter name must not occur in any set. The complete fixed-plus-set parameter
collection must match query/setup references under the existing reference
validator.

Files, names, JSON shape, parameter parsing, cross-set shape, conflicts,
references, repeat, timeout, target request, and output mode are validated
before authentication.

## Session and execution semantics

One invocation opens one physical SQL session:

1. validate all local input;
2. resolve and authenticate the target;
3. verify observed target identity;
4. execute setup exactly once;
5. run one unmeasured warm-up for each set in user-supplied order;
6. execute measured rounds;
7. write artifacts and render the report.

Within measured round `r`, set execution starts at
`r modulo setCount` and wraps around. For three sets:

```text
round 1: B, C, A
round 2: C, A, B
round 3: A, B, C
```

The implementation uses one-based round numbers and the zero-based start index
`round % setCount`, preserving this exact sequence. Every set therefore runs
once per round and exactly `repeat` times.

Setup, warm-ups, and measured runs share the same session. A failure stops the
operation; the report is not emitted as a successful partial result.

## Plan-cache boundary

SQLHarness does not clear, free, recompile, pin, or otherwise manipulate the
SQL Server plan cache. A new client session does not imply a fresh plan cache.

Every multi-set report includes this warning:

```text
Parameter-set measurements use the observed server plan-cache state; SQLHarness did not clear or isolate the plan cache.
```

The user-supplied set order is preserved because the first warm-up may influence
compilation. The report records warm-up order and measured rotation semantics.

## Result semantics

Results are compared only among measured repetitions of the same named set,
using the existing canonical ordered-result hash. `resultsStable` is calculated
per set.

Results from different sets are never compared for equivalence because
different parameter values commonly produce different correct rows. There is
no invocation-wide result-equivalence field.

Warm-up results do not contribute to stability.

## Metrics and plan signals

Each set reports:

- name;
- typed parameter metadata: names and SQL types only;
- SHA-256 typed-value hash;
- repetition count;
- result stability and result hash when stable;
- min/median/max elapsed milliseconds;
- min/median/max CPU milliseconds;
- min/median/max logical reads;
- total logical reads by table;
- warnings, spills, and implicit-conversion indicators;
- distinct plan hashes;
- artifact plan references.

The typed-value hash canonicalizes parameters by lower-case name, SQL type,
size/precision/scale metadata, null marker, and invariant typed value. Entries
sort by name before hashing. The hash is uppercase hexadecimal without `0x`.
It is an opaque correlation identifier, not a security boundary.

The invocation summary reports the minimum and maximum set medians for elapsed,
CPU, and logical reads plus the names of the sets at those extrema. It does not
label a difference as a failure or regression.

## Privacy and artifacts

Stdout, safe errors, report JSON, `runs.jsonl`, plan filenames, plan metadata,
and gain records contain no parameter values or original parameter strings.
They contain set names, parameter names/types, and typed-value hashes.

Parameter-set source files are locally sensitive and remain the only input
artifacts containing their values. SQLHarness reads but never copies them into
its own reports or metadata.

Existing `.sqlplan` artifacts remain locally sensitive because SQL Server plan
XML may independently contain parameter information. The command cannot
guarantee that server-produced plan XML omits values; documentation retains the
existing warning not to publish plans without review.

Artifact layout remains under the existing comparison/measure artifact root:

```text
<measure-artifact>/
    report.json
    runs.jsonl
    plans/
```

Each run record and plan filename uses the sanitized set name and repetition.
No source file path is persisted.

## Public report

The multi-set report contains:

```text
target
repeat
measuredRunCount
setupExecutionCount
warmupOrder[]
measuredOrderRule
planCacheWarning
sets[]
crossSetSummary
artifactDirectory
```

`setupExecutionCount` is `0` or `1`. `measuredRunCount` is
`repeat * setCount`.

The report is a distinct `SqlHarnessMeasureSetReport`; it does not overload
fields on the existing single-set `SqlHarnessMeasureReport`.

## Data flow

1. Read every set file with a 64 KiB bound.
2. Parse strict JSON and validate names.
3. Parse fixed and per-set typed parameters.
4. Validate identical per-set parameter shape and fixed conflicts.
5. Validate SQL parameter references for every effective set.
6. Resolve target, authenticate, and verify identity.
7. Execute setup once and warm each set once.
8. Execute rotated measured rounds on the same session.
9. Group runs by set and compute per-set distributions/stability/plans.
10. Compute cross-set median ranges without equivalence claims.
11. Write existing-style artifacts without input values or paths.
12. Render text/JSON and complete deferred `measure` gain accounting.

## Error handling

- Invalid file, JSON, set shape, parameter, conflict, reference, bound, or
  target request returns exit `2` before authentication.
- Authentication failure returns `3`.
- Target mismatch returns `4`.
- Setup, warm-up, measured SQL, timeout, malformed result, or server-plan
  failure returns `5`.
- Artifact or gain persistence failure returns `6`.
- Errors name the safe set label and zero-based set index when useful, but
  never the source path, parameter values, or original parameter string.
- Caller cancellation remains cancellation and is not converted to a report.

## Testing strategy

### File and CLI tests

- strict JSON and 64 KiB bound;
- valid name and duplicate-name rejection;
- at least two files;
- identical parameter names/types across sets;
- fixed/set conflicts;
- shared parser support for every parameter type;
- file precedence and safe IO failures;
- complete pre-auth validation;
- no parameter value or path in errors.

### Orchestration tests

- one connection and one verified target;
- setup zero/one time;
- warm-ups in user order;
- exact three-set rotation sequence;
- every set exactly once per round;
- stop on setup, warm-up, or measured failure;
- no successful partial report;
- no cache-control SQL.

### Result and metric tests

- stability within each set;
- no cross-set equivalence;
- warm-up excluded;
- per-set distributions and table reads;
- warnings/spills/conversions;
- distinct plan hashes;
- exact typed-value hashing across culture and input order;
- cross-set min/max medians and deterministic ties.

### Privacy and artifact tests

- no values, parameter strings, or source paths in reports/runs/filenames;
- set labels and typed metadata present;
- typed-value hash present;
- plan XML warning retained;
- server-produced plan XML stored unchanged;
- artifact failure cleanup and exit `6`.

### Regression tests

- existing no-set `measure` contracts and output remain unchanged;
- existing `compare --matrix` remains independent;
- existing artifact and gain logs remain readable;
- CLI help clearly separates `--param`, `--param-set`, and `compare --matrix`.

## Documentation

CLI help, README, `AGENTS.md`, and `skills/sqlharness/SKILL.md` document:

- strict file format and local sensitivity;
- fixed versus per-set parameters;
- one session, setup once, and rotated execution;
- per-set stability only;
- plan-cache warning and order dependence;
- no values copied to reports;
- `.sqlplan` sensitivity;
- distinction from `compare --matrix`.

## Acceptance criteria

- At least two multi-parameter sets run in one verified session.
- Setup runs once, warm-ups follow user order, and measured order rotates.
- Every set has independent stability, metrics, plan hashes, and artifacts.
- No cross-set result-equivalence claim is made.
- Parameter values and source paths never enter reports, errors, run metadata,
  filenames, or gain records.
- SQLHarness executes no plan-cache control statement.
- Existing single-set `measure` and `compare --matrix` remain compatible.
- Failure mapping, documentation, gain accounting, and full tests satisfy the
  stated contracts.
