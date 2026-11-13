# Plan 126 browser acceptance

Run: 2026-11-11T15:15:53.139Z to 2026-11-11T15:20:10.238Z. Revision: fbad429a59b7f3cf23266c26b3261adea6da2a79. Product working tree may include changes recorded in acceptance.json.

Synthetic fixture: 500 sessions; 10,000 operations; primary session 1,000 operations; 50 versions; 20 cases. Actual backend DTO snapshots are retained alongside screenshots. No database workload or real persistent write was sent.

Browser: Chromium 156.0.8078.4; win32 10.0.19045; Intel(R) Core(TM) i7-4700MQ CPU @ 2.40GHz; 8 logical CPUs; 16 GiB RAM.

Observed checks: 68/72 passed; 4 failed. Automated findings do not establish any category score.

Scope: functional/browser checks and five twenty-sample performance series. Full matrix acceptance remains incomplete while the limitations below remain open.

- FAIL performance case selection: 20 samples: locator.check: Timeout 12000ms exceeded. Call log:   - waiting for getByRole('radio', { name: '@CustomerId = 2', exact: true })     - locator resolved to <span role="radio" tabindex="-1" data-unchecked="" id="base-ui-_r_64_" aria-checked="false" data-slot="radio-group-item" aria-labelledby="_r_4u_-12-label" class="group/radio-group-item peer flex shrink-0 rounded-full border border-input outline-none group-has-[:focus-visible]/field-label:ring-0 group-has-[:focus-visible]/field-label:not-data-checked:border-input after:absolute focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50 ar…></span>   - attempting click action     - scrolling into view if needed     - done scrolling     - forcing action     - performing click action     - click action done     - waiting for scheduled navigations to finish     - navigations have finished
- FAIL selected aggregate updates table and source timing: locator.check: Timeout 12000ms exceeded. Call log:   - waiting for getByRole('radio', { name: 'Mean', exact: true })     - locator resolved to <span role="radio" tabindex="-1" data-unchecked="" id="base-ui-_r_74_" aria-checked="false" data-slot="radio-group-item" aria-labelledby="_r_6v_-1-label" class="group/radio-group-item peer flex shrink-0 rounded-full border border-input outline-none group-has-[:focus-visible]/field-label:ring-0 group-has-[:focus-visible]/field-label:not-data-checked:border-input after:absolute focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50 ari…></span>   - attempting click action     - scrolling into view if needed     - done scrolling     - forcing action     - performing click action     - click action done     - waiting for scheduled navigations to finish     - navigations have finished
- FAIL operation light accessibility and screenshot: 1 axe violations: color-contrast
- FAIL operation dark accessibility and screenshot: 1 axe violations: color-contrast

## Performance

| Action | Samples | p95 ms | Limit ms | Max long task ms |
|---|---:|---:|---:|---:|
| search | 20 | 73.7 | 200 | 0.0 |
| facet | 20 | 111.3 | 200 | 94.0 |
| session expansion | 20 | 91.2 | 200 | 51.0 |
| A/B selection | 20 | 204.5 | 500 | 108.0 |

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

Raw JSON over250 KiB was gzip-compressed after the run. The complete uncompressed bytes are preserved; packed-json-manifest.json records their SHA-256 and archive checksum. acceptance-summary.json retains check outcomes, raw performance samples and accessibility counts for review.
