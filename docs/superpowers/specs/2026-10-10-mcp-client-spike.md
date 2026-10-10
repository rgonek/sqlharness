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

## Post-upgrade client matrix (2026-10-10, build `dd697de`)

Same tap method, isolated `SQLHARNESS_HOME`, each client calling
`sqlharness_capabilities` and `sqlharness_gain` once.

| Client | Negotiated | `clientInfo` (name / title) | Capabilities | `tools/call._meta` (besides `progressToken`) | Journal `agent_kind` |
|---|---|---|---|---|---|
| Claude Code 2.1.296 | `2026-07-28` via `server/discover`; also opens `subscriptions/listen` | `claude-code` / `Claude Code` | `roots {listChanged}`, `elicitation` | `claudecode/toolUseId` + `io.modelcontextprotocol/*` | `claude` |
| Codex 0.162.1 | `2025-06-18` (`initialize`) | `codex-mcp-client` / `Codex` | `experimental.codex/auth-change`, `elicitation` | `callId`, `x-codex-turn-metadata` (model, effort, …), ids | `codex` |
| Codex 0.162.1 + `features.mcp_2026_07_28=true` | still `2025-06-18`: the flag is recognised ("under development") but does not change the stdio handshake | same | same | same | `codex` |
| Copilot CLI 1.0.95 | `2026-07-28` via `server/discover`; opens `subscriptions/listen` | `copilot-cli` / none | `sampling`, `elicitation` | `io.modelcontextprotocol/*` only, no model | `other` |
| opencode 2.0.26 | `2025-11-25` (`initialize`) | `opencode` / none | `roots {}` (no `listChanged`), `elicitation` | `ai.opencode/sessionID` | `other` |
| Grok Build 1.0.50 | `2025-11-25` (`initialize`) | `grok-shell-<server name>` (here `grok-shell-acc`) / none | `extensions.io.modelcontextprotocol/ui` (MCP Apps), `elicitation` | nothing | `other` |

`sqlharness_capabilities` reported the negotiated revision and the three
supported revisions for every client. Stderr: no lines, except one content-free
`Warning` when Claude Code ended its `subscriptions/listen` stream at shutdown.
opencode's default model (`claude-haiku-5-5` on the Go plan) failed with a
usage limit; free models `opencode/nemotron-3-ultra-free` and
`opencode/step-5-preview-free` completed the run. Grok Build loads project MCP config (`./.grok/config.toml`) only in a trusted folder and has no one-off `--mcp-config` flag; the run trusted the scratch folder in `~/.grok/trusted_folders.toml` for its duration and restored the file byte for byte. Without that, the Grok agent (with `--always-approve`) tried to trust the folder itself.
