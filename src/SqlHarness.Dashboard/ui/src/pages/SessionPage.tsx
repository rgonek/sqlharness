import { useParams } from "@tanstack/react-router"
import { NotFoundError } from "@/api/client"
import { useSession, useSessionOperations } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { OperationTable } from "@/components/OperationTable"
import { InternalOperationsToggle } from "@/components/InternalOperationsToggle"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { SessionAnnotationHeader } from "@/components/SessionAnnotationHeader"
import { SessionNoteCard } from "@/components/SessionNoteCard"
import { SessionConversations } from "@/components/SessionConversations"
import { OptimizationSection } from "@/components/optimization/OptimizationSection"
import { formatTimestamp } from "@/lib/format"
import { clientLabel, rootLabel } from "@/lib/roots"
import { agentLabel } from "@/lib/agents"
import { parseRouteId } from "@/lib/routeId"
import { useShowInternalOperations } from "@/lib/internalOperations"
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible"
import { Button } from "@/components/ui/button"

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
  const id = parseRouteId(raw)
  if (id === null) return <ErrorState error={new NotFoundError(`/sessions/${raw}`)} />
  return <SessionView id={id} />
}

function SessionView({ id }: { id: number }) {
  const [showInternal, setShowInternal] = useShowInternalOperations()
  const detail = useSession(id)
  const operationPages = useSessionOperations(id)

  if (detail.error) return <ErrorState error={detail.error} />
  if (detail.isPending) return <Skeleton className="h-64 w-full" />

  const { session, note = null, conversations = [] } = detail.data
  const loadedOperations = [...new Map((operationPages.data?.pages.flatMap(page => page.items) ?? []).map(operation => [operation.id, operation])).values()]
  return (
    <div className="session-page space-y-4">
      <Card className="session-heading border-0 shadow-none">
        <CardHeader className="px-0">
          <CardTitle>
            <SessionAnnotationHeader key={session.id} session={session} />
          </CardTitle>

        </CardHeader>
        <CardContent className="px-0">
          <Collapsible>
<div className="session-subtitle"><span>{agentLabel(session.agentKind)} via {session.transport}</span><span>·</span>{session.title && <><code>{session.sessionKey}</code><span>·</span></>}<span>{session.operations} operations</span><span>·</span><CollapsibleTrigger aria-label="Session metadata" className="cursor-pointer text-sm font-medium">Details</CollapsibleTrigger></div>
            <CollapsibleContent keepMounted><div className="mt-4 grid grid-cols-2 gap-4 md:grid-cols-4">
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
            </div></CollapsibleContent>
          </Collapsible>
        </CardContent>
      </Card>
      {session.hasCompare && <OptimizationSection sessionId={id} operations={loadedOperations} />}
      <SessionNoteCard key={session.id} sessionId={session.id} note={note} />
      {conversations.length > 0 && <SessionConversations conversations={conversations} />}
      <Card>
        <CardHeader>
          <CardTitle>All operations</CardTitle>
          <CardDescription>Newest first. Loaded {loadedOperations.length} of {session.operations} operations.</CardDescription>
        </CardHeader>
        <CardContent>
          <InternalOperationsToggle showInternal={showInternal} onChange={setShowInternal} />
          {operationPages.isError && <div role="alert" className="mb-3 flex items-center gap-3 text-sm text-destructive">
            <span>{operationPages.error instanceof Error ? operationPages.error.message : "Could not load session operations."}</span>
            <Button size="sm" variant="outline" onClick={() => void (operationPages.isFetchNextPageError ? operationPages.fetchNextPage() : operationPages.refetch())}>Retry</Button>
          </div>}
          {operationPages.isPending && <div className="mb-3 text-sm text-muted-foreground">Loading operations…</div>}
          <OperationTable operations={loadedOperations} showInternal={showInternal} />
          {operationPages.hasNextPage && <Button className="mt-3" variant="outline" disabled={operationPages.isFetchingNextPage}
            onClick={() => void operationPages.fetchNextPage()}>{operationPages.isFetchingNextPage ? "Loading…" : "Load more"}</Button>}
        </CardContent>
      </Card>
    </div>
  )
}
