# Agent sessions across MCP/CLI sessions — design

Date: 2026-10-17. Plan: 084. Depends on plan 070 (transcript matching, implemented), 079 (journal version floor) and
080 (Optimization section). Origin: consilium 2026-10-17 (Opus, Codex) and owner decisions.

## Intent

The owner works in long agent conversations (Claude Code, Codex) that span several SQLHarness MCP/CLI sessions
(reconnects, restarts). The meaningful unit is the **agent session** (one conversation). When transcript matching is
on, the dashboard shows agent sessions, the Optimization section (plan 080) works within an agent session, and the
operation-annotation scope (plan 085) follows it. When matching is off, everything stays per MCP/CLI session as today.

## Evidence (owner's journal, counts only, 2026-10-17)

- 267 operations, 110 linked to a conversation. No MCP session links to more than one conversation; one conversation
  spans two MCP sessions.
- Operations without identifiers are historical: Claude MCP 7–9 Oct (client 2.1.292–2.1.294) and CLI calls (CLI never
  carries call metadata); since 12 Oct every Claude MCP operation carries `client_call_id`. Codex: 8 operations carry
  `agent_session_id` but are unlinked — a matcher cursor bug (`TranscriptMatcher.cs` ~248-254: an index entry read
  before the thread's first SQLHarness operation is never re-read).
- External (Codex verified in Claude docs): Claude subagents can share the parent's MCP connection; Claude hooks
  receive `session_id` and `tool_use_id`. Unverified: whether `/clear` keeps the same MCP process.

## Goals

1. **Operation membership** in an agent session keyed `(agent_kind, conversation_id)`, with evidence:
   `exact` (per-operation link from a transcript tool-use id or a client conversation id), `inferred` (unlinked
   operation in an MCP/CLI session whose linked operations all point to exactly one conversation; CLI sessions via a
   shared `agent_pid` + `agent_started_at` with an MCP session that maps to exactly one conversation), or
   `unassigned`. Inferred membership is shown (marked) but never used for authorization (plan 085) or for
   Optimization version history.
2. **Codex linked immediately** from `agent_session_id` when matching is on: upsert the conversation row from the id
   (title optional, enriched later from the index). Fixes the cursor bug's 8 unlinked operations.
3. **Claude linked near-live:** the dashboard runs a targeted match cycle when the journal changes (`PRAGMA
   data_version`, debounced 1–2 s), limited to cwds with new pending call ids; the 60 s pass stays as a sweep.
   Transcript reading stays in the dashboard process, opt-in, bounded, allowlisted fields.
4. **Dashboard unit:** with matching on, the sessions list shows agent sessions as rows, expandable into their
   MCP/CLI sessions (one MCP session may appear under several agent sessions, each showing only its operations);
   sessions with nothing linked stay as top-level rows labelled `not matched`. The agent-session page aggregates its
   operations with a membership marker and a header count (`41 exact · 6 inferred · 2 unassigned`). With matching
   off: unchanged.
5. **Agent-session annotations** (owner title, note, star, archive) in a new table keyed `(agent_kind,
   conversation_id)`, not foreign-keyed to the disposable match cache (`Clear matched data`, retention). Title order:
   owner title → conversation title → newest child session title. MCP-session annotations remain on child rows.
6. **Optimization within the agent session (amends plan 080):** version history and comparison keyed by
   `(context, subject, scope)` where context is the agent session (exact members only) or the MCP session when
   matching is off. **Version labels are frozen** at first assignment (persisted per context + subject + scope + SQL
   hash), so late links never renumber `v0…vN`.
7. **Coverage diagnostics** in Settings: matched / inferred / unassigned counts by agent kind and transport, last
   cycle time.

## Calls settled after the builder check (my calls)

1. **Frozen version labels.** Labels are assigned chronologically per (context, subject, scope) so `v0` stays the
   earliest SQL. The dashboard matcher cycle persists (freezes) a label once every operation of that context that
   started before the version's first appearance has a final membership (exact link or definitively unassigned);
   until then labels are computed at read time and the UI says `versions may renumber while matching catches up`.
   Writer: the dashboard matcher (it already writes links); no writes from GET endpoints or the MCP process.
2. **Codex** links are upserted by the dashboard matcher from `agent_session_id` (cursor bug fixed); plan 085 resolves
   Codex/OpenCode scope directly from `operations.agent_session_id`, so it does not depend on the dashboard running.
3. **CLI and inferred compares** stay in the Optimization section of their own MCP/CLI child session; the agent-session
   page lists child sessions that have their own optimization history with a link. Agent-session Optimization uses
   exact members only.
4. **Unassigned operations** of a child session are listed under every agent session the child appears in, marked
   `unassigned`; Settings counters count each operation once.
5. **Lifetimes.** `Clear matched data` removes links and frozen labels (derived data) and keeps agent-session
   annotations. Retention deletes an agent session's annotations when its last operation expires, mirroring today's
   session annotations.
7. **Subagents belong to the parent agent session (owner's decision).** Claude subagent transcript lines carry the
   parent `sessionId`, so they fold in automatically. Codex subagent threads have their own id: a spike (does
   `agent_thread_source` or other metadata carry the parent thread id?) decides whether they fold into the parent;
   until then they are separate agent sessions annotated `subagent of unknown parent` when the source says subagent.
6. Inferred CLI rule: all MCP sessions sharing the CLI session's `agent_pid` + `agent_started_at` point to one
   conversation; otherwise unassigned.

## Non-goals

- Claude Code hooks as a required mechanism (possible optional accelerator later).
- Transcript reading in the MCP process (plan 085 may add one bounded on-demand lookup for its own call).
- Process ancestry as conversation evidence.

## Anti-goals

- Moving unlinked operations into a conversation silently (without the `inferred` marker).
- Authorizing anything from inferred membership or process identity.
- Losing owner annotations when matched data is cleared.

## Constraints

- Journal changes use 079's `FirstRetainedVersion` rule (next free version, step script, contract pin).
- Matching remains opt-in (`dashboard.agentTranscripts.enabled`); turning it off restores per-session views
  immediately (cached links ignored for scope).
- No auto-formatting; `just verify`.
