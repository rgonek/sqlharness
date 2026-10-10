# MCP client spike: what Claude Code and Codex send

Date: 2026-10-10. Build: `main` at `4b9147e`, published with
`scripts/publish-local.ps1`. Method: a transparent stdio tap between the client
and `sqlharness mcp serve local-playground` logged raw JSON-RPC frames; each
client made one `sqlharness_gain` call in a headless run (`claude -p
--mcp-config ... --strict-mcp-config`, `codex exec -c mcp_servers.spike...`), so
no client configuration file was changed.

## Claude Code 2.1.296

- First frame is a `server/discover` probe with protocol `2026-07-28`; SQLHarness
  answers with an error and the client falls back to `initialize` with
  `2025-11-25`. Harmless, but it costs one round trip per session.
- `initialize.clientInfo`: `name = "claude-code"`, `title = "Claude Code"`,
  `version`, `description`, `websiteUrl`.
- `initialize.capabilities`: `roots: { listChanged: true }`, `elicitation`.
- `tools/call._meta`: `claudecode/toolUseId` (for example `toolu_0145mJ…`) and
  `progressToken`. No model name.
- Transcript `~/.claude/projects/<cwd with separators replaced by ->/<sessionId>.jsonl`:
  the assistant entry whose `message.content[].type == "tool_use"` has
  `id == claudecode/toolUseId` also carries `message.model`
  (`claude-haiku-5-5` in this run), `sessionId`, `cwd`, `timestamp`,
  `isSidechain`. Exact correlation key; the server does not learn `sessionId`,
  so a lookup must search the project directory for the tool-use id.

## Codex 0.160.0

- `initialize` with protocol `2025-06-18`; `clientInfo`: `name =
  "codex-mcp-client"`, `title = "Codex"`, `version`.
- `initialize.capabilities`: `experimental["codex/auth-change"]`, `elicitation`.
  **No `roots`.**
- `tools/call._meta`: `callId`, `threadId`, and `x-codex-turn-metadata` with
  `model` (`gpt-6-sol`), `reasoning_effort`, `session_id`, `thread_id`,
  `turn_id`, `turn_trigger`, sandbox fields, `codex_version`, and
  `turn_started_at_unix_ms`. **The model arrives per call, no transcript needed.**
- Rollout `~/.codex/sessions/YYYY/MM/DD/rollout-*-<session_id>.jsonl` has
  `turn_context.model` per turn as well; not needed given the `_meta` field.

## Consequences

- Spec 1 (title, roots, cancel reason): both clients send `title`. Claude Code
  sessions will show roots; Codex sessions keep `roots_json` NULL.
- Model name (separate spec): Codex needs no disk access — read an allowlisted
  `_meta["x-codex-turn-metadata"].model` (and possibly `reasoning_effort`).
  Claude Code needs the opt-in transcript lookup keyed by
  `_meta["claudecode/toolUseId"]`.
- `_meta` must be read through an allowlist; Codex metadata also carries
  session, sandbox and account-adjacent fields that should not be stored.
