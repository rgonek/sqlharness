import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..');
const home = process.env.SQLHARNESS_HOME || path.join(os.homedir(), '.sqlharness');
// Endpoint credentials remain in memory and are never included in evidence.
const endpoint = JSON.parse(await fs.readFile(path.join(home, 'dashboard.json'), 'utf8'));
const origin = `http://127.0.0.1:${endpoint.port}`;
const headers = { Cookie: `sqlharness_dashboard=${endpoint.token}` };
const index = await fetch(origin + '/', { headers });
if (index.status !== 200) throw Error(`Dashboard index returned ${index.status}`);
const html = await index.text();
const assets = [];
for (const match of html.matchAll(/(?:src|href)="(\/assets\/[^\"]+)"/g)) {
  const assetPath = match[1];
  const response = await fetch(origin + assetPath, { headers });
  if (response.status !== 200) throw Error(`Dashboard asset returned ${response.status}`);
  const actual = Buffer.from(await response.arrayBuffer());
  const expected = await fs.readFile(path.join(root, 'src/SqlHarness.Dashboard/ui/dist', assetPath));
  if (!actual.equals(expected)) throw Error(`Installed asset differs: ${assetPath}`);
  assets.push({ path: assetPath, sha256: crypto.createHash('sha256').update(actual).digest('hex') });
}
if (assets.length < 2) throw Error('Dashboard did not serve the built UI');
const evidence = { origin, pid: endpoint.pid, startedAt: endpoint.startedAt, assets, tokenRecorded: false };
await fs.writeFile(path.join(root, 'plans/121-evidence/shadcn/installed-assets.json'), JSON.stringify(evidence, null, 2) + '\n');
console.log(JSON.stringify({ origin, pid: endpoint.pid, verifiedAssets: assets.length }));
