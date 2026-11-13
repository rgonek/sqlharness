# Plan 126 browser acceptance

Run: 2026-11-11T14:55:04.940Z to 2026-11-11T14:59:23.445Z. Revision: ae0d79d322dcd657035d216c6a0d18764066c994. Product working tree may include changes recorded in acceptance.json.

Synthetic fixture: 500 sessions; 10,000 operations; primary session 1,000 operations; 50 versions; 20 cases. Actual backend DTO snapshots are retained alongside screenshots. No database workload or real persistent write was sent.

Browser: Chromium 156.0.8078.4; win32 10.0.19045; Intel(R) Core(TM) i7-4700MQ CPU @ 2.40GHz; 8 logical CPUs; 16 GiB RAM.

Observed checks: 65/67 passed; 2 failed. Automated findings do not establish any category score.

Scope: functional/browser checks; performance intentionally excluded. Full matrix acceptance remains incomplete while the limitations below remain open.

- FAIL comparison light accessibility and screenshot: locator.click: Timeout 12000ms exceeded. Call log:   - waiting for getByRole('menuitemradio', { name: 'Light', exact: true })     - locator resolved to <div tabindex="-1" data-unchecked="" id="base-ui-_r_4t_" role="menuitemradio" aria-checked="false" data-slot="dropdown-menu-radio-item" class="relative flex min-h-7 cursor-default items-center gap-2 rounded-md py-1.5 pr-8 pl-2 text-xs outline-hidden select-none focus:bg-accent focus:text-accent-foreground focus:**:text-accent-foreground data-inset:pl-7.5 data-disabled:pointer-events-none data-disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-3.5">…</div>   - attempting click action     - waiting for element to be visible, enabled and stable     - element is visible, enabled and stable     - scrolling into view if needed     - done scrolling     - performing click action
- FAIL comparison dark accessibility and screenshot: locator.click: Timeout 12000ms exceeded. Call log:   - waiting for getByRole('menuitemradio', { name: 'Dark', exact: true })     - locator resolved to <div tabindex="-1" data-unchecked="" id="base-ui-_r_4u_" role="menuitemradio" aria-checked="false" data-slot="dropdown-menu-radio-item" class="relative flex min-h-7 cursor-default items-center gap-2 rounded-md py-1.5 pr-8 pl-2 text-xs outline-hidden select-none focus:bg-accent focus:text-accent-foreground focus:**:text-accent-foreground data-inset:pl-7.5 data-disabled:pointer-events-none data-disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-3.5">…</div>   - attempting click action     - waiting for element to be visible, enabled and stable     - element is visible, enabled and stable     - scrolling into view if needed     - done scrolling     - performing click action

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

Read acceptance-summary.json (full raw JSON: acceptance.json.gz) for exact assertions, raw samples, axe nodes, intercepted versus actual provenance and errors. Viewport screenshots are listed there; each captures the current scroll position rather than a scaled full-page image.

Raw JSON over250 KiB was gzip-compressed after the run. The complete uncompressed bytes are preserved; packed-json-manifest.json records their SHA-256 and archive checksum. acceptance-summary.json retains check outcomes, raw performance samples and accessibility counts for review.
