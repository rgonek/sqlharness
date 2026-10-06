# Plan 021: Unrecognized (non-English) STATISTICS output is reported as unavailable, never as measured zeros

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/Dialect/SqlServerDialect.cs src/SqlHarness.Core/Diagnostics.cs tests/SqlHarness.Tests/StatisticsParserTests.cs tests/SqlHarness.Tests/MeasureTests.cs tests/SqlHarness.Tests/CompareTests.cs README.md AGENTS.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1 (correctness of the product's core output)
- **Effort**: M
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md
- **Category**: bug
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

`measure`/`compare` on SQL Server derive CPU time, elapsed time and logical reads by regex-parsing the text of `SET STATISTICS IO/TIME` informational messages. The regexes match **only English** text. SQL Server localizes these messages when the login's default language is not English (for example Polish, German or French, where `sys.messages` has localized rows). In that case every regex misses, the parser returns `0`, and the run is reported as a **measured** `0 ms` / `0 reads`. Compare then shows two identical variants and agents draw false conclusions. Nothing warns the user.

The safe fix is detection, not translation. `SET STATISTICS TIME ON` always emits at least one execution-times message per executed statement, so a run with **zero** recognized TIME blocks means the output was not understood. Such runs must be marked `unavailable` with a clear warning, using the same mechanism the code already uses when messages are truncated. Forcing `SET LANGUAGE us_english` is **not** acceptable: it changes `DATEFORMAT`/`DATEFIRST` and therefore the semantics of the measured query.

## Current state

Files:
- `src/SqlHarness.Core/Diagnostics.cs:10-63`: `StatisticsIoParser` and `StatisticsTimeParser`.

  ```csharp
      private static readonly Regex ExecutionTime = new(
          @"SQL Server Execution Times:\s*CPU time\s*=\s*(?<cpu>\d+)\s*ms,\s*elapsed time\s*=\s*(?<elapsed>\d+)\s*ms\.",
          RegexOptions.Compiled | RegexOptions.CultureInvariant);

      internal static StatisticsTime Parse(string text)
      {
          long cpuTimeMs = 0;
          long elapsedTimeMs = 0;

          foreach (Match match in ExecutionTime.Matches(text))
          {
              cpuTimeMs += ...;
              elapsedTimeMs += ...;
          }

          return new StatisticsTime(cpuTimeMs, elapsedTimeMs);
      }
  ```

  `internal sealed record StatisticsTime(long CpuTimeMs, long ElapsedTimeMs);`
- `src/SqlHarness.Core/Dialect/SqlServerDialect.cs:60-95`: the measured run. After collecting messages:

  ```csharp
              var io = StatisticsIoParser.Parse(string.Join(Environment.NewLine, messages));
              var time = StatisticsTimeParser.Parse(string.Join(Environment.NewLine, messages));
              ...
              var artifact = new CompareRunArtifact(
                  variant,
                  repetition,
                  time.CpuTimeMs,
                  time.ElapsedTimeMs,
                  io.LogicalReads,
                  io.Tables,
                  result.Canonical.Hash,
                  result.PlanXmls,
                  messages.Length,
                  Metrics: TruncatedMetricsOrNull(consumed.OmittedMessageCount));
  ```
- The existing "unavailable" precedent, same file `:7-8` and `:145-164`:

  ```csharp
      private const string StatisticsTruncatedWarning =
          "SQL Server informational messages exceeded the per-command limit and {0} messages were omitted. CPU time, elapsed time and logicalReads parsed from STATISTICS output are unavailable; logicalReads 0 is not a measured zero.";
      ...
      private static BenchmarkRunMetrics? TruncatedMetricsOrNull(int omittedMessageCount) =>
          omittedMessageCount <= 0
              ? null
              : new BenchmarkRunMetrics(
                  BenchmarkMetricReport.Unavailable,
                  BenchmarkMetricReport.Unavailable,
                  null, false, null, null,
                  BenchmarkMetricReport.Unavailable,
                  null, null, null,
                  BenchmarkMetricReport.ResultStatement,
                  BenchmarkMetricText.StatementRows,
                  [string.Format(CultureInfo.InvariantCulture, StatisticsTruncatedWarning, omittedMessageCount)]);
  ```

  `BenchmarkRunMetrics` is defined at `src/SqlHarness.Core/Artifacts.cs:184-197`. Downstream summaries already honor `Unavailable` (that is how truncation is reported). Find the tests for the truncation path with `grep -rn "StatisticsTruncatedWarning\|exceeded the per-command limit" tests` and use them as the pattern.
- `tests/SqlHarness.Tests/StatisticsParserTests.cs`: 3 English-only tests.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Focused tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~StatisticsParser|FullyQualifiedName~Measure|FullyQualifiedName~Compare|FullyQualifiedName~BenchmarkSummary"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**:
- `src/SqlHarness.Core/Diagnostics.cs` (parser returns a recognized count)
- `src/SqlHarness.Core/Dialect/SqlServerDialect.cs` (choose unavailable metrics)
- `tests/SqlHarness.Tests/StatisticsParserTests.cs`, plus the test file that holds the truncation-path tests (found by grep above)
- `README.md` and `AGENTS.md`: one sentence each in the measure/compare notes

**Out of scope**:
- `SET LANGUAGE` or any change to the enable/disable batches (semantics risk, see "Why").
- Parsing localized message templates (brittle, unbounded).
- Deriving metrics from the actual plan XML (`QueryTimeStats` / `RunTimeCountersPerThread`). This is a possible follow-up (see Maintenance notes), not part of this plan.
- PostgreSQL (it reads EXPLAIN JSON and is language-independent).

## Git workflow

- Branch `fix/plan-021-unrecognized-statistics`. Commits `test(021): ...`, `fix(021): ...`, `docs(021): ...`. Do NOT push.

## Steps

### Step 1: Parser reports how many TIME blocks it recognized

Change `StatisticsTime` to `internal sealed record StatisticsTime(long CpuTimeMs, long ElapsedTimeMs, int RecognizedBlocks);` and set `RecognizedBlocks` to the match count. Update all callers. `grep -rn "StatisticsTime(" src tests` lists them. Existing callers that construct it in tests pass the count explicitly.

Add tests to `StatisticsParserTests.cs`:
- English input with two blocks → `RecognizedBlocks == 2` (extend the existing test).
- A German-style sample (write it literally in the test as a localized-looking fixture, e.g. `"SQL Server-Ausführungszeiten:\n   CPU-Zeit = 12 ms, verstrichene Zeit = 20 ms."`) → `RecognizedBlocks == 0`, `CpuTimeMs == 0`.
- Empty input → `RecognizedBlocks == 0`.

**Verify**: `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~StatisticsParser"` → all pass.

### Step 2: Unrecognized TIME output marks the run's metrics unavailable

In `SqlServerDialect.cs` add a second warning constant:

```csharp
    private const string StatisticsUnrecognizedWarning =
        "SQL Server STATISTICS TIME output was not recognized (the session language may not be English). CPU time, elapsed time and logicalReads parsed from STATISTICS output are unavailable; 0 is not a measured zero.";
```

Generalize `TruncatedMetricsOrNull(int omittedMessageCount)` into `UnavailableMetricsOrNull(int omittedMessageCount, int recognizedTimeBlocks)`:
- If `omittedMessageCount > 0` → the existing truncated metrics, unchanged text.
- Else if `recognizedTimeBlocks == 0` → the same `BenchmarkRunMetrics` shape with the new warning.
- Else → `null`.

Pass `time.RecognizedBlocks` at the call site.

**Edge case to reason about before coding.** A measured batch that executes zero statements cannot happen, because measured SQL must contain a statement (the classifier denies empty batches). If any existing test feeds the dialect run a **fake session whose messages contain no TIME text** and asserts measured zeros, that test now gets "unavailable". Inspect each such failure. If the test models a real SQL Server run, add a valid English TIME line to its fake messages, since a real server always sends one. If it asserts that zeros are measured on purpose, STOP and report.

**Verify**: focused tests (table above) all pass. Add one new test next to the truncation-path test: a fake session returning only `"Tabelle 'X'. Scananzahl 1, logische Lesevorgänge 5"` and a localized TIME line. Expect the run metrics to have `CpuTimeAvailability == BenchmarkMetricReport.Unavailable`, `LogicalReadsAvailability == Unavailable`, and a warning containing `"was not recognized"`.

### Step 3: Make it visible in summaries

Run one compare through the existing CLI test harness (pattern: the truncation test or `CompareTests`) with the localized fake messages and `--json-summary`. Assert the summary marks CPU/reads unavailable and surfaces the warning. If the summary projection drops run warnings entirely, assert on the full `--json` report instead and note this in the index row. Do **not** change the projection in this plan.

**Verify**: focused tests pass.

### Step 4: Document

- `README.md`, measure/compare section: "On SQL Server, CPU, elapsed and logical reads come from English `STATISTICS IO/TIME` messages. If the login's language is not English, those metrics are reported as `unavailable` with a warning; set the login's default language to English (`ALTER LOGIN ... WITH DEFAULT_LANGUAGE = us_english`) to get them." The ALTER LOGIN is advice for the user, never something the harness runs.
- `AGENTS.md`, under "Technical equivalence and summaries" or the benchmark notes: one line saying `unavailable` metrics mean the STATISTICS text was not recognized.

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

- Parser: 3 new or extended tests (Step 1).
- Dialect run: 1 new localized-messages test (Step 2), modelled on the truncation test.
- Summary/report: 1 assertion (Step 3).

## Done criteria

- [ ] Focused tests and the full gate pass
- [ ] `grep -n "StatisticsUnrecognizedWarning" src/SqlHarness.Core/Dialect/SqlServerDialect.cs` → definition and use
- [ ] `grep -n "RecognizedBlocks" src/SqlHarness.Core/Diagnostics.cs` → match
- [ ] Only in-scope files changed; `plans/README.md` row updated

## STOP conditions

- An existing test asserts measured zeros from a message stream with no TIME block **as intended behavior** (Step 2 edge case).
- `BenchmarkMetricReport.Unavailable` for CPU breaks a downstream consumer that assumes SQL Server CPU is always measured (for example the regression design in `plans/013-regression-contract.md` or `BenchmarkSummary`). Report it rather than special-casing.
- You find the enable batch is executed by a different code path for `measure --param-set` or `compare --matrix` that does not go through `SqlServerDialect`'s run method (`grep -rn "STATISTICS TIME ON" src`). Report it; all paths must get the same treatment.

## Maintenance notes

- Follow-up (separate plan, needs a live SQL Server): derive CPU/elapsed from `QueryPlan/QueryTimeStats` and reads from `RunTimeCountersPerThread/@ActualLogicalReads` in the STATISTICS XML plans already captured. These are language-independent, but their semantics differ from STATISTICS IO, so they must be labelled with a distinct `LogicalReadsSource`.
- A related investigate item, not part of this plan: `StatisticsTimeParser` **sums** every block. For parameterized runs (`sp_executesql`) the server may emit an inner per-statement block plus an outer cumulative block, roughly doubling CPU/elapsed. Verify on a live server before changing anything.
