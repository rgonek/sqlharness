import { useQuery, useQueryClient } from "@tanstack/react-query"
import { getJson } from "@/api/client"
import { queryKeys } from "@/api/queries"
import type { OperationSummary, Page, SessionSummary } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { InternalOperationsToggle } from "@/components/InternalOperationsToggle"
import { ActivityFeed } from "@/components/ActivityFeed"
import { Badge } from "@/components/ui/badge"
import { Skeleton } from "@/components/ui/skeleton"
import { mergeOperations, mergeSessions, reconcileRunning, runningOnly } from "@/live/liveStore"
import { useLiveFeedState } from "@/live/useLiveFeed"
import { useNow } from "@/lib/useNow"
import { isInternalOperation, useShowInternalOperations } from "@/lib/internalOperations"

export function LivePage() {
  const [showInternal, setShowInternal] = useShowInternalOperations()
  const client = useQueryClient()
  const state = useLiveFeedState()
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
  const runningIds = new Set(visibleRunning.map(operation => operation.id))
  const feedOperations = [...(operations.data ?? []).filter(operation => operation.status !== "running" || runningIds.has(operation.id)), ...visibleRunning]
  const uniqueOperations = [...new Map(feedOperations.map(operation => [operation.id, operation])).values()]
  return (
    <div className="space-y-3">
      {operations.isPending || sessions.isPending || runningOperations.isPending ? <><h1>Activity</h1><Skeleton className="h-48 w-full" /></> : <ActivityFeed sessions={sessions.data ?? []} operations={uniqueOperations.filter(operation => showInternal || !isInternalOperation(operation))} now={now} />}
      <div className="flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
        <InternalOperationsToggle showInternal={showInternal} onChange={setShowInternal} />
        <Badge variant={state === "connected" ? "secondary" : "outline"}>{state}</Badge>
      </div>

    </div>
  )
}
