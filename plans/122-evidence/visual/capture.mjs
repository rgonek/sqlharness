import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
process.env.SQLHARNESS_VISUAL_ROOT = path.resolve(here, '../../..');
const { setup, launch, ops, details, comp } = await import('../../121-evidence/visual/harness/app-capture.mjs');
const base = process.env.SQLHARNESS_VISUAL_URL ?? 'http://127.0.0.1:48132';
const preview = 'SELECT OrderId, CustomerId FROM dbo.Orders WHERE CustomerId = @CustomerId';
Object.assign(ops[0], { dashboardEvidenceVersion: 1, sqlPreview: preview, sqlPreviewTruncated: true });
Object.assign(ops[1], { dashboardEvidenceVersion: 1, sqlPreview: 'SELECT baseline_column FROM dbo.Orders', sqlPreviewTruncated: false });
Object.assign(details[22].operation, { dashboardEvidenceVersion: 1, recordsAffected: 0 });
details[22].mutationRequestId = 'offline-request-122';
details[22].mutationRequest = { requestId: 'offline-request-122', state: 'succeeded', status: 'Applied', verification: 'click', createdAt: '2026-10-27T10:00:00Z', decidedAt: '2026-10-27T10:01:00Z', finishedAt: '2026-10-27T10:01:02Z' };
for (const [index, variant] of details[23].variants.entries()) variant.tableIo = [{ table: 'Orders', scanCount: 1, logicalReads: index ? 7400 : 48000, physicalReads: index ? 2 : 4, readAheadReads: 8, lobLogicalReads: index ? 0 : 100, lobPhysicalReads: 1, lobReadAheadReads: 2 }];
Object.assign(comp.statements[0], {
  cpuBeforeMs: 80, cpuAfterMs: 0, elapsedBeforeMs: 180, elapsedAfterMs: 22,
  planRepetitionBefore: 2, planRepetitionAfter: 3,
  grantRequestedKbBefore: 1024, grantRequestedKbAfter: 0,
  grantGrantedKbBefore: 1024, grantGrantedKbAfter: 512,
  grantMaxUsedKbBefore: 800, grantMaxUsedKbAfter: 128,
  hasSpillWarningBefore: true, hasSpillWarningAfter: null,
  planLinkBefore: '/operations/23?tab=plan&variant=baseline&repetition=2',
  planLinkAfter: '/operations/23?tab=plan&variant=candidate&repetition=3',
});
Object.assign(comp.statements[0].objects[0], { lobLogicalReadsBefore: 100, lobLogicalReadsAfter: 0 });

await fs.mkdir(here, { recursive: true });
const browser = await launch();
const records = [], errors = [];
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 1100 }, deviceScaleFactor: 1 });
  page.on('pageerror', error => errors.push(error.message));
  await setup(page);
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    if (!url.pathname.endsWith('/optimization/compare') && !/^\/api\/operations\/\d+\/optimization$/.test(url.pathname)) return route.fallback();
    const data = structuredClone(comp);
    data.selectedCase = url.searchParams.get('case') ?? 'all';
    data.view = url.searchParams.get('view') ?? 'statements';
    if (data.selectedCase === 'all') {
      data.statementMetricsUnavailableReason = 'Statement timing, grants and spill evidence are unavailable for All cases. Select a single case.';
      for (const statement of data.statements) {
        for (const key of Object.keys(statement)) if (/^(cpu|elapsed|grant|hasSpillWarning|planRepetition)/.test(key)) statement[key] = null;
        for (const object of statement.objects) { object.lobLogicalReadsBefore = null; object.lobLogicalReadsAfter = null; }
      }
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(data) });
  });
  async function capture(name) {
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: path.join(here, name + '.png'), fullPage: true });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
    records.push({ name, viewport: page.viewportSize(), overflow, text: await page.locator('main').innerText() });
    if (overflow) throw new Error('Whole-page overflow: ' + name);
  }
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 900 : 1100 });
    await page.goto(base + '/');
    await page.locator('main h1').waitFor();
    const expand = page.getByRole('button', { name: 'Expand Optimizing the orders query', exact: true });
    if (await expand.count()) await expand.click();
    await page.getByText(preview, { exact: true }).waitFor();
    await capture('activity-' + width);
    await page.goto(base + '/sessions/1');
    await page.getByRole('tab', { name: 'IO', exact: true }).click();
    await page.getByText('Statement timing, grants and spill evidence are unavailable for All cases. Select a single case.', { exact: true }).waitFor();
    await capture('all-cases-' + width);
    await page.getByRole('button', { name: 'All cases · Sum', exact: true }).click();
    await page.getByRole('radio', { name: '@CustomerId = 42', exact: true }).click();
    await page.getByText(/^Requested grant \(KB\):/).waitFor();
    await capture('statement-evidence-' + width);
    if (await page.getByText('Statement memory is not retained in the journal.', { exact: true }).count()) throw new Error('Obsolete statement-memory guidance');
    await page.getByRole('button', { name: 'More columns', exact: true }).click();
    if (await page.getByRole('checkbox', { name: 'Physical', exact: true }).count()) throw new Error('Unsupported statement Physical option');
    await page.getByRole('checkbox', { name: 'LOB logical', exact: true }).check();
    await page.keyboard.press('Escape');
    await page.getByRole('radio', { name: 'Whole batch', exact: true }).click();
    await page.getByRole('button', { name: 'More columns', exact: true }).click();
    await page.getByRole('checkbox', { name: 'Physical', exact: true }).check();
    await page.keyboard.press('Escape');
    await page.getByRole('columnheader', { name: 'Physical', exact: true }).waitFor();
    await capture('whole-batch-counters-' + width);
    await page.getByRole('radio', { name: 'By statement', exact: true }).click();
    await page.getByRole('columnheader', { name: 'Physical', exact: true }).waitFor({ state: 'hidden' });
    await page.getByRole('columnheader', { name: 'LOB logical', exact: true }).waitFor();
    await capture('statement-lob-after-switch-' + width);
    await page.goto(base + '/operations/22');
    await page.getByText('Rows affected', { exact: true }).waitFor();
    await page.getByRole('tab', { name: 'Approval', exact: true }).click();
    await page.getByText('offline-request-122', { exact: true }).waitFor();
    await capture('mutation-lifecycle-' + width);
    await page.goto(base + '/operations/21');
    await page.getByRole('tab', { name: 'Results', exact: true }).click();
    await page.getByText(/Results unavailable|Result cells are not retained/).waitFor();
    await capture('query-results-' + width);
  }
  if (errors.length) throw new Error(errors.join('\n'));
  await fs.writeFile(path.join(here, 'results.json'), JSON.stringify({ fixture: 'Synthetic API evidence only; zero database connections or journal writes.', records, errors }, null, 2));
  console.log('PASS', records.length, 'offline desktop/mobile states; no page errors or whole-page overflow');
} finally { await browser.close(); }
