import { Link } from "@tanstack/react-router"
import { Sparkles } from "lucide-react"
import type { OperationSummary } from "@/api/types"
import { StatusBadge } from "@/components/StatusBadge"
import { FlagBadges } from "@/components/FlagBadges"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { formatDuration, formatNumber, formatTimestamp } from "@/lib/format"
import { agentLabel } from "@/lib/agents"

export function ActivityOperations({ operations, now }: { operations: OperationSummary[]; now: number }) {
  return <Table aria-label="Session operations"><TableHeader><TableRow><TableHead>#</TableHead><TableHead>Started</TableHead><TableHead>Operation</TableHead><TableHead>Status</TableHead><TableHead className="text-right">Duration</TableHead><TableHead className="text-right">Logical reads</TableHead></TableRow></TableHeader>
    <TableBody>{[...operations].sort((left, right) => Number(right.status === "running") - Number(left.status === "running") || right.id - left.id).map(operation => <TableRow key={operation.id}>
      <TableCell><Link to="/operations/$id" params={{ id: String(operation.id) }} search={{ returnSession: operation.sessionId }}>{operation.id}</Link></TableCell>
      <TableCell className="whitespace-nowrap text-xs text-muted-foreground">{formatTimestamp(operation.startedAt)}</TableCell>
      <TableCell className="max-w-xl whitespace-normal"><div className="flex items-center gap-2"><span className="font-medium">{operation.operation}</span>{operation.agentKeep && <Sparkles className="size-3.5 text-violet-600" aria-label="Kept by agent" />}<span className="text-sm text-muted-foreground">{operation.agentLabel ?? operation.subject}</span>{operation.progress && <span className="text-xs text-muted-foreground">{operation.progress.polls} polls</span>}</div><div className="text-xs text-muted-foreground"><span title={operation.agentModel ? `${operation.agentModel}${operation.agentModelSource === "transcript" ? " (transcript)" : ""}` : undefined}>{agentLabel(operation.agentKind)}</span> · {[operation.profile, operation.database].filter(Boolean).join(" / ")}<FlagBadges operation={operation} /></div></TableCell>
      <TableCell><StatusBadge operation={operation} /></TableCell><TableCell className="text-right tabular-nums">{formatDuration(operation.status === "running" ? Math.max(0, now - Date.parse(operation.startedAt)) : operation.durationMs)}</TableCell><TableCell className="text-right tabular-nums">{formatNumber(operation.logicalReadsMedian)}</TableCell>
    </TableRow>)}</TableBody>
  </Table>
}
