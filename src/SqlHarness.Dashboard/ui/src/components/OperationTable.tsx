import { Link } from "@tanstack/react-router"
import type { OperationSummary } from "@/api/types"
import { FlagBadges } from "@/components/FlagBadges"
import { StatusBadge } from "@/components/StatusBadge"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { formatDuration, formatNumber, formatTimestamp } from "@/lib/format"

/** Live elapsed time for running rows when `now` is given; recorded duration otherwise. */
export function OperationTable({ operations, now }: { operations: OperationSummary[]; now?: number }) {
  return (
    <div className="overflow-x-auto">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>#</TableHead>
            <TableHead>Started</TableHead>
            <TableHead>Agent</TableHead>
            <TableHead>Operation</TableHead>
            <TableHead>Status</TableHead>
            <TableHead>Target</TableHead>
            <TableHead className="text-right">Duration</TableHead>
            <TableHead className="text-right">Logical reads</TableHead>
            <TableHead>Flags</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {operations.map(operation => (
            <TableRow key={operation.id}>
              <TableCell>
                <Link to="/operations/$id" params={{ id: String(operation.id) }}>
                  {operation.id}
                </Link>
              </TableCell>
              <TableCell className="whitespace-nowrap">{formatTimestamp(operation.startedAt)}</TableCell>
              <TableCell>{operation.agentKind}</TableCell>
              <TableCell>
                <div>{operation.operation}</div>
                {operation.progress && <div className="text-sm text-muted-foreground">{operation.progress.polls} polls</div>}
              </TableCell>
              <TableCell>
                <StatusBadge status={operation.status} />
              </TableCell>
              <TableCell className="max-w-48 truncate">
                {[operation.profile, operation.database].filter(Boolean).join(" / ") || "—"}
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
