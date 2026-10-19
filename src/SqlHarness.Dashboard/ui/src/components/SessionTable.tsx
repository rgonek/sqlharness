import { Link } from "@tanstack/react-router"
import { useState } from "react"
import { Archive, ArchiveRestore, ChevronDown, ChevronRight, Star, StickyNote, TriangleAlert } from "lucide-react"
import type { AgentSessionSummary, SessionSummary } from "@/api/types"
import { useUpdateAgentSessionAnnotation, useUpdateSessionAnnotation } from "@/api/queries"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { formatAge, formatTimestamp } from "@/lib/format"
import { agentLabel } from "@/lib/agents"
import { clientLabel } from "@/lib/roots"
import { sessionDisplayName } from "@/lib/sessionName"

function SessionRow({ session, now }: { session: SessionSummary; now: number }) {
  const update = useUpdateSessionAnnotation(session.id)
  const name = sessionDisplayName(session)
  return (
    <TableRow>
      <TableCell>
        <Button variant="ghost" size="icon" disabled={update.isPending}
          aria-label={`${session.starred ? "Unstar" : "Star"} session ${session.sessionKey}`}
          aria-pressed={session.starred}
          onClick={() => update.mutate({ starred: !session.starred })}>
          <Star className={session.starred ? "fill-current" : undefined} />
        </Button>
      </TableCell>
      <TableCell>
        <div className="flex items-center gap-1">
          <Link to="/sessions/$id" params={{ id: String(session.id) }} className={session.title ? undefined : "font-mono"}>
            {name}
          </Link>
          {session.notePreview && (
            <Tooltip>
              <TooltipTrigger aria-label={`Note: ${session.notePreview}`}><StickyNote className="size-4 text-muted-foreground" /></TooltipTrigger>
              <TooltipContent>{session.notePreview}</TooltipContent>
            </Tooltip>
          )}
        </div>
        {session.title && <div className="font-mono text-sm text-muted-foreground">{session.sessionKey}</div>}
        {update.error && <div role="alert" className="text-sm text-destructive">{update.error.message}</div>}
      </TableCell>
      <TableCell>
        {agentLabel(session.agentKind)}
        {clientLabel(session) && <div className="text-sm text-muted-foreground">{clientLabel(session)}</div>}
      </TableCell>
      <TableCell>{session.transport}{session.mcpMode ? ` (${session.mcpMode})` : ""}</TableCell>
      <TableCell className="font-mono" title={session.cwd ?? undefined}>{session.cwd ? session.cwd.split(/[\\/]/).filter(Boolean).at(-1) ?? session.cwd : "—"}</TableCell>
      <TableCell className="whitespace-nowrap">{formatTimestamp(session.firstSeen)}</TableCell>
      <TableCell className="whitespace-nowrap">{formatAge(session.lastSeen, now)}</TableCell>
      <TableCell className="text-right">{session.operations}</TableCell>
      <TableCell>
        {session.hasCompare && <Link to="/sessions/$id" params={{ id: String(session.id) }} className="flex flex-wrap gap-1" aria-label="Optimization subjects">
          {session.optimization.map(item => <Badge key={item.subject} variant="outline" className="gap-1" title={`Verdict basis: ${item.verdictBasis}`}>
            {item.warning && <TriangleAlert aria-label="Warning" className="size-3 text-amber-600" />}
            {item.subject} {item.change}
          </Badge>)}
        </Link>}
      </TableCell>
      <TableCell>
        <div className="flex flex-wrap gap-1">
          {session.running > 0 && <Badge>{session.running} running</Badge>}
          {session.failed > 0 && <Badge variant="destructive">{session.failed} failed</Badge>}
          {session.rejected > 0 && <Badge variant="destructive">{session.rejected} rejected</Badge>}
          {session.abandoned > 0 && <Badge variant="outline">{session.abandoned} abandoned</Badge>}
        </div>
      </TableCell>
      <TableCell>
        <Button variant="ghost" size="icon" disabled={update.isPending}
          aria-label={`${session.archived ? "Unarchive" : "Archive"} session ${session.sessionKey}`}
          onClick={() => update.mutate({ archived: !session.archived })}>
          {session.archived ? <ArchiveRestore /> : <Archive />}
        </Button>
      </TableCell>
    </TableRow>
  )
}

export function SessionTable({ pinned, sessions, now }: { pinned: SessionSummary[]; sessions: SessionSummary[]; now: number }) {
  return (
    <div className="overflow-x-auto">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead className="w-8"><span className="sr-only">Star</span></TableHead>
            <TableHead>Session</TableHead>
            <TableHead>Agent</TableHead>
            <TableHead>Transport</TableHead>
            <TableHead>Working directory</TableHead>
            <TableHead>First seen</TableHead>
            <TableHead>Last seen</TableHead>
            <TableHead className="text-right">Operations</TableHead>
            <TableHead>Optimization</TableHead>
            <TableHead>State</TableHead>
            <TableHead className="w-8"><span className="sr-only">Actions</span></TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {pinned.map(session => <SessionRow key={session.id} session={session} now={now} />)}
          {pinned.length > 0 && sessions.length > 0 && (
            <TableRow aria-hidden="true"><TableCell colSpan={11} className="h-0 border-b-2 p-0" /></TableRow>
          )}
          {sessions.map(session => <SessionRow key={session.id} session={session} now={now} />)}
        </TableBody>
      </Table>
    </div>
  )
}

function AgentSessionRow({ agentSession, now }: { agentSession: AgentSessionSummary; now: number }) {
  const [expanded, setExpanded] = useState(false)
  const update = useUpdateAgentSessionAnnotation(agentSession.key)
  const title = agentSession.title || `${agentSession.key.agentKind}:${agentSession.key.conversationId}`
  const children = agentSession.children
  return <>
    <TableRow>
      <TableCell><Button variant="ghost" size="icon" disabled={update.isPending} aria-label={`${agentSession.starred ? "Unstar" : "Star"} agent session ${title}`} aria-pressed={agentSession.starred}
        onClick={() => update.mutate({ starred: !agentSession.starred })}><Star className={agentSession.starred ? "fill-current" : undefined} /></Button></TableCell>
      <TableCell><div className="flex items-center gap-1">
        <Button variant="ghost" size="icon" aria-label={`${expanded ? "Collapse" : "Expand"} ${title}`} aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>{expanded ? <ChevronDown /> : <ChevronRight />}</Button>
        <Link to="/agent-sessions/$kind/$id" params={{ kind: agentSession.key.agentKind, id: agentSession.key.conversationId }}>{title}</Link>
        {agentSession.notePreview && <Tooltip><TooltipTrigger aria-label={`Note: ${agentSession.notePreview}`}><StickyNote className="size-4 text-muted-foreground" /></TooltipTrigger><TooltipContent>{agentSession.notePreview}</TooltipContent></Tooltip>}
      </div><div className="ml-9 text-sm text-muted-foreground">{agentSession.exact} exact · {agentSession.inferred} inferred · {agentSession.unassigned} unassigned</div></TableCell>
      <TableCell>{agentSession.key.agentKind}</TableCell><TableCell>Agent session</TableCell><TableCell>—</TableCell>
      <TableCell>—</TableCell><TableCell className="whitespace-nowrap">{formatAge(agentSession.lastSeen, now)}</TableCell>
      <TableCell className="text-right">{agentSession.exact + agentSession.inferred + agentSession.unassigned}</TableCell><TableCell>—</TableCell><TableCell>—</TableCell>
      <TableCell><Button variant="ghost" size="icon" disabled={update.isPending} aria-label={`${agentSession.archived ? "Unarchive" : "Archive"} agent session ${title}`} onClick={() => update.mutate({ archived: !agentSession.archived })}>{agentSession.archived ? <ArchiveRestore /> : <Archive />}</Button></TableCell>
    </TableRow>
    {expanded && children.map(child => <TableRow key={child.session.id}>
      <TableCell />
      <TableCell className="pl-12"><Link to="/sessions/$id" params={{ id: String(child.session.id) }}>{sessionDisplayName(child.session)}</Link>
        <div className="text-sm text-muted-foreground">{child.exact} exact · {child.inferred} inferred · {child.unassigned} unassigned</div>
      </TableCell><TableCell>{agentLabel(child.session.agentKind)}</TableCell><TableCell>{child.session.transport}{child.session.mcpMode ? ` (${child.session.mcpMode})` : ""}</TableCell>
      <TableCell>{child.session.cwd ? child.session.cwd.split(/[\\/]/).filter(Boolean).at(-1) : "—"}</TableCell><TableCell>{formatTimestamp(child.session.firstSeen)}</TableCell>
      <TableCell>{formatAge(child.session.lastSeen, now)}</TableCell><TableCell className="text-right">{child.session.operations}</TableCell>
      <TableCell>{child.session.hasCompare && <Link to="/sessions/$id" params={{ id: String(child.session.id) }}>Optimization</Link>}</TableCell><TableCell>—</TableCell><TableCell /></TableRow>)}
    {update.error && <TableRow><TableCell colSpan={11}><span role="alert">{update.error.message}</span></TableCell></TableRow>}
  </>
}

export function AgentSessionTable({ pinned, sessions, unmatched, now }: { pinned: AgentSessionSummary[]; sessions: AgentSessionSummary[]; unmatched: SessionSummary[]; now: number }) {
  return <div className="overflow-x-auto"><Table>
    <TableHeader><TableRow><TableHead className="w-8"><span className="sr-only">Star</span></TableHead><TableHead>Session</TableHead><TableHead>Agent</TableHead><TableHead>Transport</TableHead><TableHead>Working directory</TableHead><TableHead>First seen</TableHead><TableHead>Last seen</TableHead><TableHead className="text-right">Operations</TableHead><TableHead>Optimization</TableHead><TableHead>State</TableHead><TableHead className="w-8"><span className="sr-only">Actions</span></TableHead></TableRow></TableHeader>
    <TableBody>
      {pinned.map(item => <AgentSessionRow key={`${item.key.agentKind}:${item.key.conversationId}`} agentSession={item} now={now} />)}
      {sessions.map(item => <AgentSessionRow key={`${item.key.agentKind}:${item.key.conversationId}`} agentSession={item} now={now} />)}
      {unmatched.map(item => <TableRow key={`unmatched:${item.id}`}><TableCell /><TableCell><Link to="/sessions/$id" params={{ id: String(item.id) }}>{sessionDisplayName(item)}</Link><div className="text-sm text-muted-foreground">not matched</div></TableCell><TableCell>{agentLabel(item.agentKind)}</TableCell><TableCell>{item.transport}</TableCell><TableCell>{item.cwd ?? "—"}</TableCell><TableCell>{formatTimestamp(item.firstSeen)}</TableCell><TableCell>{formatAge(item.lastSeen, now)}</TableCell><TableCell className="text-right">{item.operations}</TableCell><TableCell>—</TableCell><TableCell>—</TableCell><TableCell /></TableRow>)}
    </TableBody>
  </Table></div>
}
