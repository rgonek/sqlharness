import { useParams } from "@tanstack/react-router"
import { NotFoundError } from "@/api/client"
import { useSession } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { OperationTable } from "@/components/OperationTable"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { formatTimestamp } from "@/lib/format"
import { parseRouteId } from "@/lib/routeId"

function Fact({ label, value }: { label: string; value: string | null | undefined }) {
  return (
    <div className="min-w-0">
      <div className="text-sm text-muted-foreground">{label}</div>
      <div className="truncate">{value || "—"}</div>
    </div>
  )
}

export function SessionPage() {
  const raw = useParams({ from: "/sessions/$id" }).id
  const id = parseRouteId(raw)
  if (id === null) return <ErrorState error={new NotFoundError(`/sessions/${raw}`)} />
  return <SessionView id={id} />
}

function SessionView({ id }: { id: number }) {
  const detail = useSession(id)

  if (detail.error) return <ErrorState error={detail.error} />
  if (detail.isPending) return <Skeleton className="h-64 w-full" />

  const { session, operations } = detail.data
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle>
            <h1>Session {session.id}</h1>
          </CardTitle>
          <div className="font-mono">{session.sessionKey}</div>
          <CardDescription>
            {session.agentKind} via {session.transport} ({session.source})
          </CardDescription>
        </CardHeader>
        <CardContent className="grid grid-cols-2 gap-4 md:grid-cols-4">
          <Fact label="Client" value={session.clientName ? `${session.clientName} ${session.clientVersion ?? ""}`.trim() : null} />
          <Fact label="MCP mode" value={session.mcpMode} />
          <Fact label="Working directory" value={session.cwd} />
          <Fact label="Operations" value={String(session.operations)} />
          <Fact label="First seen" value={formatTimestamp(session.firstSeen)} />
          <Fact label="Last seen" value={formatTimestamp(session.lastSeen)} />
          <Fact label="Failed / rejected" value={`${session.failed} / ${session.rejected}`} />
          <Fact label="Running / abandoned" value={`${session.running} / ${session.abandoned}`} />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Operations</CardTitle>
          <CardDescription>Newest first, up to 500.</CardDescription>
        </CardHeader>
        <CardContent>
          <OperationTable operations={operations} />
        </CardContent>
      </Card>
    </div>
  )
}
