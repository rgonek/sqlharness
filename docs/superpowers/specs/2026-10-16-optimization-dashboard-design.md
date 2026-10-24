# Optimization dashboard (stage B) — design

Date: 2026-10-16. Plan: 080 (to be written from this design). Depends on plan 079
([stage A design](2026-10-16-optimization-evidence-core-design.md)). Origin: dashboard review of 2026-10-16, two
consilium rounds and the owner's choices in brainstorming (layout mockup "A" chosen).

## Intent

The dashboard was organized around the tool's telemetry. The owner's main job is different: after an agent
optimized SQL, find the result of a session in one or two clicks, show a reviewer **scan counts and logical reads per
table, before and after**, sometimes open the plan, and copy reproduction scripts. Today that requires expanding
nested disclosures, opening an operation, scrolling past telemetry and reading ~45 rows of zero-read temp objects.

Stage B reorganizes the dashboard around that job while keeping it general for an open-source audience: sessions
without `compare` (query/watch/snapshot exploration, cost monitoring) keep their current views, minus noise.

## Goals

1. **Session page "Optimization" section** (shown only when the session has at least one compare), at the top,
   expanded, before conversations and the operation list. One card per subject + database:
   - **Version table**: `whole query` first, fragment scopes nested under it. Columns: version, A/B pickers, logical
     reads, scans, elapsed (median), case outcome (`3/3 ok`, `2 better · 1 worse`, `results differ`), operations that
     measured it. Numbers aggregate across cases per the aggregate toggle.
   - **Two-version comparison panel** for the A/B selection:
     - badge `measured together in #214` or `different runs, same conditions`; when fingerprints differ, numbers are
       not put side by side (show "conditions differ" and the SQL diff only);
     - case selector with labels (`@CustomerId = 42`) plus `All cases`; aggregate toggle **Sum / Mean** (sum default);
     - "Mixed" warning naming the worst case when any case regresses;
     - per-table table: persisted table label, scans before/after, logical reads before/after, change; Core
       supplies collision-safe table-variable aggregates and the dashboard preserves exact saved identities.
       Historical stored `#8hex` names remain unchanged; dashboard code does not infer object kind from a label.
       worktables collapsed into one row with a count; zero-read rows collapsed;
     - actions: copy summary (Markdown, same table incl. scans), scripts for both versions (two forms each, from
       stage A), SQL diff, plans, open operation.
   - Defaults (my calls, accepted): A = v0, B = newest version with equivalent results; a version with differing
     results stays in the table but is never the default B; when a version has several measurements the newest is
     used and older ones are linked.
2. **Operation page for a compare** shows the same comparison panel for that operation's pair, with a one-line header
   (subject, scope, `v0 → v3`, database, time, status, back link to session titled by session name). Below it,
   collapsed sections: **SQL diff & conditions** (conditions list, scripts), **Plans** (per statement, changed ones
   marked), **Per-case & per-run details** (min–max, memory, waits — one case/side at a time, no four-column grid),
   **Run diagnostics** (phase timings, connection events, agent call, output bytes/tokens). Measure operations get the
   analogous order: result first, diagnostics collapsed.
3. **Sessions list**: "Optimization" column (subjects with best logical-reads change, e.g. `orders-by-customer −94%`);
   filter "contains compare"; working directory shortened to the last segment (full path in tooltip); state column
   must not clip.
4. **Operation tables** (Live, session, Operations): internal `artifact` operations hidden by default with a toggle
   (choice remembered per viewer); session/subject column; agent in one line (model in tooltip); for compare rows
   `v0 → v3` and logical reads `before → after`.
5. **Badges in three tiers**: outcome (succeeded/failed/rejected), warning (regression, results differ, mixed),
   info (not rendered as a badge — tooltip or muted text). "over-granted" only above a minimum (granted ≥ 1 MB and
   used/granted < 25%).
6. **Live**: empty "Running" and "Active sessions" cards collapse to a single line.
7. **Naming**: Cell → Case; `whole` → `whole query`; "No assessment" explained (why, what next) or hidden;
   "policy v2" in a tooltip; nav item **Statistics → Usage** (page content unchanged: cost monitoring).
8. **Copy summary Markdown** = exactly the comparison panel's table (selected case or aggregate, labelled), with
   scans, grouped temp objects, and the same equivalence/regression wording as the screen.

## Non-goals

- Linking optimization history across MCP/CLI sessions (one agent session can span several MCP sessions). Future
  work: merge via transcript matching or manual user linking; stage A fingerprints enable it.
- SSMS-like graphical plan viewer — separate future feature.
- Per-statement IO attribution and hidden-work flags (stage C), TVP/list parameters (stage D).
- Redesigning the Usage page content, Profiles or Settings.

## Anti-goals

- Hiding a regressing case or differing results behind an aggregate.
- Showing numbers side by side when conditions differ.
- Making non-optimization sessions harder to use.
- Copy output that differs from what the screen shows.

## Constraints

- Builds on plan 079's data: conditions model, case ids/labels, fingerprints, `/api/operations/{id}/repro`,
  journal-side logical reads (data + LOB) and table-variable grouping.
- Existing stack (React, TanStack Router, shadcn/Tailwind components in `src/SqlHarness.Dashboard/ui`); no new UI
  framework. Respect existing light/dark themes.
- Version identity stays per (session, subject, scope) as today (`CompareHistoryBuilder`); the card groups by
  subject + database.
- No auto-formatting; match local style. Repo verification gate (`just verify`).

## Calls settled after the builder check

1. **Better / worse is decided by logical reads (data + LOB)** — owner's decision. Per case: `better` when the
   candidate's median logical reads drop by ≥ 1% and ≥ 10 pages, `worse` when they rise by the same margins,
   otherwise `same`. Results differing makes the case `results differ` regardless of reads. The worst case is the
   largest relative logical-reads increase. The elapsed regression verdict (policy v2) is shown next to it as separate
   information and never overrides or is overridden by the reads outcome. The version table's case-outcome column,
   the "Mixed" warning and the Markdown copy all use this rule.
2. **Default B and equivalence across compares** (my call): B = newest version linked to A by a chain of compares
   whose results were equivalent under the same conditions; the panel says `results equivalent (direct)` or
   `results equivalent via v2`; a pair with no such chain says `results not compared` and cannot be the default.
   Versions whose only compares had equivalence off, incomplete or inconclusive appear in the table with that status
   and are never the default B.
3. **Different conditions inside one card** (my call): versions are grouped by operation-level conditions
   fingerprint; when a card has more than one group, a selector above the table switches between groups (labelled
   by their case set, e.g. `3 cases: @CustomerId = 1, 42, 9001`), and the table/panel only show measurements from the
   selected group. Default A = the earliest version measured in that group (v0 when present).
4. **Sessions list "Optimization" column** (my call): per subject card, the default A → B pair of the default
   conditions group; shows the logical-reads change and a warning icon when that pair is Mixed or not equivalent.
   Computed server-side for the listed page only.
5. **Engines without per-table IO (PostgreSQL)** (my call): the section is shown with the metrics the engine has
   (elapsed, CPU, buffer totals when present); the per-table table is replaced by "per-table IO not available for
   this engine"; scripts unavailable (stage A).
6. **Compares without subject/scope** (my call): the section lists them as "Unassigned compares" with links to their
   operations and no version table; they count for the "contains compare" filter but not for the Optimization column.

## Left to the builder

- Server-side shape of the session optimization summary (extend `CompareHistoryBuilder`/`DashboardModels` so the
  session page does not load every operation's detail).
- Component decomposition, URL state for selected versions/case/aggregate (should survive reload and back
  navigation), exact column widths and responsive behavior.
- Wording of the "No assessment" explanation, using the existing regression policy semantics.
