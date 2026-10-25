import { useState } from "react"
import { Link } from "@tanstack/react-router"
import { ChevronDown, ChevronRight, Search, Star } from "lucide-react"
import type { OperationSummary, SessionSummary } from "@/api/types"
import { useUpdateSessionAnnotation } from "@/api/queries"
import { ActivityOperations } from "@/components/ActivityOperations"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Badge } from "@/components/ui/badge"
import { sessionDisplayName } from "@/lib/sessionName"
import { agentLabel } from "@/lib/agents"
import { formatTimestamp } from "@/lib/format"

function TimeFilter({ label, value, values, onChange }: { label: string; value: string; values: { value: string; label: string }[]; onChange: (value: string) => void }) {
  return <Select items={values} value={value} onValueChange={next => { if (next !== null) onChange(next) }}>
    <SelectTrigger aria-label={label} className="w-auto min-w-32"><SelectValue /></SelectTrigger>
    <SelectContent>{values.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}</SelectContent>
  </Select>
}

function Facet({ label, selected, values, onChange }: { label: string; selected: string[]; values: { value: string; label: string }[]; onChange: (values: string[]) => void }) {
  return <details name="activity-facets" className="relative">
    <summary aria-label={label} className="flex h-8 cursor-pointer list-none items-center gap-2 rounded-lg border bg-background px-2.5 text-sm">
      <span className="text-muted-foreground">{label}</span><span>{selected.length ? selected.length === 1 ? values.find(item => item.value === selected[0])?.label : `${selected.length} selected` : "Any"}</span><ChevronDown className="size-3.5" />
    </summary>
    <div className="absolute left-0 z-20 mt-1 grid max-h-72 min-w-56 max-w-[min(32rem,85vw)] gap-2 overflow-y-auto rounded-lg border bg-popover p-3 shadow-md">
      <Button size="sm" variant="ghost" onClick={() => onChange([])}>Clear {label.toLowerCase()}</Button>
      {values.map(item => <label key={item.value} className="flex min-w-0 items-start gap-2 text-sm"><input type="checkbox" checked={selected.includes(item.value)} onChange={() => onChange(selected.includes(item.value) ? selected.filter(value => value !== item.value) : [...selected, item.value])} /><span className="break-all">{item.label}</span></label>)}
    </div>
  </details>
}

function SessionGroup({ session, operations, now }: { session: SessionSummary; operations: OperationSummary[]; now: number }) {
  const active = operations.some(operation => operation.status === "running")
  const [expanded, setExpanded] = useState<boolean | null>(null)
  const open = expanded ?? active
  const update = useUpdateSessionAnnotation(session.id)
  return <section className="border-b last:border-b-0" aria-label={sessionDisplayName(session)}>
    <div className="flex flex-wrap items-center gap-3 px-2 py-3 hover:bg-muted/40">
      <Button variant="ghost" size="icon-sm" aria-label={`${open ? "Collapse" : "Expand"} ${sessionDisplayName(session)}`} aria-expanded={open} onClick={() => setExpanded(!open)}>
        <ChevronRight className={open ? "rotate-90 transition-transform" : "transition-transform"} />
      </Button>
      <Button variant="ghost" size="icon-sm" disabled={update.isPending} aria-label={`${session.starred ? "Unstar" : "Star"} session ${sessionDisplayName(session)}`} aria-pressed={session.starred} onClick={() => update.mutate({ starred: !session.starred })}>
        <Star className={session.starred ? "fill-current text-amber-500" : "text-muted-foreground"} />
      </Button>
      <div className="min-w-40 flex-1">
        <div className="flex items-center gap-2"><Link to="/sessions/$id" params={{ id: String(session.id) }} className="font-medium hover:underline">{sessionDisplayName(session)}</Link>{session.archived && <Badge variant="outline">Archived</Badge>}</div>
        <p className="text-xs text-muted-foreground">{agentLabel(session.agentKind)} · {session.transport}</p>
      </div>
      <span className="min-w-0 flex-1 truncate font-mono text-xs text-muted-foreground" title={session.cwd ?? undefined}>{session.cwd ?? "—"}</span>
      <time className="text-xs text-muted-foreground">{formatTimestamp(session.lastSeen)}</time>
      {session.optimization?.map(item => <Badge key={item.subject} variant="outline">{item.subject} {item.change}</Badge>)}
      {active && <Badge variant="secondary">Running</Badge>}
      <span className="text-xs text-muted-foreground">{operations.length} shown / {session.operations} operations</span>
    </div>
    {update.error && <p role="alert" className="px-4 text-sm text-destructive">Could not update session: {update.error.message}</p>}
    {open && <div className="overflow-x-auto pb-3 pl-4 sm:pl-16"><ActivityOperations operations={operations} now={now} /><Link to="/sessions/$id" params={{ id: String(session.id) }} className="mt-2 inline-block text-xs text-muted-foreground underline">All session operations</Link></div>}
  </section>
}

export function ActivityFeed({ sessions, operations, now }: { sessions: SessionSummary[]; operations: OperationSummary[]; now: number }) {
  const [time, setTime] = useState("604800000")
  const [agent, setAgent] = useState<string[]>([])
  const [cwd, setCwd] = useState<string[]>([])
  const [target, setTarget] = useState<string[]>([])
  const [status, setStatus] = useState<string[]>([])
  const [kind, setKind] = useState<string[]>([])
  const [text, setText] = useState("")
  const [starred, setStarred] = useState(false)
  const [archived, setArchived] = useState(false)
  const [named, setNamed] = useState(false)
  const options = (items: (string | null)[]) => [...new Set(items.filter((item): item is string => !!item))].sort().map(value => ({ value, label: value }))
  const sessionById = new Map(sessions.map(session => [session.id, session]))
  const targetKey = (operation: OperationSummary) => [operation.profile, operation.server, operation.database].filter(Boolean).join(" / ")
  const matches = (operation: OperationSummary, candidate: { status?: string[]; kind?: string[]; named?: boolean; starred?: boolean } = {}) => {
    const session = sessionById.get(operation.sessionId)
    return (time === "all" || Date.parse(operation.startedAt) >= now - Number(time))
      && (!agent.length || agent.includes(operation.agentKind))
      && (!cwd.length || (session?.cwd !== null && session?.cwd !== undefined && cwd.includes(session.cwd)))
      && (!target.length || target.includes(targetKey(operation)))
      && (!(candidate.starred ?? starred) || !!session?.starred)
      && (archived || !session?.archived)
      && (!(candidate.named ?? named) || !!(operation.agentLabel || operation.agentKeep || operation.ownerStarred))
      && (!(candidate.status ?? status).length || (candidate.status ?? status).includes(operation.status))
      && (!(candidate.kind ?? kind).length || (candidate.kind ?? kind).includes(operation.operation))
      && (!text || [session && sessionDisplayName(session), session?.cwd, operation.agentLabel, operation.subject, operation.operation, operation.id, operation.database, operation.server].some(value => String(value ?? "").toLowerCase().includes(text.toLowerCase())))
  }
  const filtered = operations.filter(operation => matches(operation))
  const groups = sessions.map(session => ({ session, operations: filtered.filter(operation => operation.sessionId === session.id) }))
    .filter(group => group.operations.length > 0 || (!text && !status.length && !kind.length && !named && (!starred || group.session.starred) && (archived || !group.session.archived) && (time === "all" || Date.parse(group.session.lastSeen) >= now - Number(time)) && !target.length && (!agent.length || agent.includes(group.session.agentKind)) && (!cwd.length || (!!group.session.cwd && cwd.includes(group.session.cwd)))))
    .sort((left, right) => Number(right.operations.some(operation => operation.status === "running")) - Number(left.operations.some(operation => operation.status === "running")) || Date.parse(right.session.lastSeen) - Date.parse(left.session.lastSeen))
  const ungrouped = filtered.filter(operation => !sessionById.has(operation.sessionId))
  const toggle = (items: string[], item: string) => items.includes(item) ? items.filter(value => value !== item) : [...items, item]
  return <section aria-label="Activity feed" className="space-y-4">
    <div className="flex flex-wrap items-center gap-2" aria-label="Activity filters">
      <TimeFilter label="Time" value={time} onChange={setTime} values={[{ value: "all", label: "All time" }, { value: "604800000", label: "Last 7 days" }, { value: "86400000", label: "Last 24 hours" }, { value: "3600000", label: "Last hour" }]} />
      <Facet label="Agent" selected={agent} onChange={setAgent} values={options([...operations.map(operation => operation.agentKind), ...sessions.map(session => session.agentKind)])} />
      <Facet label="Working directory" selected={cwd} onChange={setCwd} values={options(sessions.map(session => session.cwd))} />
      <Facet label="Target" selected={target} onChange={setTarget} values={options(operations.map(targetKey))} />
      <Button size="sm" variant={starred ? "secondary" : "outline"} aria-pressed={starred} onClick={() => setStarred(!starred)}>Starred ({operations.filter(operation => matches(operation, { starred: true })).length})</Button>
      <Button size="sm" variant={archived ? "secondary" : "outline"} aria-pressed={archived} onClick={() => setArchived(!archived)}>Include archived</Button>
      <div className="relative ml-auto min-w-48 flex-1 sm:max-w-72"><Search className="absolute left-2.5 top-2.5 size-4 text-muted-foreground" /><Input aria-label="Search activity" placeholder="Search activity…" value={text} onChange={event => setText(event.target.value)} className="pl-9" /></div>
    </div>
    <div className="flex flex-wrap items-center gap-2 text-xs">
      <span className="mr-1 text-muted-foreground">Status</span>{["running", "succeeded", "failed", "rejected", "abandoned"].map(value => <Button key={value} size="sm" variant={status.includes(value) ? "secondary" : "ghost"} aria-pressed={status.includes(value)} onClick={() => setStatus(toggle(status, value))}>{`${value} (${operations.filter(operation => matches(operation, { status: [value] })).length})`}</Button>)}
      <span className="ml-3 mr-1 text-muted-foreground">Type</span>{[...new Set(operations.map(operation => operation.operation))].sort().map(value => <Button key={value} size="sm" variant={kind.includes(value) ? "secondary" : "ghost"} aria-pressed={kind.includes(value)} onClick={() => setKind(toggle(kind, value))}>{`${value} (${operations.filter(operation => matches(operation, { kind: [value] })).length})`}</Button>)}
      <span className="ml-3 text-muted-foreground">Marked</span><Button size="sm" variant={named ? "secondary" : "ghost"} aria-pressed={named} onClick={() => setNamed(!named)}>Named ({operations.filter(operation => matches(operation, { named: true })).length})</Button>
      <Button variant="ghost" size="sm" onClick={() => { setTime("604800000"); setAgent([]); setCwd([]); setTarget([]); setStatus([]); setKind([]); setText(""); setStarred(false); setArchived(false); setNamed(false) }}>Reset filters</Button>
    </div>
    <div className="overflow-hidden rounded-lg border">{groups.map(group => <SessionGroup key={group.session.id} {...group} now={now} />)}{ungrouped.length > 0 && <ActivityOperations operations={ungrouped} now={now} />}{!filtered.length && !groups.length && <p className="p-10 text-center text-sm text-muted-foreground">Nothing matches these filters.</p>}</div>
    <p className="text-xs text-muted-foreground">Showing recent activity and all running operations. <Link to="/operations" className="underline">Browse operation history</Link> · <Link to="/sessions" className="underline">Browse all sessions</Link> · <Link to="/named-operations" className="underline">Named operations</Link> · <Link to="/mutations" className="underline">Mutations</Link></p>
  </section>
}
