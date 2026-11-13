// Capture the real dashboard. Authentication stays in memory; no SQL is executed.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
const home = process.argv[2] && path.resolve(process.argv[2]);
const output = process.argv[3] && path.resolve(process.argv[3]);
if (!home || !output || !process.env.SHOWCASE_TOOLS_ROOT) throw new Error('Usage: SHOWCASE_TOOLS_ROOT=<node_modules> node scripts/showcase/capture.mjs <demo home> <output>');
const evidence = JSON.parse(fs.readFileSync(path.join(home, 'seed-evidence.json'), 'utf8'));
if (evidence.source !== 'synthetic showcase; no SQL executed') throw new Error('Only the showcase seed is supported.');
const endpoint = JSON.parse(fs.readFileSync(path.join(home, 'dashboard.json'), 'utf8'));
const origin = `http://127.0.0.1:${endpoint.port}`;
const require = createRequire(path.join(path.resolve(process.env.SHOWCASE_TOOLS_ROOT), '__showcase.cjs'));
const { chromium } = require('playwright');
fs.mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const report = { source: evidence.source, schemaVersion: evidence.schemaVersion, viewport: { width: 1500, height: 1060 }, browser: browser.version(), screenshots: [], errors: [] };
try {
  const context = await browser.newContext({ viewport: report.viewport, deviceScaleFactor: 1.5, colorScheme: 'dark', reducedMotion: 'reduce' });
  await context.addCookies([{ name: 'sqlharness_dashboard', value: endpoint.token, url: origin, httpOnly: true, sameSite: 'Strict' }]);
  await context.route('**/api/**', async route => {
    if (route.request().method() !== 'GET') {
      report.errors.push({ kind: 'blocked-write', path: new URL(route.request().url()).pathname });
      await route.abort('blockedbyclient');
    } else await route.continue();
  });
  await context.addInitScript(() => localStorage.setItem('sqlharness.theme', 'dark'));
  const page = await context.newPage();
  page.on('pageerror', error => report.errors.push({ kind: 'pageerror', message: error.message }));
  page.on('response', response => {
    if (response.status() >= 400) report.errors.push({ kind: 'http', path: new URL(response.url()).pathname, status: response.status() });
  });
  const go = async route => {
    await page.goto(origin + route);
    await page.waitForLoadState('networkidle');
    await page.evaluate(() => document.fonts.ready);
    await page.getByText('Loading comparison…', { exact: true }).waitFor({ state: 'hidden' });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  };
  const shot = async name => {
    await page.mouse.move(0, 0);
    await page.screenshot({ path: path.join(output, name + '.png'), animations: 'disabled', fullPage: name === 'optimization' });
    report.screenshots.push({ name: name + '.png', viewport: page.viewportSize(), fullPage: name === 'optimization', route: new URL(page.url()).pathname + new URL(page.url()).search });
  };
  await go('/operations/4');
  await page.getByRole('heading', { level: 1 }).waitFor();
  await shot('comparison');
  await go('/sessions/1?subject=annual-sales&scope=whole&a=v0&b=v2&view=whole');
  await page.getByRole('table', { name: 'annual-sales-NorthwindDemo versions' }).waitFor();
  await page.getByRole('tab', { name: 'IO', exact: true }).click();
  await page.getByRole('radio', { name: 'Whole batch', exact: true }).click();
  await page.waitForLoadState('networkidle');
  await page.setViewportSize({ width: 1500, height: 1300 });
  await shot('optimization');
  await page.setViewportSize(report.viewport);
  await go('/operations/4?case=demo-annual-sales');
  await page.getByRole('tab', { name: 'IO', exact: true }).click();
  await page.getByText('Loading comparison…', { exact: true }).waitFor({ state: 'hidden' });
  await page.waitForLoadState('networkidle');
  await shot('statement-io');
  const api = await context.request.get(origin + '/api/operations/4/optimization?case=all&aggregate=sum&view=whole');
  if (!api.ok()) throw new Error('Demo comparison API failed: ' + api.status());
  const comparison = await api.json();
  report.comparison = { operationId: comparison.operationId, beforeReads: comparison.wholeBatch?.before, afterReads: comparison.wholeBatch?.after, elapsed: comparison.elapsed, outcomes: comparison.outcomes };
  if (comparison.wholeBatch?.before !== 48200 || comparison.wholeBatch?.after !== 2140 || comparison.elapsed?.beforeMs !== 842 || comparison.elapsed?.afterMs !== 94) throw new Error('Unexpected demo metrics.');
  if (report.errors.length) throw new Error('Browser errors occurred; inspect capture-report.json.');
} finally {
  fs.writeFileSync(path.join(output, 'capture-report.json'), JSON.stringify(report, null, 2));
  await browser.close();
}
console.log(JSON.stringify(report, null, 2));
