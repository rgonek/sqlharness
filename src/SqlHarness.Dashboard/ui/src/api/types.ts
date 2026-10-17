// Mirrors src/SqlHarness.Dashboard/DashboardModels.cs (JSON camelCase). Pinned by DashboardContractTests.
export type Page<T> = { items: T[]; nextCursor: number | null }

export type OperationStatus = "running" | "succeeded" | "failed" | "rejected" | "abandoned"

export type WatchProgress = { polls: number; changedPolls: number; elapsedMs: number }

export type SessionRoot = { uri: string; name: string | null }

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
  clientTitle: string | null
  /** null = unknown (client lacks roots or has not answered); [] = client reported none. */
  roots: SessionRoot[] | null
  /** Negotiated MCP revision; null for CLI sessions and unrecorded rows. */
  protocolVersion: string | null
  title: string | null
  starred: boolean
  archived: boolean
  notePreview: string | null
  agentTitle: string | null
  hasCompare: boolean
  optimization: SessionOptimizationBadge[]
}

export type SessionOptimizationBadge = {
  subject: string
  change: string
  warning: boolean
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
  /** "client" | "shutdown" | "deadline" when errorKind is "cancelled"; otherwise null. */
  cancelReason: string | null
  /** Client-supplied model label from tools/call _meta (Codex); never verified. */
  agentModel: string | null
  agentReasoningEffort: string | null
  operationRef: string | null
  parentOperationRef: string | null
  dryRun: boolean
  agentModelSource: string | null
  subject: string | null
  versionPair: string | null
  readsBefore: number | null
  readsAfter: number | null
}

/** Allowlisted, client-supplied labels from tools/call _meta; never verified, never content. */
export type AgentCall = {
  callId: string | null; model: string | null; reasoningEffort: string | null; sessionId: string | null
  turnId: string | null; turnTrigger: string | null; threadSource: string | null
  transcriptModel: string | null; transcriptReasoningEffort: string | null; conversationTitle: string | null
}

export type AgentConversation = { agentKind: string; conversationId: string; title: string | null; operations: number }

export type CompareAttemptSummary = {
  operationId: number
  subject: string | null
  scope: string | null
  baselineHash: string | null
  candidateHash: string | null
  baselineVersion: string | null
  candidateVersion: string | null
  status: OperationStatus
  startedAt: string
  regression: Record<string, unknown> | null
  cases: Record<string, unknown>[] | null
  conditions: ConditionsModel | null
  summary: Record<string, unknown> | null
}

export type CompareSubjectSummary = {
  subject: string
  scopes: string[]
  latestWhole: CompareAttemptSummary | null
  latestCompletedWhole: CompareAttemptSummary | null
  startToLatestWhole: CompareAttemptSummary | null
  scopeVersions: CompareScopeVersions[]
  versionsFromRetainedHistory: boolean
}

export type CompareScopeVersions = {
  scope: string
  versions: CompareSqlVersion[]
}

export type OptimizationSection = { subjects: OptimizationSubject[]; unassigned: UnassignedCompare[] }
export type OptimizationSubject = { subject: string; database: string; engine: string; groups: ConditionsGroup[] }
export type ConditionsGroup = {
  fingerprint: string; label: string; scopes: ScopeVersions[]; defaultA: string; defaultB: string | null
  subject: string | null; database: string | null
}
export type ScopeVersions = { scope: string; versions: OptimizationVersion[]; defaultA: string | null; defaultB: string | null }
export type OptimizationVersion = {
  version: string; hash: string; logicalReadsSum: number | null; logicalReadsMean: number | null; scansSum: number | null
  elapsedMedianMs: number | null; caseOutcome: string; measuredIn: number[]; latestOperationId: number
}
export type UnassignedCompare = { operationId: number; startedAt: string; status: string }
export type OptimizationActionReference = { operationId: number; side: "baseline" | "candidate" | "measure"; caseIds: string[] }
export type OptimizationComparison = {
  a: string; b: string; basis: "MeasuredTogether" | "DifferentRunsSameConditions" | "ConditionsDiffer" | "SameSql"
  operationId: number | null; equivalenceLabel: string; cases: { id: string; label: string; outcome: string }[]
  selectedCase: string; aggregate: string; coverageLabel: string
  mixed: { better: number; worse: number; worstCaseLabel: string; worstChangePercent: number } | null
  tables: { table: string; scansBefore: number | null; scansAfter: number | null; readsBefore: number | null; readsAfter: number | null; change: string }[]
  collapsed: { count: number; label: string } | null
  elapsed: { beforeMs: number | null; afterMs: number | null; policyVerdict: string | null; differentRun: boolean }
  tableIoUnavailable?: boolean; cpuBeforeMs?: number | null; cpuAfterMs?: number | null
  buffersBefore?: PostgresBuffers | null; buffersAfter?: PostgresBuffers | null
  beforeAction?: OptimizationActionReference | null; afterAction?: OptimizationActionReference | null
}

export type CompareSqlVersion = {
  version: string
  hash: string
}

export type SessionDetail = {
  session: SessionSummary; operations: OperationSummary[]; compareSubjects: CompareSubjectSummary[]; unassignedCompareCount: number; note: string | null
  conversations: AgentConversation[]
}

export type SqlVersionText = { hash: string; text: string | null; tooLarge: boolean }
export type ConditionsParameter = { name: string; effectiveType: string; value: unknown | null }
export type ConditionsCase = { id: string; index: number; label: string; setName: string | null; varying: ConditionsParameter[]; fingerprint: string }
export type ConditionsModel = {
  version: number; engine: string; server: string | null; database: string | null
  execution: { operation: string; sessionPolicy: string; setupBinding: string; warmup: number; repeat: number; timeoutSeconds: number; comparisonMode: string | null; compareNullability: boolean | null }
  setup: { hash: string; text: string | null } | null
  fixed: ConditionsParameter[]; cases: ConditionsCase[]; fingerprint: string; omitted: string[]
}
export type ReproScriptForm = "as-executed" | "local-variables"
export type ReproScript = { text: string | null; completeness: "Complete" | "ValuesMissing" | "Unavailable"; label: string }

export type SessionAnnotation = { title: string | null; note: string | null; starred: boolean; archived: boolean }

export type AnnotationPatch = Partial<{ title: string | null; note: string | null; starred: boolean; archived: boolean }>

export type Spread = { min: number; median: number; max: number }

export type TableIoRow = {
  table: string
  logicalReads: number
  scanCount: number | null
  scans: number | null
  physicalReads: number | null
  pageServerReads: number | null
  readAheadReads: number | null
  lobLogicalReads: number | null
  lobPhysicalReads: number | null
  lobReadAheadReads: number | null
  coldRuns: number
  totalLogicalReads: number
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
  totalLogicalReadsMedian: number | null
  totalLogicalReadsLabel: string
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
  caseId: string
  caseLabel: string
}

export type OperationDetail = {
  operation: OperationSummary
  session: SessionSummary
  vars: Record<string, string> | null
  candidateSqlHash: string | null
  sqlText: string | null
  candidateSqlText: string | null
  setupSqlText: string | null
  rawTokens: number | null
  emittedTokens: number | null
  rawBytes: number | null
  emittedBytes: number | null
  tokenEstimationMethod: string
  artifactDirectory: string | null
  summary: Record<string, unknown> | null
  variants: VariantDetail[]
  dimensions: { values: DimensionValueSummary[] }
  agent: AgentCall | null
  compareSubject: string | null
  compareScope: string | null
  baselineVersion: string | null
  candidateVersion: string | null
  compareRegression: Record<string, unknown> | null
  compareCases: Record<string, unknown>[] | null
  compareConditions: Record<string, unknown> | null
  phases: OperationPhaseSummary[]
  events?: OperationEventSummary[]
  conditions: ConditionsModel | null
}

export type OperationPhaseSummary = { phase: string; count: number; totalMs: number; maxMs: number }
export type OperationEventSummary = {
  offsetMs: number; phase: string; durationMs: number; outcome: string; errorNumber: number | null
  clientConnectionId: string | null; droppedCount: number | null
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
  dimensions: DimensionStat[]; targets: TargetStat[]; targetCount: number; matrix: DimensionMatrixStats | null
}
export type WorkflowCost = {
  rootOperationRef: string | null
  outputBytes: number
  estimatedOutputTokens: number
  callCount: number
  tokenEstimationMethod: string
  accountingKind: "workflow-output" | "legacy-artifact-volume"
  coverage: { input: string; discovery: string; chat: string }
  comparison: WorkflowCostComparison | null
  dryRunCalls: number
  databaseExecutionCalls: number
}

export type WorkflowCostComparison = {
  baselineKind: "single-measured-execution-output"
  baselineBytes: number
  baselineEstimatedTokens: number
  outputOverheadBytes: number
  estimatedOutputTokenDelta: number | null
}

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
  spillOperations: number
  coldCacheOperations: number
  profileOperations: ProfileOperationCount[]
  profileDimensions: ProfileDimensionStats
  workflowCosts: WorkflowCost[]
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
export type DashboardSettings = { autoStart: boolean; port: number; idleShutdownHours: number; agentTranscripts: { enabled: boolean } }
export type DiagnosticsSettings = { enabled: boolean }
export type MutationSettings = { defaultMode: "never" | "operator" | "yolo"; verification: string; approvalTimeoutSeconds: number }
export type Settings = { journal: JournalSettings; dashboard: DashboardSettings; compare: { requireContext: boolean }; diagnostics: DiagnosticsSettings; mutations: MutationSettings }
export type SettingsFileStatus = "missing" | "valid" | "invalid"
export type SettingsResponse = { status: SettingsFileStatus; path: string; settings: Settings }
export type FieldError = { field: string; message: string }
export type TranscriptStatus = { enabled: boolean; matched: number; unmatched: number; readErrors: number; lastCycleAt: string | null }

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
  environment: string | null
  mutationMode: string
  mutationWriterKind: string | null
  acknowledgeProductionYolo: boolean
}
export type ProfilesResponse = { status: "missing" | "valid" | "invalid"; profiles: ProfileView[]; message: string | null }

export type MutationRequestSummary = {
  id: number; requestId: string; sessionKey: string; profile: string; engine: string; server: string; database: string
  tier: string; mode: string; digest: string; sql: string | null; parametersJson: string | null; state: string; status: string | null
  verification: string | null; createdAt: string; decidedAt: string | null; finishedAt: string | null; environment: string | null
}

export type PricesInfo = { source: string; fetchedAt: string; origin: "embedded" | "refreshed" }
