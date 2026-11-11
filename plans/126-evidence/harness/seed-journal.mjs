// Synthetic local journal only. Run from repo root: node <script> <new isolated home>.
import fs from 'node:fs';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { createHash, randomBytes } from 'node:crypto';
const root = process.env.SQLHARNESS_VISUAL_ROOT ?? process.cwd();
if (!process.argv[2]) throw new Error('An explicit NEW isolated home is required.');
const home = path.resolve(process.argv[2]), database = path.join(home, 'data/activity.db');
if (fs.existsSync(home)) throw new Error('Refusing to overwrite an existing home.');
fs.mkdirSync(path.dirname(database), { recursive: true });
const schemaText = fs.readFileSync(path.join(root, 'src/SqlHarness.Core/Journal/JournalSchema.cs'), 'utf8');
const schema = schemaText.match(/internal const string Current = """([\s\S]*?)""";/)[1];
const version = Number(schemaText.match(/CurrentVersion = (\d+)/)[1]);
const db = new DatabaseSync(database);
db.exec(schema);
db.exec('PRAGMA user_version = ' + version);
db.prepare("INSERT INTO journal_meta(key,value) VALUES('fingerprint_salt',?)").run(randomBytes(32));
const now = Date.now();
const stamp = id => new Date(now - (10000 - id) * 1000).toISOString();
const insert = (table, values) => {
  const keys = Object.keys(values);
  db.prepare('INSERT INTO ' + table + '(' + keys.join(',') + ') VALUES(' + keys.map(() => '?').join(',') + ')').run(...Object.values(values));
};
const sql = version => 'SELECT @CustomerId AS CustomerId; -- version ' + version;
const hash = version => createHash('sha256').update(sql(version)).digest('hex');
const cases = Array.from({ length: 20 }, (_, i) => ({
  id: String(i), index: i, label: '@CustomerId = ' + (i + 1), setName: 'customer-' + (i + 1),
  varying: [{ name: 'CustomerId', effectiveType: 'int', value: i + 1 }],
  fingerprint: 'synthetic-case-' + i, measurementFingerprint: 'synthetic-case-' + i,
  varyingFingerprint: 'synthetic-varying-' + i,
}));
const conditions = { version: 1, engine: 'sqlserver', server: 'synthetic.invalid', database: 'ShopDb',
  execution: { operation: 'compare', sessionPolicy: 'new-session-per-case', setupBinding: 'bound-per-case', warmup: 0, repeat: 3, timeoutSeconds: 30, comparisonMode: 'Ordered', compareNullability: true },
  setup: null, fixed: [], cases, fingerprint: 'synthetic-conditions', measurementFingerprint: 'synthetic-conditions', omitted: [] };
db.exec('BEGIN');
for (let id = 1; id <= 500; id++) {
  insert('sessions', { id, session_key: 'synthetic:' + id, agent_kind: id % 2 ? 'claude' : 'codex', transport: 'cli',
    source: 'synthetic-quality-fixture', host_pid: process.pid, cwd: 'D:/Synthetic/Shop', first_seen: stamp(1), last_seen: stamp(10000),
    dashboard_evidence_version: 1 });
  insert('session_annotations', { session_id: id, title: id === 1 ? 'Orders: optimization across 50 versions' : 'Investigation ' + id,
    note: id === 1 ? 'Synthetic evidence only; no SQL was executed.' : null, starred: id === 1 ? 1 : 0, updated_at: stamp(10000) });
}
let metricId = 0, statementId = 0;
for (let id = 1; id <= 10000; id++) {
  const compare = id >= 9952, failed = id === 9951, v = compare ? id - 9951 : 0;
  const sessionId = id <= 950 || id >= 9951 ? 1 : 2 + (id - 951) % 499;
  const summaryCases = cases.map((c, i) => ({ caseId: c.id, caseLabel: c.label, equivalence: { mode: 'Ordered', equivalent: true },
    regression: { verdict: i === 0 ? 'fail' : 'pass', reasons: i === 0 ? ['elapsed-regressed'] : [] } }));
  insert('operations', { id, session_id: sessionId, operation: compare || failed ? 'compare' : 'query', host_pid: process.pid,
    started_at: stamp(id), updated_at: stamp(id), finished_at: stamp(id), status: failed ? 'failed' : 'succeeded',
    exit_code: failed ? 1 : 0, error_kind: failed ? 'timeout' : null,
    error_message: failed ? 'Synthetic execution timed out after 30 seconds.' : null,
    duration_ms: compare ? 240 : 12, profile: 'dev', vars_json: '{"CustomerId":1}', engine: 'sqlserver',
    server: 'synthetic.invalid', database: 'ShopDb', result_sets: 1, rows_returned: 1,
    sql_hash: hash(compare ? v - 1 : 0), candidate_sql_hash: compare || failed ? hash(compare ? v : 50) : null,
    sql_text: sql(compare ? v - 1 : 0), candidate_sql_text: compare || failed ? sql(compare ? v : 50) : null,
    compare_subject: compare || failed ? 'orders' : null, compare_scope: compare || failed ? 'whole' : null,
    conditions_json: compare ? JSON.stringify(conditions) : null,
    conditions_fingerprint: compare ? 'synthetic-conditions' : null, measurement_fingerprint: compare ? 'synthetic-conditions' : null,
    summary_json: compare ? JSON.stringify({ kind: 'compare', comparison: 'Ordered', resultsEquivalent: true, regression: { verdict: 'fail' }, cases: summaryCases }) : null });
  if (!compare) continue;
  insert('operation_annotations', { operation_id: id, agent_label_initial: 'Version ' + v, agent_label: 'Version ' + v,
    agent_keep: v === 49 ? 1 : 0, updated_at: stamp(id) });
  for (const [caseIndex, c] of cases.entries()) for (const side of ['baseline', 'candidate']) {
    const before = Math.round((4000 + caseIndex * 20) * 0.95 ** (v - 1));
    const reads = side === 'baseline' || caseIndex === 3 ? before : Math.round(before * 0.95);
    const elapsed = side === 'candidate' && caseIndex === 0 ? 30 : 10;
    const mid = ++metricId;
    insert('operation_metrics', { id: mid, operation_id: id, ordinal: caseIndex * 2 + Number(side === 'candidate'),
      variant: side, case_id: c.id, case_label: c.label, parameter_set: c.setName, runs: 3,
      elapsed_ms_min: elapsed, elapsed_ms_median: elapsed, elapsed_ms_max: elapsed,
      cpu_ms_min: 5, cpu_ms_median: 5, cpu_ms_max: 5,
      logical_reads_min: reads, logical_reads_median: reads, logical_reads_max: reads,
      spill_count: 0, has_warnings: 0, has_implicit_conversion: 0, missing_index_count: 0,
      result_sets: 1, rows_returned: 1, omitted_statements: caseIndex === 19 ? 1 : 0 });
    insert('operation_table_io', { metric_id: mid, table_name: 'Orders', logical_reads: reads,
      scan_count: 1, physical_reads: 0, read_ahead_reads: caseIndex === 19 ? null : 0,
      lob_logical_reads: 0, lob_physical_reads: 0, lob_read_ahead_reads: 0, cold_runs: 0 });
    // One trustworthy statement plus an omitted statement in the final case.
    const sid = ++statementId;
    insert('operation_statements', { id: sid, operation_id: id, ordinal: caseIndex * 2 + Number(side === 'candidate'),
      variant: side, case_id: c.id, case_label: c.label, parameter_set: c.setName,
      statement_ordinal: 0, statement_hash: hash(0), statement_key: 'orders-select', occurrence: 1,
      statement_text: 'SELECT @CustomerId AS CustomerId;', executions: 1, status: 'exact',
      cpu_ms: 5, elapsed_ms: elapsed, operators_json: '[]', hidden_work_json: '[]' });
    insert('operation_statement_objects', { statement_id: sid, object: 'Orders', kind: 'table',
      logical_reads: reads, lob_logical_reads: 0, scans: 1 });
  }
}
db.exec('COMMIT');
const counts = { sessions: db.prepare('SELECT COUNT(*) n FROM sessions').get().n, operations: db.prepare('SELECT COUNT(*) n FROM operations').get().n,
  primarySessionOperations: db.prepare('SELECT COUNT(*) n FROM operations WHERE session_id=1').get().n };
db.close();
fs.writeFileSync(path.join(home, 'config.json'), JSON.stringify({ journal: { enabled: true, storeSensitive: true, operationAnnotations: true, retention: { enabled: false } },
  dashboard: { port: 48130, autoStart: false, agentTranscripts: { enabled: false }, safetyNotifications: true },
  mutations: { defaultMode: 'never' } }, null, 2));
fs.writeFileSync(path.join(home, 'targets.json'), JSON.stringify({ dev: { server: 'synthetic.invalid', database: 'ShopDb', auth: 'integrated', engine: 'sqlserver', vars: {}, environment: 'dev' } }, null, 2));
fs.writeFileSync(path.join(home, 'seed-evidence.json'), JSON.stringify({ ...counts, versions: 50, cases: 20, seededAt: new Date(now).toISOString(), schemaVersion: version }, null, 2));
console.log(JSON.stringify({ home, ...counts, versions: 50, cases: 20, schemaVersion: version }));
