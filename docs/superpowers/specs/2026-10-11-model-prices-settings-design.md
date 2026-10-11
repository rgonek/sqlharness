# Model prices, Settings autosave, persistent dashboard token, and removing `gain`

Status: approved design, pending implementation plan. **Start only after
`feat/mcp-session-metadata` (plan `2026-10-10-mcp-session-metadata.md`) has
merged**: both change the acceptance script, MCP tests and the Settings page.

## Goal

1. Estimate token savings in USD in the dashboard, from a model-price table
   that ships with every release and can be refreshed from the dashboard.
2. Make Settings save without a Save button, without ever persisting a
   half-typed value that could delete journal data.
3. Make the dashboard reachable from a bookmark and let an open tab recover by
   itself after the dashboard restarts (the behaviour the user sees in Aspire).
4. Keep the CLI to agent commands plus `dashboard`: remove `gain` from the CLI
   and from MCP.

## Decisions (2026-10-11)

- Price source: models.dev (`https://models.dev/api.json`, MIT, repository
  `anomalyco/models.dev`). Its model ids match what clients report (`gpt-6-sol`,
  `claude-haiku-5-5`, `opencode/step-5-preview-free`). Rejected: OpenRouter
  (different ids, reseller prices), LiteLLM (mixed licence, many deployment
  variants), a hand-maintained table (misses Chinese and free models).
- Distribution: a snapshot in the repository and binary, refreshed only by a
  button in dashboard Settings. No CLI command, no automatic network access.
- Valuation: saved tokens × **today's** input price at display time; no price
  history, no output, cache or turn accounting.
- Savings appear only in the dashboard (Statistics), not in any agent output.
- Settings: autosave with safeguards (below).
- Dashboard token: persistent token and persistent cookie; open tabs recover.
- `gain` is removed from the CLI and from MCP (MCP goes from 11 to 10 tools).

## Model prices

### Data

- `src/SqlHarness.Core/Pricing/model-prices.json` (checked in, embedded as a
  resource): the cost fields of every models.dev provider and model, about
  630 KB. Shape:
  `{ "source": "models.dev", "fetchedAt": "<ISO-8601>", "providers": { "<provider>": { "<model>": { "input": n, "output": n, "cache_read": n?, "cache_write": n?, "tiers": [...]? } } } }`,
  costs in USD per 1M tokens as models.dev publishes them. Models without
  `cost` are left out.
- `scripts/update-model-prices.ps1` regenerates the file from models.dev before
  a release; the change is reviewed as an ordinary diff. Builds never download.
- Refreshed copy: `~/.sqlharness/data/model-prices.json`, same shape, written
  owner-only. It wins over the embedded snapshot when it parses and its
  `fetchedAt` is newer; otherwise the embedded snapshot is used.
- Only `input` is used for valuation; the other fields are kept so later work
  does not need a new format.

### Lookup

Inputs: the operation's agent kind and its model id (`operations.agent_model`
from the session-metadata work; later also the Claude transcript matcher).

1. Model id of the form `provider/model` (opencode): look up `provider` and
   `model` directly.
2. Otherwise map the agent kind to a provider: `claude` → `anthropic`,
   `codex` → `openai`, `grok` → `xai`; look the model up there.
3. Otherwise, or when step 2 misses: look the model id up in the primary
   providers `anthropic`, `openai`, `google`, `xai`, in that order. Never pick a
   reseller (Bedrock, Azure, OpenRouter, …) by fallback.
4. No match: the operation is "unpriced".

### Valuation and display

- Saving of an operation = (`raw_tokens` − `emitted_tokens`) × input price / 1e6,
  only for operations with both footprints and a priced model. Negative savings
  (output grew) count as negative.
- Dashboard Statistics gets a "Savings (USD)" card: total, a per-model
  breakdown, and counts of operations without a model and with an unpriced
  model. The card states "at current prices (<source>, <date>)". A missing
  model is information, not an error.
- Computation lives in the dashboard reader/API (one new stats field); Core
  provides the price table and lookup.

### Refresh from Settings

- Button "Refresh model prices" in Settings with the current source and date
  next to it.
- New endpoint `POST /api/prices/refresh`: the dashboard's second write
  endpoint, with exactly the protections of `PUT /api/settings` (token cookie,
  `X-SqlHarness-Dashboard` header, same-origin `Origin`).
- The server fetches `https://models.dev/api.json` with a 30 s timeout and a
  20 MB response limit, extracts the cost fields, validates the shape, and
  writes the file atomically (temp file + replace). Failure leaves the current
  table untouched and returns a fixed, content-free error.
- This is the only network access SQLHarness makes besides database
  connections, and it happens only on that click.

## Settings autosave

- Toggles save immediately after a change.
- Number fields save on commit (blur or Enter) and only with a valid value; an
  invalid value shows its error at the field and is not written. Never on each
  keystroke: intermediate values such as `3` while typing `300` are valid and
  could make retention delete data.
- Changes that can delete data ask for confirmation in a dialog: enabling
  retention, and lowering `maxAgeDays` or `maxSizeMb` while retention is
  enabled. Cancel restores the previous value.
- Saves keep using `PUT /api/settings` with the whole settings document,
  serialised in a client-side queue so a slower earlier request cannot
  overwrite a later change.
- The Save button is removed. Each section shows "Saving…", "Saved" or an
  error. The existing note that new processes use the settings and running
  `mcp serve` sessions must restart stays.

## Persistent dashboard token and recovering tabs

### Today

The dashboard generates a new token per start and sets a session cookie
(`SameSite=Strict`). The browser is launched with an owner-only local file
(`~/.sqlharness/dashboard-open.html`) that redirects to `/?t=<token>`; the
server sets the cookie and redirects to `/`. Because the navigation started on
a `file://` page, the browser treats it as cross-site and does not send the
`Strict` cookie on the redirect, so `/` answers 401 "Open the dashboard with:
sqlharness dashboard". Pasting the token URL works (user-initiated
navigation). A restart invalidates the cookie, so bookmarks and open tabs stop
working. (Root cause to be confirmed by the first test in the plan.)

### Design

- Token: generated once, stored in `~/.sqlharness/dashboard-token`
  (owner-only, same helper as the journal); reused on every start. Deleting the
  file rotates it on the next start.
- Cookie: `HttpOnly`, `SameSite=Strict`, `Path=/`, `Max-Age` 400 days.
- `?t=<token>` with a valid token answers 200 with a minimal page that sets the
  cookie and calls `location.replace` to the same path without `t`; that second
  navigation is same-origin, so the cookie is sent. An invalid token still
  answers 401.
- Unchanged: loopback-only binding, `Host` check, `Origin` + header check for
  writes, launching the browser through the owner-only local file so the token
  never appears in a process list, printing the token URL on start.
- Recovering tabs: when requests or the live stream fail with a network error,
  the UI retries with backoff (capped, for example 1 s → 30 s) and shows a
  banner "Dashboard unavailable — reconnecting". When the server is back, data
  and the live stream resume without a reload. If nothing is listening, the
  banner adds that `sqlharness dashboard` or the next `mcp serve` with
  autostart starts it. A 401 (token rotated) shows "Open the dashboard with:
  sqlharness dashboard" instead of retrying forever.

### Threat model note

Against web pages in the user's browser, the `Host`/`Origin`/header checks are
the protection; the token adds nothing there. The token protects the journal
from other local OS users on shared machines. It does not protect against the
user's own processes, which can read `~/.sqlharness` anyway.

## Removing `gain`

- Remove the CLI command `gain`, the MCP tool `sqlharness_gain`, the Core gain
  operation, and `JournalGainStore`.
- Update `capabilities` output, `AGENTS.md` ("Exactly 10 tools"; drop the
  `gain` examples and the gain paragraphs of the journal bullet that only
  concern the command), `docs/mcp.md`, `README.md`, MCP tool-schema tests and
  the `tools/list` budget test.
- Tests and the acceptance script that used `gain` as a harmless offline call
  use `sqlharness_capabilities` instead.
- The dashboard is unaffected (its token statistics come from
  `JournalReader`). Existing journal rows for the `gain` operation stay as
  history.

## Error handling

| Situation | Behaviour |
|---|---|
| Refreshed price file missing, unreadable or malformed | Embedded snapshot is used; Settings shows its date |
| Refresh: network error, timeout, oversized or invalid response | Current table kept; fixed error text; nothing written |
| Model unknown or unpriced | Operation counted as unpriced; excluded from the USD total |
| Autosave rejected by validation | Field error; previous value stays in the file |
| Autosave request fails | Section error with retry; later queued saves still run in order |
| Token file unreadable | Dashboard start fails with a content-free error (same as other local-storage failures, exit 6) |
| Server unreachable from an open tab | Banner and capped backoff; resumes without reload |

## Testing

- Prices: snapshot parsing; lookup order (provider/model form, agent-kind
  mapping, primary-provider fallback, no reseller fallback); valuation with
  negative and missing footprints; refreshed file preferred only when newer and
  valid; refresh endpoint protections (missing header, foreign `Origin`, no
  cookie); refresh failure keeps the table; atomic write.
- Settings (vitest): toggle saves immediately; number field saves on blur and
  Enter only; invalid value not sent; confirmation for destructive changes and
  cancel restores; queued saves arrive in order; no Save button.
- Token: first test reproduces today's 401 after the `file://` launch page
  (fails before the fix); token persists across two server starts; cookie has
  `Max-Age`; `?t=` answers 200 with the hand-off page; invalid token 401;
  owner-only token file on Unix.
- Recovering tab (vitest + one host test): network error → banner and retries;
  server back → data resumes; 401 → no endless retry.
- `gain` removal: CLI command absent, MCP lists 10 tools, capabilities no
  longer mention it, acceptance script and smokes pass with
  `sqlharness_capabilities`.
- Gates: `pwsh ./scripts/verify.ps1`, then `pwsh ./scripts/verify-linux.ps1`.

## Out of scope

- Price history, output/cache/turn-based cost, savings in agent output.
- Trimming or compressing the single-file binary (138 MB, measured
  2026-10-11: .NET runtime 84 MB, ASP.NET Core 28 MB for the dashboard, SQL
  Server client and Entra auth 12 MB, ScriptDom 7 MB).
- A UI to rotate the dashboard token (deleting the file rotates it).
