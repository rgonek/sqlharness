import { Link } from "@tanstack/react-router"
import type { OperationSummary } from "@/api/types"
import { OwnerStarButton } from "@/components/OperationTable"
import { FlagBadges } from "@/components/FlagBadges"
import { StatusBadge } from "@/components/StatusBadge"
import { HugeiconsIcon } from "@hugeicons/react"
import { SparklesIcon } from "@hugeicons/core-free-icons"
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
        <TableCell className="whitespace-nowrap"><time dateTime={operation.startedAt} title={formatTimestamp(operation.startedAt)}>{new Date(operation.startedAt).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}</time></TableCell>
        <TableCell>{operation.operation}</TableCell>
        <TableCell className="min-w-48 max-w-80">
          <div className="flex items-center gap-1">
            <OwnerStarButton operation={operation} prominent />
            {operation.agentKeep && <HugeiconsIcon icon={SparklesIcon} className="size-4 shrink-0 text-muted-foreground" aria-label="Kept by agent" />}
            <span className="truncate" title={[operation.agentLabel, operation.subject, operation.sqlPreview, target].filter(Boolean).join("\n")}>{operation.agentLabel || operation.sqlPreview || operation.subject || operation.operation}{!operation.agentLabel && operation.sqlPreview && operation.sqlPreviewTruncated ? "…" : ""}</span>
            {operation.progress && <span className="shrink-0 text-xs text-muted-foreground">{operation.progress.polls} polls</span>}
            {operation.versionPair && <span className="shrink-0 text-xs text-muted-foreground">{operation.versionPair} · {formatNumber(operation.readsBefore)} → {formatNumber(operation.readsAfter)}</span>}
            <FlagBadges operation={operation} />
          </div>
        </TableCell>
        <TableCell><StatusBadge operation={operation} /></TableCell>
        <TableCell className="text-right whitespace-nowrap">{formatDuration(operation.durationMs)}</TableCell>
        <TableCell className="text-right">{formatNumber(operation.logicalReadsMedian)}</TableCell>
      </TableRow>
    })}</TableBody>
  </Table>
}
