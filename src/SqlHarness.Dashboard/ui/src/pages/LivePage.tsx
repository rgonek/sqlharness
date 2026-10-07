import { useQuery } from "@tanstack/react-query"
import { getJson } from "@/api/client"
import { queryKeys } from "@/api/queries"
import type { OperationSummary, Page, SessionSummary } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { OperationTable } from "@/components/OperationTable"
import { SessionTable } from "@/components/SessionTable"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { isActiveSession } from "@/live/liveStore"
import { useLiveFeed } from "@/live/useLiveFeed"
import { useNow } from "@/lib/useNow"

export function LivePage() {
  const { connected } = useLiveFeed()
  const now = useNow(1000)
  // Seeded once; the live feed keeps these caches current through setQueryData.
  const operations = useQuery({
    queryKey: queryKeys.liveOperations,
    queryFn: async () => (await getJson<Page<OperationSummary>>("/api/operations", { limit: 100 })).items,
    staleTime: Infinity,
  })
  const sessions = useQuery({
    queryKey: queryKeys.liveSessions,
    queryFn: async () => (await getJson<Page<SessionSummary>>("/api/sessions", { limit: 100 })).items,
    staleTime: Infinity,
  })

  const error = operations.error ?? sessions.error
  if (error) return <ErrorState error={error} />

  const running = (operations.data ?? []).filter(operation => operation.status === "running")
  const recent = (operations.data ?? []).slice(0, 50)
  const active = (sessions.data ?? []).filter(session => isActiveSession(session, now))

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2">
        <h1>Live</h1>
        <Badge variant={connected ? "secondary" : "outline"}>{connected ? "connected" : "disconnected"}</Badge>
      </div>
      <Card role="region" aria-label="Running">
        <CardHeader>
          <CardTitle>Running</CardTitle>
        </CardHeader>
        <CardContent>
          {operations.isPending ? <Skeleton className="h-16 w-full" /> : running.length === 0
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
