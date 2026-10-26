# Plan 121 implementation

Base: `443f468e620e98c2e65fb109040d91ef06c286c5` on `main`.
Branch: `dashboard-design-layout` in the managed `16f4/sqlharness` worktree.

- Imported selected Claude Design HTML references and their preview support file.
- Consolidated primary navigation into Activity, Usage, Profiles and Settings.
  History, Named operations and Mutations remain reachable from Activity.
- Replaced the three Live cards with session groups, compact operation rows,
  active-session expansion, multi-select facets and contextual chip counts.
  Running-operation pagination and live-cache reconciliation remain authoritative;
  the merged feed does not impose the recent-list cap on running operations.
- Organized operation content into type-specific SQL, IO, Results, Approval,
  Timing and Details tabs. Compare uses SQL diff, IO, Plan, Timing and Details.
- Added the session comparison SQL diff / IO / Plan tabs, compact IO pairs,
  statement disclosure, whole-batch totals and selectable additional counters.
  A/B table selection is the diff's version selection. Version annotations come
  from loaded observing-operation annotations and retain their provenance tooltip.
- Stacked executed SQL above the interactive plan graph, preserving existing
  source-map selection and statement/document/repetition controls.
- Grouped settings into six navigable sections, retaining save queues, validation,
  journal/annotation coupling and sensitive-data/retention confirmations.

## Evidence boundaries

The Activity filters apply to loaded recent activity plus every running operation;
full historical lists and their existing pagination remain available through links.

Additional IO counters are read from the exact comparison actions and selected
case mappings. Missing cases, ambiguous variants or missing counters remain
unavailable; no partial subset is presented as an all-case total. Statement memory
and additional per-statement physical counters are not retained in the current
journal projection and are labelled unavailable. Batch memory is explicitly
labelled as sum/mean of recorded selected-case medians.

Query result cells are not retained by the activity journal. The Results tab says
so instead of copying example cells from the design. Approval decisions remain
in the existing Mutations interface; no approval is supplied by this change.

Two source-review axes and scoped corrections are recorded in [reviews](reviews.md).
Browser inventory was empty and `createBrowserTab("iab", ...)` reported that the
browser was unavailable. Visual and responsive browser acceptance were unverified at that delivery; the [visual follow-up](visual-verification.md) now records actual browser evidence.

## Delivery

Locally integrated and published after green Windows verification. See
[verification](verification.md), [integration](integration.json) and
[publication](publication.json). User README/index edits remain uncommitted.

## Browser correction follow-up (2026-10-25)

The unavailable-browser statements above describe the original delivery only.
The subsequent authorized Playwright/Chromium run rendered the selected exports
and actual application, saved before/after matched states, and corrected material
layout differences. See [design QA](../../design-qa.md),
[screenshot gallery](visual/index.html), and [follow-up review](visual-review.md).
Missing journal data remains explicitly unavailable by operator decision; no API
or persistence contract was expanded. Final gate and delivery receipts are recorded
separately for this follow-up.

## Updated data-gap inventory (2026-10-26)

The [mock/data audit](shadcn/mock-alignment/data-audit.md) distinguishes unretained data from missing dashboard projections. In particular, CPU/elapsed per statement are already persisted in operation_statements; the current dashboard comparison model does not expose them. Earlier unavailable-data wording must not be interpreted as absence of all collection. The audit also lists safe UI simplifications and existing-data navigation gaps.
