# Task 2 report — seed bulk journal rows without the per-call writer cost

**Status:** DONE_WITH_CONCERNS
**Commit:** `4bdb73578960f381e5d8b09e2031ba0df086b4ed` — `test: seed reader fixtures in one transaction`
**Branch:** `opt-windows-gate`, parent `85d7062` (Task 1). Working tree clean after the commit.
**No `src/` file changed.** Nothing in this task needed a production change: the cost removed was purely
`ActivityJournal`'s per-call non-pooled connection and commit, paid by a fixture.

---

## What I added and why

Three additions to `tests/SqlHarness.Tests/Dashboard/JournalSeed.cs`, plus the two seeding lines in
`tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`.

### 1. `public static void Operations(string databasePath, int count, Action<int, long>? onSession = null)`

Exactly the signature the brief's Interfaces block specifies. One pooled `SqliteConnection`, one
`BeginTransaction`, one `Commit`; the loop reuses two prepared commands (a session insert with
`RETURNING id`, and an operation insert) with their parameters rebound per row — no string-concatenated SQL.
Inserts `count` sessions keyed `cli:000` … `cli:{count-1:D3}`, each with one completed `query` operation,
`last_seen = HostStarted.AddSeconds(index)` so ordering by time matches ordering by id.

### 2. `public static void Compares(string databasePath, string sessionKey, int count)`

A sibling helper for the compare-paging test, same pooled single-transaction shape. See "Deviations" below for
why the brief's Step 3 could not be applied literally.

### 3. `private static void EnsureSchema(string)`, `SessionInsert(...)`, `Insert(...)`, `Timestamp(...)`

`EnsureSchema` opens the journal once with a fixed time provider so a bulk helper can seed a fixture on its own
(`ActivityJournal.Initialize` is idempotent and disposes its migration connection). `SessionInsert` builds the one
prepared session insert both helpers share, modelled on the writer's own column set
(`ActivityJournal.UpsertSession`, `src/SqlHarness.Core/Journal/ActivityJournal.cs:670-681`). It is **not**
row-for-row fidelity — see "Known deliberate gap" under "Schema columns I inserted". `Timestamp` writes the
journal's format `yyyy-MM-dd'T'HH:mm:ss.fff'Z'` with `InvariantCulture`, because `JournalReader` orders these
strings (`ORDER BY started_at, id`) and `CompareHistoryBuilder.Build` sorts them
(`src/SqlHarness.Dashboard/CompareHistoryBuilder.cs:33`).

### Style

Followed `Annotate`'s established pattern: a raw pooled `SqliteConnection`, a single statement per row, and a
comment recording the measured saving (`8.3 s for the 240 sessions of the starred cursor test`, about 35 ms per
seeded operation on this machine).

## Schema columns I inserted

Column names were read from `src/SqlHarness.Core/Journal/JournalSchema.cs` (`CurrentVersion = 13`), and the set
was chosen from the columns the two reader tests actually select.

`sessions` (both helpers, via `SessionInsert`):

| column | value |
|---|---|
| `session_key` | `cli:{index:D3}` / the given key |
| `agent_kind` | `claude` (from `JournalSeed.Session`) |
| `transport` | `cli` (from `JournalTransport.Cli`) |
| `source` | `process-tree` |
| `host_pid` | `5151` |
| `host_started_at` | `HostStarted` |
| `cwd` | `/work` |
| `first_seen`, `last_seen` | `HostStarted.AddSeconds(index)` |

`client_name`, `client_version`, `client_title`, `mcp_mode`, `roots_json` and `protocol_version` are deliberately
not inserted: `JournalSeed.Session` leaves them null and the writer stores null for them too, so omitting them
produces the same row the writer writes. `JournalReader.SessionColumns`
(`src/SqlHarness.Dashboard/JournalReader.cs:54-71`) selects them and `ReadSessionRow` reads them as nullable.

**Known deliberate gap — the helper is not row-for-row fidelity.** `agent_pid` and `agent_started_at` are also not
inserted, and for those two columns the difference from a writer-seeded row is real rather than cosmetic:
`JournalSeed.Session` passes `4242` and `HostStarted.AddMinutes(-5)` into `SessionIdentity.AgentPid` /
`AgentStartedAt` (`tests/SqlHarness.Tests/Dashboard/JournalSeed.cs:20-21`), and `UpsertSession` writes both
(`src/SqlHarness.Core/Journal/ActivityJournal.cs:670-673`, `691-692`). A bulk-seeded session row therefore carries
`agent_pid = NULL` and `agent_started_at = NULL` where a writer-seeded row carries `4242` and
`2026-10-07T07:55:00.000Z`. Nothing observes the difference today: neither reader test selects those columns,
`SessionColumns` does not include them, and `JournalReader.cs` selects them nowhere. Any future test that starts
reading `agent_pid` or `agent_started_at` must add both columns to `JournalSeed.SessionInsert` first, or it will
read null and fail against expectations taken from a writer-seeded fixture.

`operations` — `Operations` helper (`operation` = `'query'`):

`session_id, operation, host_pid, host_started_at, started_at, updated_at, finished_at, status, exit_code,
duration_ms, profile, engine, server, database, result_sets, rows_returned, sql_hash`

with `status` = `'succeeded'`, `exit_code` = `0`, `duration_ms` = `10`, `profile` = `'local'`,
`engine`/`server`/`database` = `'sqlserver'`/`'srv'`/`'db'`, `result_sets` = `1`, `rows_returned` = `3`,
`sql_hash` = `OperationJournalDescriber.SqlHash("SELECT 1")` — the same constants `JournalSeed.Operation` passes
by default.

`operations` — `Compares` helper (`operation` = `'compare'`): the same list plus
`compare_subject` (`'orders'`), `compare_scope` (`'whole'`), `candidate_sql_hash` (`candidate-{index}`), with
`sql_hash` = `'base'`.

The union is exactly what the two tests read: `SessionColumns`, `OperationColumns`
(`JournalReader.cs:35-52`), `CompareHistoryRows` (`:291-304`, reads `operation`, `compare_subject`,
`compare_scope`, `sql_hash`, `candidate_sql_hash`, `status`, `started_at`, `host_pid`, `host_started_at`), and
`HydrateCompareEvidence` (`:320-328`, reads `summary_json` and `compare_conditions_json`, both null here).
Everything else keeps its schema default or stays null — `mutation_requested` and `dry_run` default to 0, which
is what the writer stores for a non-mutation, non-dry-run operation. Columns outside this union are the helper's
known gap: see "Known deliberate gap" above for the two that differ from writer-seeded rows.

## Commands and output

### Step 4 — correctness before timing

```
> dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~JournalReaderTests'
Passed!  - Failed: 0, Passed: 64, Skipped: 0, Total: 64, Duration: 8 s - SqlHarness.Tests.dll (net10.0)
```

64 passed, 0 failed — the brief's expected count, unchanged from before the change. No assertion was touched:
the `git diff` on `JournalReaderTests.cs` is 15 lines, all of them the seeding. The version assertions
(`"v0"` / `$"v{attempts}"`), `Assert.Equal(attempts, allIds.Count)`, `Assert.Equal(120, seen.Count)` and the
distinctness check all still pass against the bulk rows, which is the failure signal the brief asked for. Re-run
after the commit: same 64/64.

### Step 5 — before/after class total

```
> dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~JournalReaderTests' --logger "trx;LogFileName=jr.trx" --results-directory D:/temp/opencode/trx-seed-before
Passed!  - Failed: 0, Passed: 64, Skipped: 0, Total: 64, Duration: 18 s
> dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~JournalReaderTests' --logger "trx;LogFileName=jr.trx" --results-directory D:/temp/opencode/trx-seed
Passed!  - Failed: 0, Passed: 64, Skipped: 0, Total: 64, Duration: 8 s
```

Sum of `UnitTestResult/@duration` over the 64 results in each `jr.trx`:

| | class total | `Starred_list_pages_...` | `Compare_pages_have_no_gaps_...` |
|---|---|---|---|
| before (at `85d7062`) | **18,35 s** | 8,32 s | 2,34 s |
| after | **8,09 s** | 0,23 s | 0,13 s |

Both numbers are recorded in `plans/080-verification-evidence.md` (Step 8) with the command and the date
(2026-10-16). The two targeted tests were 10,66 s of 18,35 s and are now 0,36 s of 8,09 s; the remaining
8,09 s is per-test reader-construction cost Task 2 does not touch (the slowest survivor costs 0,88 s in both
runs).

### Step 6 — no writer test uses the bulk helper

```
> Select-String -Path tests/SqlHarness.Tests/**/*.cs -Pattern 'JournalSeed\.Operations'
tests\SqlHarness.Tests\Dashboard\JournalReaderTests.cs:1367: JournalSeed.Operations(home.DatabasePath, 240, (index, id) =>

> Get-ChildItem -Path tests -Recurse -Filter *.cs | Select-String -Pattern 'JournalSeed\.(Operations|Compares)'
tests\SqlHarness.Tests\Dashboard\JournalReaderTests.cs:135:  JournalSeed.Compares(home.DatabasePath, "cli:compare-history", attempts);
tests\SqlHarness.Tests\Dashboard\JournalReaderTests.cs:1367: JournalSeed.Operations(home.DatabasePath, 240, (index, id) =>
```

Both call sites are in the reader test file. Neither test asserts what `ActivityJournal` wrote: the starred test
only asserts the id-cursor page walk, and the compare test only asserts what `JournalReader.CompareAttempts`,
`CompareHistoryBuilder` and `Session(...).CompareSubjects` compute from the stored rows. Writer coverage is
untouched elsewhere: `seed.Operation(` still appears 131 times across the suite and `seed.Compare(` 14 times
(12 in `JournalReaderTests.cs`, 2 in `DashboardServerTests.cs`).

### Step 7 — format and build

```
> pwsh ./scripts/format-changed.ps1 -Fix
format: 2 changed C# file(s) (working-tree)
format: OK
> dotnet build SqlHarness.sln --no-restore -warnaserror
Build succeeded.  0 Warning(s)  0 Error(s)
```

`insert_final_newline = false` honoured: both touched files still end `0x0A, 0x7D` (`\n}`) — no trailing
newline added or removed. `git diff --check` is clean.

### Whole gate (not required by the brief, run because `JournalSeed` is shared)

```
> just verify
==> ui-install skipped (lockfile unchanged)
==> ui-check ok 6m14s
==> restore ok 5,2s
==> build ok 13,9s
==> test ok 2m03s
==> format ok 1m09s
verify: OK  stages=6  9m45s
```

`SqlHarness.Tests` 4,006 passed / 11 skipped; `SqlHarness.Mcp.Tests` 402 passed / 4 skipped. The gate ran after
the last C# edit; only `plans/080-verification-evidence.md` was added afterwards, which changes no executable
behaviour.

## Decisions the brief left open

1. **Step 3 cannot be applied literally — added `Compares`.**
   `Operations(databasePath, count, onSession)` creates `count` *separate* sessions, each with one `query`
   operation. `Compare_pages_have_no_gaps_...` needs `attempts` compare operations in **one** session and asserts
   `Assert.Single(reader.Sessions(...).Items)`, `Assert.Equal(attempts, allIds.Count)`,
   `Assert.Equal("v0", latest.BaselineVersion)` and `Assert.Equal($"v{attempts}", latest.CandidateVersion)`. The
   identical replacement would have failed all four. I read Step 3 as "replace the seeding loop with a bulk
   helper in the same style" and added `Compares(databasePath, sessionKey, count)` with the same pooled,
   single-transaction, prepared-statement shape. The test keeps every assertion and its comment; only the loop is
   replaced by `JournalSeed.Compares(home.DatabasePath, "cli:compare-history", attempts);`. Subject, scope,
   baseline hash and candidate shape are the fixture's constants and are documented on the helper.
   I verified the version labels by reading `CompareHistoryBuilder` rather than by relaxing the assertions:
   baseline `'base'` first occurrence → `v0`, candidate `candidate-{index}` at occurrence `index + 1` → `v{index+1}`,
   so the newest attempt (highest id, 137th) is `v137` and `Subjects(...).LatestWhole.OperationId` is `allIds.Max()`.

2. **`onSession` runs after the commit, not inside the transaction.** The brief says "invoked inside the
   transaction after each session insert", and its Step 2 callback calls `JournalSeed.Annotate`, which opens its
   own connection. The journal is WAL, so an open write transaction holds the single writer lock; the callback's
   connection would block on its first write until the 30 s command timeout and then throw `SQLITE_BUSY` — 120
   times, so the test would have gone from 8 s to over an hour. The transaction therefore covers the inserts
   only, which is where all the saving is, and the callbacks run immediately after it, where the session rows
   already exist. This is stated in the helper's XML comment. The alternative (giving `Annotate` an optional
   connection/transaction so the callback could join the transaction) would have changed `Annotate`'s signature
   and the exact call shape the brief fixed in Step 2, so I did not do it.

3. **`last_seen` is per index, not one constant.** Read literally, `last_seen = HostStarted.AddSeconds(count - 1)`
   is a single value shared by all `count` sessions, which cannot make "ordering by time match ordering by id".
   I used `HostStarted.AddSeconds(index)`, which satisfies the stated intent and gives the literal value for the
   last session. It cannot affect the test either way: the starred query orders by `s.id DESC` only
   (`JournalReader.cs:148`).

4. **Both tests dropped `var seed = new JournalSeed(home.DatabasePath)`.** It existed only to create and migrate
   the database and would have become an unused local. `EnsureSchema` inside the helpers does it instead, so a
   reader test can seed without constructing a writer-backed fixture.

5. **One added `using` and two shortened type names.** `using Microsoft.Data.Sqlite;` was added
   (`System.Globalization` for `InvariantCulture`) and the two pre-existing
   `Microsoft.Data.Sqlite.SqliteConnection` occurrences in `Annotate` and `Link` became `SqliteConnection`, so the
   file has one style. Provably a no-op; no other line in those two methods changed.

## Concerns

- **The brief's 52,9 s figure is not reproducible on this machine.** The plan header's class total came from a
  slower, contended box; the before-run here is 18,35 s. I did not re-measure on that box and do not claim to
  have reproduced 52,9 s. The claim I make is the one-machine before/after (18,35 s → 8,09 s, −56 %, with the two
  targeted tests −97 %), recorded in `plans/080-verification-evidence.md`. The gate's own stage totals from this
  run (ui-check 6m14s against the 36,9 s quiet baseline) confirm the box was contended and are not comparable to
  the recorded 2m25s gate baseline.
- **The starred test still spends ~0,2 s in `Annotate`.** 120 pooled connections, one autocommit each. Removing
  that means inserting `session_annotations` rows in the same transaction, which needs `Annotate` to accept the
  caller's connection and transaction — a signature change the brief's Step 2 call shape rules out. Left alone and
  flagged here rather than done silently.
- **The bulk rows are not byte-equivalent to writer rows**, by design: `agent_pid` and `agent_started_at` are null
  where the writer stores `4242` and `2026-10-07T07:55:00.000Z` (the known deliberate gap above), no
  `operation_phases` row is written (the writer's `Complete` writes one), and `vars_json` / token columns are null.
  No reader in these two tests reads any of them, and `JournalReader.cs` selects `agent_pid` / `agent_started_at`
  nowhere. Both helpers are documented as fixtures for reader tests that assert nothing about the writer, and
  Step 6 proves only reader tests use them.
- **Recommendation, not done here (out of scope for this task):** add `agent_pid` and `agent_started_at` to
  `JournalSeed.SessionInsert`. It is two columns and two parameters, it removes the only place where a bulk row
  differs from a writer row in a value a reader could care about, and it makes the helper's XML comment true instead
  of caveated. Leaving it out is defensible for now (the brief asked only for columns the reader tests read) but the
  gap should close before any reader starts selecting those columns.
- **`id` in the Step 2 lambda is unused** (kept verbatim from the brief). C# emits no warning for an unused lambda
  parameter and the build is clean under `-warnaserror`; `dotnet format` did not object. If the repo later turns
  on `EnforceCodeStyleInBuild`, IDE0060 would fire and the parameter should become `_`.
- **No `src/` observation worth acting on.** The production cost is real and intentional (`Pooling = false` with
  a comment explaining that concurrent CLI processes, the MCP host and the dashboard reader share the file), so
  there is nothing to escalate to the plan as a production change.
- Following Task 1's precedent, I did not tick the plan's step checkboxes and did not change `plans/README.md`
  (plan 080 is already listed as IN PROGRESS with this work in its scope).

---

## Fix round 1

**Status:** DONE_WITH_CONCERNS (one Important finding from the review, documentation only)

**Commit:** the commit that adds this section to the report file (message:
`docs: correct the Task 2 seeding fidelity claim`). It touches this report only.

### Finding

The review returned spec ✅ and code quality approved, with one Important finding: this report claimed that
`JournalSeed.Session` leaves `agent_pid` and `agent_started_at` null and that "the writer stores null for them too".
Both claims were false.

**Evidence:**

- `tests/SqlHarness.Tests/Dashboard/JournalSeed.cs:20-21` —
  `Session(string key, string agent = "claude", int hostPid = 5151) => new(key, agent, "process-tree",
  JournalTransport.Cli, null, null, null, 4242, HostStarted.AddMinutes(-5), hostPid, HostStarted, "/work")`.
  Mapped onto `SessionIdentity`'s positional parameters
  (`src/SqlHarness.Core/Journal/JournalModels.cs:14-29`: `… ClientVersion, McpMode, AgentPid, AgentStartedAt,
  HostPid, …`), the three `null`s are `ClientName`, `ClientVersion` and `McpMode`; the `4242` and
  `HostStarted.AddMinutes(-5)` land on `AgentPid` and `AgentStartedAt`.
- `src/SqlHarness.Core/Journal/ActivityJournal.cs:670-673` and `691-692` — `UpsertSession` lists `agent_pid` and
  `agent_started_at` in the insert and binds `$agentPid` / `$agentStarted` to
  `session.AgentPid` / `Nullable(session.AgentStartedAt)`.

So a bulk-seeded session row carries `agent_pid = NULL` and `agent_started_at = NULL` where a writer-seeded row
carries `4242` and `2026-10-07T07:55:00.000Z`.

### What I corrected

The false claim appeared in **three** places in this report; all three now state the truth.

1. **"What I added and why", item 3** (the `SessionInsert` description) said the helper uses "the writer's own
   column set … with the nullable writer columns left null, which is exactly what `JournalSeed.Session(...)`
   produces". Now: modelled on the writer's column set, **not** row-for-row fidelity, pointing at the gap section.
2. **"Schema columns I inserted"** (the paragraph listing what is deliberately not inserted) is split. The six
   columns that really are null in both (`client_name`, `client_version`, `client_title`, `mcp_mode`, `roots_json`,
   `protocol_version`) keep the claim, because for those it is true. `agent_pid` and `agent_started_at` moved into a
   new **"Known deliberate gap — the helper is not row-for-row fidelity"** paragraph carrying the two file:line
   references, the resulting stored values, the fact that nothing observes the difference
   (`SessionColumns` does not include them and `JournalReader.cs` selects them nowhere — verified by
   `Select-String -Path src/SqlHarness.Dashboard/JournalReader.cs -Pattern 'agent_pid|agent_started_at'`, no
   matches), and the instruction for whoever reuses the helper: add both columns to `SessionInsert` before any test
   starts reading them.
3. **Concerns** — the "not byte-equivalent to writer rows" bullet now leads with the two agent columns instead of
   only `operation_phases` / `vars_json`, and a new bullet records my recommendation to close the gap (two columns,
   two parameters in `SessionInsert`) as a recommendation rather than an unrequested change.

Also updated the paragraph that summarises what the helper writes, so it no longer reads as if every omitted column
matched the writer.

### Not changed, deliberately

- **`JournalSeed.cs` is untouched in this round**, per the fix-round instruction. The code is correct: the brief
  asked only for columns the reader tests select, and no reader selects `agent_pid` / `agent_started_at`. Adding
  them is now recorded as a recommendation in the concerns list above.
- **`plans/080-verification-evidence.md` is untouched.** It was reviewed as honest and this finding does not touch
  any measurement in it.
- **No test was re-run and no measurement changed**, because this round changed documentation only. The committed
  C# from `4bdb735` is exactly the tree that passed `JournalReaderTests` 64/64 and `just verify`.

### One thing the fix round surfaced

The same false claim is in the shipped code, not only in this report — `tests/SqlHarness.Tests/Dashboard/JournalSeed.cs:201-202`,
the `SessionInsert` XML comment: *"One prepared session insert carrying the writer's own column set … nullable
writer columns stay null because `Session` leaves them null."* It is inaccurate for the same two columns. Fixing it
means either correcting the comment or adding the columns; both touch `JournalSeed.cs`, which this round excludes,
so I have left it and am flagging it for the controller.