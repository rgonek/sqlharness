import { Link } from "@tanstack/react-router"
import type { OperationSummary } from "@/api/types"
import { OwnerStarButton } from "@/components/OperationTable"
import { FlagBadges } from "@/components/FlagBadges"
import { StatusBadge } from "@/components/StatusBadge"
import { Badge } from "@/components/ui/badge"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { formatDuration, formatNumber, formatTimestamp } from "@/lib/format"
import { isInternalOperation } from "@/lib/internalOperations"

/** Session transport is in the header; summaries do not carry per-operation transport. */
export function SessionOperationsTable({ operations, showInternal }: { operations: OperationSummary[]; showInternal: boolean }) {
  const visible = showInternal ? operations : operations.filter(operation => !isInternalOperation(operation))
  return <Table>
    <TableHeader><TableRow>
      <TableHead>ID</TableHead><TableHead>Time</TableHead><TableHead>Type</TableHead><TableHead>Description</TableHead>
      <TableHead>Status</TableHead><TableHead className="text-right">Duration</TableHead><TableHead className="text-right">Reads</TableHead>
    </TableRow></TableHeader>
    <TableBody>{visible.map(operation => {
      const target = [operation.profile, operation.engine, operation.server, operation.database].filter(Boolean).join(" / ")
      return <TableRow key={operation.id}>
        <TableCell><Link to="/operations/$id" params={{ id: String(operation.id) }} search={{ returnSession: operation.sessionId }}>{operation.id}</Link></TableCell>
        <TableCell className="whitespace-nowrap">{formatTimestamp(operation.startedAt)}</TableCell>
        <TableCell>{operation.operation}</TableCell>
        <TableCell className="min-w-48 max-w-80 whitespace-normal break-words">
          <div className="flex flex-wrap items-center gap-1"><OwnerStarButton operation={operation} />{operation.agentLabel && <span>{operation.agentLabel}</span>}{operation.agentKeep && <Badge variant="outline">Agent keep</Badge>}</div>
          {operation.subject && <div>{operation.subject}</div>}
          {operation.sqlPreview && <code className="block break-all">{operation.sqlPreview}{operation.sqlPreviewTruncated ? "…" : ""}</code>}
          {target && <div className="text-sm text-muted-foreground">{target}</div>}
          {operation.progress && <div className="text-sm text-muted-foreground">{operation.progress.polls} polls</div>}
          {operation.versionPair && <div className="text-sm text-muted-foreground">{operation.versionPair} · {formatNumber(operation.readsBefore)} → {formatNumber(operation.readsAfter)}</div>}
          <FlagBadges operation={operation} />
        </TableCell>
        <TableCell><StatusBadge operation={operation} /></TableCell>
        <TableCell className="text-right whitespace-nowrap">{formatDuration(operation.durationMs)}</TableCell>
        <TableCell className="text-right">{formatNumber(operation.logicalReadsMedian)}</TableCell>
      </TableRow>
    })}</TableBody>
  </Table>
}
