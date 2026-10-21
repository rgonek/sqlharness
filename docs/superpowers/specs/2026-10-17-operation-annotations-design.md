# Operation annotations by agents and owner — design

Date: 2026-10-17. Plan: 085. Depends on plan 077 (MCP envelope v3, tool set), 079 (journal floor) and 084 (agent
sessions). Origin: consilia 2026-10-17 (three rounds, Opus and Codex) and owner decisions.

## Intent

Beyond SQL optimization, agents run investigations across databases; most queries are dead ends and one or two
matter. The owner later reuses the useful ones. Agents name operations and mark the useful ones; the owner reviews
and reuses them in the dashboard. SQLHarness already saved tokens versus ad-hoc harness code; this spends a little of
that on reviewable, reusable investigations.

## Owner decisions

- Agent can label and star **at call time** and **annotate after the fact** (rename, star, un-star), because it may
  later realize an operation was a dead end.
- The owner gets their own star and note on operations, separate from the agent's.
- One switch enables all agent-facing parts; it is **independent of `journal.storeSensitive`** (enabling it is consent
  to store label text). Reuse/replay of SQL depends on `storeSensitive` (SQL is stored only then).
- Handle for post-hoc annotation: the existing **`operationRef`**, returned only when the feature is on.
- Scope is enforced by SQLHarness only; the agent learns nothing about it beyond "not available".

## Goals

1. **Config** `journal.operationAnnotations` (default `false`). Off ⇒ no `label`/`keep` schema members, no annotation
   tool, no `operationRef` in results, no capability member, no mention in descriptions; attempted use rejected.
   Config is read at process start (restart + reconnect to change).
2. **Call-time arguments** on `query`, `measure`, `compare`, `watch`, `snapshot`: `label` (plain text, ≤ 120 chars,
   control characters and newlines rejected) and `keep` (boolean agent star). Meaning lives only in the parameter
   descriptions. Not on `mutation`.
3. **`operationRef` in results** when on: optional envelope-level field (077 v3 "only when populated"), emitted only
   when a journal row exists, counted inside the byte budget before serialization checks, never clipped, also in
   minimal error fallbacks. Requires a documented amendment to 077 D1 (which removes refs unconditionally).
4. **Annotation tool** (new, only when on): input `{ items: [ { operationRef, label?, keep? } ] }` (1–20 items;
   omitted field = unchanged; `label: ""` clears; idempotent). Result per item: `{ operationRef, tool, label, keep }`
   (echo of tool name and resulting label so a wrong copied ref is visible) or the uniform error
   `operation_not_available`. The annotation call is journaled for cost accounting, returns no handle of its own and
   cannot be annotated.
5. **Scope rule (server-side):**
   - Matching off: the target belongs to the caller's MCP/CLI session.
   - Matching on and the caller's current call resolves to a conversation (Codex `agent_session_id` immediately;
     Claude `client_call_id` via 084's matcher, with at most one bounded on-demand lookup of the caller's own call id):
     the target has an **exact** link to the same conversation.
   - Caller unresolved: the target belongs to the caller's MCP/CLI session **and** is not known to belong to a
     different conversation.
   - Inferred membership never authorizes. Unknown, expired, foreign and out-of-scope refs return byte-identical
     `operation_not_available`; malformed refs fail format validation. Authorize and write in one transaction.
6. **Storage:** agent annotations (original call-time label, current label, keep, updated_at) separate from owner
   annotations (star, note) on operations; retention deletes them with their operation.
7. **Dashboard:** owner star/note on operations; agent `keep` visually distinct; "Named operations" view (label,
   agent keep, owner star, tool, database, status, date, agent session/MCP session) with text search across sessions;
   labels rendered as plain text; reuse actions (copy SQL / plan 079 scripts) only when SQL was stored, else
   `SQL not stored`.

## Calls settled after the builder check (my calls)

1. **Unlinked target in the caller's session** (the common "query, then annotate" case): allowed when the target
   belongs to the caller's MCP/CLI session and has no exact link to a different conversation. This supersedes the
   on-demand transcript lookup: **the MCP process never reads transcripts**; scope uses journal data only
   (`operation_agent_links`, `operations.agent_session_id`).
2. **Caller resolution:** a caller is resolved immediately when its call carries a client conversation id
   (`agent_session_id`: Codex, OpenCode). A Claude caller (only `client_call_id`, not yet linked at call time) is
   resolved to the conversation of the caller's MCP session **only when every exactly linked operation of that MCP
   session points to the same single conversation**; if the session has links to several conversations (shared
   transport, e.g. subagents or `/clear`), the caller is unresolved and the "caller unresolved" rule applies.
3. **Matching flag** (`dashboard.agentTranscripts.enabled`) is re-read on every annotation call (authorization input);
   `journal.operationAnnotations` stays process-start.
4. **Which tools:** `operationRef` is emitted, and targets are annotatable, only for `query`, `measure`, `compare`,
   `watch`, `snapshot`. Mutation, artifact, plan, capabilities and dry-run results carry no ref and are not targets.
5. **MCP only.** No CLI `annotate` command; CLI operations are annotated by the owner in the dashboard.
6. **Order and 077:** implement after 077 is merged and after plan 084; amend 077's D1 contract docs within plan 085;
   `schemaVersion` stays 3 (optional field).
7. **Completion gate** = deterministic byte measurements (tools/list and results on/off, golden off output). Paired
   agent trials and the second-session reuse trial are optional, operator-authorized evaluation; the baseline is the
   current practice of the agent writing useful SQL into a work folder.
8. **Owner features** (operation star/note, Named operations view) are always available in the dashboard,
   independent of the switch; the view lists labelled, agent-kept or owner-starred operations.
9. Config with annotations on but journal disabled is rejected at startup; the label limit counts Unicode code points;
   a batch with any failed item sets `isError` false but reports per-item status.

## Non-goals

- Reading annotations back to agents (later; prompt-injection risk; would need a separate, explicit read surface).
- A ticket/work-item field (put the key in the label or session title; a generic `group` may come later).
- Annotating pre-module rejections (not journaled today).

## Anti-goals

- Labels influencing safety classification, mutation approval or any policy.
- Any byte change to MCP output or tools/list when the feature is off (golden test).
- Agent annotations overwriting owner annotations.

## Constraints

- Lands after 077; the tool count pin (`McpLimits.cs`, budget tests) becomes "9, or 10 when annotations are on";
  tools/list with the feature on stays within 32 KiB.
- Measurement: tools/list bytes on/off; 077-style paired agent trials on/off; a second-session reuse trial against a
  work-folder baseline. Byte counts reported separately from provider tokens.
- The bundled skill gets no text unless trials show agents under-label (then one conditional sentence).

## Amendment 2 — compact annotation receipts (Plan 094, 2026-10-20)

The conditional `sqlharness_annotate` tool now defaults to a compact `receipt` response. Each input retains its ordered status and opaque reference; successful outcomes include tool and keep state without echoing labels, while unavailable outcomes retain only reference and status. Callers can request `responseDetail: "full"` to receive resulting labels. The optional result budget cannot exceed the operator-owned process ceiling. Full-detail responses that exceed the effective budget roll back all annotations before commit and report a supported next action. The MCP envelope remains v3 and annotation-off output remains byte-identical to the pre-amendment golden.

Plan 094 records deterministic served-workflow UTF-8 byte and call-count measurements separately from model tokens. Its results describe a fixture, not provider tokenization, an actual-agent trial, or live-database behavior.
