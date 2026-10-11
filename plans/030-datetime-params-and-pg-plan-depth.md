# Plan 030: Date/time parameters never shift silently or fail late, and deep PostgreSQL plans parse

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/Postgres/PostgresParameters.cs src/SqlHarness.Core/Postgres/PostgresBenchmark.cs src/SqlHarness.Core/Diagnostics.cs src/SqlHarness.Core/PlanIdentity.cs tests/SqlHarness.Tests/Postgres/ tests/SqlHarness.Tests/SqlParameterParserTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding. Adapt paths,
> symbols and obsolete test expectations within the authorized intent and scope;
> record the adjustment. Stop only for a substantive incompatible contract,
> an unapproved safety change or work outside scope.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW–MED (Step 1 tightens an input contract)
- **Depends on**: plans/016-restore-green-ci.md. Coordinate with plan 022, which changes the Npgsql version; run this plan's PG tests after 022 if both land.
- **Category**: bug
- **Planned at**: commit `5280f78`, 2026-10-03; revised against `729d88f`, 2026-10-11

## Why this matters

1. **Silent clock shift.** `--param d:datetime2=2026-07-29T12:00:00+02:00` is parsed with `DateTimeStyles.RoundtripKind`, which converts an offset-bearing string to a `DateTimeKind.Local` value in the **harness machine's time zone**. The query then receives a different wall-clock time depending on where SQLHarness runs. A `Z` suffix gives `Kind=Utc`. Neither SQL Server `datetime2` nor PostgreSQL `timestamp` has an offset, so accepting one is ambiguous.
2. **Late failure on PostgreSQL.** Npgsql (6+) refuses a `DateTime` with `Kind=Utc` for `timestamp` and a `DateTimeOffset` with a non-zero offset for `timestamptz`. Today those values reach the server path, after connect and setup, and fail with exit 5 (SQL execution) instead of exit 2 (validation).
3. **Deep plans fail after measuring.** `EXPLAIN (FORMAT JSON)` nests two JSON levels per plan node. Two parsers use the default `JsonDocument` `MaxDepth` of 64, so a plan deeper than about 31 nodes (wide joins, nested views) throws **after** the query already ran. Other parsers in the repo already raise the limit (`PlanIdentity.cs:105` uses 128; `PostgresPlanDistiller.cs:30` uses its configured limit + 16).

## Current state

- `src/SqlHarness.Core/SqlSafety.cs` (`SqlParameterParser`, around `:1140-1150`):

  ```csharp
              "datetime" => CreateDateTime(name, value),
              "datetime2" => new($"@{name}", SqlDbType.DateTime2, DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), null),
              "smalldatetime" => CreateSmallDateTime(name, value),
              "datetimeoffset" => CreateDateTimeOffset(name, value),
  ```

  Read `CreateDateTime` and `CreateSmallDateTime` to see which `DateTimeStyles` they use. `CreateDateTimeOffset` requires an explicit offset (~`:1456-1464`). Parameter-value errors are thrown as `SqlHarnessSafetyException` with `IsParameterValue` semantics. Follow the existing invalid-value pattern in this file and mirror the existing invalid-datetime classification. Caller-provided date/time text may appear in bounded diagnostics; protect credentials through the shared secret filter rather than hiding all values.
- `src/SqlHarness.Core/Postgres/PostgresParameters.cs:80-104` (`Map`):

  ```csharp
              SqlDbType.DateTime or SqlDbType.DateTime2 or SqlDbType.SmallDateTime => NpgsqlDbType.Timestamp,
              SqlDbType.DateTimeOffset => NpgsqlDbType.TimestampTz,
          ...
          if (value is DBNull)
              return (dbType, DBNull.Value);

          if (parameter.Type == SqlDbType.TinyInt)
              value = Convert.ToInt16(value, CultureInfo.InvariantCulture);

          return (dbType, value);
  ```
- JSON parsing sites with the default depth: `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs:34` (`ParseStats`: `JsonDocument.Parse(json)`) and `src/SqlHarness.Core/Diagnostics.cs:115` (`ExecutionPlanParser.ParseJson`: `JsonDocument.Parse(json)`).
- Tests: `tests/SqlHarness.Tests/Postgres/PostgresParameterTests.cs:70-90` (`Bind_maps_types_onto_npgsql_command`: uses `d:datetime2=2026-07-29T12:00:00`, no offset, and `at:datetimeoffset=2026-07-29T12:00:00Z`), and `tests/SqlHarness.Tests/SqlParameterParserTests.cs`.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Find offset uses in tests | `grep -rnE "(datetime2?|smalldatetime)[^=]*=[0-9T:-]+(Z|[+-][0-9]{2}:[0-9]{2})" tests` | inventory |
| Focused tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Parameter|FullyQualifiedName~Postgres|FullyQualifiedName~PlanParser|FullyQualifiedName~Benchmark"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `SqlSafety.cs` (the `datetime`/`datetime2`/`smalldatetime` parsing only), `PostgresParameters.cs`, `PostgresBenchmark.cs` (`ParseStats` options), `Diagnostics.cs` (`ParseJson` options), the related test files, and README/AGENTS parameter notes (one sentence).

**Out of scope**: `time`/`date` parsing, `datetimeoffset` input rules, and the PostgreSQL plan distiller (already bounded).

## Git workflow

Branch `fix/plan-030-datetime-depth`. Commits per step. Do NOT push.

## Steps

### Execution phases — user priority update, 2026-10-11

- **A, first: shared date/time parameter correctness.** Step 1 and the matching Step 4 documentation/tests. Shared parser changes apply to both engines and must not break existing PG behavior; the required platform gates remain Windows and Linux.
- **B, deferred: PostgreSQL binding and deep plans.** Steps 2 and 3, their focused tests and remaining Step 4 documentation. A's completion does not imply these improvements are shipped.
- Stage A can run now that 026 is DONE. Sequence it with 045 because both edit SqlSafety.cs. After A, record PARTIAL with scope and gate evidence; mark the whole plan DONE only when B is complete too. Focused checks must run the intended cases, not pass with zero selected tests.

### Step 1: Offset-less types reject offsets (validation, exit 2)

Inventory existing offset-acceptance tests and explain the old behavior they pin. Apply the intended semantics below and update those expectations with regression evidence for the silent clock shift. The number of tests is not an approval threshold. Stop only for evidence of a separate supported public contract whose migration exceeds this scope.

Change `datetime2` parsing to `DateTimeStyles.None` (and the same for `CreateDateTime`/`CreateSmallDateTime` if they use `RoundtripKind`/`AssumeLocal`/`AdjustToUniversal`). Explicitly reject input whose parsed `Kind != DateTimeKind.Unspecified`, or whose text contains a `Z`/offset suffix (check with a regex on the trimmed value: `(Z|[+-]\d{2}:\d{2})$`). Use the file's existing invalid-parameter-value error, with a message such as "Offset-less date/time types do not accept a time-zone offset; use datetimeoffset." A bounded diagnostic may include caller-supplied date/time text; credentials must still be protected.

Tests (in `SqlParameterParserTests.cs`, following an existing invalid-value test):
- `d:datetime2=2026-07-29T12:00:00` → `Kind == Unspecified`, 12:00.
- `d:datetime2=2026-07-29T12:00:00Z` → rejected; message explains the offset conflict and recommends `datetimeoffset`.
- `d:datetime2=2026-07-29T12:00:00+02:00` → rejected.
- Same two rejections for `datetime` and `smalldatetime`.

**Verify**: focused tests pass.

### Step 2: PostgreSQL normalizes `DateTimeOffset` to UTC

In `PostgresParameters.Map`, after the DBNull check:

```csharp
        // 030: timestamptz carries an instant; Npgsql accepts only offset-zero values.
        if (value is DateTimeOffset offset)
            value = offset.ToUniversalTime();
```

Test in `PostgresParameterTests.cs`: `at:datetimeoffset=2026-07-29T12:00:00+02:00` binds as `DateTimeOffset` with `Offset == TimeSpan.Zero` and `UtcDateTime == 2026-07-29T10:00:00`.

**Verify**: Postgres parameter tests pass.

### Step 3: One generous JSON depth for PostgreSQL plan JSON

Add `internal static readonly JsonDocumentOptions PlanJsonOptions = new() { MaxDepth = 256 };` in a sensible shared place. `Postgres/PostgresBenchmark.cs` works, or a small `PostgresPlanJson` static class. Use it in `PostgresBenchmark.ParseStats` and `Diagnostics.ExecutionPlanParser.ParseJson`.

Test: build a synthetic EXPLAIN JSON with 60 nested `Plans` levels (a loop that wraps `{"Node Type":"Seq Scan"}` in `{"Node Type":"Nested Loop","Plans":[ ... ]}`), wrapped as `[{"Plan": ..., "Planning Time": 1.0, "Execution Time": 2.0}]`. Assert that `ParseStats` returns `Execution Time` 2.0 and that `ExecutionPlanParser` (via its public/internal entry; find the call in `PostgresBenchmark.cs`) returns 61 operators. Put the test in the existing Postgres benchmark test file (`grep -ln "ParseStats" tests`).

**Verify**: focused tests pass, then `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

### Step 4: Docs

AGENTS.md and README parameter paragraphs: "`datetime`, `datetime2` and `smalldatetime` take a local ISO 8601 value without offset; use `datetimeoffset` for instants (bound as UTC on PostgreSQL)."

## Test plan

Six parser tests, one PG binding test and one deep-plan test, following the patterns named in each step.

## Done criteria

- [ ] Focused tests and the full gate pass
- [ ] `grep -n "DateTimeStyles.RoundtripKind" src/SqlHarness.Core/SqlSafety.cs` → no match on the datetime2 line (other uses, if any, are justified in a comment)
- [ ] `grep -n "PlanJsonOptions" src/SqlHarness.Core` → defined and used twice
- [ ] `plans/README.md` row updated

## STOP conditions

- Evidence establishes a supported offset-conversion contract requiring a migration outside scope; report the evidence and a concrete migration proposal.
- The deep-plan test shows another failure after the depth fix (e.g. a recursive operator walker overflows the stack). Report it; do not rewrite the walker here.

## Maintenance notes

- If users ask to pass UTC instants into `timestamp` columns, the answer is `datetimeoffset` (PG `timestamptz`) or an explicit `AT TIME ZONE` in SQL. Do not reintroduce silent conversion.
- Any new JSON parse of server output must use bounded but generous options. Reviewers should grep for bare `JsonDocument.Parse(`.
