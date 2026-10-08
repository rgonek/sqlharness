# SQLHarness activity journal and local dashboard

> Amended by `2026-10-07-dashboard-settings-errors-theme-design.md`: one guarded write endpoint (`PUT /api/settings`), read-only profiles, stored error messages (schema v4), SQL highlighting CSS, and an operator-selectable theme.

Status: design approved in conversation on 2026-10-06; implemented in phases 1–5 (plans `docs/superpowers/plans/2026-10-06-activity-journal-phase1.md` and `2026-10-07-activity-journal-phase{2,3,4,5}.md`; each plan's "As built" section records accepted deviations). Phase 5 decisions recorded on 2026-10-07 are reflected below.

## Goal

Give the operator a local browser view of what agents are doing through SQLHarness **right now** (primary) and **what they did** (secondary), grouped by agent session and agent kind (Claude, Codex, other), with the performance and IO metrics SQLHarness already measures.

Success criteria:

1. Every `mcp serve` tool call and CLI command that reaches `SqlHarnessModule` is recorded in a local SQLite journal without changing any agent-visible output, exit code, or token budget.
2. Sessions are identified implicitly. The agent sends nothing extra and is not told the dashboard exists.
3. `sqlharness dashboard` serves a read-only React SPA on loopback that shows running operations live and lets the operator browse sessions, operations, IO metrics, and plans.
4. Sensitive content (SQL text, full plans) is stored only when the operator opts in with one flag. Parameter values, secrets and result cells are never stored.

## Non-goals (separate future sub-projects)

- Notifications and mutation approval. An earlier design exists (`2026-09-26-windows-approval-tray-design.md`). The dashboard API in this design is read-only.
- Correlating sessions with Claude/Codex conversation transcripts. This design only records the data such a correlator would need: timestamps, tool/command, working directory, and agent and host process identity.
- Session identity supplied by the agent or by configuration: environment variables, hooks, or a `--session` flag.
- Graphical plan rendering (SSMS-style tree).
- IO/plan statistics for `query`: rejected (2026-10-07); `measure` and `compare` are the measurement commands.
- Visual design. The UI is unstyled shadcn; styling comes later.

## Decisions

| Topic | Decision |
|---|---|
| Storage | SQLite (`Microsoft.Data.Sqlite`), WAL mode, `~/.sqlharness/data/activity.db` |
| Session identity | Layer 1: MCP `clientInfo` plus one `mcp serve` process instance. Layer 2: CLI process-tree walk. Nothing else. MCP is the primary path, CLI is the fallback |
| Sensitive content | One flag `journal.storeSensitive` (default `false`) gates both SQL text and full plans |
| Journal on/off | `journal.enabled`, default `true` (hash-and-metadata only by default, like the former `gain.jsonl`) |
| Retention | Opt-in, `journal.retention.enabled` default `false` |
| Gain statistics | Aggregated from the journal (operations with an emitted footprint); gain.jsonl is no longer written or read; no migration of old data; gain write failures never change an exit code |
| Dashboard lifetime | Explicit `sqlharness dashboard`. Opt-in `dashboard.autoStart` makes `mcp serve` spawn it as a detached process |
| UI | React + TypeScript + Vite SPA, embedded into the .NET assembly. shadcn/ui on Base UI primitives, Tailwind v4, default `neutral` theme, no custom styling |

## Architecture

```
agent ─MCP/CLI─► SqlHarnessModule ──► IActivityJournal ──► activity.db (SQLite, WAL)
                    │ begin: row "running"                         ▲
                    │ complete: exit code, timings, metrics        │ read-only
                    ▼                                              │
              mcp serve ──(opt-in autostart, detached)──► sqlharness dashboard ──HTTP/SSE──► browser
```

### Units

- **`IActivityJournal` / `ActivityJournal` (Core).** `Begin(SessionIdentity, OperationStart) → JournalHandle`, `RecordWatchProgress(JournalHandle?, WatchProgress) → bool` (used by `watch`; false means not written, and the caller stops reporting), `Complete(handle, OperationEnd)`. Writes are best-effort: any exception is caught, one line without SQL, values, or paths is written to stderr, and the operation's outcome and exit code stay unchanged. This is the opposite of `GainStore`, which maps a failure to exit `6`, and the difference is deliberate. A `NullActivityJournal` is used when the journal is disabled or unusable.
- **Hook point.** `SqlHarnessModule` dispatch, the single facade shared by CLI and MCP. `Begin` runs when the operation starts. `Complete` runs on the same path that builds the emission receipt today (`WithReceipt`), so token footprints are available. The journal never reads emitted output for any other purpose.
- **`SessionIdentity` (Core, with adapters).** `{ AgentKind, SessionKey, Source, Transport, ClientName?, ClientVersion?, McpMode?, AgentPid?, AgentStartedAt?, HostPid, HostStartedAt, Cwd }`.
  - MCP: computed once after `initialize` from `clientInfo` (name → `AgentKind` mapping, unknown names → `other`). `SessionKey` is a random UUID per `mcp serve` process. `McpMode` is `fixed` or `request`.
  - CLI: computed once per process by walking ancestor processes until one matches a known agent executable (`claude`, `codex`, …). `SessionKey = hash(agentKind, agentPid, agentStartedAt)`, so consecutive CLI calls from one agent session share a row. With no match, `AgentKind = unknown`, `Source = unknown`, and `SessionKey = hash("unknown", parentPid, parentStartedAt)`.
  - The process walk sits behind `IProcessInfo`, with Windows and Linux (`/proc`) implementations. A walk failure yields `unknown` and never fails the operation.
- **Configuration (`~/.sqlharness/config.json`, new file).** It is separate from `targets.json`, which describes targets rather than tool behavior. Invalid or unreadable config falls back to defaults and warns on stderr and in `doctor`. It **fails closed**: an invalid config never enables `storeSensitive` or `autoStart`.

  ```json
  {
    "journal": {
      "enabled": true,
      "storeSensitive": false,
      "retention": { "enabled": false, "maxAgeDays": 30, "maxSizeMb": 500 }
    },
    "dashboard": { "autoStart": false, "port": 47800, "idleShutdownHours": 8 }
  }
  ```

  The default port is `47800` (documented, bookmarkable). When it is busy, the server falls back to the next free port as described below.
- **`SqlHarness.Dashboard` (new project).** ASP.NET Core minimal API on Kestrel, read-only access to `activity.db`, embedded SPA assets, lock-file singleton, SSE live feed. CLI command: `sqlharness dashboard [--background] [--no-open]`.
- **`ui/` (inside `SqlHarness.Dashboard`).** Vite + React + TypeScript SPA. An MSBuild target runs the npm build and embeds `dist/` as resources, so end users do not need Node.

## Data model

Schema version is stored in `PRAGMA user_version`. Forward migrations run in a `BEGIN IMMEDIATE` transaction on open. All timestamps are UTC ISO-8601.

### `sessions`

| column | notes |
|---|---|
| `id INTEGER PRIMARY KEY` | surrogate key used by URLs and FKs |
| `session_key TEXT NOT NULL UNIQUE` | sqlharness-derived key (MCP UUID / CLI hash). **Not** the agent's conversation id |
| `agent_kind` | `claude` / `codex` / `other` / `unknown` |
| `transport`, `source` | `mcp` / `cli`; `mcp-clientinfo` / `process-tree` / `unknown` |
| `client_name`, `client_version`, `mcp_mode` | MCP only |
| `agent_pid`, `agent_started_at`, `host_pid`, `host_started_at`, `cwd` | identity and later correlation |
| `first_seen`, `last_seen` | |

Upsert: `INSERT … ON CONFLICT(session_key) DO UPDATE SET last_seen = excluded.last_seen RETURNING id`.

### `operations`

| group | columns |
|---|---|
| identity | `id INTEGER PRIMARY KEY`, `session_id` → `sessions.id`, `operation` (`query`/`measure`/`compare`/`watch`/`snapshot`/`counts`/`schema`/`space`/`ping`/`qstop`/`indexes`/`validate`/`plan`/`artifact`/…), `started_at`, `updated_at`, `finished_at` |
| process | `host_pid`, `host_started_at` (per operation, because every CLI call is its own process; used for `abandoned` detection) |
| state | `status` (`running`/`succeeded`/`failed`/`rejected`/`abandoned`), `exit_code`, `error_kind`, `duration_ms` |
| target | `profile`, `vars_json` (profile variable values; these are scope, not secrets), `engine`, `server`, `database`, `mutation_requested` |
| result | `result_sets`, `rows_returned`, `artifact_dir`, `summary_json` (bounded, see below), `progress_json` (`watch`: polls, condition met) |
| tokens | `raw_tokens`, `emitted_tokens` |
| SQL | `sql_hash` (always; for compare also `candidate_sql_hash`), `sql_text`, `candidate_sql_text` (only with `storeSensitive`) |

Indexes: `(session_id, id)`, `(updated_at)`, `(status)`, `(sql_hash)`.

`rejected` means exit `2` (validation/safety). It is a distinct status so the operator can filter safety rejections. `abandoned` is reported by the dashboard when a row is `running` but the operation's `(host_pid, host_started_at)` pair no longer exists; it is computed at read time and never stored (see "Dashboard server"). Both PID and start time are checked because PIDs are reused.

### `operation_metrics` (always stored; contains no SQL text and no values)

One row per measured variant (`measure`, `baseline`, `candidate`, matrix cell, parameter set by name). It stores medians plus min/max where the benchmark already computes distributions. A metric the run marks unavailable (truncated `STATISTICS` messages, Postgres CPU) is stored as `NULL`, never `0`:

- elapsed ms, CPU ms, logical reads;
- memory grant requested / granted / max used (KB), DOP, compile time / CPU;
- spill flag and count, warnings, implicit conversions, missing-index count (plan-derived columns are best-effort: an unparseable plan records zero spills, warnings, and missing indexes);
- top wait stats from the plan (`WaitStats`, up to 10 by wait time);
- Postgres: shared hit / read / dirtied / written, temp read / written.

### `operation_table_io` (always stored)

Per variant and table, from `STATISTICS IO` (SQL Server): scan count, logical reads, physical reads, page server reads, read-ahead reads, lob logical / physical / read-ahead, and `cold_runs` (measured runs with physical, read-ahead, or LOB physical/read-ahead reads). Page server read-ahead and LOB page server counters are not kept. `Worktable` and `Workfile` rows are kept as-is. Lines without logical reads (localized output, columnstore segment lines) are not recorded, and truncated `STATISTICS` output records no table IO. Postgres rows carry relation buffer reads as `logical_reads`; the SQL Server-only counters are null. `cold_runs` and the detail counters are meaningful only for SQL Server rows with detail (`physical_reads` non-null); other rows store `cold_runs = 0`.

### `operation_plans` (always stored) and `plans` (only with `storeSensitive`)

- `operation_plans(metric_id, repetition, ordinal, plan_hash)` — always stored; plan identity hashes are not sensitive.
- `plans(hash TEXT PRIMARY KEY, format, raw_size, gz BLOB, first_seen)` — only with `storeSensitive`: `format` is `showplan-xml` or `explain-json`; deduplicated by the existing `PlanIdentity` hash, gzip-compressed (measured: median plan 21 KB, max 227 KB, ~17× compression). A stored plan is the first actual plan observed for its shape.

### Never stored, regardless of configuration

`--param` / `--param-set` values as columns, passwords, tokens, connection strings, and result cell values. Stored plans may still embed parameter values (see the caveats below).

Caveats:

- With `storeSensitive`, stored plans may embed parameter values, including `--param-set` and matrix values (`ParameterCompiledValue` / `ParameterRuntimeValue`, or literals in Postgres plans), and always embed SQL text (`StatementText`). Metric rows still identify matrix cells by index only. Documentation states this in the same way it does for `.sqlplan` artifacts.
- Without `storeSensitive`, `summary_json` must not carry plan predicates or statement text, because `PlanDistiller` predicates can contain SQL literals. Operators are recorded by physical op, object, and flags only.

## Metrics capture changes

- Add a journal-only `STATISTICS IO` detail parser that keeps scan count and logical, physical, page server, read-ahead, and LOB logical / physical / read-ahead reads per table (`StatisticsIoParser` keeps only logical + lob logical reads).
- Extract memory grant, DOP, compile stats, waits, and spills from the actual plan XML, and Postgres buffer/temp counters from `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`. Extraction runs before the `storeSensitive` decision, so the metrics are kept even when the plan is discarded.
- **Agent-visible reports are unchanged.** New fields feed only the journal. Exposing any of them to agents is a separate decision.

## Dashboard server

- Binds only to `127.0.0.1`, one listener. Kestrel/ASP.NET Core configuration (`ASPNETCORE_URLS`, `Kestrel:Endpoints`, environment name, `appsettings`) is ignored. Rejects any `Host` header other than `127.0.0.1:<port>` or `localhost:<port>` (DNS-rebinding defense), and any connection on a local endpoint other than the bound `127.0.0.1` port.
- Only `GET` and `HEAD` are served; other methods get `405`.
- Access token: 32 random bytes per server start, published in `dashboard.json`. The opening URL carries `?t=<token>`, which the server exchanges for an `HttpOnly`, `SameSite=Strict` cookie and then redirects to a token-free URL. Every request without the cookie gets `401`. Known limitation: browsers do not isolate cookies by port, so the cookie is also sent to other services on `127.0.0.1`/`localhost` visited in the same browser; the dashboard is meant for single-user workstations.
- Browser launch: the browser opens an owner-only `dashboard-open.html` redirect page in the SQLHarness home, never the token URL, so the token does not appear in process arguments. The serving process deletes the page when it stops.
- Singleton: an exclusive lock on `~/.sqlharness/dashboard.lock` (an advisory `flock` on Unix) held for the server's lifetime; the endpoint `{ pid, startedAt, port, token }` is published in owner-only `~/.sqlharness/dashboard.json`, which is trusted only while the lock is held. A second `sqlharness dashboard` reads it and opens the existing URL. If the configured port is busy, the server takes the next free port, or an ephemeral loopback port when none of the next ones is free, and publishes it. Starting the dashboard makes the SQLHarness home directory owner-only (`0700`) on Unix.
- Stopping: Ctrl+C and SIGTERM stop the server cleanly with exit `0`, releasing the lock.
- `abandoned` is computed at read time (API, statistics, live feed) when a running row's `(host_pid, host_started_at)` no longer exists; the dashboard does not write to the journal. On platforms without a process reader (macOS) rows are reported as stored.
- `watch` writes `progress_json` after each completed poll (`polls`, `changedPolls`, `elapsedMs`); progress recording stops for that watch after the first failed journal write.
- Autostart (opt-in): after startup validation and before the MCP transport starts, `mcp serve` checks the published dashboard. If no live server exists, it launches `sqlharness dashboard --background` (which never opens a browser) and does not wait. A launch failure is logged to stderr and otherwise ignored. MCP stdout stays protocol-only.
- Autostart launches the dashboard detached: ShellExecute with a hidden window on Windows, closed redirected stdio on Unix, the SQLHarness home as working directory. It is not placed in a new session or process group, so a signal to the MCP client's process group (Ctrl+C, SIGHUP, closing a Windows console) also stops it; the next `mcp serve` relaunches it.
- Idle shutdown applies to `--background` (autostarted) only: the server exits after `idleShutdownHours` with no journal commits (`PRAGMA data_version`), no authenticated requests, and no open live streams. The dashboard's own retention commits do not count as activity.
- Retention (opt-in) runs at dashboard start, then hourly, and also at `mcp serve` start so it applies without the dashboard. Order of deletion:
  1. operations older than `maxAgeDays`, never a running operation whose host process is alive (abandoned rows are deleted like finished ones; a failed liveness lookup keeps the row);
  2. unreferenced plans and empty sessions;
  3. while the used size (pages minus free pages) still exceeds `maxSizeMb`, oldest operations (same running-row rule), then orphans again;
  4. finally `PRAGMA incremental_vacuum`. The database is created with `auto_vacuum = INCREMENTAL`; a database without it is not converted and is not vacuumed.

  Deletes run in transactions of at most 500 rows, so concurrent writers stay within their 1 s busy budget.

### API (read-only; no write endpoints exist)

| endpoint | purpose |
|---|---|
| `GET /api/live` | SSE stream of `session` and `operation` upserts. The server checks `PRAGMA data_version` about once per second; when it changed, it re-reads a 10 s lookback window (commit order is not timestamp order) and resends rows whose payload changed. Running rows are re-read every tick, and a running operation that leaves the running set is always fetched and sent |
| `GET /api/sessions?agent=&transport=&from=&to=&cursor=` | keyset pagination by `id` |
| `GET /api/sessions/{id}` | session plus operation timeline |
| `GET /api/operations?session=&status=&operation=&from=&to=&cursor=` | |
| `GET /api/operations/{id}` | metadata, metrics, table IO, SQL text when stored, and plan links (hash and whether the plan is stored) |
| `GET /api/plans/{hash}` | decompressed plan download (`.sqlplan` / `.explain.json`); `?view=distilled` returns the distilled plan |
| `GET /api/stats?from=&to=` | aggregated series for the statistics view |

## UI

shadcn/ui generated for Base UI primitives. Files in `components/ui` stay exactly as the shadcn CLI generates them. Views use only layout utilities (grid, flex, gap, width, overflow, sticky) and functional variants such as `Badge` variants for status. There is no custom CSS and no theme changes. Dark mode follows the system preference. Tables use shadcn `Table` with TanStack Table. Charts use the shadcn `Chart` component (Recharts) with its defaults.

Implemented with TanStack Router (code-based route tree) and TanStack Query; TanStack Table was not needed (tables are not client-sortable in this version). The SPA is embedded through an MSBuild target; `-p:SkipDashboardUi=true` builds without Node and serves the placeholder.

This version does not build, from the views listed below:

- Live active-session cards without profile/database or last operation (they show agent, cwd and idle time; the operation feed carries target and status);
- no physical-reads KPI tile on operation detail; physical reads appear in the per-table IO table;
- "tables by logical reads" is a top-N table for the selected window, not a series over time.

Views:

1. **Live** (home):
   - active-session cards (agent, cwd, profile/database, last operation, idle time);
   - running operations with an elapsed timer; `watch` progress;
   - a feed of recently completed operations with a status badge and logical reads;
   - flags for cold cache (physical or read-ahead reads > 0), spill, and over-granted memory.
2. **Sessions:** filterable list with operation, failure and rejection counts and time span.
3. **Session detail:** operation timeline.
4. **Operation detail:**
   - KPI strip (elapsed, CPU, logical reads, physical reads, grant used/granted, spills, DOP);
   - per-table IO table sorted by logical reads; waits;
   - for `compare`, baseline and candidate side by side with deltas and the equivalence verdict;
   - SQL text, distilled operator table, and plan download when stored.
5. **Statistics:**
   - operations per day by agent;
   - exit-code and rejection distribution;
   - most frequent and slowest `sql_hash`;
   - tables by logical reads over time;
   - operations with spills or cold cache;
   - top waits, token savings, and per profile/database breakdown.

## Error handling

| situation | behavior |
|---|---|
| journal write fails (lock, disk, permissions) | operation continues and its exit code is unchanged; one stderr line per process; `busy_timeout` 250 ms, and the command timeout of 1 s (the `Microsoft.Data.Sqlite` minimum) bounds the busy-retry loop |
| `activity.db` corrupt | renamed to `activity.db.corrupt-<timestamp>`, recreated; dashboard shows a banner |
| `user_version` newer than the binary | journal disabled for this process (no writes); dashboard refuses to run, with a clear message |
| concurrent first start / migration | `BEGIN IMMEDIATE`; losers wait on `busy_timeout`, then re-check the version |
| invalid `config.json` | defaults (fail-closed) and a warning in stderr and `doctor` |
| dashboard lock held | open the existing URL |
| file permissions | `activity.db*`, `config.json`, `dashboard.lock`, `dashboard.json` and `dashboard-open.html` are owner-only on Unix. The journal does this itself whether or not plan 034 has landed. On Windows they inherit the user-profile ACL |

## Testing

- **Core journal**, against a real SQLite file in a temporary `SQLHARNESS_HOME` (no mocks): begin/progress/complete, concurrent session upserts from parallel processes, best-effort behavior under a held write lock, migrations from each prior version, retention order, corruption recovery, newer-version refusal.
- **Sensitivity gate (critical).** Run operations with a marker in the SQL and in a `--param` value, then scan the raw bytes of `activity.db`, `-wal` and `-shm`:
  - with `storeSensitive: false`, there is no SQL marker and no plan;
  - with any setting, there is no parameter marker outside stored plans.
- **Contract unchanged.** CLI `--json`, `--json-summary`, human output and MCP tool results are byte-identical with the journal enabled and disabled, and exit codes are unchanged.
- **Parsers:** extended `StatisticsIoParser` on text fixtures; grant, DOP, wait and spill extraction on existing `.sqlplan` fixtures; Postgres buffer extraction on EXPLAIN JSON fixtures.
- **Process tree:** `IProcessInfo` fakes for the walk logic, plus one real smoke test per OS.
- **MCP:** `clientInfo` produces the session row; fixed and request modes are recorded; autostart with a fake spawner, which must not write to stdout.
- **Dashboard API**, in-memory `TestServer`: `401` without the cookie, foreign `Host` rejected, token exchange, pagination, SSE event after a write from another connection, and a route table containing only `GET`.
- **UI:** Vitest with React Testing Library for views and data hooks, TypeScript typecheck (`tsc -b`), oxlint. No end-to-end browser tests in this version.
- **Gates:** add a `ui` stage (`npm ci`, `npm run build`, `npm test`, typecheck, lint) to `scripts/verify.ps1`, `scripts/verify-linux.ps1`, `.github/workflows/ci.yml` and `release.yml`. Pin Node in `.nvmrc`. `setup-linux-gate.ps1` provisions Node in WSL.

## Implementation phases

Each phase is independently verifiable and keeps both gates green.

1. **Journal core and session identity:** config, SQLite schema, `ActivityJournal`, module hook, MCP `clientInfo`, CLI process walk, sensitivity and contract tests.
2. **Metrics capture:** extended IO parser, plan/EXPLAIN metric extraction, `operation_metrics`, `operation_table_io`, deduplicated plans.
3. **Dashboard server and API:** project, `dashboard` command, lock/singleton, auth, endpoints, SSE, abandoned detection.
4. **SPA:** Vite/React/shadcn (Base UI) scaffold, embedding, the five views, `ui` gate stage.
5. **Autostart and retention:** detached spawn from `mcp serve`, idle shutdown, retention job.

## Documentation

- Update `AGENTS.md` and `README.md`: the journal location and its sensitivity, `config.json`, `storeSensitive` caveats (plans embed SQL and may embed parameter values), and the `dashboard` command.
- Agents need no new instructions. The journal is invisible to them, so `docs/mcp.md` changes only to mention autostart and the stdout guarantee.
