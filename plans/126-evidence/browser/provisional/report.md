# Plan 126 browser acceptance

Run: 2026-11-10T19:28:32.463Z to 2026-11-10T19:31:43.019Z. Revision: 45a7e54c13a75b3d492c2867bd1640a59f333a4e. Product working tree may include changes recorded in acceptance.json.

Synthetic fixture: 500 sessions; 10,000 operations; primary session 1,000 operations; 50 versions; 20 cases. Actual backend DTO snapshots are retained alongside screenshots. No database workload or real persistent write was sent.

Browser: Chromium 156.0.8078.4; win32 10.0.19045; Intel(R) Core(TM) i7-4700MQ CPU @ 2.40GHz; 8 logical CPUs; 16 GiB RAM.

Observed checks: 59/65 passed; 6 failed. Automated findings do not establish any category score.

- FAIL actual DTO mixed IO/policy, zero/missing counters, sum/mean and case timing: GET /api/operations/10000/optimization?case=0&aggregate=sum&view=whole returned 404
- FAIL performance facet toggle: 20 samples: p95 200.6ms (limit 200); max long task 153.0ms (limit 200)
- FAIL operation light accessibility and screenshot: 1 axe violations: label
- FAIL operation dark accessibility and screenshot: 1 axe violations: label
- FAIL actual case outcome dialog explains lower reads with policy failure; keyboard focus return: Mixed case reason absent or wrong ordering
- FAIL intercepted approval request keyboard focus return; no decision sent: locator.waitFor: Timeout 12000ms exceeded. Call log:   - waiting for getByRole('dialog') to be hidden     27 × locator resolved to visible <div tabindex="0" role="dialog" data-expanded="" data-slot="toast" aria-modal="false" aria-labelledby="_r_7u_" aria-describedby="_r_7v_" class="group/toast pointer-events-auto absolute right-0 bottom-0 z-[calc(1000-var(--toast-index))] w-full origin-bottom rounded-md border bg-popover text-popover-foreground shadow-lg will-change-transform outline-none select-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 [--gap:0.75rem] [--height:var(--toast-frontmost-height,var…>…</div>

## Performance

| Action | Samples | p95 ms | Limit ms | Max long task ms |
|---|---:|---:|---:|---:|
| search | 20 | 102.1 | 200 | 60.0 |
| facet | 20 | 200.6 | 200 | 153.0 |
| session expansion | 20 | 109.9 | 200 | 72.0 |
| A/B selection | 20 | 93.8 | 500 | 109.0 |
| case change | 20 | 310.4 | 500 | 160.0 |

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
