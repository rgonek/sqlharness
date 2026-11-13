// Creates synthetic local dashboard evidence. Never connects to a database.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { DatabaseSync } from 'node:sqlite';
import { createHash } from 'node:crypto';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
if (!process.argv[2]) throw new Error('Usage: node scripts/showcase/seed.mjs <NEW isolated home> [port]');
const home = path.resolve(process.argv[2]);
const port = Number(process.argv[3] ?? 48139);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Port must be an integer from 1024 to 65535.');
if (fs.existsSync(home)) throw new Error('Refusing to overwrite an existing home. Choose a NEW directory.');
fs.mkdirSync(path.join(home, 'data'), { recursive: true });
const schemaText = fs.readFileSync(path.join(root, 'src/SqlHarness.Core/Journal/JournalSchema.cs'), 'utf8');
const schema = schemaText.match(/internal const string Current = """([\s\S]*?)""";/)?.[1];
const schemaVersion = Number(schemaText.match(/CurrentVersion = (\d+)/)?.[1]);
if (!schema || !schemaVersion) throw new Error('Cannot read the current journal schema.');
const db = new DatabaseSync(path.join(home, 'data/activity.db'));
db.exec(schema);
db.exec('PRAGMA user_version = ' + schemaVersion);
const insert = (table, values) => {
  const keys = Object.keys(values);
  db.prepare(`INSERT INTO ${table} (${keys.join(',')}) VALUES (${keys.map(() => '?').join(',')})`).run(...Object.values(values));
};
insert('journal_meta', { key: 'fingerprint_salt', value: Buffer.alloc(32, 7) });
const sql = [
  `SELECT c.CustomerId, c.Name, SUM(o.Total) AS Revenue\nFROM dbo.Customers AS c\nJOIN dbo.Orders AS o ON o.CustomerId = c.CustomerId\nWHERE YEAR(o.OrderDate) = @Year\nGROUP BY c.CustomerId, c.Name\nORDER BY Revenue DESC, c.CustomerId;`,
  `SELECT c.CustomerId, c.Name, SUM(o.Total) AS Revenue\nFROM dbo.Customers AS c\nJOIN dbo.Orders AS o ON o.CustomerId = c.CustomerId\nWHERE o.OrderDate >= @StartDate\n  AND o.OrderDate < @EndDate\nGROUP BY c.CustomerId, c.Name\nORDER BY Revenue DESC, c.CustomerId;`,
  `WITH Sales AS (\n  SELECT CustomerId, SUM(Total) AS Revenue\n  FROM dbo.Orders\n  WHERE OrderDate >= @StartDate\n    AND OrderDate < @EndDate\n  GROUP BY CustomerId\n)\nSELECT c.CustomerId, c.Name, s.Revenue\nFROM Sales AS s\nJOIN dbo.Customers AS c ON c.CustomerId = s.CustomerId\nORDER BY s.Revenue DESC, c.CustomerId;`,
];
const hash = version => 'sha256:' + createHash('sha256').update(sql[version]).digest('hex');
const now = Date.now();
const stamp = minutes => new Date(now - minutes * 60000).toISOString();
const cases = [{ id: '0', index: 0, label: 'Annual sales · 2025', setName: 'annual-sales', varying: [], fingerprint: 'demo-annual-sales', measurementFingerprint: 'demo-annual-sales', varyingFingerprint: 'demo-no-varying' }];
const conditions = { version: 1, engine: 'sqlserver', server: 'demo.invalid', database: 'NorthwindDemo',
  execution: { operation: 'compare', sessionPolicy: 'new-session-per-case', setupBinding: 'bound-per-case', warmup: 1, repeat: 7, timeoutSeconds: 30, comparisonMode: 'Ordered', compareNullability: true },
  setup: null, fixed: [{ name: 'Year', effectiveType: 'int', value: 2025 }, { name: 'StartDate', effectiveType: 'date', value: '2025-01-01' }, { name: 'EndDate', effectiveType: 'date', value: '2026-01-01' }],
  cases, fingerprint: 'demo-sales-conditions', measurementFingerprint: 'demo-sales-measurement', omitted: [] };
insert('sessions', { id: 1, session_key: 'showcase:sales', agent_kind: 'codex', transport: 'mcp', source: 'synthetic-showcase', client_name: 'Codex',
  host_pid: process.pid, cwd: 'demo/sales-report', first_seen: stamp(28), last_seen: stamp(1), dashboard_evidence_version: 1 });
insert('session_annotations', { session_id: 1, title: 'Annual sales report · Demo data', note: 'Synthetic measurements · Date-range filter → join experiment (timeout) → pre-aggregate orders → final verification.', starred: 1, updated_at: stamp(1) });
const versions = [
  { elapsed: 842, cpu: 790, reads: 48200, scans: 186, orders: 46800, customers: 1400 },
  { elapsed: 216, cpu: 188, reads: 9200, scans: 186, orders: 7800, customers: 1400 },
  { elapsed: 94, cpu: 78, reads: 2140, scans: 2, orders: 740, customers: 1400 },
];
let metricId = 0, statementId = 0;
const comparisons = [{ id: 1, before: 0, after: 1, minutes: 24, label: 'Date-range filter', keep: false },
  { id: 3, before: 1, after: 2, minutes: 8, label: 'Pre-aggregate orders', keep: true },
  { id: 4, before: 0, after: 2, minutes: 2, label: 'Final verification', keep: true }];
db.exec('BEGIN');
for (const attempt of comparisons) {
  insert('operations', { id: attempt.id, session_id: 1, operation: 'compare', host_pid: process.pid,
    started_at: stamp(attempt.minutes), updated_at: stamp(attempt.minutes), finished_at: stamp(attempt.minutes), status: 'succeeded', exit_code: 0,
    duration_ms: (versions[attempt.before].elapsed + versions[attempt.after].elapsed) * 8, profile: 'demo', engine: 'sqlserver', server: 'demo.invalid', database: 'NorthwindDemo',
    result_sets: 1, rows_returned: 120, sql_hash: hash(attempt.before), candidate_sql_hash: hash(attempt.after), sql_text: sql[attempt.before], candidate_sql_text: sql[attempt.after],
    compare_subject: 'annual-sales', compare_scope: 'whole', conditions_json: JSON.stringify(conditions), conditions_fingerprint: conditions.fingerprint, measurement_fingerprint: conditions.measurementFingerprint,
    summary_json: JSON.stringify({ kind: 'compare', comparison: 'Ordered', resultsEquivalent: true, regression: { verdict: 'pass', reasons: [] }, cases: [{ caseId: '0', caseLabel: cases[0].label, equivalence: { mode: 'Ordered', equivalent: true }, regression: { verdict: 'pass', reasons: [] } }] }) });
  insert('operation_annotations', { operation_id: attempt.id, agent_label_initial: attempt.label, agent_label: attempt.label, agent_keep: Number(attempt.keep), owner_note: 'Demo data · synthetic measurements', updated_at: stamp(attempt.minutes) });
  for (const [index, variant] of ['baseline', 'candidate'].entries()) {
    const version = index === 0 ? attempt.before : attempt.after;
    const v = versions[version], mid = ++metricId, sid = ++statementId;
    insert('operation_metrics', { id: mid, operation_id: attempt.id, ordinal: index, variant, case_id: '0', case_label: cases[0].label, parameter_set: cases[0].setName, runs: 7,
      elapsed_ms_min: v.elapsed - 4, elapsed_ms_median: v.elapsed, elapsed_ms_max: v.elapsed + 6, cpu_ms_min: v.cpu - 3, cpu_ms_median: v.cpu, cpu_ms_max: v.cpu + 5,
      logical_reads_min: v.reads, logical_reads_median: v.reads, logical_reads_max: v.reads,
      spill_count: 0, has_warnings: 0, has_implicit_conversion: 0, missing_index_count: 0, result_sets: 1, rows_returned: 120 });
    for (const [table, reads] of [['Orders', v.orders], ['Customers', v.customers]]) {
      insert('operation_table_io', { metric_id: mid, table_name: table, logical_reads: reads, scan_count: table === 'Orders' ? v.scans - 1 : 1,
        physical_reads: 0, read_ahead_reads: 0, lob_logical_reads: 0, lob_physical_reads: 0, lob_read_ahead_reads: 0, cold_runs: 0 });
    }
    insert('operation_statements', { id: sid, operation_id: attempt.id, ordinal: index, variant, parameter_set: cases[0].setName, case_id: '0', case_label: cases[0].label,
      statement_ordinal: 0, statement_hash: hash(version), statement_key: 'annual-sales-select', occurrence: 1, statement_text: sql[version], executions: 1,
      status: 'confirmed', cpu_ms: v.cpu, elapsed_ms: v.elapsed, operators_json: '[]', hidden_work_json: '[]',
      grant_requested_kb: version === 0 ? 16384 : 2048, grant_granted_kb: version === 0 ? 16384 : 2048,
      grant_max_used_kb: version === 0 ? 12288 : 1536, has_spill_warning: 0 });
    for (const [table, reads] of [['Orders', v.orders], ['Customers', v.customers]]) {
      insert('operation_statement_objects', { statement_id: sid, object: table, kind: 'table', logical_reads: reads, lob_logical_reads: 0, scans: table === 'Orders' ? v.scans - 1 : 1 });
    }
  }
}
insert('operations', { id: 2, session_id: 1, operation: 'compare', host_pid: process.pid, started_at: stamp(17), updated_at: stamp(16.5), finished_at: stamp(16.5), status: 'failed', exit_code: 1,
  duration_ms: 30000, profile: 'demo', engine: 'sqlserver', server: 'demo.invalid', database: 'NorthwindDemo', sql_hash: hash(1), candidate_sql_hash: 'sha256:demo-discarded-attempt',
  sql_text: sql[1], candidate_sql_text: '-- Demo: discarded join experiment', compare_subject: 'annual-sales', compare_scope: 'whole', error_kind: 'timeout', error_message: 'Demo data: discarded join experiment exceeded the 30-second timeout.' });
insert('operation_annotations', { operation_id: 2, agent_label_initial: 'Join experiment timed out', agent_label: 'Join experiment timed out', agent_keep: 0, updated_at: stamp(16.5) });
db.exec('COMMIT');
const integrity = db.prepare('PRAGMA integrity_check').get().integrity_check;
if (integrity !== 'ok' || db.prepare('PRAGMA foreign_key_check').all().length) throw new Error('Demo journal integrity check failed.');
db.close();
fs.writeFileSync(path.join(home, 'config.json'), JSON.stringify({ journal: { enabled: true, storeSensitive: true, operationAnnotations: true, retention: { enabled: false } }, dashboard: { port, autoStart: false, agentTranscripts: { enabled: false } }, mutations: { defaultMode: 'never' } }, null, 2));
fs.writeFileSync(path.join(home, 'targets.json'), JSON.stringify({ demo: { server: 'demo.invalid', database: 'NorthwindDemo', auth: 'integrated', engine: 'sqlserver', vars: {}, environment: 'dev' } }, null, 2));
fs.writeFileSync(path.join(home, 'seed-evidence.json'), JSON.stringify({ source: 'synthetic showcase; no SQL executed', schemaVersion, sessions: 1, operations: 4, measuredVersions: 3, cases: 1, runs: 7 }, null, 2));
console.log(JSON.stringify({ home, port, integrity, sessions: 1, operations: 4 }));
