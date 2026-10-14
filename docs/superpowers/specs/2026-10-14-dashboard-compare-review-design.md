# Dashboard compare review (Spec B)

Status: design agreed with the operator, 2026-10-14. Implementation plan: not yet written. Order: after Plan 062, [session curation](2026-10-14-dashboard-session-curation-design.md) (A) and [agent transcript matching](2026-10-14-dashboard-agent-transcript-matching-design.md) (C).

> **Checkpoint before execution.** The editor choice (CodeMirror 6) is provisional. The implementation plan's first step must ask the operator whether CodeMirror 6 is the final decision, and must not start until they answer. If they choose differently (e.g. Monaco), revise this spec first.

## Goal

After an agent optimises SQL, the operator opens a PR and shows the reviewer what changed: the SQL diff, reads per table, time/CPU, plan changes and the equivalence/regression verdicts. The dashboard is loopback-only, so the reviewer gets a Markdown copy rather than a link.

## Builds on Plan 062

062 delivers compare context (`subject`/`scope`), SQL versions per subject/scope, `CompareDetail`, `CompareSessionOverview` and a bounded `SqlDiff` (256 KiB total / 4000 lines, no new diff package). This spec replaces 062's `SqlDiff` rendering with the editor-based one and keeps 062's limits, version model and the rule that different measurement conditions never form one trend.

## Editor

- `@codemirror/merge` (`MergeView` side by side, `unifiedMergeView` unified) and `@codemirror/lang-sql` with the engine's dialect; read-only.
- Bundled by Vite as same-origin files; CSP stays `script-src 'self'` (no `blob:` workers, no CDN). Loaded lazily with `import()` so other pages do not pay for it.
- Light/dark theme follows `ThemeMenu`.

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

- Vitest: Table IO (union, only-in, zero/null baseline, total, matrix); Plan changes (Scan→Seek, warnings, same plan, missing plan); Copy (Markdown content; no server/database/vars/parameters/setup); diff (limits, ignore formatting, formatter failure, `storeSensitive` off); session version selection; lazy loading does not break other pages.
- xUnit: SQL-by-version endpoint if added (bounds, 404, hash-only mode).
- Isolated fixture page: desktop and 390 px width, keyboard navigation through tabs and controls, light/dark. No operator dashboard or real data.
- `npm run check`, `npm run build`, then both gates, one at a time.

## Documentation

README dashboard section (compare review, Copy contents and exclusions). `plans/README.md` row. Not agent-facing: `Capabilities.cs` and `skills/sqlharness/SKILL.md` unchanged.

## Out of scope

Editing SQL in the dashboard, publishing or sharing links, plan graphical diff, cross-version metric trends.
