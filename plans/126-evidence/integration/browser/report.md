# Plan 126 browser acceptance

Run: 2026-11-13T09:40:48.303Z to 2026-11-13T09:44:04.490Z. Revision: 0420529c0c212c003e97ed2a3abd4a82bfb9d5c4. Product working tree may include changes recorded in acceptance.json.

Synthetic fixture: 500 sessions; 10,000 operations; primary session 1,000 operations; 50 versions; 20 cases. Actual backend DTO snapshots are retained alongside screenshots. No database workload or real persistent write was sent.

Browser: Chromium 156.0.8078.4; win32 10.0.19045; Intel(R) Core(TM) i7-4700MQ CPU @ 2.40GHz; 8 logical CPUs; 16 GiB RAM.

Observed checks: 67/67 passed; 0 failed. Automated findings do not establish any category score.

Scope: functional/browser checks; performance intentionally excluded. Full matrix acceptance remains incomplete while the limitations below remain open.


## Performance

Performance was intentionally skipped in this focused run. Retained provisional metrics remain separate and are not replaced by this report.

| Action | Samples | p95 ms | Limit ms | Max long task ms |
|---|---:|---:|---:|---:|

Three warm-up iterations precede each twenty-sample series. Input capture and second requestAnimationFrame define paint; comparison fetch responseEnd defines the response boundary, while cached changes use input-to-paint. These are browser instrumentation proxies, not external input-latency hardware measurements.

## Limitations and open matrix evidence

- Automated browser evidence is not a screen-reader or usability study; screen-reader acceptance remains pending.
- Local synthetic journal measurements do not establish production performance.
- axe colour contrast covers text where determinable; a complete manual non-text contrast audit remains pending.
- Browser zoom is approximated by equivalent 720 CSS-pixel reflow at a 1440 physical-pixel viewport; native browser 200% zoom remains pending.
- Separate measure operations and cross-condition fixtures are not present in this journal. Non-adjacent independent pairing is tested through the actual backend; approximate rendering uses an explicitly intercepted DTO.
- Pending/delayed route, live disconnect/reconnect, empty history, rejected operation, copy/manual-copy failure and long SQL/labels require additional focused evidence if absent from the recorded checks.
- Complete end-to-end keyboard navigation, native zoom and manual non-text contrast remain required before accessibility can justify 9.0.
- Per-category 9.0 claims are intentionally withheld while any required matrix evidence is missing.

Read acceptance.json for exact assertions, raw samples, axe nodes, intercepted versus actual provenance and errors. Viewport screenshots are listed there; each captures the current scroll position rather than a scaled full-page image.
