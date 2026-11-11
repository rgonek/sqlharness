// Reproducible synthetic-journal acceptance. Never executes SQL or sends a real write.
// AUDIT_TOOLS_ROOT points to an existing node_modules containing playwright and @axe-core/playwright.
// node plans/126-evidence/harness/acceptance.mjs <isolated SQLHARNESS_HOME> [output directory]
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
const require = createRequire(path.join(process.env.AUDIT_TOOLS_ROOT ?? 'D:/temp/sqlharness-dashboard-audit/node_modules', '__audit.cjs'));
const { chromium } = require('playwright');
const { default: AxeBuilder } = require('@axe-core/playwright');
const home = path.resolve(process.argv[2] ?? 'D:/temp/sqlharness-quality-home-20261110');
const output = path.resolve(process.argv[3] ?? 'plans/126-evidence/browser');
const skipPerformance = process.env.AUDIT_SKIP_PERFORMANCE === '1';
fs.mkdirSync(output, { recursive: true });
const endpoint = JSON.parse(fs.readFileSync(path.join(home, 'dashboard.json'), 'utf8'));
const origin = `http://127.0.0.1:${endpoint.port}`;
const report = {
  startedAt: new Date().toISOString(), source: 'actual release dashboard, synthetic local journal',
  revision: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
  dirtyPaths: execFileSync('git', ['status', '--short'], { encoding: 'utf8' }).trim().split('\n'),
  fixture: JSON.parse(fs.readFileSync(path.join(home, 'seed-evidence.json'), 'utf8')),
  performanceSkipped: skipPerformance,
  scope: skipPerformance ? 'functional/browser checks; performance intentionally excluded' : 'functional/browser checks and five twenty-sample performance series',
  fullMatrixAcceptanceComplete: false,
  host: { hostname: os.hostname(), platform: os.platform(), release: os.release(), architecture: os.arch(), cpus: os.cpus().length, cpu: os.cpus()[0]?.model, memoryGiB: Math.round(os.totalmem() / 2 ** 30) },
  checks: [], errors: [], requests: [], screenshots: [], accessibility: [], performance: [],
  limitations: [
    'Automated browser evidence is not a screen-reader or usability study; screen-reader acceptance remains pending.',
    'Local synthetic journal measurements do not establish production performance.',
    'axe colour contrast covers text where determinable; a complete manual non-text contrast audit remains pending.',
    'Browser zoom is approximated by equivalent 720 CSS-pixel reflow at a 1440 physical-pixel viewport; native browser 200% zoom remains pending.',
  ],
};
const browser = await chromium.launch({ headless: true });
report.host.browser = browser.version();
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce', permissions: ['clipboard-read', 'clipboard-write'] });
// Authentication material stays local: no URL query, screenshots, storageState, trace or logs.
await context.addCookies([{ name: 'sqlharness_dashboard', value: endpoint.token, url: origin, httpOnly: true, sameSite: 'Strict' }]);
await context.route('**/api/**', async route => {
  if (!['GET', 'HEAD', 'OPTIONS'].includes(route.request().method())) {
    report.errors.push({ kind: 'blocked-real-write', method: route.request().method(), path: new URL(route.request().url()).pathname });
    await route.abort('blockedbyclient');
  } else await route.continue();
});
await context.addInitScript(() => {
  window.__acceptance = { events: [], longTasks: [], start: 0, activeRequests: 0 };
  const originalFetch = window.fetch;
  window.fetch = async (...args) => {
    window.__acceptance.activeRequests++;
    try { return await originalFetch(...args); }
    finally { window.__acceptance.activeRequests--; }
  };
  for (const name of ['input', 'click', 'keydown']) document.addEventListener(name, () => window.__acceptance.events.push(performance.now()), true);
  new PerformanceObserver(list => window.__acceptance.longTasks.push(...list.getEntries().map(e => ({ start: e.startTime, duration: e.duration })))).observe({ type: 'longtask', buffered: true });
});
const page = await context.newPage();
page.setDefaultTimeout(12000);
page.on('pageerror', error => report.errors.push({ kind: 'pageerror', message: error.message }));
page.on('response', response => {
  if (response.status() >= 400 && new URL(response.url()).pathname.startsWith('/api/')) report.requests.push({ path: new URL(response.url()).pathname, status: response.status() });
});
const check = async (name, action, source = 'actual backend') => {
  if (skipPerformance && name.startsWith('performance ')) return;
  const start = Date.now();
  try { const detail = await action(); report.checks.push({ name, source, passed: true, milliseconds: Date.now() - start, detail }); }
  catch (error) { report.checks.push({ name, source, passed: false, milliseconds: Date.now() - start, error: String(error.message).slice(0, 2000) }); }
  finally {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
    await page.evaluate(() => {
      if (window.__clipboardWriteText) {
        Object.defineProperty(navigator.clipboard, 'writeText', { configurable: true, value: window.__clipboardWriteText });
        delete window.__clipboardWriteText;
      }
    }).catch(() => {});
  }
  fs.writeFileSync(path.join(output, 'acceptance.json'), JSON.stringify(report, null, 2));
};
function assert(value, message) { if (!value) throw new Error(message); }
const paint = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(performance.now())))));
const goto = async route => { await page.goto(origin + route, { waitUntil: 'domcontentloaded' }); await page.locator('main').waitFor(); await paint(); };
const shot = async name => { const file = `${name}.png`; await page.screenshot({ path: path.join(output, file), fullPage: false }); report.screenshots.push({ file, viewport: page.viewportSize(), route: new URL(page.url()).pathname }); };
const api = async route => { const response = await context.request.get(origin + route); assert(response.ok(), `GET ${route} returned ${response.status()}`); return response.json(); };
const waitComparison = async () => {
  await page.waitForFunction(() => window.__acceptance.activeRequests === 0 && document.querySelector('section[aria-label="Version comparison"][aria-busy="false"]'));
  await paint();
};
const widthCheck = async () => page.evaluate(() => {
  const width = document.documentElement.clientWidth;
  return { width, scrollWidth: document.documentElement.scrollWidth, overflow: [...document.querySelectorAll('body *')].filter(e => { const r = e.getBoundingClientRect(); return r.width && (r.left < -1 || r.right > width + 1) && getComputedStyle(e).position !== 'fixed' && !e.closest('[data-slot="table-container"],.monaco-editor,.overflow-x-auto'); }).slice(0, 12).map(e => ({ tag: e.tagName, className: String(e.className).slice(0, 120), text: e.textContent?.slice(0, 50) })) };
});
const axe = async name => {
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
  const measuredContrast = result.passes.filter(v => v.id === 'color-contrast').flatMap(v => v.nodes).flatMap(n => n.any.filter(x => x.id === 'color-contrast').map(x => ({ target: n.target, data: x.data })));
  const data = { name, violations: result.violations.map(v => ({ id: v.id, impact: v.impact, description: v.description, nodes: v.nodes.map(n => ({ target: n.target, summary: n.failureSummary, contrast: n.any.filter(x => x.id === 'color-contrast').map(x => x.data) })) })), incomplete: result.incomplete.map(v => ({ id: v.id, count: v.nodes.length })), passes: result.passes.map(v => v.id), measuredContrast: measuredContrast.slice(0, 200), measuredContrastTotal: measuredContrast.length, measuredContrastOmitted: Math.max(0, measuredContrast.length - 200) };
  report.accessibility.push(data);
  assert(data.violations.length === 0, `${data.violations.length} axe violations: ${data.violations.map(v => v.id).join(', ')}`);
  return data;
};
const performanceSamples = async (name, action, settle, threshold, prepare = async () => {}) => {
  // Warm up controls, JS and data cache separately; excluded from the twenty samples.
  for (let i = 0; i < 3; i++) { await prepare(i); await action(i); await settle(i); await paint(); }
  const samples = [];
  for (let i = 0; i < 20; i++) {
    await prepare(i + 3);
    await page.evaluate(() => { performance.clearResourceTimings(); window.__acceptance.events = []; window.__acceptance.start = performance.now(); });
    await action(i + 3); await settle(i + 3);
    const end = await paint();
    const sample = await page.evaluate(end => {
      const state = window.__acceptance, input = state.events[0] ?? state.start;
      const resources = performance.getEntriesByType('resource').filter(e => e.name.includes('/api/') && e.startTime >= state.start);
      const responseEnd = Math.max(0, ...resources.map(e => e.responseEnd));
      const tasks = state.longTasks.filter(e => e.start + e.duration >= input && e.start <= end);
      return { inputToPaintMs: end - input, settlementAfterResponseMs: responseEnd ? Math.max(0, end - responseEnd) : null, responseObserved: responseEnd > 0, maxLongTaskMs: Math.max(0, ...tasks.map(e => e.duration)) };
    }, end);
    samples.push(sample);
  }
  const p95 = values => [...values].sort((a, b) => a - b)[Math.ceil(values.length * .95) - 1];
  const values = samples.map(s => threshold === 500 ? s.settlementAfterResponseMs ?? s.inputToPaintMs : s.inputToPaintMs);
  const result = { name, sampleCount: samples.length, warmupCount: 3, thresholdMs: threshold, p95Ms: p95(values), maxLongTaskMs: Math.max(...samples.map(s => s.maxLongTaskMs)), samples, note: threshold === 500 ? 'Response-to-paint when fetched; input-to-paint for cached data. Warm-up excluded.' : 'Input event capture to second animation frame after asserted state.' };
  report.performance.push(result);
  assert(result.p95Ms <= threshold && result.maxLongTaskMs <= 200, `p95 ${result.p95Ms.toFixed(1)}ms (limit ${threshold}); max long task ${result.maxLongTaskMs.toFixed(1)}ms (limit 200)`);
  return result;
};
let direct, section, group, scopeName, route;
try {
  await check('fixture counts frozen', async () => { const f = report.fixture; assert(f.sessions === 500 && f.operations === 10000 && f.primarySessionOperations === 1000 && f.versions === 50 && f.cases === 20, 'Unexpected frozen journal sizes'); return f; });
  await check('actual DTO mixed IO/policy, zero/missing counters, sum/mean and case timing', async () => {
    direct = await api('/api/operations/10000/optimization?case=all&aggregate=sum&view=whole');
    const selected = await api('/api/operations/10000/optimization?case=synthetic-case-0&aggregate=sum&view=whole');
    const mean = await api('/api/operations/10000/optimization?case=all&aggregate=mean&view=whole');
    const partial = await api('/api/operations/10000/optimization?case=synthetic-case-19&aggregate=sum&view=whole');
    fs.writeFileSync(path.join(output, 'actual-comparison-dtos.json'), JSON.stringify({ direct, selected, mean, partial }, null, 2));
    assert(direct.cases.length === 20, 'Expected 20 cases');
    assert(selected.caseOutcomes.some(c => c.readsAfter < c.readsBefore && c.ioOutcome === 'better' && c.policyVerdict === 'fail'), 'Fixture must exercise IO better while recorded policy fails');
    assert(direct.elapsed.beforeMs === 200 && direct.elapsed.afterMs === 220, 'All-case sums must be 200/220ms');
    assert(mean.elapsed.beforeMs === 10 && mean.elapsed.afterMs === 11, 'All-case means must be 10/11ms');
    assert(selected.elapsed.beforeMs === 10 && selected.elapsed.afterMs === 30, 'Selected-case median must be 10/30ms');
    assert(partial.tables.some(t => t.physicalReadsBefore === 0 && t.readAheadReadsBefore === null), 'Zero physical reads must remain distinct from missing read-ahead');
    assert(partial.omittedStatementsBefore > 0 && partial.statements.length > 0, 'Partial statement coverage must retain supported statement');
    return { basis: direct.basis, outcomes: direct.outcomes, sum: direct.elapsed, mean: mean.elapsed, selected: selected.elapsed };
  });
  await check('actual optimization has 50 versions and failed attempt excluded', async () => {
    section = await api('/api/sessions/1/optimization');
    fs.writeFileSync(path.join(output, 'actual-optimization-section.json'), JSON.stringify(section, null, 2));
    group = section.subjects[0].groups[0]; const scope = group.scopes[0]; scopeName = scope.scope;
    assert(scope.versions.length === 50, `Expected 50 retained versions; got ${scope.versions.length}`);
    assert(!scope.versions.some(v => v.measuredIn.includes(9951)), 'Failed operation entered optimization evidence');
    const search = new URLSearchParams({ subject: 'orders', db: 'ShopDb', group: group.fingerprint, scope: scopeName, a: 'v48', b: 'v49', case: 'all', agg: 'sum', view: 'whole' });
    route = '/sessions/1?' + search;
    return { versionCount: scope.versions.length, defaultA: scope.defaultA, defaultB: scope.defaultB, group: group.fingerprint };
  });
  await check('independent non-adjacent pair keeps policy unavailable in actual DTO/UI', async () => {
    assert(route && group, 'Optimization route unavailable');
    const query = new URLSearchParams({ subject: 'orders', database: 'ShopDb', group: group.fingerprint, scope: scopeName, a: 'v0', b: 'v49', case: 'all', aggregate: 'sum', view: 'whole' });
    const independent = await api('/api/sessions/1/optimization/compare?' + query);
    fs.writeFileSync(path.join(output, 'actual-independent-comparison.json'), JSON.stringify(independent, null, 2));
    assert(independent.basis === 'DifferentRunsSameConditions', `Unexpected independent basis ${independent.basis}`);
    assert(independent.outcomes.policyPass === 0 && independent.outcomes.policyFail === 0 && independent.outcomes.policyUnavailable === 20, 'Independent evidence must not invent recorded policy');
    await goto(route.replace('a=v48', 'a=v0')); await waitComparison();
    assert(/different runs|independent/i.test(await page.locator('section[aria-label="Version comparison"]').innerText()), 'Independent provenance not explained');
    await shot('independent-evidence'); return { basis: independent.basis, outcomes: independent.outcomes };
  });
  await check('activity readable and keyboard skip link', async () => {
    await goto('/'); await page.getByRole('textbox', { name: 'Search activity' }).waitFor();
    await page.keyboard.press('Tab');
    const first = await page.locator(':focus').textContent();
    assert(/Skip to (main )?content/i.test(first ?? ''), `First keyboard focus: ${first}`);
    await page.keyboard.press('Enter'); assert(await page.locator('main').evaluate(e => e === document.activeElement), 'Skip did not focus main');
    await shot('activity-desktop'); return { firstFocus: first };
  });
  await check('activity search and facet survive detail round trip and browser back/forward', async () => {
    await goto('/'); const search = page.getByRole('textbox', { name: 'Search activity' }); await search.fill('Orders');
    await page.getByRole('button', { name: /^Starred/ }).click(); await paint();
    const starting = new URL(page.url()).search;
    await page.getByRole('link', { name: 'Orders: optimization across 50 versions', exact: true }).first().click();
    await page.getByRole('heading', { name: 'Orders: optimization across 50 versions', exact: true }).waitFor();
    await page.getByRole('link', { name: 'Activity', exact: true }).last().click();
    assert(await search.inputValue() === 'Orders', 'Search reset by Activity breadcrumb');
    assert(await page.getByRole('button', { name: /^Starred/ }).getAttribute('aria-pressed') === 'true', 'Facet reset');
    await page.goBack(); await page.locator('.session-page').waitFor(); await page.goForward(); await search.waitFor();
    assert(await search.inputValue() === 'Orders', 'Search lost by browser forward'); return { search: starting };
  });
  await check('performance search typing: 20 samples', async () => {
    await goto('/'); await page.getByRole('textbox', { name: 'Search activity' }).waitFor();
    return performanceSamples('search', i => page.getByRole('textbox', { name: 'Search activity' }).fill(i % 2 ? 'Orders' : ''), async i => { assert(await page.getByRole('textbox', { name: 'Search activity' }).inputValue() === (i % 2 ? 'Orders' : ''), 'Input value not settled'); }, 200);
  });
  await check('performance facet toggle: 20 samples', async () => {
    await page.getByRole('textbox', { name: 'Search activity' }).fill('');
    return performanceSamples('facet', () => page.getByRole('button', { name: /^Starred/ }).click(), async () => {}, 200);
  });
  await check('performance session expand: 20 samples', async () => {
    await goto('/'); await page.getByRole('button', { name: /^(Expand|Collapse) Orders:/ }).waitFor();
    return performanceSamples('session expansion', () => page.getByRole('button', { name: /^(Expand|Collapse) Orders:/ }).click(), async () => {}, 200);
  });
  await check('comparison pair and independent dimensions visible through actual UI', async () => {
    assert(route, 'Optimization DTO route unavailable'); await goto(route); await waitComparison();
    const comparison = page.locator('section[aria-label="Version comparison"]');
    const body = await comparison.innerText(); assert(/Policy/.test(body) && /IO/.test(body) && /Results/.test(body), 'Independent dimensions absent');
    await page.getByRole('tab', { name: 'IO', exact: true }).click(); await shot('session-comparison-io');
    assert((await page.locator('table.version-table tbody tr').count()) === 50, 'Expected 50 rendered version rows');
    return { text: body.slice(0, 1600) };
  });
  await check('performance A/B selection: 20 samples', async () => {
    assert(route, 'Optimization route unavailable'); await goto(route); await waitComparison();
    return performanceSamples('A/B selection', i => page.getByRole('radio', { name: i % 2 ? 'B v49' : 'B v48', exact: true }).click({ force: true }), waitComparison, 500);
  });
  await check('performance case selection: 20 samples', async () => {
    assert(route, 'Optimization route unavailable'); await goto(route); await waitComparison();
    return performanceSamples('case change', async i => {
      await page.getByRole('radio', { name: i % 2 ? '@CustomerId = 1' : '@CustomerId = 2', exact: true }).click({ force: true });
      await page.keyboard.press('Escape');
    }, async i => { await page.waitForFunction(expected => new URL(location.href).searchParams.get('case') === expected, i % 2 ? 'synthetic-case-0' : 'synthetic-case-1'); await waitComparison(); }, 500, () => page.getByRole('button', { name: /(?:All cases|@CustomerId = \d+) · (?:Sum|Mean)/ }).click());
  });
  await check('selected aggregate updates table and source timing', async () => {
    await goto(route); await waitComparison();
    await page.getByRole('button', { name: 'All cases · Sum', exact: true }).click();
    await page.getByRole('radio', { name: 'Mean', exact: true }).click({ force: true }); await page.keyboard.press('Escape'); await waitComparison();
    assert(await page.getByRole('columnheader', { name: 'Elapsed (mean of case medians)', exact: true }).isVisible(), 'Mean timing heading absent');
    const active = page.locator('table.version-table tbody tr').filter({ has: page.getByRole('radio', { name: 'B v49', exact: true }) });
    assert(/11(?:\.0)?\s*ms/.test(await active.innerText()), 'Active candidate does not show 11ms mean');
    await shot('selected-mean');
  });
  await check('history successive pages stay bounded', async () => {
    let cursor, count = 0; const sizes = []; const seen = new Set();
    do { const data = await api('/api/sessions/1/operations?limit=500' + (cursor ? '&cursor=' + cursor : '')); sizes.push(data.items.length); for (const row of data.items) { assert(!seen.has(row.id), 'Duplicate across history pages'); seen.add(row.id); } count += data.items.length; cursor = data.nextCursor; } while (cursor && sizes.length < 4);
    assert(count === 1000 && sizes.every(n => n <= 500), 'Paging count/cap mismatch'); return { sizes, count };
  });
  await check('failed compare reason retained in actual detail', async () => {
    await goto('/operations/9951'); await page.getByText('Synthetic execution timed out after 30 seconds.', { exact: true }).waitFor(); await shot('failed-compare');
  });
  await check('actual operation header agrees with comparison pair', async () => {
    assert(direct, 'Direct comparison unavailable'); await goto('/operations/10000'); await waitComparison();
    const title = await page.getByRole('heading', { level: 1 }).innerText();
    assert(title.includes(`${direct.a} → ${direct.b}`), `Heading '${title}' conflicts with actual comparison '${direct.a} → ${direct.b}'`); return { title };
  });
  for (const [name, target, ready] of [
    ['activity', '/', () => page.getByRole('textbox', { name: 'Search activity' }).waitFor()],
    ['comparison', route, waitComparison],
    ['operation', '/operations/10000', waitComparison],
    ['usage', '/stats', async () => {
      await page.locator('[aria-label="Usage summary"]').getByText('10,000', { exact: true }).waitFor();
      await page.waitForFunction(() => window.__acceptance.activeRequests === 0);
    }],
    ['profiles', '/profiles', () => page.getByRole('heading', { name: 'dev', exact: true }).waitFor()],
    ['settings', '/settings', () => page.getByRole('heading', { name: 'Settings', exact: true }).waitFor()],
  ]) {
    if (!target) continue;
    for (const theme of ['light', 'dark']) await check(`${name} ${theme} accessibility and screenshot`, async () => {
      await page.setViewportSize({ width: 1440, height: 1000 });
      await goto(target); await ready();
      await page.getByRole('button', { name: /^Theme:/ }).click();
      await page.getByRole('menuitemradio', { name: theme === 'dark' ? 'Dark' : 'Light', exact: true }).click(); await paint();
      await page.keyboard.press('Escape');
      await page.getByRole('menu').waitFor({ state: 'hidden' });
      await page.waitForTimeout(100);
      assert(await page.evaluate(() => document.documentElement.classList.contains('dark')) === (theme === 'dark'), 'Theme menu did not apply intended theme');
      await page.evaluate(() => window.scrollTo(0, 0)); await paint();
      await shot(`${name}-${theme}`); return axe(`${name}-${theme}`);
    });
    await check(`${name} initial route settlement after required responses`, async () => {
      await goto(target); await ready(); const end = await paint();
      const timing = await page.evaluate(end => {
        const entries = performance.getEntriesByType('resource').filter(e => e.name.includes('/api/'));
        const responseEnd = Math.max(0, ...entries.map(e => e.responseEnd));
        return { navigationToSettledMs: end, lastRequiredResponseMs: responseEnd, responseToSettledMs: end - responseEnd, longTasks: window.__acceptance.longTasks };
      }, end);
      assert(timing.responseToSettledMs <= 2000, `Settlement took ${timing.responseToSettledMs.toFixed(1)}ms after responses`); return timing;
    });
    for (const width of [390, 768, 1440, 1920]) await check(`${name} ${width}px page reflow`, async () => {
      await page.setViewportSize({ width, height: 1000 }); await goto(target); await ready(); await paint();
      await page.evaluate(() => window.scrollTo(0, 0)); await paint();
      const layout = await widthCheck(); await shot(`${name}-${width}`);
      assert(layout.scrollWidth <= width + 1, `Page width ${layout.scrollWidth} exceeds ${width}: ${JSON.stringify(layout.overflow)}`); return layout;
    });
  }
  await check('200% equivalent CSS reflow and reduced motion', async () => {
    await page.setViewportSize({ width: 720, height: 500 }); await goto(route); await waitComparison();
    const layout = await widthCheck(); await shot('comparison-200percent-equivalent');
    assert(layout.scrollWidth <= 721, `Reflow overflow ${layout.scrollWidth}`);
    assert(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches), 'Reduced motion not active'); return layout;
  });
  await check('actual case outcome dialog explains lower reads with policy failure; keyboard focus return', async () => {
    await page.setViewportSize({ width: 1440, height: 1000 }); await goto(route); await waitComparison();
    const trigger = page.locator('section[aria-label="Version comparison"]').getByRole('button', { name: /^Policy:/ }).first();
    await trigger.focus(); await page.keyboard.press('Enter'); await page.getByRole('dialog', { name: 'Case outcomes', exact: true }).waitFor();
    const row = page.getByRole('table', { name: 'Case outcome details' }).getByRole('row').nth(1);
    assert(/@CustomerId = 1/.test(await row.innerText()) && /Fail/.test(await row.innerText()) && /elapsed-regressed/.test(await row.innerText()), 'Mixed case reason absent or wrong ordering');
    await shot('case-outcome-policy-fail'); await page.keyboard.press('Escape');
    await page.waitForFunction(() => !document.querySelector('[role="dialog"]'));
    await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label')?.startsWith('Policy:'));
    assert(await trigger.evaluate(e => e === document.activeElement), 'Case outcome dialog focus did not return');
  });
  await check('approximate comparison caveat remains prominent', async () => {
    assert(direct && route, 'Actual comparison template unavailable');
    const approximate = { ...direct, a: 'v48', b: 'v49', basis: 'ApproximateAcrossConditions', operationId: null, groupLabelA: 'Repeat 3 · synthetic condition A', groupLabelB: 'Repeat 5 · synthetic condition B' };
    await page.route('**/api/sessions/1/optimization/compare**', r => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(approximate) }));
    await goto(route); await waitComparison();
    await page.getByText('Different conditions — approximate, not a verdict', { exact: true }).waitFor(); await shot('approximate-intercepted');
    await page.unroute('**/api/sessions/1/optimization/compare**');
  }, 'intercepted approximate DTO based on actual comparison; not journal-derived cross-condition proof');
  await check('copy baseline/candidate preserves actual stored SQL and manual fallback focus', async () => {
    await goto('/operations/10000'); await waitComparison();
    const detail = await api('/api/operations/10000');
    const baseline = page.getByRole('button', { name: 'Copy baseline SQL', exact: true });
    const candidate = page.getByRole('button', { name: 'Copy candidate SQL', exact: true });
    await baseline.click();
    await page.getByText('Copied baseline SQL', { exact: true }).waitFor();
    assert(await page.evaluate(() => navigator.clipboard.readText()) === detail.sqlText, 'Baseline clipboard payload differs from stored SQL');
    await candidate.click();
    await page.getByText('Copied candidate SQL', { exact: true }).waitFor();
    assert(await page.evaluate(() => navigator.clipboard.readText()) === detail.candidateSqlText, 'Candidate clipboard payload differs from stored SQL');
    await page.evaluate(() => {
      window.__clipboardWriteText = navigator.clipboard.writeText.bind(navigator.clipboard);
      Object.defineProperty(navigator.clipboard, 'writeText', { configurable: true, value: async () => { throw new Error('Synthetic clipboard unavailable'); } });
    });
    await baseline.click(); const modal = page.getByRole('dialog', { name: 'Copy manually', exact: true }); await modal.waitFor();
    const text = page.getByRole('textbox', { name: 'Text to copy', exact: true });
    assert(await text.inputValue() === detail.sqlText, 'Manual-copy payload differs from stored baseline');
    await text.waitFor(); await paint(); assert(await text.evaluate(e => e === document.activeElement), 'Manual-copy text did not receive focus');
    await shot('manual-copy-fallback'); await page.keyboard.press('Escape'); await modal.waitFor({ state: 'hidden' });
    assert(await baseline.evaluate(e => e === document.activeElement), 'Manual-copy close did not return focus');
    await page.evaluate(() => Object.defineProperty(navigator.clipboard, 'writeText', { configurable: true, value: window.__clipboardWriteText }));
    return { baselineLength: detail.sqlText.length, candidateLength: detail.candidateSqlText.length };
  }, 'actual stored SQL; intercepted Clipboard write failure for manual-copy branch');
  await check('large SQL and long subject stay bounded and preserve original copy', async () => {
    const detail = await api('/api/operations/10000');
    const longSql = 'SELECT 1 AS SyntheticValue; -- bounded preview acceptance\n'.repeat(6000);
    detail.sqlText = longSql; detail.compareSubject = 'Orders reconciliation with a deliberately long synthetic subject name to verify readable wrapping on a narrow screen';
    await page.route('**/api/operations/10000', r => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
    await page.setViewportSize({ width: 390, height: 1000 }); await goto('/operations/10000'); await waitComparison();
    await page.getByText('Diff unavailable: SQL exceeds the 256 KiB or 4,000 line limit.', { exact: true }).waitFor();
    await page.evaluate(() => {
      window.__clipboardWriteText = navigator.clipboard.writeText.bind(navigator.clipboard);
      Object.defineProperty(navigator.clipboard, 'writeText', { configurable: true, value: async text => { window.__lastClipboardPayload = text; await window.__clipboardWriteText(text); } });
    });
    await page.getByRole('button', { name: 'Copy baseline SQL', exact: true }).click();
    await page.getByText('Copied baseline SQL', { exact: true }).waitFor();
    assert(await page.evaluate(() => window.__lastClipboardPayload) === longSql, 'Copy API payload truncated original text');
    const nativeClipboard = await page.evaluate(() => navigator.clipboard.readText());
    assert(nativeClipboard.replaceAll('\r\n', '\n') === longSql, 'Native clipboard differs beyond Windows line endings');
    const layout = await widthCheck(); await page.evaluate(() => window.scrollTo(0, 0)); await paint(); await shot('large-sql-long-label-390');
    assert(layout.scrollWidth <= 391, `Long SQL/subject page overflow ${layout.scrollWidth}`); return { sqlBytes: Buffer.byteLength(longSql), lines: 6000, ...layout };
  }, 'intercepted large SQL and long subject in actual detail DTO');
  await check('rejected operation shows reason without fabricating successful evidence', async () => {
    const detail = await api('/api/operations/9951');
    detail.operation.status = 'rejected'; detail.operation.errorKind = 'mutation-policy';
    detail.operation.errorMessage = 'Synthetic policy rejected this request. No SQL was executed.';
    await page.route('**/api/operations/9951', r => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
    await page.setViewportSize({ width: 1440, height: 1000 }); await goto('/operations/9951');
    await page.getByText(detail.operation.errorMessage, { exact: true }).waitFor();
    assert(await page.locator('section[aria-label="Version comparison"]').count() === 0, 'Rejected operation exposes successful comparison panel'); await shot('rejected-operation');
  }, 'intercepted rejected operation based on actual failed detail');
  await check('delayed comparison has local loading feedback then settles', async () => {
    assert(route, 'Comparison route unavailable');
    await page.route('**/api/sessions/1/optimization/compare**', async r => {
      await new Promise(resolve => setTimeout(resolve, 1200)); await r.continue();
    });
    await goto(route); await page.getByText('Loading comparison…', { exact: true }).waitFor(); await shot('comparison-loading');
    await waitComparison();
  }, 'actual backend comparison with 1200ms intercepted delay, excluded from CPU timings');
  await check('empty Activity journal view has usable filter/search next step', async () => {
    for (const pattern of ['**/api/operations?**', '**/api/sessions?**']) await page.route(pattern, r => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], nextCursor: null }) }));
    await goto('/?time=all'); await page.getByText('Nothing matches these filters.', { exact: true }).waitFor();
    assert(await page.getByRole('textbox', { name: 'Search activity', exact: true }).isEnabled(), 'Search unavailable in empty state'); await shot('empty-activity');
  }, 'intercepted empty journal pages; actual shell and UI');
  await check('disconnect feedback and reconnect restore actual route', async () => {
    await page.route('**/api/**', r => r.abort('internetdisconnected'));
    await goto('/operations/9948'); await page.getByText(/Dashboard unavailable — reconnecting/).waitFor(); await shot('disconnected');
    await page.unroute('**/api/**');
    await page.getByRole('heading', { level: 1 }).waitFor({ timeout: 15000 });
    await page.getByText(/Dashboard unavailable — reconnecting/).waitFor({ state: 'hidden', timeout: 15000 }); await shot('reconnected');
  }, 'intercepted transport outage and removal; actual backend on retry');
  await check('profile editor and intercepted save failure retain next step', async () => {
    await page.setViewportSize({ width: 1440, height: 1000 }); await goto('/profiles'); await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByRole('textbox', { name: 'Server', exact: true }).fill('synthetic.invalid.example');
    await page.route('**/api/profiles', async r => r.request().method() === 'PUT' ? r.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'Synthetic conflict: profile changed. Reload and try again.' }) }) : r.fallback());
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByText('Synthetic conflict: profile changed. Reload and try again.', { exact: true }).waitFor();
    assert(await page.getByRole('button', { name: 'Save', exact: true }).isEnabled(), 'Save remains disabled after error'); await shot('profile-save-failure');
    page.once('dialog', dialog => dialog.accept()); await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.unroute('**/api/profiles');
  }, 'intercepted PUT failure; actual GET profile, no persistent write');
  for (const status of [401, 408, 500]) await check(`intercepted operation HTTP ${status} feedback`, async () => {
    await page.route('**/api/operations/9949', r => r.fulfill({ status, contentType: 'application/json', body: JSON.stringify({ error: { code: 'synthetic', message: 'Synthetic request failed; retry this view.' } }) }));
    await goto('/operations/9949'); await page.getByRole('alert').first().waitFor(); await shot(`operation-http-${status}`);
    const text = await page.getByRole('alert').first().innerText(); await page.unroute('**/api/operations/9949'); return { text };
  }, 'intercepted transient HTTP response');
  await check('intercepted approval request keyboard focus return; no decision sent', async () => {
    const pending = { requestId: 'synthetic-request', request: { profile: 'dev', server: 'synthetic.invalid', database: 'ShopDb', engine: 'sqlserver', tier: 'DML', sql: 'UPDATE dbo.Orders SET Flag = @flag WHERE Id = @id;', parameters: [{ name: 'flag', type: 'int', value: '1' }, { name: 'id', type: 'int', value: '7' }], digest: 'a'.repeat(64), grantees: [] }, environment: 'dev', writerSameAsReader: true, expiresAt: new Date(Date.now() + 180000).toISOString(), requester: { sessionId: 1, agentLabel: 'codex' } };
    await page.route('**/api/mutations/events', r => r.fulfill({ status: 200, contentType: 'text/event-stream', body: `event: snapshot\ndata: ${JSON.stringify([pending])}\n\n` }));
    await goto('/'); const trigger = page.getByRole('button', { name: 'Review', exact: true }).first(); await trigger.waitFor(); await trigger.focus(); await page.keyboard.press('Enter');
    const modal = page.locator('[data-slot="dialog-content"]');
    await modal.waitFor(); await shot('synthetic-approval'); await axe('synthetic-approval');
    assert(await page.getByRole('button', { name: 'Deny', exact: true }).isVisible(), 'Deny action missing');
    await page.keyboard.press('Escape'); await modal.waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.activeElement?.textContent === 'Review');
    assert(await trigger.evaluate(e => e === document.activeElement), 'Approval focus did not return to trigger'); await page.unroute('**/api/mutations/events');
  }, 'intercepted SSE snapshot; no approval/denial sent');
} catch (error) {
  report.errors.push({ kind: 'harness-fatal', message: String(error.stack).slice(0, 3000) });
} finally {
  report.finishedAt = new Date().toISOString(); report.passed = report.checks.every(c => c.passed) && !report.errors.length;
  fs.writeFileSync(path.join(output, 'acceptance.json'), JSON.stringify(report, null, 2));
  const failed = report.checks.filter(c => !c.passed);
  const text = [
    '# Plan 126 browser acceptance', '',
    `Run: ${report.startedAt} to ${report.finishedAt}. Revision: ${report.revision}. Product working tree may include changes recorded in acceptance.json.`, '',
    'Synthetic fixture: 500 sessions; 10,000 operations; primary session 1,000 operations; 50 versions; 20 cases. Actual backend DTO snapshots are retained alongside screenshots. No database workload or real persistent write was sent.', '',
    `Browser: Chromium ${report.host.browser}; ${report.host.platform} ${report.host.release}; ${report.host.cpu}; ${report.host.cpus} logical CPUs; ${report.host.memoryGiB} GiB RAM.`, '',
    `Observed checks: ${report.checks.length - failed.length}/${report.checks.length} passed; ${failed.length} failed. Automated findings do not establish any category score.`, '',
    `Scope: ${report.scope}. Full matrix acceptance remains incomplete while the limitations below remain open.`, '',
    ...failed.map(c => `- FAIL ${c.name}: ${c.error.replaceAll('\n', ' ')}`), '',
    '## Performance', '',
    ...(skipPerformance ? ['Performance was intentionally skipped in this focused run. Retained provisional metrics remain separate and are not replaced by this report.', ''] : []),
    '| Action | Samples | p95 ms | Limit ms | Max long task ms |', '|---|---:|---:|---:|---:|',
    ...report.performance.map(p => `| ${p.name} | ${p.sampleCount} | ${p.p95Ms.toFixed(1)} | ${p.thresholdMs} | ${p.maxLongTaskMs.toFixed(1)} |`), '',
    'Three warm-up iterations precede each twenty-sample series. Input capture and second requestAnimationFrame define paint; comparison fetch responseEnd defines the response boundary, while cached changes use input-to-paint. These are browser instrumentation proxies, not external input-latency hardware measurements.', '',
    '## Limitations and open matrix evidence', '',
    ...report.limitations.map(s => '- ' + s),
    '- Separate measure operations and cross-condition fixtures are not present in this journal. Non-adjacent independent pairing is tested through the actual backend; approximate rendering uses an explicitly intercepted DTO.',
    '- Pending/delayed route, live disconnect/reconnect, empty history, rejected operation, copy/manual-copy failure and long SQL/labels require additional focused evidence if absent from the recorded checks.',
    '- Complete end-to-end keyboard navigation, native zoom and manual non-text contrast remain required before accessibility can justify 9.0.',
    '- Per-category 9.0 claims are intentionally withheld while any required matrix evidence is missing.', '',
    'Read acceptance.json for exact assertions, raw samples, axe nodes, intercepted versus actual provenance and errors. Viewport screenshots are listed there; each captures the current scroll position rather than a scaled full-page image.', '',
  ].join('\n');
  fs.writeFileSync(path.join(output, 'report.md'), text);
  await context.close(); await browser.close();
}
console.log(JSON.stringify({ passed: report.passed, checks: report.checks.length, failed: report.checks.filter(c => !c.passed).map(c => c.name), errors: report.errors.length, output }));
process.exitCode = report.passed ? 0 : 1;
