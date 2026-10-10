# MCP protocol upgrade: negotiate 2025-06-18, 2025-11-25 and 2026-07-28

Status: approved design, pending implementation plan. Must merge before
`2026-10-10-mcp-session-metadata-design.md` is implemented.

## Goal

Let each MCP client use the newest protocol revision both sides support,
instead of forcing every client onto `2025-11-25`, without losing anything the
host records or guarantees today. Remove the SDK log noise from stderr in the
same change, because it lives in the same host layer.

## Evidence

`2026-10-10-mcp-client-spike.md` (real frames from Claude Code 2.1.296 and Codex
0.160.0) and the `ModelContextProtocol.Core` 2.2.0 documentation:

- SDK 2.2.0 is the latest package on NuGet and already implements
  `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25` and `2026-07-28`.
- The host sets `McpServerOptions.ProtocolVersion = "2025-11-25"`, which pins
  the server: Claude Code's `server/discover` probe for `2026-07-28` is
  rejected with `-32022` and the client falls back to `initialize`; Codex asks
  for `2025-06-18` and is answered with `2025-11-25`.
- `ProtocolVersion = null` makes the server support every SDK revision: an
  `initialize` request gets its requested version when supported (otherwise
  `2025-11-25`), and `server/discover` advertises `2026-07-28`.
- `2026-07-28` (SEP-2575) removes the `initialize` handshake: client info and
  capabilities travel in each request's `_meta`
  (`io.modelcontextprotocol/clientInfo`, `/clientCapabilities`). On that
  revision `McpServer.ClientInfo` and `McpServer.ClientCapabilities` are null on
  the root server and must be read from the request-scoped server
  (`RequestContext.Server`). The request-scoped server carries them on
  handshake revisions too.
- The host's stderr logger writes every SDK log event (Trace, Debug,
  Information) as a content-free line. It costs no model tokens (clients keep
  server stderr in their own logs) but is noise.

## Decisions

1. Supported revisions: `2025-06-18`, `2025-11-25`, `2026-07-28`, with real
   negotiation. `2024-11-05` and `2025-03-26` are not offered: an `initialize`
   asking for them is answered with `2025-11-25`, which the MCP lifecycle
   specification allows ("Otherwise, the server MUST respond with another
   protocol version it supports"); a client that cannot use it disconnects.
2. `sqlharness_capabilities` reports the version negotiated for the current
   request in its existing protocol-version field, plus a new
   `supportedProtocolVersions` list.
3. Stderr keeps only `Warning`, `Error` and `Critical` SDK events.
4. Tests: per-revision smoke for all three revisions, full existing suites on
   `2025-11-25`, and key scenarios repeated on `2026-07-28`. Real-client
   acceptance is a manual script, not CI.

## Components

### Negotiation

- `McpServerOptions.ProtocolVersion = null`.
- `McpHost.SupportedProtocolVersions = ["2025-06-18", "2025-11-25", "2026-07-28"]`
  replaces `McpHost.PinnedProtocolVersion` as the single source for the filter,
  the capabilities document and tests. `McpHost.FallbackProtocolVersion =
  "2025-11-25"`.
- New `McpProtocolVersionFilter`, registered in
  `McpServerOptions.Filters.Message.IncomingFilters`: for a JSON-RPC request
  whose method is `initialize` and whose `params.protocolVersion` is a string
  outside the two handshake revisions (`2025-06-18`, `2025-11-25`), it replaces
  that value with `2025-11-25` before the SDK handles the request. Every other
  message, and an `initialize` without a string version, passes unchanged (the
  SDK keeps rejecting malformed handshakes as today).
- Fallback if a message filter cannot change request parameters before the
  handler runs (verified first in the plan): apply the same rewrite in the
  host's input stream wrapper, next to `McpDuplicateJsonFieldGuardInput`.

### Client identity

- New process-level `McpClientIdentity` holder (first non-null wins, thread
  safe). Each tool handler records `ctx.Server.ClientInfo` from the
  request-scoped server before it runs the module; a null context (unit tests)
  records nothing.
- `McpHost` builds the session identity from the holder instead of
  `running?.ClientInfo`. The lazy, first-journaled-call resolution stays; by
  then the call has recorded its client info.
- Journal behaviour is unchanged: `client_name` / `client_version` keep the
  first non-null value per session.

### Capabilities document

- `McpCapabilitiesDocument` keeps its protocol-version field, now filled with
  `ctx.Server.NegotiatedProtocolVersion` of the current request, or
  `McpHost.FallbackProtocolVersion` when that is unavailable.
- New field `supportedProtocolVersions` = `McpHost.SupportedProtocolVersions`.
  The field is additive; existing agents that read the version field see
  `2025-11-25` until their client negotiates something else.

### Stderr

- `McpStderrLogger.IsEnabled(level)` returns true only for `Warning`, `Error`,
  `Critical`. The line format (category, level, numeric event id, no state, no
  formatter output) is unchanged; the comment states that the level gate reduces
  noise and is not the leak protection.

### Unchanged

Tools and their schemas, budgets, the execution gate, cancellation
(`notifications/cancelled` exists in all three revisions), progress
(`progressToken` per request), fixed and request-scope modes, the duplicate
JSON field guard, dashboard autostart, retention.

## Error handling

| Situation | Behaviour |
|---|---|
| `initialize` asks for `2024-11-05` / `2025-03-26` | Answered with `2025-11-25` |
| `initialize` asks for an unknown version | Answered with `2025-11-25` (SDK behaviour, unchanged) |
| `initialize` without a string `protocolVersion` | Passed through; SDK rejects as today |
| `2026-07-28` request without `io.modelcontextprotocol/clientInfo` | No identity recorded from it; the next request may supply it; journal falls back to process-tree identity if none ever does |
| Negotiated version unavailable in a handler | Capabilities reports `2025-11-25` |

## Documentation

- `docs/mcp.md` "Versions": supported revisions and negotiation, the
  `2025-11-25` answer for older requests, `server/discover` for `2026-07-28`,
  the capabilities fields. "Stdout and stderr": stderr carries SDK warnings and
  errors only.
- `AGENTS.md` MCP section: replace "Tested SDK `ModelContextProtocol` 2.2.0 with
  protocol revision `2025-11-25` only" with the three revisions and negotiation.

## Testing

- Filter feasibility first: a message filter rewrite of `initialize`
  `params.protocolVersion` is observed by the SDK handler.
- Negotiation: SDK client requesting `2025-06-18`, `2025-11-25`, `2026-07-28`
  gets the same version; requesting `2024-11-05`, `2025-03-26` gets
  `2025-11-25`; `2026-07-28` goes through `server/discover`.
- Per-revision smoke (all three): handshake or discover, `tools/list` returns
  the 11 tools, `clientInfo` reaches the journal, `sqlharness_gain`
  succeeds, a mid-flight cancellation reports cancelled and releases the gate,
  progress notifications arrive for a database call, stdin EOF ends the stdio
  host with exit 0.
- Capabilities: per revision, the version field equals the negotiated version
  and `supportedProtocolVersions` equals the full list.
- Identity regression: on `2026-07-28` the journal row has `client_name` and
  `client_version`.
- Stderr: a stdio process run through handshake and one tool call writes no
  `Trace`, `Debug` or `Information` line; existing leak tests stay green.
- Existing tests that used `McpHost.PinnedProtocolVersion` or a local
  `"2025-11-25"` constant request `2025-11-25` explicitly and keep their
  assertions.
- Gates: `pwsh ./scripts/verify.ps1`, then `pwsh ./scripts/verify-linux.ps1`.

## Real-client acceptance (manual, before merge)

`scripts/mcp-client-acceptance.ps1`, derived from the spike tap:

- Runs the built `sqlharness` behind a logging stdio tap, through
  `claude -p --mcp-config <temp> --strict-mcp-config` and `codex exec -c
  mcp_servers.<name>...`; never edits client configuration files.
- Each client calls `sqlharness_capabilities` once and the target-free
  `sqlharness_gain` operation once. Capabilities stays outside the journal;
  `gain` creates the operation row used to verify the session identity.
- Checks: Claude Code negotiated `2026-07-28` (via `server/discover`), Codex
  negotiated `2025-06-18`; each capabilities result reports its negotiated
  version; the `gain` operation joins to an isolated session with matching
  non-empty `client_name` and `client_version` from that client's `clientInfo`.
- Each server receives its own `SQLHARNESS_HOME` and synthetic profile so the
  acceptance run does not inspect the user's journal, profiles or credentials.
- Prints a short pass/fail table. Frames and journal rows stay local, and their
  paths and contents are not printed (frames contain tool results).

## Out of scope

- Session metadata (title, roots, cancel reason, `_meta` labels): the follow-up
  spec, re-checked after this merges.
- `subscriptions/listen`, MRTR server-to-client requests, resources, prompts,
  HTTP transport.
- Offering `2024-11-05` or `2025-03-26`.
