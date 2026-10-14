# Dashboard session curation (Spec A)

Status: design agreed with the operator, 2026-10-14. Implemented by Plan 069. Order: after Plan 062; this spec (A) → [agent transcript matching](2026-10-14-dashboard-agent-transcript-matching-design.md) (C) → [compare review](2026-10-14-dashboard-compare-review-design.md) (B).

## Goal

The operator organises agent sessions in the dashboard: gives a session a title and a note, stars it so it stays pinned at the top, archives it out of the default list, and finds it again by searching. These are operator-authored data; agents never write them.

## Decisions

- Storage: the existing journal `~/.sqlharness/data/activity.db`, in a **separate table** `session_annotations`, not new columns on `sessions`. Agent-recorded data and operator-authored data stay apart. A separate database was considered and rejected: the same file gives one-query JOINs for pinning, search and cursor paging; the residual risks (below) are equal or smaller.
- Retention is not changed: when `JournalRetention` deletes a session, its annotation goes with it. A star does not protect a session (a size cap could otherwise never be met). README states this.
- Archive only affects the Sessions list. Live, Operations and Stats keep counting archived sessions. Archiving does not clear the star.
- Title from the agent (Spec C) is not part of this spec; the display-name rule leaves a slot for it.

## Data

Next journal schema version after the one Plan 062 introduces (062 adds compare context columns; take the current `JournalSchema.CurrentVersion + 1` at implementation time). Migration runs in `ActivityJournal.Migrate()` like every other version.

```sql
CREATE TABLE session_annotations (
    session_id INTEGER PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
    title TEXT,                       -- 1..200 chars after trim; empty is stored as NULL
    note TEXT,                        -- plain text, multi-line, max 10 000 chars; empty is NULL
    starred INTEGER NOT NULL DEFAULT 0,
    archived_at TEXT,                 -- NULL = active; ISO-8601 UTC like other journal timestamps
    updated_at TEXT NOT NULL
);
CREATE INDEX ix_session_annotations_starred ON session_annotations(starred) WHERE starred = 1;
```

A row with no title, no note, not starred and not archived may be deleted instead of kept. Retention's orphan/session deletion must delete the annotation row in the same batch (verify foreign-key behaviour in `JournalRetention`; do not rely on cascade if foreign keys are off for that connection).

Display name (computed at read time, never stored): `annotation.title ?? agent conversation title (Spec C) ?? session_key`.

## Writes from the dashboard

The dashboard currently opens the journal read-only (`JournalReader.OpenReadOnly`). Add one small writer used only for annotations:

- Opens its own read-write connection with the journal's pragmas (WAL is already set by the journal; `busy_timeout` comparable to `ActivityJournal`).
- Checks `PRAGMA user_version == JournalSchema.CurrentVersion` before writing; otherwise refuses (409). The dashboard never runs migrations.
- Writes are single short transactions; they must never block or slow agent journal writes beyond the existing busy budget.

Risks accepted: retention deletes annotations; deleting `activity.db` deletes them; a dashboard binary older/newer than the journal schema refuses to write.

## API

- `GET /api/sessions` gains:
  - `archived=false|true` (default `false`);
  - `q`: case-insensitive substring over title, note, `cwd`, `session_key`, `client_title` (finds ticket keys like `BSI-12345`). Bound the length of `q` (e.g. 200) and escape `LIKE` wildcards.
  - `starred=true|false` filter. The UI shows starred sessions as a separate pinned list above the regular one (two filtered requests, each paged with the existing id cursor), so paging never crosses the starred/unstarred boundary. Without the filter the order is `starred DESC`, then the existing order (first page only; documented).
- `PATCH /api/sessions/{id}/annotation` with a partial body `{ title?, note?, starred?, archived? }` (`archived: true` sets `archived_at = now`, `false` clears it). Goes through the existing `DashboardSecurity` token/origin checks like `PUT /api/settings`. Responses: 200 with the updated annotation; 400 validation (length, unknown fields, wrong types); 404 unknown session; 409 schema version mismatch, with a readable message.
- `SessionSummary` gains `title` (display-ready user title, nullable), `starred`, `archived`, `hasNote`; `SessionDetail` additionally carries `note`. C#/TS types pinned by the existing contract test.

## UI

Sessions list:
- Search box (debounced) and an **Active / Archived** toggle above the agent tabs.
- First column: star icon button (toggles). Starred rows come first, separated by a thin divider.
- Session column shows the display name; when a title exists, `session_key` appears beneath it in small muted text. A note icon with the first note line as tooltip when a note exists.
- Row end: Archive / Unarchive icon button.

Session page:
- Header: inline-editable title (click or pencil; Enter saves, Esc cancels, empty falls back to the next display-name source), star, Archive button; an "Archived" badge when archived.
- **Note** card: textarea, Save/Cancel, character counter, "Saved" confirmation. Collapsed to an "Add note" button when empty.
- Optimistic updates of the session detail in the TanStack Query cache; lists refetch after the save. On error roll back and show the server message inline (destructive alert next to the control; the dashboard has no toast component).
- Icon buttons have accessible labels and are keyboard-operable.

## Tests

- xUnit: migration from the previous version keeps all rows and creates the table; retention removes annotations with their sessions; PATCH validation, 404, 409, token/origin rejection; starred-first ordering with cursor paging over 500+ sessions without duplicates or gaps; `q` (including `%`/`_` literal) and `archived` filters; contract test of C#/TS types.
- Vitest: SessionsPage (search, Active/Archived, star toggle, pinned order), SessionPage (title edit/cancel/clear, note save, archive, rollback on error).
- Both gates green: `pwsh ./scripts/verify.ps1`, then `pwsh ./scripts/verify-linux.ps1`.

## Documentation

README: Activity journal (operator annotations live in the journal; retention deletes them; dashboard writes only this table) and dashboard section. `Capabilities.cs` and `skills/sqlharness/SKILL.md` unchanged (not agent-facing). `plans/README.md` row for the plan.

## Out of scope

Tags/labels beyond the star, bulk actions, Markdown rendering of notes, protecting starred sessions from retention, agent titles (Spec C).
