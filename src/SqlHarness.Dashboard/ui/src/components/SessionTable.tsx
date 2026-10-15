import { Link } from "@tanstack/react-router"
import { Archive, ArchiveRestore, Star, StickyNote } from "lucide-react"
import type { SessionSummary } from "@/api/types"
import { useUpdateSessionAnnotation } from "@/api/queries"
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
      <TableCell className="max-w-64 truncate font-mono">{session.cwd ?? "—"}</TableCell>
      <TableCell className="whitespace-nowrap">{formatTimestamp(session.firstSeen)}</TableCell>
      <TableCell className="whitespace-nowrap">{formatAge(session.lastSeen, now)}</TableCell>
      <TableCell className="text-right">{session.operations}</TableCell>
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
            <TableHead>State</TableHead>
            <TableHead className="w-8"><span className="sr-only">Actions</span></TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {pinned.map(session => <SessionRow key={session.id} session={session} now={now} />)}
          {pinned.length > 0 && sessions.length > 0 && (
            <TableRow aria-hidden="true"><TableCell colSpan={10} className="h-0 border-b-2 p-0" /></TableRow>
          )}
          {sessions.map(session => <SessionRow key={session.id} session={session} now={now} />)}
        </TableBody>
      </Table>
    </div>
  )
}
