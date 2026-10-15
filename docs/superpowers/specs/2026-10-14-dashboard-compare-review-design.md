# Dashboard compare review (Spec B)

Status: implemented by Plan 071. Implementation plan: [Plan 071](../../../plans/071-dashboard-compare-review.md). Order: after Plan 062, [session curation](2026-10-14-dashboard-session-curation-design.md) (A) and [agent transcript matching](2026-10-14-dashboard-agent-transcript-matching-design.md) (C).

## Goal

After an agent optimises SQL, the operator opens a PR and shows the reviewer what changed: the SQL diff, reads per table, time/CPU, plan changes and the equivalence/regression verdicts. The dashboard is loopback-only, so the reviewer gets a Markdown copy rather than a link.

## Builds on Plan 062

062 delivers compare context (`subject`/`scope`), SQL versions per subject/scope, `CompareDetail`, `CompareSessionOverview` and a bounded `SqlDiff` (256 KiB total / 4000 lines, no diff package in that implementation). This spec replaces 062's `SqlDiff` rendering with the editor-based one, adds a small pure diff package, and keeps 062's limits, version model and the rule that different measurement conditions never form one trend.

## Editor

- Use `@monaco-editor/react` and `monaco-editor` in a provider module loaded lazily from `SqlEditors.tsx`. Keep provider-neutral `SqlViewer` and `SqlMergeView` props; Monaco types and APIs stay inside `sql-editor/monaco.tsx` so a later provider replacement does not change consumers.
- Bundle Monaco's core editor worker as a same-origin Vite `?worker` asset. Configure the React wrapper with the locally imported Monaco package. Do not use a CDN, `blob:`/`data:` workers, `eval`, or expand the existing CSP.
- Register provider-owned SQL tokenizers/configurations for PostgreSQL and T-SQL explicitly. Do not leak editor vendor dialect types across the provider boundary.
- Keep `unifiedDiffText` in a pure `lib/` helper using a small maintained diff package such as `diff`; it must remain usable without importing or initializing the editor.
- Read-only. The provider follows light/dark changes made by `ThemeMenu`.

## SQL card on a compare operation

Replaces the two stacked `SqlBlock`s:

- Tabs **Baseline**, **Candidate** (read-only editor with highlighting) and **Diff**.
- Diff controls: **Side by side / Unified**, and **Ignore formatting**, which formats both sides with `sql-formatter` (engine dialect) before diffing. If formatting fails, show the raw diff with a notice.
- Selected tab and diff mode are remembered in `localStorage` (per-browser preference; wrapped in try/catch).
- Above 062's limits: a message instead of the diff; Baseline/Candidate tabs still work. With `journal.storeSensitive` off: the existing "SQL text is not stored" message and no Diff tab.
- Copy icon on each tab: the side's SQL; on Diff, a plain-text unified diff.

## Table IO: baseline vs candidate

New card above the variant tabs.

- Rows: union of table names from both sides (`operation_table_io`, median per run).
- Columns: Logical reads B | C | Change %, Scan count B | C; physical and read-ahead in an expandable section.
- A table on only one side is labelled "only in baseline"/"only in candidate". No percentage when baseline is 0 or null.
- Total row; default sort by absolute change.
- Matrix: one table per case; a regression in one case is never hidden by the total.
- PostgreSQL: show the buffers data that exists (`PostgresBuffers`); do not invent per-table reads.

## Plan changes

Card computed client-side from the two distilled plans (`/api/plans/{hash}`), using each side's first repetition. Equal hashes → "Same plan".

- Access per object (table/index): operator baseline → candidate (e.g. `Clustered Index Scan → Index Seek`) with executions and actual rows.
- Warnings by side: spill, implicit conversion, missing index (e.g. "missing index: resolved").
- Top 5 operators by cost on each side.
- Plan not stored or not distillable → an explicit message; the card never silently disappears.

## Copy for PR

A copy icon (tooltip "Copy") in the compare header puts Markdown on the clipboard:

- Header: subject/scope, engine, repetitions, regression and equivalence verdicts.
- Metrics table: Baseline | Candidate | Change.
- Table IO table (above).
- Plan changes: access changes and warnings only.
- A ` ```diff ` block of the SQL, only when the text is stored and within limits.

Excluded: server and database names, vars, parameter values, setup SQL. Success shows a "Copied" toast; when the Clipboard API is blocked, a dialog shows the selected text for manual copy. The operator decides what to paste; this follows the "local outputs are locally sensitive" contract.

## Session view

In `CompareSessionOverview` (062) each subject/scope group gets a **Diff** button:

- Default pair: first SQL version (v0) vs latest in the group; two selects allow any pair (e.g. v0→v3).
- Text is fetched only for the two selected versions (no N+1). If 062 does not expose SQL by version, add a bounded endpoint for it.
- Same editor component. No metrics or percentages across versions from different compares; metrics stay on each compare's page.

## Tests

- Vitest: Table IO (union, only-in, zero/null baseline, total, matrix); Plan changes (Scan→Seek, warnings, same plan, missing plan); Copy (Markdown content; no server/database/vars/parameters/setup); diff (limits, ignore formatting, formatter failure, `storeSensitive` off); session version selection; lazy loading does not break other pages. Verify build output keeps the provider lazy, emits the worker same-origin, and leaves the CSP unchanged.
- xUnit: SQL-by-version endpoint if added (bounds, 404, hash-only mode).
- Isolated fixture page: desktop and 390 px width, keyboard navigation through tabs and controls, light/dark. No operator dashboard or real data.
- `npm run check`, `npm run build`, then `just verify`, one at a time. `just verify-linux` is optional under the current local-CI policy in `docs/agents/verification.md`.

## Documentation

README dashboard section (compare review, Copy contents and exclusions). `plans/README.md` row. Not agent-facing: `Capabilities.cs` and `skills/sqlharness/SKILL.md` unchanged.

## Out of scope

Editing SQL in the dashboard, publishing or sharing links, plan graphical diff, cross-version metric trends.
