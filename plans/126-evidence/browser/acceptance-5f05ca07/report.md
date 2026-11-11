# Plan 126 browser acceptance

Run: 2026-11-11T15:23:43.527Z to 2026-11-11T15:27:06.891Z. Revision: 5f05ca073887c58ecc22a567782124438baca07c. Product working tree may include changes recorded in acceptance.json.

Synthetic fixture: 500 sessions; 10,000 operations; primary session 1,000 operations; 50 versions; 20 cases. Actual backend DTO snapshots are retained alongside screenshots. No database workload or real persistent write was sent.

Browser: Chromium 156.0.8078.4; win32 10.0.19045; Intel(R) Core(TM) i7-4700MQ CPU @ 2.40GHz; 8 logical CPUs; 16 GiB RAM.

Observed checks: 72/72 passed; 0 failed. Automated findings do not establish any category score.

Scope: functional/browser checks and five twenty-sample performance series. Full matrix acceptance remains incomplete while the limitations below remain open.


## Performance

| Action | Samples | p95 ms | Limit ms | Max long task ms |
|---|---:|---:|---:|---:|
| search | 20 | 82.3 | 200 | 0.0 |
| facet | 20 | 95.1 | 200 | 101.0 |
| session expansion | 20 | 90.5 | 200 | 53.0 |
| A/B selection | 20 | 206.7 | 500 | 116.0 |
| case change | 20 | 146.6 | 500 | 72.0 |

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

Read acceptance-summary.json (full raw JSON: acceptance.json.gz) for exact assertions, raw samples, axe nodes, intercepted versus actual provenance and errors. Viewport screenshots are listed there; each captures the current scroll position rather than a scaled full-page image.

## Coordinator supplemental evidence

Actual native browser200% zoom/reflow is separately confirmed on the same product revision for four routes in [native zoom](../native-accessibility/final-5f05ca07/zoom.json). [Control contrast](../native-accessibility/final-5f05ca07/contrast.json) and [synthetic graph contrast](../native-accessibility/final-5f05ca07/graph.json) repeat sampled non-text measurements in both themes. See the [native report](../native-accessibility/report.md) for measurement scope. Native NVDA remains blocked by Secure Desktop. The general limitations above describe this automation harness alone; the linked supplemental results close its native-zoom gap.

Raw JSON over250 KiB was gzip-compressed after the run. The complete uncompressed bytes are preserved; packed-json-manifest.json records their SHA-256 and archive checksum. acceptance-summary.json retains check outcomes, raw performance samples and accessibility counts for review.
