import { Link } from "@tanstack/react-router"
import type { SessionSummary } from "@/api/types"
import { Badge } from "@/components/ui/badge"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { formatAge, formatTimestamp } from "@/lib/format"

export function SessionTable({ sessions, now }: { sessions: SessionSummary[]; now: number }) {
  return (
    <div className="overflow-x-auto">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Session</TableHead>
            <TableHead>Agent</TableHead>
            <TableHead>Transport</TableHead>
            <TableHead>Working directory</TableHead>
            <TableHead>First seen</TableHead>
            <TableHead>Last seen</TableHead>
            <TableHead className="text-right">Operations</TableHead>
            <TableHead>State</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {sessions.map(session => (
            <TableRow key={session.id}>
              <TableCell className="font-mono">
                <Link to="/sessions/$id" params={{ id: String(session.id) }}>
                  {session.sessionKey}
                </Link>
              </TableCell>
              <TableCell>
                {session.agentKind}
                {session.clientName && <div className="text-sm text-muted-foreground">{session.clientName} {session.clientVersion}</div>}
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
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  )
}
