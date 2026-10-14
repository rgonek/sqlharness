# Dashboard agent transcript matching (Spec C)

Status: design agreed with the operator, 2026-10-14. Implementation plan: not yet written. Order: after Plan 062 and [session curation](2026-10-14-dashboard-session-curation-design.md) (A); before [compare review](2026-10-14-dashboard-compare-review-design.md) (B).

## Goal

Fill gaps in what the journal knows about the agent by reading the agent's own local transcripts, behind an opt-in setting. Extract exactly two things:

1. **Conversation title** (Claude `aiTitle`, Codex `thread_name`) as the default session title. The operator's title from Spec A always wins.
2. **Model and reasoning effort per operation** for agents that do not send them in the call (today: Claude Code). Codex already sends `model`/`reasoning_effort` in `x-codex-turn-metadata`.

Git branch and token usage were considered and left out.

## Findings (2026-10-14, local inspection of keys only)

- Claude Code sends `_meta["claudecode/toolUseId"]`; `McpCallMetadata` stores it as `operations.client_call_id` (present on 22 of 94 Claude operations; older ones lack it). The same value is the `id` of a `tool_use` content block in `~/.claude/projects/<encoded cwd>/<sessionId>.jsonl`. Transcript lines carry `sessionId`, `cwd`, `message.model`, `effort`, and `ai-title` entries with `aiTitle`.
- Codex sends `x-codex-turn-metadata.session_id` (stored as `agent_session_id`) plus model and effort (27 of 57 Codex operations). `~/.codex/session_index.jsonl` has `{id, thread_name, updated_at}`; `~/.codex/state_5.sqlite` table `threads` has `title` too.
- One SQLHarness MCP session can serve several agent conversations (e.g. Claude `/clear` keeps the MCP process). Matching is therefore **per operation**, and a session may map to several conversations.

Verify the exact entry type names (`ai-title`, a user-set custom title if present) and the `cwd` → directory encoding against current transcripts at implementation time; these are external, unversioned formats.

## Setting

`dashboard.agentTranscripts.enabled`, default `false`, persisted through the existing config writer and shown in Settings as a toggle with the text "Reads local Claude/Codex transcripts; only titles, model and effort are extracted." Turning it off stops new matching immediately; already matched data stays until the operator presses **Clear matched data**. Settings also shows counters: matched, unmatched, read errors, last cycle time.

## Matching (exact only)

- **Claude:** for operations with `client_call_id` and a session `cwd`, look in `<claude home>/projects/<encoded cwd>/*.jsonl` (claude home: `CLAUDE_CONFIG_DIR` or `~/.claude`). A `tool_use` block whose `id` equals the call id identifies the conversation (`sessionId`), and the enclosing assistant entry gives `message.model` and effort when present. Conversation title: a user-set title if the format has one, else the latest `aiTitle`.
- **Codex:** for operations with `agent_session_id`, read `<codex home>/session_index.jsonl` (codex home: `CODEX_HOME` or `~/.codex`); the last entry per `id` gives `thread_name`. Model/effort come from the journal already. Do not depend on the versioned `state_N.sqlite` file.
- Operations without an identifier stay unmatched. No time-window/`cwd` heuristics.

## Execution

- Runs in the dashboard process on a background loop, only while enabled: at start and every 60 s.
- Incremental: per transcript file keep `(path, size, mtime, offset)`; read only appended bytes; a truncated/rewritten file (size shrank or mtime moved back) is re-read from the start. Ignore a partial last line until it is complete.
- Open files with read + write/delete sharing; the agent is still appending.
- Only directories for `cwd`s of sessions that still have unmatched identifiable operations are scanned.
- Budget per cycle (e.g. 32 MiB read); the remainder waits for the next cycle so the dashboard stays responsive.
- Missing directories, unreadable files and unparsable lines are counted, never thrown. A changed format degrades to "unmatched".

## Privacy

Parse one line at a time, extract only allowlisted fields (ids, title, model, effort), and drop the rest immediately. Prompts, responses and tool results never reach the journal, logs or API. Titles are locally sensitive like the rest of the journal. Values are length-capped like `McpCallMetadata` (256).

## Data

Journal schema version after Spec A. Derived data only, never written into `session_annotations` or over existing `operations` columns.

```sql
CREATE TABLE agent_conversations (
    id INTEGER PRIMARY KEY,
    agent_kind TEXT NOT NULL,          -- 'claude' | 'codex'
    conversation_id TEXT NOT NULL,
    title TEXT,
    updated_at TEXT NOT NULL,
    UNIQUE (agent_kind, conversation_id)
);
CREATE TABLE operation_agent_links (
    operation_id INTEGER PRIMARY KEY REFERENCES operations(id) ON DELETE CASCADE,
    conversation INTEGER NOT NULL REFERENCES agent_conversations(id),
    model TEXT,
    effort TEXT
);
CREATE TABLE transcript_cursors (
    path TEXT PRIMARY KEY,
    size INTEGER NOT NULL,
    mtime TEXT NOT NULL,
    offset INTEGER NOT NULL
);
```

Retention deletes links with their operations and conversations no link references. **Clear matched data** empties all three tables. Writes use the dashboard writer introduced in Spec A (schema-version check, short transactions).

## Presentation

- Session display name: `annotation.title ?? title of the session's most recent linked conversation ?? session_key`. In the title editor the agent title is the placeholder, marked "from Claude"/"from Codex".
- Session page: a **Conversations** list (title, agent, operation count) when a session links to one or more conversations.
- Operation page, "Agent call" card: model/effort from the call as today; when only the transcript has them, show them with a "from transcript" marker.
- Stats and workflow cost: model = `COALESCE(operations.agent_model, operation_agent_links.model)`; the call value wins.
- `SessionSummary`/`SessionDetail`/`OperationDetail` gain the fields above; contract test updated.

## Tests

Fixture transcripts for Claude and Codex in a temp home (`CLAUDE_CONFIG_DIR`/`CODEX_HOME`): exact match; no match without id; incremental read after append; partial last line; truncated file; several conversations in one session; missing directories; read budget spreads work across cycles; setting off performs zero file reads; Clear matched data; retention removes links; Stats uses the transcript model only when the call has none; no transcript text other than allowlisted fields ever reaches the journal (assert on stored rows). Both gates green, run one at a time.

## Documentation

README (dashboard settings, what is read and stored, retention), `plans/README.md` row. Not agent-facing: `Capabilities.cs` and `skills/sqlharness/SKILL.md` unchanged.

## Out of scope

Heuristic matching, git branch, token usage/cost from transcripts, other agents (add later per agent with the same tables), writing anything back to agent files.
