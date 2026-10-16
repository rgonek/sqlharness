# Plan 080 verification evidence

Evidence note for plan 080 ([080-windows-gate-speedup.md](080-windows-gate-speedup.md)), appended per task. Each section
records the exact command, the machine it ran on, and the numbers it produced, so a later task can extend it without
re-running an earlier measurement. Dates are local dates on the machine named in the section.

## Task 2 — bulk journal seeding in the reader fixtures (2026-10-16)

**Change:** `JournalSeed.Operations` and `JournalSeed.Compares` seed the journal through one pooled connection and one
transaction instead of paying `ActivityJournal`'s per-call non-pooled connection and commit
(`src/SqlHarness.Core/Journal/ActivityJournal.cs:641-654`). Only the two reader tests that needed volume switched to them;
both kept every assertion they had. No `src/` file changed.

**Command (identical before and after):**

```bash
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build \
  --filter 'FullyQualifiedName~JournalReaderTests' \
  --logger "trx;LogFileName=jr.trx" --results-directory <dir>
```

Class total is the sum of `UnitTestResult/@duration` over the 64 results in the resulting `jr.trx`.

| | tests | failed | `JournalReaderTests` class total |
|---|---|---|---|
| before (`trx-seed-before`, at `85d7062`) | 64 | 0 | **18,35 s** |
| after (`trx-seed`, Task 2 working tree) | 64 | 0 | **8,09 s** |

The two tests the task targeted, from the same two trx files:

| test | before | after |
|---|---|---|
| `Starred_list_pages_with_the_id_cursor_without_gaps` | 8,32 s | 0,23 s |
| `Compare_pages_have_no_gaps_and_versions_use_all_retained_history_before_paging` | 2,34 s | 0,13 s |

Those two tests were 10,66 s of the 18,35 s class total before and 0,36 s of 8,09 s after. The remaining class time is
per-test reader-construction cost that Task 2 does not touch: the slowest survivor,
`Dimension_stats_are_complete_filtered_and_keep_missing_values_separate_from_unknown_text`, costs 0,88 s both before and
after, and the next five cost 0,16–0,69 s each in both runs.

**Machine caveat.** The plan header records a 52,9 s `JournalReaderTests` class total and 23,2 s for the starred test
alone. That measurement was taken on a different (slower, contended) box. The table above is one machine before and
after on 2026-10-16, so only the ratio is meaningful: **the class total fell 56 %, and the two targeted tests fell
97 %.** The 52,9 s figure was not re-measured here and is not claimed to be reproduced.

**Whole-gate check.** `just verify` exited 0 on this working tree: `verify: OK  stages=6  9m45s`, with
`SqlHarness.Tests` 4,006 passed / 11 skipped and `SqlHarness.Mcp.Tests` 402 passed / 4 skipped. The box was heavily
contended during that run — `ui-check` alone took 6m14s against the 36,9 s quiet baseline recorded in the plan header —
so this run's stage totals are not comparable to the recorded 2m25s gate baseline. The class-level trx numbers above are
the measurement this task claims; the gate result only shows nothing else in the suite regressed.