# Fresh task review: Activity and shared interaction

Reviewed `0e3aeb42` and `99da22e3` against `b10a2bb2`, using the approved
`docs/superpowers/specs/2026-11-10-dashboard-quality-design.md`, Plan 126 and the Activity/shared reports.
Sibling backend/comparison changes were excluded. No product files were edited by this reviewer.
Findings below describe the reviewed commits; the coordinator began corrections during review.

## Findings

### P2: Bare Activity history entries have no stable filter selection

`src/SqlHarness.Dashboard/ui/src/lib/useActivityFilters.ts:8-10`

An empty route search is interpreted through session storage, but that resolved selection is never
written into the history entry. For example, open `/` with defaults, visit a detail, return to Activity,
choose a search, then return through history to the original `/`. Activity remounts and interprets that
old bare address using the newer stored search. The original default selection is lost. Similarly,
returning through the Activity link restores remembered facets while leaving the address `/`, so sharing
that address does not share the visible selection. The approved design requires state preservation across
back/forward and prefers shareable route state. The existing back test covers two explicit, nonempty
search objects and does not cover this case. Canonicalize a resolved initial selection into its current
entry, preserving the distinct semantics of default and remembered entries.

Evidence: direct control-flow inspection; the coordinator added a regression during this review and its
initial red run failed on the missing explicit `time` in an initially bare history entry. The coordinator's
subsequent correction needs its own green rerun; it is outside these reviewed commit snapshots.

### P2: Expansion state grows in memory and can immediately discard the latest selection

`src/SqlHarness.Dashboard/ui/src/components/ActivityFeed.tsx:81-84,153`;
`src/SqlHarness.Dashboard/ui/src/lib/activitySearch.ts:63-64`

Single-session toggles keep spreading every historical ID into React state. The 200-entry bound is
applied only to the serialized copy, so a long-lived feed can accumulate arbitrary historical session
IDs despite its bounded visible session cache. Serialization uses `Object.entries(...).slice(-200)`;
integer object keys are enumerated numerically, so this preserves the highest IDs rather than recent
operator choices. After previously expanding IDs 2 through 201, explicitly collapse an active older
session 1 and navigate to detail/back: its just-set `false` is discarded, and a running or force-open
session reopens. Bound the live state too and preserve the newly applied choice when trimming.

Evidence: executed the actual helpers under Node with an in-memory storage implementation. Starting
with IDs 2..201 set true, then setting ID 1 false, produced 201 in-memory entries and 200 restored
entries; restored ID 1 was absent while ID 201 remained true. This is an observed helper effect, not
a claim that the full browser round trip was exercised.

## Remaining acceptance evidence

- `scrollRestoration: true` alone does not establish restoration through an Activity return link.
  The installed router's default restoration key uses the history-entry key, and a new link navigation
  creates a different entry. Test a scrolled Activity -> detail -> Activity-link round trip as well as
  browser Back. The Activity report accurately marks real round-trip scroll restoration pending.
- Monaco tests mock the editor. Native drag resizing, persistence after a content/case change,
  keyboard navigation at narrow widths, 200% zoom, focus appearance, both themes and actual browser
  accessibility remain matrix evidence. The coordinator is exercising these; this review did not
  duplicate that browser work or infer a browser pass from the mocked tests.
- During this review the coordinator reported real-browser keyboard/native drag checks passed, but
  changing B to v1 remounted the editor and reset its manual height. The coordinator owns that follow-up
  and its bounded-preference correction. This matches `ResizableSqlFrame` keeping height only in local
  component state (`monaco.tsx:152-155` in the reviewed commit). The coordinator also added a regression
  for distinct baseline/candidate native input labels; the reviewed diff editor supplies only the shared
  `ariaLabel`. These are additional acceptance gaps established by the coordinator's matrix/regressions,
  not browser checks performed by this reviewer.
- Inspected the compact-filter CSS, named/manual copy flow, original SQL preservation, script query
  keys and endpoint encoding, side-action slots, SQL/diff bounds, skip link, reduced-motion rule and
  manual read retries. No additional material defect was found beyond the findings and coordinator
  follow-ups above. Existing
  query retry policy remains authoritative; the changes add no new automatic SQL/mutation retry.
- Both task reports distinguish focused implementation evidence from the full gate and broader
  acceptance. No misleading final category score, full verification pass or live acceptance claim
  was found. A final Windows gate remains the coordinator's responsibility.

## Reviewer checks

Reviewed the committed diff and its focused tests against the approved requirements. Ran the actual
expansion read/write helpers as described above. Started the eight-file focused Vitest command for
ActivityContext, CopyButton, ReproScriptButtons, SqlDiff, Monaco, ErrorState, AppLayout and SqlBlock.
That run overlapped the coordinator adding initial red Activity/Monaco regressions. It finished with
exit 1: 34 passed, 3 failed across 8 files (116.39s). The failures were exactly the new default-history,
version-driven editor-remount height, and distinct native diff-input-label regressions. This is useful red
evidence and is not a final-tree gate. The coordinator owns correction and green rerun; none was repeated
by this reviewer. Report diff whitespace check passed.
