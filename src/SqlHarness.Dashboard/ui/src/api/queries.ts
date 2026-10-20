import { keepPreviousData, QueryClient, useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { ConflictError, getJson, HttpStatusError, NetworkError, NotFoundError, patchJson, postJson, putJson, UnauthorizedError } from "./client"
import type { AgentSessionAnnotation, AgentSessionDetail, AgentSessionKey, AgentSessionSummary, AnnotationPatch, CompareAttemptSummary, DashboardStats, KeyCount, MutationRequestSummary, OperationAnnotation, OperationAnnotationPatch, OperationDetail, OperationPlanManifest, OperationSummary, OptimizationComparison, OptimizationSection, Page, PlanGraphBuildResult, PlanGraphPropertyResult, PlanSourceMapResponse, PricesInfo, ProfilesResponse, ReproScript, ReproScriptForm, SessionAnnotation, SessionDetail, SessionSummary, Settings, SettingsResponse, SqlVersionText, TranscriptStatus } from "./types"

export type SessionFilters = { agent?: string; transport?: string; archived?: boolean; starred?: boolean; q?: string; hasCompare?: boolean }

const flag = (value: boolean | undefined) => (value === undefined ? undefined : String(value))
export type OperationFilters = { from?: string; to?: string; profile?: string; unprofiled?: boolean; dimensions?: Record<string, string | null>; status?: string; operation?: string }
export type StatsRange = "24h" | "7d" | "30d" | "all"
export type StatsWindow = { from?: string; to?: string }

export const queryKeys = {
  agents: ["agents"] as const,
  sessions: (filters: SessionFilters) => ["sessions", filters] as const,
  session: (id: number) => ["session", id] as const,
  compares: (id: number) => ["session-compares", id] as const,
  optimization: (id: number) => ["session-optimization", id] as const,
  optimizationComparison: (id: number, params: OptimizationCompareParams) => ["session-optimization-comparison", id, params] as const,
  operation: (id: number) => ["operation", id] as const,
  operationPlans: (id: number) => ["operation-plans", id] as const,
  planSourceMap: (id: number, selection: PlanSourceMapParams) => ["plan-source-map", id, selection] as const,
  planGraph: (sha: string) => ["plan-graph", sha] as const,
  planProperties: (sha: string, nodeId: string) => ["plan-properties", sha, nodeId] as const,
  operations: (filters: OperationFilters) => ["operations", filters] as const,
  stats: (range: StatsRange, window: StatsWindow, profile?: string | null, dimensions?: Record<string, string | null>, rowDimension?: string, columnDimension?: string, unprofiled?: boolean) =>
    ["stats", range, window, profile, dimensions, rowDimension, columnDimension, unprofiled] as const,
  settings: () => ["settings"] as const,
  profiles: () => ["profiles"] as const,
  prices: () => ["prices"] as const,
  mutations: ["mutations"] as const,
  liveOperations: ["live", "operations"] as const,
  liveRunning: ["live", "running"] as const,
  liveSessions: ["live", "sessions"] as const,
}

export const shouldRetry = (count: number, error: unknown) =>
  error instanceof NetworkError ? true
  : error instanceof HttpStatusError ? error.status >= 500 && count < 2
  : !(error instanceof UnauthorizedError || error instanceof NotFoundError) && count < 2

export const retryDelay = (attempt: number) => Math.min(1000 * 2 ** attempt, 30_000)

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 10_000,
        refetchOnWindowFocus: false,
        retry: shouldRetry,
        retryDelay,
      },
    },
  })
}

export function useAgentKinds() {
  return useQuery({ queryKey: queryKeys.agents, queryFn: () => getJson<KeyCount[]>("/api/agents") })
}

export function useSessions(filters: SessionFilters) {
  return useInfiniteQuery({
    queryKey: queryKeys.sessions(filters),
    queryFn: ({ pageParam }) => getJson<Page<SessionSummary>>("/api/sessions", {
      agent: filters.agent, transport: filters.transport,
      archived: flag(filters.archived), starred: flag(filters.starred), q: filters.q,
      hasCompare: flag(filters.hasCompare),
      cursor: pageParam, limit: 50,
    }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: last => last.nextCursor ?? undefined,
    // A search or view change keeps the current table until the new list arrives.
    placeholderData: keepPreviousData,
  })
}

export function useAgentSessions(filters: { archived?: boolean; starred?: boolean; agent?: string; q?: string; hasCompare?: boolean }, enabled = true) {
  return useInfiniteQuery({
    queryKey: ["agent-sessions", filters],
    queryFn: ({ pageParam }) => getJson<Page<AgentSessionSummary>>("/api/agent-sessions", {
      archived: flag(filters.archived), starred: flag(filters.starred), agent: filters.agent, q: filters.q,
      hasCompare: flag(filters.hasCompare), cursor: pageParam, limit: 50,
    }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: page => page.nextCursor ?? undefined,
    placeholderData: keepPreviousData,
    enabled,
  })
}

export function useUnmatchedAgentSessions(filters: SessionFilters, enabled = true) {
  return useInfiniteQuery({
    queryKey: ["agent-session-unmatched", filters],
    queryFn: ({ pageParam }) => getJson<Page<SessionSummary>>("/api/agent-sessions/unmatched", {
      agent: filters.agent, transport: filters.transport, archived: flag(filters.archived), starred: flag(filters.starred), q: filters.q,
      hasCompare: flag(filters.hasCompare), cursor: pageParam, limit: 50,
    }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: page => page.nextCursor ?? undefined,
    placeholderData: keepPreviousData,
    enabled,
  })
}

export function useAgentSession(key: AgentSessionKey, enabled = true) {
  return useQuery({ queryKey: ["agent-session", key], queryFn: () => getJson<AgentSessionDetail>(`/api/agent-sessions/${encodeURIComponent(key.agentKind)}/${encodeURIComponent(key.conversationId)}`), enabled })
}

export function useUpdateAgentSessionAnnotation(key: AgentSessionKey) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (patch: AnnotationPatch) => patchJson<AgentSessionAnnotation>(`/api/agent-sessions/${encodeURIComponent(key.agentKind)}/${encodeURIComponent(key.conversationId)}/annotation`, patch),
    onSettled: () => {
      void client.invalidateQueries({ queryKey: ["agent-sessions"] })
      void client.invalidateQueries({ queryKey: ["agent-session", key] })
    },
  })
}

export function useAgentSessionOptimization(key: AgentSessionKey, enabled = true) {
  return useQuery({ queryKey: ["agent-session-optimization", key], queryFn: () => getJson<OptimizationSection>(`/api/agent-sessions/${encodeURIComponent(key.agentKind)}/${encodeURIComponent(key.conversationId)}/optimization`), enabled })
}

export function useAgentSessionOptimizationComparison(key: AgentSessionKey, params: OptimizationCompareParams | null) {
  return useQuery({
    queryKey: ["agent-session-optimization-comparison", key, params],
    queryFn: () => getJson<OptimizationComparison>(`/api/agent-sessions/${encodeURIComponent(key.agentKind)}/${encodeURIComponent(key.conversationId)}/optimization/compare`, {
      subject: params!.subject, database: params!.database, group: params!.group, scope: params!.scope,
      a: params!.a, b: params!.b, case: params!.case, aggregate: params!.aggregate, view: params!.view ?? "changed",
    }),
    enabled: params !== null,
  })
}

export function useUpdateSessionAnnotation(id: number) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (patch: AnnotationPatch) => patchJson<SessionAnnotation>(`/api/sessions/${id}/annotation`, patch),
    onMutate: async patch => {
      await client.cancelQueries({ queryKey: queryKeys.session(id) })
      const previous = client.getQueryData<SessionDetail>(queryKeys.session(id))
      if (previous) {
        client.setQueryData<SessionDetail>(queryKeys.session(id), {
          ...previous,
          note: patch.note !== undefined ? patch.note : previous.note,
          session: {
            ...previous.session,
            ...(patch.title !== undefined ? { title: patch.title } : {}),
            ...(patch.starred !== undefined ? { starred: patch.starred } : {}),
            ...(patch.archived !== undefined ? { archived: patch.archived } : {}),
          },
        })
      }
      return { previous }
    },
    onError: (_error, _patch, context) => {
      if (context?.previous) client.setQueryData(queryKeys.session(id), context.previous)
    },
    onSettled: () => {
      void client.invalidateQueries({ queryKey: ["sessions"] })
      void client.invalidateQueries({ queryKey: queryKeys.session(id) })
      // The live feed does not resend a session after an annotation write.
      void client.invalidateQueries({ queryKey: queryKeys.liveSessions })
    },
  })
}

export function useSession(id: number) {
  return useQuery({ queryKey: queryKeys.session(id), queryFn: () => getJson<SessionDetail>(`/api/sessions/${id}`) })
}

export function useSessionCompares(id: number) {
  return useInfiniteQuery({
    queryKey: queryKeys.compares(id),
    queryFn: ({ pageParam }) => getJson<Page<CompareAttemptSummary>>(`/api/sessions/${id}/compares`, { cursor: pageParam, limit: 100 }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: page => page.nextCursor ?? undefined,
  })
}

export type OptimizationCompareParams = {
  subject: string; database: string; group: string; scope?: string; a: string; b: string; case: string; aggregate: string; view?: "changed" | "whole" | "statements"
}

export function useSessionOptimization(id: number, enabled = true) {
  return useQuery({
    queryKey: queryKeys.optimization(id),
    queryFn: () => getJson<OptimizationSection>(`/api/sessions/${id}/optimization`),
    enabled,
  })
}

export function useSessionOptimizationComparison(id: number, params: OptimizationCompareParams | null) {
  return useQuery({
    queryKey: params ? queryKeys.optimizationComparison(id, params) : ["session-optimization-comparison", id, null],
    queryFn: () => getJson<OptimizationComparison>(`/api/sessions/${id}/optimization/compare`, {
      subject: params!.subject, database: params!.database, group: params!.group, scope: params!.scope,
      a: params!.a, b: params!.b, case: params!.case, aggregate: params!.aggregate, view: params!.view ?? "changed",
    }),
    enabled: params !== null,
  })
}

export function useOperationOptimization(id: number, selectedCase = "all", aggregate = "sum", view: "changed" | "whole" | "statements" = "changed") {
  return useQuery({
    queryKey: ["operation-optimization", id, selectedCase, aggregate, view],
    queryFn: () => getJson<OptimizationComparison>(`/api/operations/${id}/optimization`, { case: selectedCase, aggregate, view }),
  })
}

export function useSessionSqlText(sessionId: number, hash: string | null, agentSessionKey?: AgentSessionKey) {
  return useQuery({
    queryKey: agentSessionKey ? ["agent-session-sql", agentSessionKey, hash] : ["session-sql", sessionId, hash],
    queryFn: () => agentSessionKey
      ? getJson<SqlVersionText>(`/api/agent-sessions/${encodeURIComponent(agentSessionKey.agentKind)}/${encodeURIComponent(agentSessionKey.conversationId)}/sql`, { hash })
      : getJson<SqlVersionText>(`/api/sessions/${sessionId}/sql`, { hash }),
    enabled: hash !== null,
    staleTime: Infinity,
  })
}

export function useOperation(id: number) {
  return useQuery({ queryKey: queryKeys.operation(id), queryFn: () => getJson<OperationDetail>(`/api/operations/${id}`) })
}

export function useNamedOperations(search?: string) {
  return useInfiniteQuery({
    queryKey: ["named-operations", search],
    queryFn: ({ pageParam }) => getJson<Page<OperationSummary>>("/api/named-operations", { q: search, cursor: pageParam, limit: 50 }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: page => page.nextCursor ?? undefined,
    placeholderData: keepPreviousData,
  })
}

export function useUpdateOperationAnnotation(id: number) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (patch: OperationAnnotationPatch) => patchJson<OperationAnnotation>(`/api/operations/${id}/annotation`, patch),
    onSettled: () => {
      void client.invalidateQueries({ queryKey: ["named-operations"] })
      void client.invalidateQueries({ queryKey: ["operations"] })
      void client.invalidateQueries({ queryKey: queryKeys.operation(id) })
    },
  })
}

export type PlanSourceMapParams = { variant: string; parameterSet?: string | null; repetition: number; caseId: string }

export function useOperationPlans(id: number) {
  return useQuery({ queryKey: queryKeys.operationPlans(id), queryFn: () => fetchOperationPlans(id) })
}

export function fetchOperationPlans(id: number): Promise<OperationPlanManifest> {
  return getJson<OperationPlanManifest>(`/api/operations/${id}/plans`)
}

export function usePlanSourceMap(id: number, selection: PlanSourceMapParams | null) {
  return useQuery({
    queryKey: queryKeys.planSourceMap(id, selection ?? { variant: "", repetition: 0, caseId: "" }),
    queryFn: () => fetchPlanSourceMap(id, selection!),
    enabled: selection !== null,
    staleTime: Infinity,
  })
}

export function fetchPlanSourceMap(id: number, selection: PlanSourceMapParams): Promise<PlanSourceMapResponse> {
  const query = new URLSearchParams()
  query.set("variant", selection.variant)
  if (selection.parameterSet !== null && selection.parameterSet !== undefined) query.set("parameterSet", selection.parameterSet)
  query.set("repetition", String(selection.repetition))
  query.set("caseId", selection.caseId)
  return getJson<PlanSourceMapResponse>(`/api/operations/${id}/source-map?${query.toString()}`)
}

export function usePlanGraph(sha: string, enabled = true) {
  return useQuery({ queryKey: queryKeys.planGraph(sha), queryFn: () => fetchPlanGraph(sha), enabled, staleTime: Infinity })
}

export function fetchPlanGraph(sha: string): Promise<PlanGraphBuildResult> {
  return getJson<PlanGraphBuildResult>(`/api/plan-documents/${sha}/graph`)
}

export function usePlanProperties(sha: string, nodeId: string, enabled = true) {
  return useQuery({ queryKey: queryKeys.planProperties(sha, nodeId), queryFn: () => fetchPlanProperties(sha, nodeId), enabled, staleTime: Infinity })
}

export function fetchPlanProperties(sha: string, nodeId: string): Promise<PlanGraphPropertyResult> {
  return getJson<PlanGraphPropertyResult>(`/api/plan-documents/${sha}/nodes/${encodeURIComponent(nodeId)}/properties`)
}

export function fetchReproScript(operationId: number, side: string, caseId: string, form: ReproScriptForm): Promise<ReproScript> {
  return getJson<ReproScript>(`/api/operations/${operationId}/repro`, { side, case: caseId, form })
}

export function useOperations(filters: OperationFilters) {
  return useInfiniteQuery({
    queryKey: queryKeys.operations(filters),
    queryFn: ({ pageParam }) => getJson<Page<OperationSummary>>("/api/operations", {
      from: filters.from, to: filters.to, profile: filters.profile,
      unprofiled: filters.unprofiled ? "true" : undefined, status: filters.status, operation: filters.operation,
      dimensions: filters.dimensions && Object.keys(filters.dimensions).length ? JSON.stringify(filters.dimensions) : undefined,
      cursor: pageParam,
      limit: 50,
    }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: page => page.nextCursor ?? undefined,
  })
}

const rangeHours: Record<StatsRange, number | null> = { "24h": 24, "7d": 24 * 7, "30d": 24 * 30, all: null }

/** Start of the range, rounded down to the minute so the request is stable while the view is open. */
export function rangeStart(range: StatsRange, now: number): string | undefined {
  const hours = rangeHours[range]
  if (hours === null) return undefined
  const start = Math.floor((now - hours * 3_600_000) / 60_000) * 60_000
  return new Date(start).toISOString()
}

export function rangeWindow(range: StatsRange, now: number): StatsWindow {
  const to = new Date(Math.floor(now / 60_000) * 60_000).toISOString()
  return { from: rangeStart(range, now), to }
}

export function useStats(
  range: StatsRange, window: StatsWindow, profile?: string | null, dimensions: Record<string, string | null> = {},
  rowDimension?: string, columnDimension?: string, unprofiled = false,
) {
  return useQuery({
    queryKey: queryKeys.stats(range, window, profile, dimensions, rowDimension, columnDimension, unprofiled),
    queryFn: () => getJson<DashboardStats>("/api/stats", {
      ...window,
      profile,
      unprofiled: unprofiled ? "true" : undefined,
      dimensions: Object.keys(dimensions).length ? JSON.stringify(dimensions) : undefined,
      rowDimension,
      columnDimension,
    }),
  })
}

export function useSettings() {
  return useQuery({ queryKey: queryKeys.settings(), queryFn: () => getJson<SettingsResponse>("/api/settings") })
}

export function useProfiles() {
  return useQuery({ queryKey: queryKeys.profiles(), queryFn: () => getJson<ProfilesResponse>("/api/profiles") })
}

export function useMutationHistory() {
  return useInfiniteQuery({
    queryKey: queryKeys.mutations,
    queryFn: ({ pageParam }) => getJson<Page<MutationRequestSummary>>("/api/mutations", { cursor: pageParam, limit: 50 }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: page => page.nextCursor ?? undefined,
  })
}

export function useSaveSettings() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ settings, overwriteInvalid }: { settings: Settings; overwriteInvalid?: boolean }) =>
      putJson<SettingsResponse>("/api/settings", settings, overwriteInvalid ? { overwriteInvalid: "true" } : undefined),
    onSuccess: data => {
      client.setQueryData(queryKeys.settings(), data)
      void client.invalidateQueries({ queryKey: ["sessions"] })
      void client.invalidateQueries({ queryKey: ["agent-sessions"] })
      if (!data.settings.dashboard.agentTranscripts.enabled) {
        client.removeQueries({ queryKey: ["agent-session"] })
        client.removeQueries({ queryKey: ["agent-session-optimization"] })
        client.removeQueries({ queryKey: ["agent-session-optimization-comparison"] })
        client.removeQueries({ queryKey: ["agent-session-sql"] })
      }
    },
    // 409: the file became invalid after the page loaded; refetch so the page shows its invalid-file state.
    onError: error => { if (error instanceof ConflictError) void client.invalidateQueries({ queryKey: queryKeys.settings() }) },
  })
}

export function usePrices() {
  return useQuery({ queryKey: queryKeys.prices(), queryFn: () => getJson<PricesInfo>("/api/prices") })
}

export function useRefreshPrices() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: () => postJson<PricesInfo>("/api/prices/refresh", {}),
    onSuccess: data => {
      client.setQueryData(queryKeys.prices(), data)
      void client.invalidateQueries({ queryKey: ["stats"] })
    },
  })
}

export function useTranscriptStatus() {
  return useQuery({ queryKey: ["transcripts"], queryFn: () => getJson<TranscriptStatus>("/api/transcripts") })
}

export function useClearTranscripts() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: () => postJson<TranscriptStatus>("/api/transcripts/clear", {}),
    onSuccess: data => {
      client.setQueryData(["transcripts"], data)
      void client.invalidateQueries({ queryKey: ["sessions"] })
      void client.invalidateQueries({ queryKey: ["session"] })
      void client.invalidateQueries({ queryKey: ["operations"] })
      void client.invalidateQueries({ queryKey: ["operation"] })
      // These long-lived live queries merge fetched rows into their cache. Reset the cached rows
      // first so equal timestamps cannot retain transcript-derived fields after a clear.
      void client.resetQueries({ queryKey: queryKeys.liveOperations })
      void client.resetQueries({ queryKey: queryKeys.liveRunning })
      void client.resetQueries({ queryKey: queryKeys.liveSessions })
    },
  })
}
