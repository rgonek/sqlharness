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
