import { QueryClient, useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { ConflictError, getJson, NotFoundError, putJson, UnauthorizedError } from "./client"
import type { DashboardStats, DistilledPlan, OperationDetail, OperationSummary, Page, ProfilesResponse, SessionDetail, SessionSummary, Settings, SettingsResponse } from "./types"

export type SessionFilters = { agent?: string; transport?: string }
export type OperationFilters = { from?: string; to?: string; profile?: string; unprofiled?: boolean; dimensions?: Record<string, string | null>; status?: string; operation?: string }
export type StatsRange = "24h" | "7d" | "30d" | "all"
export type StatsWindow = { from?: string; to?: string }

export const queryKeys = {
  sessions: (filters: SessionFilters) => ["sessions", filters] as const,
  session: (id: number) => ["session", id] as const,
  operation: (id: number) => ["operation", id] as const,
  operations: (filters: OperationFilters) => ["operations", filters] as const,
  stats: (range: StatsRange, window: StatsWindow, profile?: string | null, dimensions?: Record<string, string | null>, rowDimension?: string, columnDimension?: string, unprofiled?: boolean) =>
    ["stats", range, window, profile, dimensions, rowDimension, columnDimension, unprofiled] as const,
  settings: () => ["settings"] as const,
  profiles: () => ["profiles"] as const,
  plan: (hash: string) => ["plan", hash] as const,
  liveOperations: ["live", "operations"] as const,
  liveRunning: ["live", "running"] as const,
  liveSessions: ["live", "sessions"] as const,
}

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 10_000,
        refetchOnWindowFocus: false,
        retry: (count, error) => !(error instanceof UnauthorizedError || error instanceof NotFoundError) && count < 2,
      },
    },
  })
}

export function useSessions(filters: SessionFilters) {
  return useInfiniteQuery({
    queryKey: queryKeys.sessions(filters),
    queryFn: ({ pageParam }) => getJson<Page<SessionSummary>>("/api/sessions", { ...filters, cursor: pageParam, limit: 50 }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: last => last.nextCursor ?? undefined,
  })
}

export function useSession(id: number) {
  return useQuery({ queryKey: queryKeys.session(id), queryFn: () => getJson<SessionDetail>(`/api/sessions/${id}`) })
}

export function useOperation(id: number) {
  return useQuery({ queryKey: queryKeys.operation(id), queryFn: () => getJson<OperationDetail>(`/api/operations/${id}`) })
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

export function useDistilledPlan(hash: string, enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.plan(hash),
    queryFn: () => getJson<DistilledPlan>(`/api/plans/${hash}`, { view: "distilled" }),
    enabled,
    staleTime: Infinity,
  })
}

export function useSettings() {
  return useQuery({ queryKey: queryKeys.settings(), queryFn: () => getJson<SettingsResponse>("/api/settings") })
}

export function useProfiles() {
  return useQuery({ queryKey: queryKeys.profiles(), queryFn: () => getJson<ProfilesResponse>("/api/profiles") })
}

export function useSaveSettings() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ settings, overwriteInvalid }: { settings: Settings; overwriteInvalid?: boolean }) =>
      putJson<SettingsResponse>("/api/settings", settings, overwriteInvalid ? { overwriteInvalid: "true" } : undefined),
    onSuccess: data => client.setQueryData(queryKeys.settings(), data),
    // 409: the file became invalid after the page loaded; refetch so the page shows its invalid-file state.
    onError: error => { if (error instanceof ConflictError) void client.invalidateQueries({ queryKey: queryKeys.settings() }) },
  })
}
