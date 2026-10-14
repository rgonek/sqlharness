import { useParams, useSearch } from "@tanstack/react-router"
import { NotFoundError } from "@/api/client"
import { useSession } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { OperationTable } from "@/components/OperationTable"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { SessionAnnotationHeader } from "@/components/SessionAnnotationHeader"
import { SessionNoteCard } from "@/components/SessionNoteCard"
import { CompareSessionOverview } from "@/components/CompareSessionOverview"
import { formatTimestamp } from "@/lib/format"
import { clientLabel, rootLabel } from "@/lib/roots"
import { agentLabel } from "@/lib/agents"
import { parseRouteId } from "@/lib/routeId"

const unknownRootsHint =
  "Unknown: the client did not declare roots, has not answered yet, or uses MCP 2026-07-28, where SQLHarness does not request roots."

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
  const search = useSearch({ from: "/sessions/$id" })
  const id = parseRouteId(raw)
  if (id === null) return <ErrorState error={new NotFoundError(`/sessions/${raw}`)} />
  return <SessionView id={id} returnSubject={search.subject} returnScope={search.scope} returnUnassigned={search.unassigned} />
}

function SessionView({ id, returnSubject, returnScope, returnUnassigned }: { id: number; returnSubject?: string; returnScope?: string; returnUnassigned?: boolean }) {
  const detail = useSession(id)

  if (detail.error) return <ErrorState error={detail.error} />
  if (detail.isPending) return <Skeleton className="h-64 w-full" />

  const { session, operations, note = null, compareSubjects = [], unassignedCompareCount = 0 } = detail.data
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle>
            <SessionAnnotationHeader key={session.id} session={session} />
          </CardTitle>
          <div className="font-mono">{session.sessionKey}</div>
          <CardDescription>{agentLabel(session.agentKind)} via {session.transport} ({session.source})</CardDescription>
        </CardHeader>
        <CardContent>
          <details>
            <summary className="cursor-pointer text-sm font-medium">Session metadata</summary>
            <div className="mt-4 grid grid-cols-2 gap-4 md:grid-cols-4">
              <Fact label="Client" value={clientLabel(session)} />
              <Fact label="MCP mode" value={session.mcpMode} />
              <Fact label="MCP protocol" value={session.protocolVersion} />
              <Fact label="Working directory" value={session.cwd} />
              <div className="min-w-0 md:col-span-2">
                <div className="text-sm text-muted-foreground">Workspace roots</div>
                {session.roots === null ? (
                  <div aria-label="Workspace roots" title={unknownRootsHint}>—</div>
                ) : session.roots.length === 0 ? (
                  <div aria-label="Workspace roots">none</div>
                ) : (
                  <ul aria-label="Workspace roots" className="font-mono text-sm">
                    {session.roots.map((root) => (
                      <li key={root.uri} className="truncate">
                        <span>{rootLabel(root.uri)}</span>
                        {root.name && <> <span className="text-muted-foreground">({root.name})</span></>}
                      </li>
                    ))}
                  </ul>
                )}
              </div>
              <Fact label="Operations" value={String(session.operations)} />
              <Fact label="First seen" value={formatTimestamp(session.firstSeen)} />
              <Fact label="Last seen" value={formatTimestamp(session.lastSeen)} />
              <Fact label="Failed / rejected" value={`${session.failed} / ${session.rejected}`} />
              <Fact label="Running / abandoned" value={`${session.running} / ${session.abandoned}`} />
            </div>
          </details>
        </CardContent>
      </Card>
      <SessionNoteCard key={session.id} sessionId={session.id} note={note} />
      <CompareSessionOverview sessionId={id} subjects={compareSubjects} unassignedCompareCount={unassignedCompareCount} returnSubject={returnSubject} returnScope={returnScope} returnUnassigned={returnUnassigned} />
      <Card>
        <CardHeader>
          <CardTitle>All operations</CardTitle>
          <CardDescription>Newest first, up to 500.</CardDescription>
        </CardHeader>
        <CardContent>
          <OperationTable operations={operations} />
        </CardContent>
      </Card>
    </div>
  )
}
