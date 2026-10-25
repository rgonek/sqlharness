import { Archive, ArchiveRestore, Pencil, Star } from "lucide-react"
import { useEffect, useState } from "react"
import { Link, useNavigate, useParams } from "@tanstack/react-router"
import { useAgentSession, useAgentSessionOperations, useAgentSessionOptimization, useSettings, useUpdateAgentSessionAnnotation } from "@/api/queries"
import { NotFoundError } from "@/api/client"
import { ErrorState } from "@/components/ErrorState"
import { InternalOperationsToggle } from "@/components/InternalOperationsToggle"
import { OperationTable } from "@/components/OperationTable"
import { OptimizationSection } from "@/components/optimization/OptimizationSection"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Skeleton } from "@/components/ui/skeleton"
import { Textarea } from "@/components/ui/textarea"
import { useShowInternalOperations } from "@/lib/internalOperations"

export function AgentSessionPage() {
  const { kind, id } = useParams({ from: "/agent-sessions/$kind/$id" })
  const key = { agentKind: kind, conversationId: id }
  const settings = useSettings()
  const matchingEnabled = settings.data?.settings.dashboard.agentTranscripts.enabled ?? false
  const detail = useAgentSession(key, matchingEnabled)
  const operationPages = useAgentSessionOperations(key, matchingEnabled)
  const optimization = useAgentSessionOptimization(key, matchingEnabled)
  const navigate = useNavigate({ from: "/agent-sessions/$kind/$id" })
  const [showInternal, setShowInternal] = useShowInternalOperations()
  const update = useUpdateAgentSessionAnnotation(key)
  const [titleDraft, setTitleDraft] = useState<string | null>(null)
  const [noteDraft, setNoteDraft] = useState<string | null>(null)

  useEffect(() => {
    if (settings.data && !settings.data.settings.dashboard.agentTranscripts.enabled) {
      void navigate({ to: "/sessions", replace: true })
    }
  }, [settings.data, navigate])

  if (settings.data && !settings.data.settings.dashboard.agentTranscripts.enabled)
    return <p role="status">Transcript matching is off. Returning to MCP/CLI sessions…</p>
  if (settings.error) return <ErrorState error={settings.error} />
  if (detail.error) return <ErrorState error={detail.error instanceof NotFoundError ? detail.error : detail.error} />
  if (detail.isPending || settings.isPending || operationPages.isPending) return <Skeleton className="h-64 w-full" />
  if (operationPages.error && !operationPages.data) return <ErrorState error={operationPages.error} />

  const { agentSession } = detail.data
  const operations = operationPages.data?.pages.flatMap(page => page.items) ?? []
  const displayTitle = agentSession.title ?? `${kind}:${id}`
  const membership = Object.fromEntries(operations.map(item => [item.operation.id, item.membership]))
  const firstChild = agentSession.children[0]
  const optimizationSessionId = firstChild?.session.id
  return <div className="session-page space-y-4">
    <Card className="session-heading">
      <CardHeader className="px-0">
        <CardTitle><div className="session-title-row flex flex-wrap items-center gap-2">
          {titleDraft === null ? <><h1 className="min-w-0 flex-1">{displayTitle}</h1><Button className="session-edit-title" size="icon" variant="ghost" aria-label="Edit title" onClick={() => setTitleDraft(agentSession.ownerTitle ?? "")}><Pencil /></Button></>
            : <><Input aria-label="Agent session title" maxLength={200} value={titleDraft} onChange={event => setTitleDraft(event.target.value)} />
              <Button size="sm" disabled={update.isPending} onClick={() => update.mutate({ title: titleDraft.trim() || null }, { onSuccess: () => setTitleDraft(null) })}>Save title</Button>
              <Button size="sm" variant="ghost" onClick={() => setTitleDraft(null)}>Cancel</Button></>}
          <Button size="icon" variant="ghost" aria-label={agentSession.starred ? "Unstar" : "Star"} aria-pressed={agentSession.starred} onClick={() => update.mutate({ starred: !agentSession.starred })}><Star className={agentSession.starred ? "fill-amber-400 text-amber-500" : ""} /></Button>
          <Button size="icon" variant="ghost" aria-label={agentSession.archived ? "Unarchive" : "Archive"} onClick={() => update.mutate({ archived: !agentSession.archived })}>{agentSession.archived ? <ArchiveRestore /> : <Archive />}</Button>
        </div></CardTitle>
        <CardDescription>{kind} agent session · {id}</CardDescription>
        <p>{agentSession.exact} exact · {agentSession.inferred} inferred · {agentSession.unassigned} unassigned</p>
        {agentSession.title === "subagent of unknown parent" && <p className="text-sm text-muted-foreground">subagent of unknown parent</p>}
      </CardHeader>

    </Card>
    {<OptimizationSection sessionId={optimizationSessionId ?? 0} agentSessionKey={key} operations={operations.map(item => item.operation)} />}
    <Card>      <CardContent className="space-y-3">
        {noteDraft === null ? <><p className="whitespace-pre-wrap">{detail.data.note ?? "No note"}</p>
          <Button size="sm" variant="outline" onClick={() => setNoteDraft(detail.data.note ?? "")}>{detail.data.note ? "Edit note" : "Add note"}</Button></>
          : <><Textarea aria-label="Agent session note" maxLength={10_000} rows={5} className="w-full rounded border p-2" value={noteDraft} onChange={event => setNoteDraft(event.target.value)} />
            <Button size="sm" disabled={update.isPending} onClick={() => update.mutate({ note: noteDraft.trim() || null }, { onSuccess: () => setNoteDraft(null) })}>Save note</Button>
            <Button size="sm" variant="ghost" onClick={() => setNoteDraft(null)}>Cancel</Button></>}
        {update.error && <p role="alert">{update.error.message}</p>}
        {agentSession.children.length > 0 && <div><h2 className="font-medium">MCP/CLI sessions</h2><ul className="list-disc pl-5">{agentSession.children.map(child => <li key={child.session.id}>
          <Link className="underline" to="/sessions/$id" params={{ id: String(child.session.id) }}>{child.session.title ?? child.session.sessionKey}</Link>
          <span className="ml-2 text-sm text-muted-foreground">{child.session.transport} · {child.exact} exact · {child.inferred} inferred · {child.unassigned} unassigned</span>
          {child.session.hasCompare && <> · <Link className="underline" to="/sessions/$id" params={{ id: String(child.session.id) }}>Optimization history</Link></>}
        </li>)}</ul></div>}
      </CardContent></Card>
    {optimization.data?.versionLabelsProvisional && optimization.data.versionLabelNote && <p role="note" className="text-sm text-muted-foreground">{optimization.data.versionLabelNote}</p>}
    {agentSession.children.some(child => child.session.hasCompare) && <Card><CardHeader><CardTitle>Child Optimization histories</CardTitle><CardDescription>Each child session keeps its own history, including inferred and CLI compares.</CardDescription></CardHeader>
      <CardContent><ul className="list-disc pl-5">{agentSession.children.filter(child => child.session.hasCompare).map(child => <li key={child.session.id}><Link className="underline" to="/sessions/$id" params={{ id: String(child.session.id) }}>{child.session.title ?? child.session.sessionKey}</Link> · Optimization history</li>)}</ul></CardContent></Card>}
    <Card><CardHeader><CardTitle>Operations</CardTitle><CardDescription>Loaded {operations.length} of {agentSession.exact + agentSession.inferred + agentSession.unassigned} operations. Newest first; membership is shown for every operation.</CardDescription></CardHeader><CardContent className="space-y-3">
      <InternalOperationsToggle showInternal={showInternal} onChange={setShowInternal} /><OperationTable operations={operations.map(item => item.operation)} showInternal={showInternal} membership={membership} />
      {operationPages.isFetchNextPageError && <p role="alert">Could not load older operations.</p>}
      {operationPages.hasNextPage && <Button variant="outline" disabled={operationPages.isFetchingNextPage} onClick={() => void operationPages.fetchNextPage()}>
        {operationPages.isFetchNextPageError ? "Retry" : operationPages.isFetchingNextPage ? "Loading…" : "Load more"}
      </Button>}
    </CardContent></Card>
  </div>
}
