import type { DashboardStats, OperationDetail, OperationSummary, SessionSummary, VariantDetail } from "@/api/types"

export const session = (over: Partial<SessionSummary> = {}): SessionSummary => ({
  id: 1, sessionKey: "cli:abc", agentKind: "claude", transport: "cli", source: "process-tree",
  clientName: null, clientVersion: null, mcpMode: null, cwd: "/work",
  firstSeen: "2026-10-07T09:00:00.000Z", lastSeen: "2026-10-07T09:05:00.000Z",
  operations: 3, failed: 1, rejected: 0, running: 0, abandoned: 0, clientTitle: null, roots: null, protocolVersion: null, ...over,
})

export const operation = (over: Partial<OperationSummary> = {}): OperationSummary => ({
  id: 10, sessionId: 1, agentKind: "claude", operation: "query", status: "succeeded", exitCode: 0, errorKind: null,
  startedAt: "2026-10-07T09:00:00.000Z", updatedAt: "2026-10-07T09:00:01.000Z", finishedAt: "2026-10-07T09:00:01.000Z",
  durationMs: 1200, profile: "local", engine: "sqlserver", server: "srv", database: "db", mutationRequested: false,
  sqlHash: "sha256:0123456789abcdef", rowsReturned: 3, logicalReadsMedian: null, hasSpill: false, coldCache: false,
  overGranted: false, progress: null, errorMessage: null, cancelReason: null, agentModel: null, agentReasoningEffort: null,
  operationRef: null, parentOperationRef: null, dryRun: false, ...over,
})

export const variant = (over: Partial<VariantDetail> = {}): VariantDetail => ({
  ordinal: 0, variant: "measure", parameterSet: null, matrixCell: null, runs: 3,
  elapsedMs: { min: 10, median: 12, max: 14 }, cpuMs: { min: 8, median: 9, max: 10 },
  logicalReads: { min: 50, median: 50, max: 50 },
  grantRequestedKb: 8192, grantGrantedKb: 8192, grantMaxUsedKb: 512, dop: 4, compileTimeMs: 12, compileCpuMs: 10,
  spillCount: 1, hasWarnings: true, hasImplicitConversion: true, missingIndexCount: 1,
  waits: [{ waitType: "PAGEIOLATCH_SH", averageWaitMs: 40, averageWaitCount: 7 }], postgres: null,
  tableIo: [{ table: "Orders", logicalReads: 50, scanCount: 1, physicalReads: 3, pageServerReads: 0, readAheadReads: 0,
    lobLogicalReads: 0, lobPhysicalReads: 0, lobReadAheadReads: 0, coldRuns: 1 }],
  plans: [{ repetition: 1, ordinal: 0, hash: "A".repeat(64), stored: false }], ...over,
})

export const operationDetail = (over: Partial<OperationDetail> = {}): OperationDetail => ({
  operation: operation({ operation: "measure", logicalReadsMedian: 50, hasSpill: true, coldCache: true, overGranted: true }),
  session: session(), vars: { tenant: "acme" }, candidateSqlHash: null, sqlText: null, candidateSqlText: null,
  rawTokens: 100, emittedTokens: 10, rawBytes: 400, emittedBytes: 40, tokenEstimationMethod: "utf8-bytes-div-4",
  artifactDirectory: "/artifacts/m1", summary: { kind: "measure", resultsStable: true },
  variants: [variant()], dimensions: { values: [{ name: "tenant", value: "acme", isUnknown: false, source: "recorded",
    operations: null, percentage: null, totalDurationMs: null, durationAvailableOperations: null,
    durationUnavailableOperations: null, failed: null, rejected: null }] }, agent: null, ...over,
})

export const stats = (over: Partial<DashboardStats> = {}): DashboardStats => ({
  operationsPerDay: [
    { day: "2026-10-06", agentKind: "claude", count: 2 },
    { day: "2026-10-07", agentKind: "claude", count: 3 },
    { day: "2026-10-07", agentKind: "codex", count: 1 },
  ],
  statuses: [{ key: "succeeded", count: 5 }, { key: "rejected", count: 1 }],
  exitCodes: [{ key: "0", count: 5 }, { key: "2", count: 1 }],
  operations: [{ key: "query", count: 4 }, { key: "measure", count: 2 }],
  topSqlByCount: [{ sqlHash: "sha256:aaaaaaaaaaaaaaaa", count: 4, totalDurationMs: 400, maxDurationMs: 150 }],
  topSqlByDuration: [{ sqlHash: "sha256:bbbbbbbbbbbbbbbb", count: 1, totalDurationMs: 9000, maxDurationMs: 9000 }],
  topTablesByLogicalReads: [{ table: "Orders", logicalReads: 210, operations: 1 }],
  topWaits: [{ waitType: "PAGEIOLATCH_SH", totalWaitMs: 120 }],
  targets: [{ profile: "local", database: "db", count: 6, engine: "sqlserver", server: "srv" }],
  workflowCosts: [{ rootOperationRef: "root-1", outputBytes: 4000, estimatedOutputTokens: 1000, callCount: 3,
    tokenEstimationMethod: "utf8-bytes-div-4", accountingKind: "workflow-output",
    coverage: { input: "unmeasured", discovery: "unmeasured", chat: "unmeasured" },
    comparison: { baselineKind: "single-measured-execution-output", baselineBytes: 4000,
      baselineEstimatedTokens: 1000, outputOverheadBytes: 0, estimatedOutputTokenDelta: 0 },
    dryRunCalls: 0, databaseExecutionCalls: 1 }], spillOperations: 1, coldCacheOperations: 1,
  profileOperations: [{ profile: "local", operations: 6 }],
  profileDimensions: {
    profile: "local", profileDefinitionAvailable: true, operations: 6,
    dimensions: [{ name: "tenant", values: [{ name: "tenant", value: "acme", isUnknown: false, source: "recorded",
      operations: 6, percentage: 100, totalDurationMs: 7200, durationAvailableOperations: 6, durationUnavailableOperations: 0, failed: 0, rejected: 0 }] }],
    targets: [{ profile: "local", database: "db", count: 6, engine: "sqlserver", server: "srv" }], targetCount: 1, matrix: null,
  },
  ...over,
})
