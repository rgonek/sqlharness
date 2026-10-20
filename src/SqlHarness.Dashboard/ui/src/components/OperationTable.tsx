import { Link } from "@tanstack/react-router"
import type { OperationSummary } from "@/api/types"
import { FlagBadges } from "@/components/FlagBadges"
import { StatusBadge } from "@/components/StatusBadge"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { agentLabel } from "@/lib/agents"
import { formatDuration, formatNumber, formatTimestamp } from "@/lib/format"
import { isInternalOperation } from "@/lib/internalOperations"
import { useUpdateOperationAnnotation } from "@/api/queries"
import { Button } from "@/components/ui/button"

function OwnerStarButton({ operation }: { operation: OperationSummary }) {
  const update = useUpdateOperationAnnotation(operation.id)
  return <Button variant="ghost" size="sm" aria-label={operation.ownerStarred ? "Unstar operation" : "Star operation"}
    aria-pressed={operation.ownerStarred} disabled={update.isPending}
    onClick={() => update.mutate({ starred: !operation.ownerStarred })}>
    {operation.ownerStarred ? "★" : "☆"}
  </Button>
}

/** Live elapsed time for running rows when `now` is given; recorded duration otherwise. */
export function OperationTable({ operations, now, showInternal, membership }: { operations: OperationSummary[]; now?: number; showInternal: boolean; membership?: Record<number, string> }) {
  const visibleOperations = showInternal ? operations : operations.filter(operation => !isInternalOperation(operation))
  return (
    <div className="overflow-x-auto">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>#</TableHead>
            <TableHead>Owner</TableHead>
            <TableHead>Label</TableHead>
            <TableHead>Started</TableHead>
            <TableHead>Session / subject</TableHead>
            <TableHead>Agent</TableHead>
            <TableHead>Operation</TableHead>
            {membership && <TableHead>Membership</TableHead>}
            <TableHead>Status</TableHead>
            <TableHead>Target</TableHead>
            <TableHead className="text-right">Duration</TableHead>
            <TableHead className="text-right">Logical reads</TableHead>
            <TableHead>Flags</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {visibleOperations.map(operation => (
            <TableRow key={operation.id}>
              <TableCell>
                <Link to="/operations/$id" params={{ id: String(operation.id) }} search={{ returnSession: operation.sessionId }}>
                  {operation.id}
                </Link>
              </TableCell>
              <TableCell><div className="flex items-center gap-1"><OwnerStarButton operation={operation} />{operation.agentKeep && <span className="rounded border px-1.5 py-0.5 text-xs" title="Kept by agent">Agent keep</span>}</div></TableCell>
              <TableCell>{operation.agentLabel ?? "—"}</TableCell>
              <TableCell className="whitespace-nowrap">{formatTimestamp(operation.startedAt)}</TableCell>
              <TableCell>
                <Link to="/sessions/$id" params={{ id: String(operation.sessionId) }} className="underline">Session #{operation.sessionId}</Link>
                {operation.subject && <div className="text-sm text-muted-foreground">{operation.subject}</div>}
              </TableCell>
              <TableCell>
                <span className="whitespace-nowrap" title={operation.agentModel ? `${operation.agentModel}${operation.agentModelSource === "transcript" ? " (transcript)" : ""}` : undefined}>{agentLabel(operation.agentKind)}</span>
              </TableCell>
              <TableCell>
                <div>{operation.operation}</div>
                {operation.progress && <div className="text-sm text-muted-foreground">{operation.progress.polls} polls</div>}
                {operation.versionPair && <div className="text-sm text-muted-foreground">{operation.versionPair} · {formatNumber(operation.readsBefore)} → {formatNumber(operation.readsAfter)}</div>}
              </TableCell>
              {membership && <TableCell><span className="rounded border px-1.5 py-0.5 text-xs">{membership[operation.id] ?? "unassigned"}</span></TableCell>}
              <TableCell>
                <StatusBadge operation={operation} />
              </TableCell>
              <TableCell className="max-w-48 truncate" title={[operation.profile, operation.engine, operation.server, operation.database].filter(Boolean).join(" / ")}>
                {[operation.profile, operation.engine, operation.server, operation.database].filter(Boolean).join(" / ") || "—"}
              </TableCell>
              <TableCell className="text-right whitespace-nowrap">
                {operation.status === "running" && now !== undefined
                  ? formatDuration(now - Date.parse(operation.startedAt))
                  : formatDuration(operation.durationMs)}
              </TableCell>
              <TableCell className="text-right">{formatNumber(operation.logicalReadsMedian)}</TableCell>
              <TableCell>
                <FlagBadges operation={operation} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  )
}
