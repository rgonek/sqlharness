import { useQuery, useQueryClient } from "@tanstack/react-query"
import { getJson } from "@/api/client"
import { queryKeys } from "@/api/queries"
import type { OperationSummary, Page, SessionSummary } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { OperationTable } from "@/components/OperationTable"
import { SessionTable } from "@/components/SessionTable"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { isActiveSession, mergeOperations, mergeSessions, reconcileRunning, runningOnly } from "@/live/liveStore"
import { useLiveFeed } from "@/live/useLiveFeed"
import { useNow } from "@/lib/useNow"

export function LivePage() {
  const client = useQueryClient()
  const { state } = useLiveFeed()
  const now = useNow(1000)
  // Fetched on load and on every live-feed (re)connect; in between, pushed events keep these
  // caches current through setQueryData. A fetch merges into the cache instead of replacing it.
  const operations = useQuery({
    queryKey: queryKeys.liveOperations,
    queryFn: async () => {
      const page = await getJson<Page<OperationSummary>>("/api/operations", { limit: 100 })
      return mergeOperations(client.getQueryData<OperationSummary[]>(queryKeys.liveOperations) ?? [], page.items)
    },
    staleTime: Infinity,
  })
  // The running list is its own query so a long-running operation never depends on the recent
  // page or its cap; each fetch is authoritative for what was running when it was sent.
  const runningOperations = useQuery({
    queryKey: queryKeys.liveRunning,
    queryFn: async () => {
      const before = client.getQueryData<OperationSummary[]>(queryKeys.liveRunning) ?? []
      const fetched: OperationSummary[] = []
      let cursor: number | null = null
      do {
        const page: Page<OperationSummary> = await getJson<Page<OperationSummary>>("/api/operations", { status: "running", limit: 200, cursor })
        fetched.push(...page.items)
        cursor = page.nextCursor
      } while (cursor !== null)
      return reconcileRunning(before, client.getQueryData<OperationSummary[]>(queryKeys.liveRunning) ?? [], fetched)
    },
    staleTime: Infinity,
  })
  const sessions = useQuery({
    queryKey: queryKeys.liveSessions,
    queryFn: async () => {
      const page = await getJson<Page<SessionSummary>>("/api/sessions", { limit: 100 })
      return mergeSessions(client.getQueryData<SessionSummary[]>(queryKeys.liveSessions) ?? [], page.items)
    },
    staleTime: Infinity,
  })

  const error = operations.error ?? runningOperations.error ?? sessions.error
  if (error) return <ErrorState error={error} />

  const running = runningOnly(runningOperations.data ?? [])
  const recent = (operations.data ?? []).slice(0, 50)
  const active = (sessions.data ?? []).filter(session => isActiveSession(session, now))

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2">
        <h1>Live</h1>
        <Badge variant={state === "connected" ? "secondary" : "outline"}>{state}</Badge>
      </div>
      <Card role="region" aria-label="Running">
        <CardHeader>
          <CardTitle>Running</CardTitle>
        </CardHeader>
        <CardContent>
          {runningOperations.isPending ? <Skeleton className="h-16 w-full" /> : running.length === 0
            ? <p className="text-sm text-muted-foreground">Nothing is running.</p>
            : <OperationTable operations={running} now={now} />}
        </CardContent>
      </Card>
      <Card role="region" aria-label="Active sessions">
        <CardHeader>
          <CardTitle>Active sessions</CardTitle>
        </CardHeader>
        <CardContent>
          {sessions.isPending ? <Skeleton className="h-16 w-full" /> : active.length === 0
            ? <p className="text-sm text-muted-foreground">No session was active in the last 15 minutes.</p>
            : <SessionTable sessions={active} now={now} />}
        </CardContent>
      </Card>
      <Card role="region" aria-label="Recent operations">
        <CardHeader>
          <CardTitle>Recent operations</CardTitle>
        </CardHeader>
        <CardContent>
          {operations.isPending ? <Skeleton className="h-32 w-full" /> : <OperationTable operations={recent} />}
        </CardContent>
      </Card>
    </div>
  )
}
