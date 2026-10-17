import { useQuery, useQueryClient } from "@tanstack/react-query"
import { getJson } from "@/api/client"
import { queryKeys } from "@/api/queries"
import type { OperationSummary, Page, SessionSummary } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { OperationTable } from "@/components/OperationTable"
import { InternalOperationsToggle } from "@/components/InternalOperationsToggle"
import { SessionTable } from "@/components/SessionTable"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { isActiveSession, mergeOperations, mergeSessions, reconcileRunning, runningOnly } from "@/live/liveStore"
import { useLiveFeed } from "@/live/useLiveFeed"
import { useNow } from "@/lib/useNow"
import { isInternalOperation, useShowInternalOperations } from "@/lib/internalOperations"

export function LivePage() {
  const [showInternal, setShowInternal] = useShowInternalOperations()
  const client = useQueryClient()
  const state = useLiveFeed()
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
      // Starred and unstarred separately: each is an id-ordered list the server can stop early on.
      const [starred, unstarred] = await Promise.all(["true", "false"].map(value =>
        getJson<Page<SessionSummary>>("/api/sessions", { archived: "any", starred: value, limit: 100 })))
      return mergeSessions(client.getQueryData<SessionSummary[]>(queryKeys.liveSessions) ?? [], [...starred.items, ...unstarred.items])
    },
    staleTime: Infinity,
  })

  const error = operations.error ?? runningOperations.error ?? sessions.error
  if (error) return <ErrorState error={error} />

  const running = runningOnly(runningOperations.data ?? [])
  const visibleRunning = showInternal ? running : running.filter(operation => !isInternalOperation(operation))
  const recent = (operations.data ?? []).slice(0, 50)
  const active = (sessions.data ?? []).filter(session => isActiveSession(session, now))
  const noRunning = !runningOperations.isPending && visibleRunning.length === 0
  const noActive = !sessions.isPending && active.length === 0

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
            ? noActive ? <p className="sr-only">Nothing running</p> : <p className="text-sm text-muted-foreground">Nothing is running.</p>
            : <><InternalOperationsToggle showInternal={showInternal} onChange={setShowInternal} />{visibleRunning.length > 0
              ? <OperationTable operations={running} now={now} showInternal={showInternal} />
              : !noActive && <p className="text-sm text-muted-foreground">No visible operations.</p>}</>}
        </CardContent>
      </Card>
      <Card role="region" aria-label="Active sessions">
        <CardHeader>
          <CardTitle>Active sessions</CardTitle>
        </CardHeader>
        <CardContent>
          {sessions.isPending ? <Skeleton className="h-16 w-full" /> : active.length === 0
            ? noRunning ? <p className="sr-only">No active sessions</p> : <p className="text-sm text-muted-foreground">No session was active in the last 15 minutes.</p>
            : <SessionTable pinned={[]} sessions={active} now={now} />}
          {noRunning && noActive && <p className="text-sm text-muted-foreground">Nothing running · no active sessions</p>}
        </CardContent>
      </Card>
      <Card role="region" aria-label="Recent operations">
        <CardHeader>
          <CardTitle>Recent operations</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="space-y-3">
            <InternalOperationsToggle showInternal={showInternal} onChange={setShowInternal} />
            {operations.isPending ? <Skeleton className="h-32 w-full" /> : <OperationTable operations={recent} showInternal={showInternal} />}
          </div>
        </CardContent>
      </Card>
    </div>
  )
}
