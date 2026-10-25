import { Link } from "@tanstack/react-router"
import { Sparkles } from "lucide-react"
import type { OperationSummary } from "@/api/types"
import { StatusBadge } from "@/components/StatusBadge"
import { FlagBadges } from "@/components/FlagBadges"
import { agentLabel } from "@/lib/agents"
import { formatDuration, formatNumber } from "@/lib/format"

export function ActivityOperations({ operations, now }: { operations: OperationSummary[]; now: number }) {
  return <div aria-label="Session operations" className="activity-operations">{[...operations].sort((left, right) => Number(right.status === "running") - Number(left.status === "running") || right.id - left.id).map(operation => <div key={operation.id} className="activity-operation" data-status={operation.status}>
    <Link to="/operations/$id" params={{ id: String(operation.id) }} search={{ returnSession: operation.sessionId }} className="underline">{operation.id}</Link>
    <time title={operation.startedAt} className="text-[13px] text-muted-foreground">{new Date(operation.startedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false })}</time>
    <div className="flex min-w-0 items-center gap-2"><span className="shrink-0 capitalize">{operation.operation}</span><span className="sr-only" title={operation.agentModel ? `${operation.agentModel}${operation.agentModelSource === "transcript" ? " (transcript)" : ""}` : undefined}>{agentLabel(operation.agentKind)}</span>{operation.agentKeep && <Sparkles className="size-3.5 shrink-0 text-violet-600" aria-label="Kept by agent" />}<span className="truncate text-[13px] text-muted-foreground" title={operation.agentLabel ?? operation.subject ?? undefined}>{operation.agentLabel ?? operation.subject ?? "SQL preview unavailable"}</span>{operation.progress && <span className="text-xs text-muted-foreground">{operation.progress.polls} polls</span>}<FlagBadges operation={operation} /></div>
    <div className="activity-operation-status"><StatusBadge operation={operation} /></div>
    <span className="text-right tabular-nums">{formatDuration(operation.status === "running" ? Math.max(0, now - Date.parse(operation.startedAt)) : operation.durationMs)}</span><span className="text-right tabular-nums">{formatNumber(operation.logicalReadsMedian)}</span>
  </div>)}</div>
}
