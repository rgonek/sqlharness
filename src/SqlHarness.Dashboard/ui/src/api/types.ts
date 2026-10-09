// Mirrors src/SqlHarness.Dashboard/DashboardModels.cs (JSON camelCase). Pinned by DashboardContractTests.
export type Page<T> = { items: T[]; nextCursor: number | null }

export type OperationStatus = "running" | "succeeded" | "failed" | "rejected" | "abandoned"

export type WatchProgress = { polls: number; changedPolls: number; elapsedMs: number }

export type SessionSummary = {
  id: number
  sessionKey: string
  agentKind: string
  transport: string
  source: string
  clientName: string | null
  clientVersion: string | null
  mcpMode: string | null
  cwd: string | null
  firstSeen: string
  lastSeen: string
  operations: number
  failed: number
  rejected: number
  running: number
  abandoned: number
}

export type OperationSummary = {
  id: number
  sessionId: number
  agentKind: string
  operation: string
  status: OperationStatus
  exitCode: number | null
  errorKind: string | null
  startedAt: string
  updatedAt: string
  finishedAt: string | null
  durationMs: number | null
  profile: string | null
  engine: string | null
  server: string | null
  database: string | null
  mutationRequested: boolean
  sqlHash: string | null
  rowsReturned: number | null
  logicalReadsMedian: number | null
  hasSpill: boolean
  coldCache: boolean
  overGranted: boolean
  progress: WatchProgress | null
  /** Full error text; stored only with journal.storeSensitive. */
  errorMessage: string | null
}

export type SessionDetail = { session: SessionSummary; operations: OperationSummary[] }

export type Spread = { min: number; median: number; max: number }

export type TableIoRow = {
  table: string
  logicalReads: number
  scanCount: number | null
  physicalReads: number | null
  pageServerReads: number | null
  readAheadReads: number | null
  lobLogicalReads: number | null
  lobPhysicalReads: number | null
  lobReadAheadReads: number | null
  coldRuns: number
}

export type PlanLinkRow = { repetition: number; ordinal: number; hash: string; stored: boolean }

export type PostgresBuffers = {
  sharedHit: number | null
  sharedRead: number | null
  sharedDirtied: number | null
  sharedWritten: number | null
  tempRead: number | null
  tempWritten: number | null
}

export type Wait = { waitType: string; averageWaitMs: number; averageWaitCount: number }

export type VariantDetail = {
  ordinal: number
  variant: string
  parameterSet: string | null
  matrixCell: number | null
  runs: number
  elapsedMs: Spread | null
  cpuMs: Spread | null
  logicalReads: Spread | null
  grantRequestedKb: number | null
  grantGrantedKb: number | null
  grantMaxUsedKb: number | null
  dop: number | null
  compileTimeMs: number | null
  compileCpuMs: number | null
  spillCount: number
  hasWarnings: boolean
  hasImplicitConversion: boolean
  missingIndexCount: number
  waits: Wait[] | null
  postgres: PostgresBuffers | null
  tableIo: TableIoRow[]
  plans: PlanLinkRow[]
}

export type OperationDetail = {
  operation: OperationSummary
  session: SessionSummary
  vars: Record<string, string> | null
  candidateSqlHash: string | null
  sqlText: string | null
  candidateSqlText: string | null
  rawTokens: number | null
  emittedTokens: number | null
  artifactDirectory: string | null
  summary: Record<string, unknown> | null
  variants: VariantDetail[]
}

export type KeyCount = { key: string; count: number }
export type DayAgentCount = { day: string; agentKind: string; count: number }
export type SqlHashStat = { sqlHash: string; count: number; totalDurationMs: number; maxDurationMs: number }
export type TableReadStat = { table: string; logicalReads: number; operations: number }
export type WaitStat = { waitType: string; totalWaitMs: number }
export type TargetStat = { profile: string | null; database: string | null; count: number; engine?: string | null; server?: string | null }
export type ProfileOperationCount = { profile: string | null; operations: number }
export type DimensionValueSummary = {
  name: string; value: string; isUnknown: boolean; source: string; operations: number | null; percentage: number | null
  totalDurationMs: number | null; durationAvailableOperations: number | null; durationUnavailableOperations: number | null
  failed: number | null; rejected: number | null
}
export type DimensionStat = { name: string; values: DimensionValueSummary[] }
export type DimensionAggregate = {
  operations: number; totalDurationMs: number | null; durationAvailableOperations: number
  durationUnavailableOperations: number; failed: number; rejected: number
}
export type DimensionMatrixCell = { row: DimensionValueSummary; column: DimensionValueSummary; metrics: DimensionAggregate }
export type DimensionMatrixStats = {
  rowDimension: string; columnDimension: string; cells: DimensionMatrixCell[]; totals: DimensionAggregate
}
export type ProfileDimensionStats = {
  profile: string | null; profileDefinitionAvailable: boolean; operations: number
  dimensions: DimensionStat[]; targets: TargetStat[]; matrix: DimensionMatrixStats | null
}
export type TokenStat = { raw: number; emitted: number }

export type DashboardStats = {
  operationsPerDay: DayAgentCount[]
  statuses: KeyCount[]
  exitCodes: KeyCount[]
  operations: KeyCount[]
  topSqlByCount: SqlHashStat[]
  topSqlByDuration: SqlHashStat[]
  topTablesByLogicalReads: TableReadStat[]
  topWaits: WaitStat[]
  targets: TargetStat[]
  tokens: TokenStat
  spillOperations: number
  coldCacheOperations: number
  profileOperations: ProfileOperationCount[]
  profileDimensions: ProfileDimensionStats
}

// GET /api/plans/{hash}?view=distilled (PlanDistiller / PostgresPlanDistiller, camelCase).
export type PlanNode = {
  physicalOp: string
  logicalOp?: string
  objectName?: string
  indexName?: string
  estimatedRows?: number
  actualRows?: number
  executions?: number
  costFraction?: number
  predicate?: string
  warnings?: string[]
  children?: PlanNode[]
}
export type MissingIndex = {
  table: string
  equalityColumns: string[]
  inequalityColumns: string[]
  includeColumns: string[]
  impact: number
}
// PlanStatementJsonConverter writes the text as "sql" and omits "missingIndexes" when empty.
export type DistilledStatement = { sql?: string; root: PlanNode; missingIndexes?: MissingIndex[] }
export type DistilledPlan = { statements: DistilledStatement[] }

// Mirrors src/SqlHarness.Dashboard/DashboardSettings.cs (JSON camelCase).
export type JournalSettings = {
  enabled: boolean
  storeSensitive: boolean
  retention: { enabled: boolean; maxAgeDays: number; maxSizeMb: number }
}
export type DashboardSettings = { autoStart: boolean; port: number; idleShutdownHours: number }
export type Settings = { journal: JournalSettings; dashboard: DashboardSettings }
export type SettingsFileStatus = "missing" | "valid" | "invalid"
export type SettingsResponse = { status: SettingsFileStatus; path: string; settings: Settings }
export type FieldError = { field: string; message: string }

export type ProfileVariable = { name: string; rule: string }
export type ProfileView = {
  name: string
  engine: string
  server: string
  database: string
  auth: string
  sqlUser: string | null
  passwordEnvVar: string | null
  sslMode: string | null
  trustServerCertificate: boolean
  tls: string
  rootCertificate: string | null
  vars: ProfileVariable[]
}
export type ProfilesResponse = { status: "missing" | "valid" | "invalid"; profiles: ProfileView[]; message: string | null }
