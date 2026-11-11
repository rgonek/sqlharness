# Activity iteration evidence

Baseline: a274b0ec. Branch: dashboard-quality-9. No DB execution.

- Initial new Activity context tests: 3 failed for intended reset/missing URL/back behaviour.
- After implementation: ActivityContext/LivePage/ActivityOperations 21/21 passed, exit 0.
- Added explicit retry regression: initial failure because Try again was absent.
- After wiring manual refetch: ActivityContext 4/4 passed, exit 0.
- Existing tests now clear only the two Activity storage keys between tests to prevent cross-test state.
- Browser built UI at 390/768/1440/1920: document widths equal viewport widths; no page errors.
- Inspected 390px closed/open filters and 1440px Activity screenshots.
- Search and facets use validated, bounded route state; expansion uses bounded session storage.
- TanStack scroll restoration enabled. Real round-trip scroll restoration remains to be checked.
- Read error retry wired to Live, Session, AgentSession, Profiles, Settings, Operations, NamedOperations,
  Mutations and Usage. No automatic mutation/SQL retries were added.
- Global supporting text strengthened; 14px body, 13px supporting CSS, wider detail container, readable
  focus outline, reduced-motion support. Contrast verification and full browser matrix remain pending.

The first axe run failed in the harness before reporting results because Playwright's browser.newPage
does not provide the explicitly managed context axe requires. This is a harness failure, not a UI pass.

## Review and browser follow-up, 2026-11-11

- Fresh review found ambiguous bare Activity history entries and recent low-ID expansion eviction.
  Both now have observed failing regressions and passing fixes: explicit initial route state and a
  bounded recent-change collection in memory/storage. Activity search/context plus validation: 7/7.
- Real browser Activity -> detail -> Activity link initially lost scroll (600 -> 0); after the
  dedicated tab-local scroll memory it restored 600 -> 600.
- Real Monaco keyboard resize (106 -> 150px), native drag (150 -> 230px), then version change
  preserves 230px. This is real editor evidence, distinct from mocked component tests.
- Additional observed RED -> GREEN for Monaco mode update clearing baseline/candidate input names.
  Current editor unit scope 19/19 passes. Read-only mode uses labelled textarea support and lets Tab
  leave; side labels are repeated in mode updates because Monaco clears omitted labels.
- Current built Activity/Settings/session/operation axe scans in both themes: eight scans with zero
  reported violations. Incomplete checks still require inspection; this is not full accessibility acceptance.
- Actual Chrome native tab zoom (temporary isolated extension, tabs.setZoom(2)): viewport1440/DPR1
  becomes720/DPR2, document width720. This supplements the earlier equivalent-width reflow checks.
- Session history search/optimization and Usage range/SQL ordering round trips initially failed.
  Bounded, validated tab-local display preferences now retain those selections plus profile/axes/dimensions.
  Navigation/Sessions/Stats/Profiles scope: 50/50. Preference malformed/oversized/blocked-storage and
  navigation scope: 4/4. Focused lint emitted no diagnostics; final full gate remains pending.
- Strengthened metadata/status/brand contrast and bounded the version table with sticky headings,
  version identity and A/B controls. Essential Results values wrap rather than truncate in narrow cards.

No category score is established solely by these scoped checks. Final source identity, real backend
evidence, performance, screen-reader/manual contrast checks, whole-branch review and Windows gate remain open.
