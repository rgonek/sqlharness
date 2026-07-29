# Agent-Oriented Command Extensions Design

**Date:** 2026-07-29
**Status:** Draft for review

## Purpose

Motivation (measured in CivicLens agent transcripts across Claude Code, Codex, Grok, and
OpenCode): agents already have `query`, `schema`, `measure`, `compare`, and `plan`, but most
live DB work still went through `docker exec … sqlcmd` or ad-hoc `SqlConnection` scripts
because several **recurring jobs** are awkward as free-form SQL. These commands collapse many
agent turns into one CLI call, keep passwords out of argv, and emit compact JSON.

## Goals

- Replace repetitive agent-side polling and before/after comparison with bounded, read-only
  SQLHarness commands.
- Add compact readiness, row-inventory, object-schema, and storage-inspection helpers without
  weakening the existing target, authentication, or mutation-safety contracts.
- Keep JSON output concise enough for agent context while retaining locally sensitive artifacts
  only where a command requires them.
- Deliver the commands in independently useful slices instead of as one release-sized change.

## Non-goals

- Managing Docker, Aspire, SQL Server processes, or application migrations.
- Automatically mutating schema, indexes, database files, recovery settings, or data.
- Replacing arbitrary `GROUP BY` analysis or ticket-specific SQL with specialized commands.
- Implementing Query Store, index-advice, or multi-parameter measurement work in the first
  `watch` and `snapshot` slice.

## Delivery boundaries

The design contains four independently testable slices. Slice A delivers `watch` and `snapshot`.
Slice B delivers `ping`, `counts`, and exact-object schema inspection. Slice C delivers `space`.
Slice D is a later performance roadmap covering Query Store consumers, index analysis, and
multi-parameter measurement. Each slice requires its own implementation plan and must preserve
the shared safety and output contracts in this design.

Evidence buckets (approximate, tool-call classified):

| Pattern | Typical agent action | Why a dedicated command helps |
| --- | --- | --- |
| Progress polling | 10–25 near-identical `SELECT COUNT/SUM…` while a sync runs | `watch` emits only deltas |
| Before/after job | Hand-diff count tables after re-import | `snapshot --diff` |
| Coverage smoke | `UNION ALL` of `COUNT(*)` on 4–12 tables | `counts` |
| Schema shape check | `sys.columns` / `COL_LENGTH` / `OBJECT_ID` one-offs | `schema --object` (or exact filter) |
| Storage pressure | `sp_spaceused`, top tables by MB, file sizes | `space` |
| Connectivity | `SELECT 1` via docker sqlcmd + health loops | `ping` |
| Perf proof | Almost never used, but plans 068/077 need it | existing `measure`/`compare` (+ roadmap) |

Implementation order is **not** “all at once”: ship `watch` + `snapshot` first (highest turn
savings), then thin read-only helpers (`ping`, `counts`, `space`), then Query Store / index
roadmap items.

---

## `watch` — poll a query until a condition, emit only deltas

```
sqlharness watch <profile> [--var k=v ...] --query <file|stdin> [--param ...]
    [--interval <sec>=30] [--max-duration <e.g. 30m>=15m]
    [--until <predicate>] [--until-unchanged <n-polls>=3] [--json]
```

- Runs the query (same safety pipeline as `query`: read-only classification, bounded, `--max-rows`
  applies) every `--interval` seconds.
- `--until`: predicate over the first row of the first result set, form `<column> <op> <value>`
  (`=`, `!=`, `<`, `<=`, `>`, `>=`); numeric compare when both sides parse as numbers. Exactly one
  of `--until` / `--until-unchanged` may be given; default is `--until-unchanged 3`.
- Output: first poll result in full, then only polls whose result **changed** (row-set inequality,
  same canonicalization as `compare`), then a final summary: poll count, elapsed, exit reason
  (`condition-met` | `unchanged` | `max-duration`, exit codes `0` / `0` / `7`).
- Gain accounting: raw bytes = sum of all poll results; emitted = what was printed.

### When this would have helped (CivicLens)

| Session pattern | What agents did | With `watch` |
| --- | --- | --- |
| Live BeSTi@ / CRJU / BZP / KRS sync | Repeated `SELECT COUNT(*) FROM Contracts` / watermark columns every few turns while `dotnet run … sync-*` ran in the background | One call: poll every 30s until `Imported >= @target` or until unchanged for 3 polls; agent only sees deltas |
| Place-media import (`sync-place-media --limit N`) | Manual re-query of media manifest row counts after each batch | `watch --until RowCount >= 3` (or unchanged) while the importer runs |
| Aspire / SQL container recovery | Shell loop: `for i in seq; docker exec … sqlcmd SELECT 1; sleep 5` | Prefer `ping` for readiness; use `watch` when waiting on **data** not TCP (e.g. leftover test DBs dropping to zero) |
| Risk detection long run | Re-check `RiskSignalDetectionRuns` / scope columns mid-job | `watch` on a one-row progress query; exit when `Status = 'Completed'` |

Example:

```powershell
# progress.sql → SELECT COUNT(*) AS Contracts FROM Contracts;
sqlharness watch civiclens-dev --query .\progress.sql --interval 30 --max-duration 45m --until "Contracts >= 1000000" --json
```

---

## `snapshot` — named aggregate snapshot + diff after a re-run

```
sqlharness snapshot <profile> [--var ...] --query <file|stdin> --name <label> [--json]
sqlharness snapshot <profile> [--var ...] --query <file|stdin> --name <label> --diff [--json]
```

- Without `--diff`: executes the (read-only, multi-result-set) batch, stores canonicalized results
  under `~/.sqlharness/snapshots/<label>.json` (locally sensitive, same policy as compare
  artifacts). Overwrite of an existing label requires `--force`.
- With `--diff`: re-executes the same batch, compares against the stored snapshot per result set
  (using `compare`'s canonical equivalence), prints only differing cells/rows plus a one-line
  verdict `identical` / `N differences` (exit `0` / `8`). Rejects diff when column shape changed.
- Primary use case: idempotency smoke checks — snapshot counts, run the job, `--diff` must report
  `identical` (or the expected delta after first run).

### When this would have helped (CivicLens)

| Session pattern | What agents did | With `snapshot` |
| --- | --- | --- |
| BZP reimport / reproducibility (plan 056) | Hand-written before/after COUNT and checksum-ish probes; agent compared numbers in chat | `snapshot --name bzp-pre` → run import → `snapshot --name bzp-pre --diff` |
| Batched contract ingestion (plan 069) | Multi-table counts (`Contracts`, `PublicEntities`, identifiers) before and after a batch | One multi-result SQL file snapshotted as `contract-batch-1` |
| CRJU incremental / published_at backfill | OpenCode: `COUNT(*)` + `SUM(CASE WHEN SourcePublishedAt IS NOT NULL…)` around backfill | Snapshot those two metrics; diff must show only the expected column fill, not row explosion |
| Idempotent re-run of any sync | Second run should not change row counts | `--diff` → `identical` is the pass criterion without the agent re-deriving baselines |
| Place-media manifest | After `sync-place-media`, compare Id/Kind/ByteSize sample set | Snapshot sample query; fail if unexpected rows appear |

Example:

```powershell
# coverage.sql — several SELECT COUNT / GROUP BY result sets
sqlharness snapshot civiclens-dev --query .\coverage.sql --name after-bzp-full --json
# … re-run importer …
sqlharness snapshot civiclens-dev --query .\coverage.sql --name after-bzp-full --diff --json
```

---

## `ping` — connection + database readiness (no docker)

```
sqlharness ping <profile> [--var ...] [--json] [--timeout <sec>=5]
```

- Opens the profile connection, runs a fixed internal probe (`SELECT 1 AS Ok, DB_NAME() AS Db,
  @@SERVERNAME AS Server, SUSER_SNAME() AS Login`), returns compact JSON.
- Exit `0` on success; `3` auth; `5` SQL/network failure (same family as other commands). Does **not**
  start containers — only answers “can this profile talk to SQL right now?”.

### When this would have helped (CivicLens)

| Session pattern | What agents did | With `ping` |
| --- | --- | --- |
| Almost every long session start | `docker exec civiclens-sql … sqlcmd -Q "SELECT 1"` or PowerShell `TcpClient` on 14334 | One `sqlharness ping civiclens-dev --json` without password in argv |
| After `docker start civiclens-sql` | Poll sqlcmd until ready (Git Bash path bugs: `MSYS_NO_PATHCONV`, wrong sqlcmd path) | Outer shell still starts docker; readiness = `ping` in a short loop or once after sleep |
| Worktree CI-ish setup | Test-NetConnection + connection-string echo | `ping` is the single success signal before `dotnet test` / migrations |

Example:

```powershell
sqlharness ping civiclens-dev --json
# agent only proceeds if exit 0
```

---

## `counts` — table / object row inventory

```
sqlharness counts <profile> [--var ...]
    [--table <name>]...          # repeatable; schema-qualified optional (dbo.Contracts)
    [--like <pattern>]           # sys.tables name LIKE, e.g. %Sync%
    [--top <n>=50]               # when using --like / default catalog walk
    [--exact]                    # use COUNT_BIG(*) instead of partition statistics
    [--json]
```

- Read-only catalog + count probe only (no user SQL batch). Default without filters: top N user
  tables by approximate row count from partition statistics. Exact `COUNT_BIG(*)` is opt-in via
  `--exact`, regardless of the number of selected tables.
- Prefer **approx by default** (agent smoke is almost always “order of magnitude + which tables
  moved”); `--exact` for small tables or when the agent must match `COUNT(*)`.
- Output: `{ schema, name, rows, method: "approx"|"exact" }[]` plus optional `omitted` count.
- Rejects unknown `--table` names with exit `2` (validation), not a silent zero.

### When this would have helped (CivicLens)

| Session pattern | What agents did | With `counts` |
| --- | --- | --- |
| Domain smoke after import | Hand-built `UNION ALL SELECT 'Contracts', COUNT(*) FROM Contracts …` (often wrong table names first: `PublicEntity` vs `PublicEntities`) | `counts --table Contracts --table BudgetFigures --table OfficeTerms --json` |
| Identifier coverage | `EntityIdentifiers` grouped by Type via ad-hoc SQL | Still needs `query` for GROUP BY; use `counts --table EntityIdentifiers` as the row-total half |
| “What landed in DB?” after multi-source sync | Repeated count probes across Contracts / Budget* / OfficeTerms / SourceFiles | `counts --like "%"` or explicit table list in one call |
| Plan 068 set-based aggregates | Baseline table sizes before rewriting public queries | `counts --top 30 --json` as a one-shot context pack |

Example:

```powershell
sqlharness counts civiclens-dev --table Contracts --table SourceFiles --table BudgetFigures --exact --json
sqlharness counts civiclens-dev --like "%Sync%" --json
```

Not a substitute for arbitrary GROUP BY (years, DocType, currency) — those stay on `query`.

---

## `space` — database and table storage footprint

```
sqlharness space <profile> [--var ...]
    [--top <n>=25]
    [--object <schema.name>]     # optional: single-table breakdown (indexes, compression)
    [--json]
```

- Read-only DMVs only: data/log file sizes, reserved/used/data MB, top tables by reserved space,
  optional per-index breakdown for `--object`.
- No `DBCC SHRINKFILE`, no recovery-model changes, no compression apply — those remain explicit
  user-approved mutations via `query --allow-mutation` if ever needed.
- Output compact enough for agent context: files + top N tables; omit deep partition noise unless
  `--object` is set.

### When this would have helped (CivicLens)

| Session pattern | What agents did | With `space` |
| --- | --- | --- |
| OpenCode “Analiza rozmiaru bazy CivicLens” | Long chain: `sys.database_files`, `sp_spaceused`, top-25 tables by pages, index usage, then shrink/recovery | `space --top 25 --json` for diagnosis; mutations stay human-gated |
| StagingRawRows / SourceFiles growth | `SUM(DATALENGTH(RawLine))`, file counts by DocType | `space --object dbo.StagingRawRows` + `query` for DocType breakdown |
| Import platform pressure | Agents guessed whether disk/SQL memory was the bottleneck | One `space` snapshot before blaming the importer |

Example:

```powershell
sqlharness space civiclens-dev --top 25 --json
sqlharness space civiclens-dev --object dbo.BudgetFigures --json
```

---

## `schema` enhancements — exact object, not only LIKE filter

Existing: `schema --filter "%Place%" --max-objects 50`.

Add:

```
sqlharness schema <profile> --object <name|schema.name> [--json]
sqlharness schema <profile> --filter <pattern> --exact-name   # optional: = instead of LIKE
```

- `--object` returns **one** table/view with columns, indexes, FKs (fail if missing, exit `2`/`5`).
- Avoids agents writing `sys.columns` / `COL_LENGTH('SyncRuns','RiskSignalScope')` /
  `OBJECT_ID('RiskSignalDetectionRuns')` one-liners that re-enter the transcript as giant sqlcmd
  wrappers.

### When this would have helped (CivicLens)

| Session pattern | What agents did | With `--object` |
| --- | --- | --- |
| Migration verification | `COL_LENGTH('SyncRuns','RiskSignalScope')`, `OBJECT_ID('…')` via docker sqlcmd | `schema --object SyncRuns --json` and read columns |
| Budget / OfficeTerms / Contracts column inventory | Multi-table `sys.columns` JOIN for three names | Three `--object` calls or one `--filter` with low `--max-objects` |
| Place-media / new tables | “What columns did we just migrate?” | `--object PlaceMedia` (or actual table name) after `dotnet ef database update` |

Example:

```powershell
sqlharness schema civiclens-dev --object SyncRuns --json
sqlharness schema civiclens-dev --filter "%Budget%" --max-objects 20 --json
```

---

## Roadmap commands (fit measured work; implement after the thin helpers)

These already appear in the product README roadmap; below ties them to CivicLens-style work.

### `qstop` — Query Store top consumers

```
sqlharness qstop <profile> [--var ...] [--top 20] [--window <e.g. 24h>] [--json]
```

**When it would help:** plan 068/077 query optimization, set-based public aggregates, slow API
endpoints — agents today rarely capture plan/IO evidence and instead re-run `dotnet test` or
eyeball latency. Pairs with existing `measure`/`compare`/`plan` once a heavy query is identified.

### `indexes` — missing-index suggestions vs existing indexes

```
sqlharness indexes <profile> [--var ...] [--object <name>] [--json]
```

**When it would help:** OpenCode sessions that already joined `dm_db_index_usage_stats` and
guessed compression; API slow paths on `Contracts` / place-scoped aggregates. Emit overlap so
agents do not propose duplicate indexes.

### `measure` multi-parameter sets (parameter sniffing)

```
sqlharness measure <profile> --query q.sql --param-set a.sqljson --param-set b.sqljson --repeat 5
```

**When it would help:** public place endpoints and budget-by-year queries where one “warm”
parameter set hides a bad plan for another municipality/year. CivicLens agents almost never
proved multi-parameter performance; this closes that gap without SSMS.

---

## Out of scope (keep out of default agent surface)

| Need seen in transcripts | Why not a first-class happy-path command |
| --- | --- |
| `docker start/logs/stats/restart` | Container lifecycle ≠ SQL harness |
| `dotnet ef database update` / migrations | EF owns schema evolution |
| `DBCC SHRINKFILE`, `ALTER DATABASE … RECOVERY` | Destructive/admin; rare; use gated `query --allow-mutation` under explicit user approval only |
| `sp_estimate_data_compression_savings` apply | Analysis maybe later under `space`; applying compression is a migration decision |
| Aspire dynamic port discovery | Profile `server` override / env var is enough; do not scrape `docker ps` inside sqlharness |
| Dropping leftover `*Tests_*` databases | Mutation + multi-db; optional later `civiclens-master` profile + approved batch, not a silent cleaner |

---

## Shared notes

- Default remains **read-only**. Commands that only run internal catalog/DMV SQL never accept
  arbitrary user batches (`ping`, `counts`, `space`, enhanced `schema`).
- `watch` and `snapshot` accept user SQL through the same pipeline as `query` (classification,
  bounds, params); mutation classification rejects with exit `2`.
- All new commands record gain metadata like existing ones; `gain` needs no contract change
  beyond new command names in the log.
- Prefer `--json` in agent skills; document **PowerShell `--file`** examples (agents on Windows
  repeatedly failed at bash heredoc / `<<<` stdin).
- New exit codes: `7` watch hit max-duration; `8` snapshot diff found differences. Reuse `0/2/3/4/5/6`
  for success, validation/safety, auth, target mismatch, SQL failure, local storage.

## Suggested implementation slices

| Slice | Commands | Why first |
| --- | --- | --- |
| A | `watch`, `snapshot` | Highest multi-turn savings on sync/import sessions |
| B | `ping`, `counts`, `schema --object` | Replaces the majority of sqlcmd smoke + shape checks |
| C | `space` | Replaces long storage-analysis chains without enabling shrink |
| D | `qstop`, `indexes`, multi-param `measure` | Perf evidence for planned query work |

## Acceptance sketches (per command)

- **watch:** fixture clock / fake reader advances; only changed polls appear in emitted output;
  gain raw > emitted when values stay flat then jump once.
- **snapshot:** write → identical `--diff` exit 0; mutate fixture data → exit 8 with compact
  cell diff only.
- **ping:** fake open success/fail maps to exit 0 vs 5/3; no password in argv/help samples.
- **counts:** known tables return stable row methods; unknown `--table` → exit 2.
- **space:** top N ordering deterministic in tests; no mutation SQL in the command path.
- **schema --object:** missing object → non-zero; present object matches column/index fixture.
