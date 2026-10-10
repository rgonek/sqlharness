# MCP session metadata: client title, workspace roots, cancellation reason, call metadata

Status: approved design and plan. **Implement after the MCP protocol upgrade
plan merges.** The upgraded host negotiates `2026-07-28` with Claude Code and
`2025-06-18` with Codex. On `2026-07-28`, `clientInfo` and client capabilities
come from the request-scoped server, and `tools/call._meta` contains protocol
keys alongside client labels. The title and roots read paths must use that
context. `2026-07-28` has no `notifications/initialized`; whether a
request-scoped `roots/list` works over stdio remains unproven. If the Task 5
probe fails, roots stay NULL for `2026-07-28` sessions. The plan's
"Post-upgrade adjustments" section defines that decision.

## Goal

Record facts the MCP protocol already offers but the activity journal does not
keep, and show them in the dashboard:

1. `clientInfo.title` from the first request that carries a non-blank title
   (or `initialize` on handshake revisions);
2. the client's workspace roots (`roots/list`);
3. who cancelled an operation that ended with `error_kind = 'cancelled'`;
4. allowlisted per-call metadata from `tools/call._meta` (Claude Code tool-use
   id; Codex call id, model, reasoning effort, agent session, turn, trigger,
   thread source). Evidence: `2026-10-10-mcp-client-spike.md`.

## Out of scope

- A model name declared at registration (argument or environment variable): it
  would label the whole session, including subagents and `/model` switches, so
  it was rejected. Codex sends the model per call (below); Claude Code's model
  comes later from the opt-in transcript matcher (follow-ups).
- Reading agent transcripts from disk.
- Cost estimation: agent turn counts, cache pricing, output tokens of tool
  arguments, `tools/list` size, budget truncation, `sqlharness_artifact`
  follow-ups, rejections before the module runs.
- `clientInfo.description`, `websiteUrl`, `icons`; client capabilities other
  than `roots`; any `_meta` key not on the allowlist below (Codex sandbox and
  feature flags, `windowId`, `itemId`, `progressToken`, `codex_version`,
  `turn_started_at_unix_ms`).
- Roots history (one snapshot per session only) and per-operation roots.
- A new status value or an `error_kind` filter in the dashboard.
- CLI sessions: the new columns stay NULL there.

## Schema (journal `Version6`)

```sql
ALTER TABLE sessions ADD COLUMN client_title TEXT;
ALTER TABLE sessions ADD COLUMN roots_json TEXT;
ALTER TABLE operations ADD COLUMN cancel_reason TEXT;
```

`JournalSchema.CurrentVersion` becomes 6. Existing rows keep NULL.

### `sessions.client_title`

- `clientInfo.title`, trimmed; empty or whitespace is NULL; at most 256
  characters (longer values are cut).
- Upsert: `client_title = COALESCE(sessions.client_title, excluded.client_title)`,
  as for `client_name`.

### `sessions.roots_json`

- JSON array of objects `{"uri": "...", "name": "..."}`; `name` is omitted when
  the client sent none; `Root.Meta` is discarded.
- NULL means unknown: the client did not declare `capabilities.roots`, has not
  answered yet, or every request failed. `[]` means the client answered with an
  empty list. The two states stay distinct everywhere, including the UI.
- Limits: at most 32 roots (the first 32 in client order), `uri` at most 2048
  characters, `name` at most 256 characters. Excess roots are dropped and long
  values cut. Stderr reports only counts (for example `roots: kept 32 of 40`),
  never URIs or names.
- URIs are stored as received, including non-`file://` schemes; SQLHarness
  records and does not validate them.
- Sensitivity: same treatment as `sessions.cwd` — stored regardless of
  `journal.storeSensitive`.
- Upsert: `roots_json = COALESCE(excluded.roots_json, sessions.roots_json)`.
  A non-NULL snapshot overwrites; NULL never erases.

### `operations.cancel_reason`

- `shutdown` when the host lifetime fired (stdin EOF or process shutdown),
  `client` when the request token fired (the SDK cancels it on
  `notifications/cancelled`), `deadline` when only the per-call time budget
  (`CancelAfter` of `maxOperationSeconds`) fired. Precedence when several fired:
  `shutdown` > `client` > `deadline`.
- Written only together with `error_kind = 'cancelled'`; NULL otherwise, on old
  rows, on CLI rows, and when the reason is unknown.
- Status semantics are unchanged: a cancelled operation stays `failed`.

### Cancellation translated into an outcome

Core maps an `OperationCanceledException` raised during SQL execution to a
failed outcome with exit `5` (`OperationFailureMapper`), so `JournalingModule`
today records such a cancellation as an SQL failure while MCP tells the client
the call was cancelled. New rule: when the inner module returns an outcome with
a non-success exit code and the module's cancellation token is cancelled,
`JournalingModule` records the row as a cancellation — the same
`OperationEnd` as the thrown case (`status = failed`, `exit_code = -1`,
`error_kind = cancelled`) plus `cancel_reason` — and returns the outcome
unchanged. Controlled successes (`0`, `7`, `8`) are never rewritten. These rows
stop counting as SQL errors in the journal and dashboard; that is intended.

### Per-call metadata (`operations`, also in `Version6`)

```sql
ALTER TABLE operations ADD COLUMN client_call_id TEXT;
ALTER TABLE operations ADD COLUMN agent_model TEXT;
ALTER TABLE operations ADD COLUMN agent_reasoning_effort TEXT;
ALTER TABLE operations ADD COLUMN agent_session_id TEXT;
ALTER TABLE operations ADD COLUMN agent_turn_id TEXT;
ALTER TABLE operations ADD COLUMN agent_turn_trigger TEXT;
ALTER TABLE operations ADD COLUMN agent_thread_source TEXT;
```

Allowlist, read from `tools/call` `params._meta` only:

| Column | Claude Code | Codex |
|---|---|---|
| `client_call_id` | `claudecode/toolUseId` | `callId` |
| `agent_model` | — | `x-codex-turn-metadata.model` |
| `agent_reasoning_effort` | — | `x-codex-turn-metadata.reasoning_effort` |
| `agent_session_id` | — | `x-codex-turn-metadata.session_id` (opencode: `ai.opencode/sessionID`) |
| `agent_turn_id` | — | `x-codex-turn-metadata.turn_id` |
| `agent_turn_trigger` | — | `x-codex-turn-metadata.turn_trigger` |
| `agent_thread_source` | — | `x-codex-turn-metadata.thread_source` |

- Only JSON strings are read; a missing key, another JSON type, or an empty
  string is NULL. Each value is cut to 256 characters. Parsing never throws and
  never affects the call.
- Values are client-supplied labels, not verified facts; they are stored as
  received (after the cap), like `clientInfo`.
- Stored regardless of `journal.storeSensitive` (identifiers and labels, no
  content). Every operation of a call carries them, including target-free tools.
- Claude Code's `client_call_id` is the key a later transcript matcher uses to
  find `message.model`; recording it now makes retroactive matching possible.

## Components and data flow

### Client title

The `tools/call` request filter records `context.Server.ClientInfo` in
`McpClientIdentity`; name and version keep the first non-null client info.
Record title independently as the first non-blank title so an earlier client
info object without a usable title cannot suppress a later one. Pass that
title, falling back to `running?.ClientInfo?.Title` on handshake revisions,
to `SessionIdentities.Mcp`. The session key stays fixed
for the serve process, but `JournalingModule` resolves the session identity
for each operation so a later request can fill a previously NULL title.
`ActivityJournal` keeps the first non-NULL title. A first `2026-07-28` call
without client info must not prevent a later call from supplying it.

### Roots snapshot

A new MCP-layer unit (working name `McpRootsTracker`) owns the in-memory
snapshot:

- On handshake revisions it starts after `notifications/initialized` when the
  client declared `capabilities.roots`, and again on
  `notifications/roots/list_changed` when the client declared
  `roots.listChanged`. A `list_changed` from a client that did not declare it
  is ignored. For `2026-07-28`, first prove request-scoped `roots/list` over
  stdio in Task 5. If it works, start a fetch from the first eligible
  `tools/call`; `subscriptions/listen` refresh stays out of scope. If it fails,
  keep roots NULL on that revision and document the limitation.
- Each fetch calls `McpServer.RequestRootsAsync` with a 5 second timeout, linked
  to the host lifetime token. Starting a new fetch cancels the previous one, so
  a slow older response can never overwrite a newer one.
- Success: normalise (limits above) and publish the serialized snapshot
  atomically. Failure (JSON-RPC error, timeout, malformed result, disconnect):
  keep the previous snapshot; write a content-free stderr diagnostic naming
  only the failure class.
- It never blocks a tool call and never writes the journal itself.

Journal writes stay where they are. The session identity used by
`JournalingModule` gains a roots value read from the tracker at each `Begin`, so
every operation upsert carries the latest snapshot. Consequences accepted in
design: an operation that starts before the first response leaves roots NULL
until a later operation; a change after the session's last operation is not
saved.

`IActivityJournal` gets no new method.

### Cancellation reason

`JournalingModule` gains an optional `Func<CancellationToken, string?>`
cancellation-reason provider (constructor argument; absent means NULL). On a
cancellation (thrown or translated) it calls the provider with the token the
module itself received, inside the existing swallow-all pattern; a throwing
provider yields NULL. `OperationEnd` carries the reason to
`ActivityJournal.Complete`, which writes `cancel_reason`.

The MCP layer supplies the provider. Each of the three MCP run sites
(`McpToolHandlers.RunAsync`, `McpToolHandlers.RunDbAsync`,
`McpRequestToolHandlers.RunAsync`) already creates a linked source from the
request token, the host shutdown token and the call budget, and passes its
token to the module. Those sites register the linked source with its request
and shutdown tokens in a small registry for the duration of the call; the
provider looks the module token up there (`shutdown`, then `client`, then
`deadline` when only the linked source fired). Lookup by token, not by "the
current call", because target-free tools do not take the execution gate and
can run concurrently with a database call. The CLI passes no provider.

### Per-call metadata flow

`McpCallMetadata.Read(JsonObject? meta)` (MCP layer) applies the allowlist and
returns a Core `AgentCallMetadata` record or NULL when nothing matched. The
three MCP run sites already register each call in the token-keyed registry for
the cancellation reason; the same registration carries the call's metadata
(read from `RequestContext.Params.Meta`; target-free run sites receive the
request context too). `JournalingModule` gains an optional
`Func<CancellationToken, AgentCallMetadata?>` provider, read at `Begin`, and
`OperationStart` carries the record to `ActivityJournal.Begin`, which writes the
seven columns.

## Dashboard

- `JournalReader` selects the three columns; `DashboardModels` add
  `ClientTitle`, `Roots` (parsed array or NULL) to the session model and
  `CancelReason` to the operation model; `ui/src/api/types.ts` mirrors them as
  nullable fields.
- `SessionTable`: client label is `clientTitle ?? clientName`, version as today.
- `SessionPage`: a "Workspace roots" fact next to `cwd`. Each root shows the
  local path for `file://` URIs (the raw URI otherwise) and its `name` when
  present. NULL shows `—`; `[]` shows `none`.
- `StatusBadge` / `OperationPage`: `describeError` in `lib/errors.ts` describes
  `cancelled` with the reason: "Cancelled by the client", "Cancelled because the
  MCP host shut down", "Cancelled when the call time budget ran out", or the
  existing generic text when the reason is NULL.
- Call metadata: `OperationSummary` gains `agentModel` and
  `agentReasoningEffort`; `OperationDetail` gains `agent` (`callId`, `model`,
  `reasoningEffort`, `sessionId`, `turnId`, `turnTrigger`, `threadSource`, or
  NULL when all are NULL). `OperationPage` shows an "Agent call" card with those
  facts; the operations table shows the model next to the agent kind when known.
- `ui/dist` is not tracked in git; the normal build produces it.

## Error handling

The journal rule holds: nothing here changes output or exit codes.

| Situation | Behaviour |
|---|---|
| Client without `capabilities.roots` | No request sent; `roots_json` NULL; no log line |
| `roots/list` error, timeout, malformed result | Previous snapshot kept; content-free stderr diagnostic |
| Host shutdown during a fetch | Fetch cancelled with the lifetime; no journal write after shutdown |
| Reason provider throws | `cancel_reason` NULL; outcome unchanged |
| Journal write fails | Existing best-effort behaviour |

## Documentation

- `AGENTS.md`, journal paragraph: MCP sessions also record `clientInfo.title`
  and the client's workspace roots; cancelled operations record whether the
  client or a host shutdown cancelled them.
- `docs/mcp.md`: describe the handshake root requests and refreshes. Describe
  `2026-07-28` roots according to the Task 5 probe outcome; a capability
  declaration alone does not prove a snapshot can be fetched. Client roots
  never widen `--input-root` and never authorize a file input.

## Testing

Test-first for every unit.

- `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`: v5 → v6 migration on
  an existing database; title and roots upsert rules (NULL does not erase,
  non-NULL overwrites, `[]` distinct from NULL); `cancel_reason` round trip.
- `JournalingModule` tests: provider returns `client`, `shutdown`, `deadline`,
  NULL; a throwing provider gives NULL and the outcome is unchanged; an exit-5
  outcome with a cancelled token is recorded as `cancelled`; the same outcome
  with an uncancelled token stays an SQL failure; exit `0`/`7`/`8` with a
  cancelled token are not rewritten.
- `tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs`: `notifications/cancelled`
  during a tool gives `client`; stdin EOF gives `shutdown`; both give
  `shutdown`; an exhausted call budget gives `deadline`.
- New `tests/SqlHarness.Mcp.Tests/McpRootsTests.cs` with an in-process client
  whose `RootsHandler` answers: snapshot stored; `list_changed` overwrites; a
  slow older response does not overwrite a newer one; client without `roots`
  receives no request and leaves NULL; error and timeout keep the previous
  snapshot; limits 32 / 2048 / 256.
- `McpStderrLeakRegressionTests`: no root URI or name reaches stderr.
- `McpJournalTests.cs`: `clientInfo.title` reaches `sessions.client_title`.
- `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`: new fields, NULL on
  pre-v6 rows.
- UI (vitest): `SessionPage.test.tsx` (roots list / `—` / `none`, title label),
  `StatusBadge` cancellation wording, `fixtures.ts` updated.
- Gates: `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1`, run
  one after the other, both green.

## First plan step: real-client check

Before implementation, start `mcp serve` under Claude Code and under Codex and
record whether each sends `clientInfo.title` and declares `capabilities.roots`
(and `listChanged`). The result does not change the design; it sets
expectations for what the dashboard will show.

## Agent kinds and dashboard agent filter

- `AgentKindFromClientName` also returns `copilot` (name contains "copilot",
  e.g. `copilot-cli`), `opencode` and `grok` (Grok Build sends
  `grok-shell-<server name>`); process-tree classification recognises
  `copilot` (native, or node with `@github/copilot`) and `opencode` (native, or
  node/bun with `opencode-ai` / `@opencode/cli`) and `grok` (native). Other
  names stay `other`.
  Existing `other` rows are not rewritten (single-user journal).
- Dashboard: `GET /api/agents` returns the agent kinds present with session
  counts. The Sessions page shows "All" plus only those kinds (known kinds
  first, then other names, then `other`, then `unknown`), resetting to "All"
  when the selected kind disappears. Labels: Claude Code, Codex, Copilot,
  opencode, Grok, Other, Unknown; unrecognised kinds show raw. The same labels are
  used wherever the dashboard prints an agent kind.

## Upgrade overhead fixes

- The session identity is resolved per operation (protocol upgrade), but the
  process tree is walked once per serve process; client info is applied on top
  (`SessionIdentities.WithMcpClient`).
- `McpProtocolVersionRewriteInput` skips JSON parsing for frames that do not
  contain `"initialize"`.
- Accepted as is: a frame above 16 MiB ends the transport instead of rejecting
  one request (inline payloads above 1 MiB already must be files).

## Follow-ups (separate specs, in this order)

1. **MCP protocol upgrade and stderr noise** — merged to `main`
   in `dd697de`. The host now
   negotiates Claude Code's `2026-07-28` and Codex's `2025-06-18`, records
   request-scoped client identity, and keeps SDK stderr at `Warning` and above.
2. **Claude Code transcript matcher (opt-in, default off)** — resolve
   `message.model` by `client_call_id` in
   `~/.claude/projects/<escaped cwd>/*.jsonl`, also retroactively. Possibly
   later: more transcript facts or a link that opens the transcript; undecided.
3. **Analytics and reliability signals** (approved 2026-10-10):
   - dashboard analyses on data already collected: p50/p95 duration per tool,
     repeated `sql_hash` within a session or turn, call sequences per turn, gain
     per operation kind, tokens per returned row (flagged as approximate for
     plans, aggregates and multiple result sets);
   - new collection: a budget-truncation flag per operation, journaling of
     rejections before the module runs (busy, invalid scope, oversized input),
     and MCP handler time next to module time (medium priority);
   - rejected: `tools/list` size and request-size accounting (cost estimation is
     out of scope; Claude Code defers MCP tool schemas behind `ToolSearch`, so
     the cost is often not incurred).
