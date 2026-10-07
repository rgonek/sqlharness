import { QueryClient, useInfiniteQuery, useQuery } from "@tanstack/react-query"
import { getJson, NotFoundError, UnauthorizedError } from "./client"
import type { DashboardStats, DistilledPlan, OperationDetail, Page, SessionDetail, SessionSummary } from "./types"

export type SessionFilters = { agent?: string; transport?: string }
export type StatsRange = "24h" | "7d" | "30d" | "all"

export const queryKeys = {
  sessions: (filters: SessionFilters) => ["sessions", filters] as const,
  session: (id: number) => ["session", id] as const,
  operation: (id: number) => ["operation", id] as const,
  stats: (range: StatsRange) => ["stats", range] as const,
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

const rangeHours: Record<StatsRange, number | null> = { "24h": 24, "7d": 24 * 7, "30d": 24 * 30, all: null }

/** Start of the range, rounded down to the minute so the request is stable while the view is open. */
export function rangeStart(range: StatsRange, now: number): string | undefined {
  const hours = rangeHours[range]
  if (hours === null) return undefined
  const start = Math.floor((now - hours * 3_600_000) / 60_000) * 60_000
  return new Date(start).toISOString()
}

export function useStats(range: StatsRange) {
  return useQuery({
    queryKey: queryKeys.stats(range),
    queryFn: () => getJson<DashboardStats>("/api/stats", { from: rangeStart(range, Date.now()) }),
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
