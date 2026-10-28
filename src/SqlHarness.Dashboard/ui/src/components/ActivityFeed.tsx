import { Tooltip, TooltipTrigger, TooltipContent } from "@/components/ui/tooltip"
import { useId, useState } from "react"
import { Link } from "@tanstack/react-router"
import type { OperationSummary, SessionSummary } from "@/api/types"
import { useUpdateSessionAnnotation } from "@/api/queries"
import { ActivityOperations } from "@/components/ActivityOperations"
import { Button } from "@/components/ui/button"
import { InputGroup, InputGroupInput, InputGroupAddon } from "@/components/ui/input-group"
import { Checkbox } from "@/components/ui/checkbox"
import { Toggle } from "@/components/ui/toggle"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"
import { Field, FieldLabel, FieldSet, FieldLegend, FieldGroup } from "@/components/ui/field"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Badge } from "@/components/ui/badge"
import { sessionDisplayName } from "@/lib/sessionName"
import { agentLabel } from "@/lib/agents"
import { HugeiconsIcon } from "@hugeicons/react";
import { ArrowDown01Icon, ArrowRight01Icon, SearchIcon, StarIcon } from "@hugeicons/core-free-icons";

function formatRelativeTime(value: string, now: number) {
  const minutes = Math.max(0, Math.floor((now - Date.parse(value)) / 60000))
  return minutes < 1 ? "now" : minutes < 60 ? `${minutes} min ago` : minutes < 1440 ? `${Math.floor(minutes / 60)} hr ago` : `${Math.floor(minutes / 1440)} days ago`
}

function TimeFilter({ label, value, values, onChange }: { label: string; value: string; values: { value: string; label: string }[]; onChange: (value: string) => void }) {
  return <Select items={values} value={value} onValueChange={next => { if (next !== null) onChange(next) }}>
    <SelectTrigger aria-label={label} className="w-auto min-w-32"><span className="text-muted-foreground">Time</span><SelectValue /></SelectTrigger>
    <SelectContent><SelectGroup>{values.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}</SelectGroup></SelectContent>
  </Select>
}

function Facet({ label, selected, values, onChange }: { label: string; selected: string[]; values: { value: string; label: string; count?: number }[]; onChange: (values: string[]) => void }) {
  const id = useId()
  return <Popover>
    <PopoverTrigger render={<Button variant="outline" />} aria-label={label}>
      <span className="text-muted-foreground">{label}</span><span>{selected.length ? selected.length === 1 ? values.find(item => item.value === selected[0])?.label : `${selected.length} selected` : "Any"}</span><HugeiconsIcon icon={ArrowDown01Icon} strokeWidth={2} data-icon="inline-end" />
    </PopoverTrigger>
    <PopoverContent align="start" className="max-h-72 max-w-[85vw] overflow-y-auto">
      <Button size="sm" variant="ghost" onClick={() => onChange([])}>Clear {label.toLowerCase()}</Button>
      <FieldSet><FieldLegend className="sr-only">{label}</FieldLegend><FieldGroup>
      {values.map((item, index) => <Field key={item.value} orientation="horizontal"><Checkbox id={`${id}-${index}`} checked={selected.includes(item.value)} onCheckedChange={() => onChange(selected.includes(item.value) ? selected.filter(value => value !== item.value) : [...selected, item.value])} /><FieldLabel htmlFor={`${id}-${index}`} className="min-w-0 flex-1 break-all">{item.label}</FieldLabel><span aria-hidden="true" className="text-muted-foreground tabular-nums">{item.count}</span></Field>)}
      </FieldGroup></FieldSet>
    </PopoverContent>
  </Popover>
}

function SessionGroup({ session, operations, now, expanded, onExpand }: { session: SessionSummary; operations: OperationSummary[]; now: number; expanded: boolean; onExpand: () => void }) {
  const optimizationTooltipId = useId()
  const active = operations.some(operation => operation.status === "running")
  const open = expanded
  const update = useUpdateSessionAnnotation(session.id)
  return <section className="border-b last:border-b-0" aria-label={sessionDisplayName(session)}>
    <div className="activity-session-row cursor-pointer" onClick={event => {
      if ((event.target as HTMLElement).closest("a,button,input,[role=button]")) return
      onExpand()
    }}>
      <Button variant="ghost" size="icon-sm" aria-label={`${open ? "Collapse" : "Expand"} ${sessionDisplayName(session)}`} aria-expanded={open} onClick={onExpand}>
        <HugeiconsIcon icon={ArrowRight01Icon} strokeWidth={2} className={open ? "rotate-90 transition-transform" : "transition-transform"} />
      </Button>
      <Toggle className="session-star" size="sm" disabled={update.isPending} aria-label={`${session.starred ? "Unstar" : "Star"} session ${sessionDisplayName(session)}`} pressed={session.starred} onPressedChange={starred => update.mutate({ starred })}>
        <HugeiconsIcon icon={StarIcon} strokeWidth={2} className={session.starred ? "fill-current text-amber-500" : "text-muted-foreground"} />
      </Toggle>
      <div className="min-w-0">
        <div className="flex items-center gap-2"><Link to="/sessions/$id" params={{ id: String(session.id) }} className="block truncate font-medium underline">{sessionDisplayName(session)}</Link>{session.archived && <Badge variant="outline">Archived</Badge>}</div>
        <p className="text-xs text-muted-foreground"><span>{agentLabel(session.agentKind)}</span> · {session.transport}</p>
      </div>
      <span className="activity-directory min-w-0 truncate font-mono text-[13px] text-muted-foreground" title={session.cwd ?? undefined}>{session.cwd ?? "—"}</span>
      <time className="activity-last-seen text-[13px] text-muted-foreground" title={session.lastSeen}>{formatRelativeTime(session.lastSeen, now)}</time>
      <div className="activity-optimization min-w-0">{!!session.optimization?.length && <Tooltip><TooltipTrigger aria-describedby={optimizationTooltipId} render={<span tabIndex={0} className="block truncate text-xs text-muted-foreground" />}>{session.optimization.map(item => `${item.subject} ${item.change}`).join(" · ")}</TooltipTrigger><TooltipContent id={optimizationTooltipId} role="tooltip" className="max-w-sm"><ul className="flex flex-col gap-1">{session.optimization.map((item, index) => <li key={`${item.subject}:${index}`} className="break-all">{item.subject} {item.change}</li>)}</ul></TooltipContent></Tooltip>}</div>
      <div className="activity-session-counts">{active && <span className="activity-running">{operations.filter(item => item.status === "running").length} running</span>}
      <span className="text-xs text-muted-foreground">{operations.length === session.operations ? `${operations.length} ops` : `${operations.length} shown / ${session.operations} ops`}</span></div>
    </div>
    {update.error && <p role="alert" className="px-4 text-sm text-destructive">Could not update session: {update.error.message}</p>}
    {open && <div className="activity-session-operations"><ActivityOperations operations={operations} now={now} /></div>}
  </section>
}

export function ActivityFeed({ sessions, operations, now }: { sessions: SessionSummary[]; operations: OperationSummary[]; now: number }) {
  const [expansion, setExpansion] = useState<Record<number, boolean>>({})
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
  const matches = (operation: OperationSummary, candidate: { status?: string[]; kind?: string[]; named?: boolean; starred?: boolean; agent?: string[]; cwd?: string[]; target?: string[]; archived?: boolean } = {}) => {
    const session = sessionById.get(operation.sessionId)
    return (time === "all" || Date.parse(operation.startedAt) >= now - Number(time))
      && (!(candidate.agent ?? agent).length || (candidate.agent ?? agent).includes(operation.agentKind))
      && (!(candidate.cwd ?? cwd).length || (session?.cwd !== null && session?.cwd !== undefined && (candidate.cwd ?? cwd).includes(session.cwd)))
      && (!(candidate.target ?? target).length || (candidate.target ?? target).includes(targetKey(operation)))
      && (!(candidate.starred ?? starred) || !!session?.starred)
      && ((candidate.archived ?? archived) || !session?.archived)
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
  const forceOpen = !!(text || status.length || kind.length || named || target.length)
  const isOpen = (group: typeof groups[number]) => expansion[group.session.id] ?? (forceOpen || group.operations.some(item => item.status === "running"))
  const allOpen = groups.length > 0 && groups.every(isOpen)
  const dirty = time !== "604800000" || !!(agent.length || cwd.length || target.length || status.length || kind.length || text || starred || archived || named)
  const facetOptions = (dimension: "agent" | "cwd" | "target", values: ReturnType<typeof options>) => values.map(item => ({ ...item, count: operations.filter(operation => matches(operation, { [dimension]: [item.value] })).length }))
  const chip = (label: string, count: number, pressed: boolean, onClick: () => void) => <Toggle variant="outline" pressed={pressed} onPressedChange={onClick}>{label}<span className="text-muted-foreground tabular-nums">{count}</span></Toggle>
  return <section aria-label="Activity feed" className="flex flex-col gap-3">
    <div className="activity-filterbar" aria-label="Activity filters">
      <h1>Activity</h1>
      <InputGroup className="activity-search"><InputGroupAddon><HugeiconsIcon icon={SearchIcon} strokeWidth={2} /></InputGroupAddon><InputGroupInput aria-label="Search activity" placeholder="Search label, session, target" value={text} onChange={event => { setText(event.target.value); setExpansion({}) }} /></InputGroup>
      <TimeFilter label="Time" value={time} onChange={setTime} values={[{ value: "all", label: "All time" }, { value: "604800000", label: "Last 7 days" }, { value: "86400000", label: "Last 24 hours" }, { value: "3600000", label: "Last hour" }]} />
      <Facet label="Agent" selected={agent} onChange={setAgent} values={facetOptions("agent", options([...operations.map(operation => operation.agentKind), ...sessions.map(session => session.agentKind)]))} />
      <Facet label="Directory" selected={cwd} onChange={setCwd} values={facetOptions("cwd", options(sessions.map(session => session.cwd)))} />
      <Facet label="Target" selected={target} onChange={values => { setTarget(values); setExpansion({}) }} values={facetOptions("target", options(operations.map(targetKey)))} />
      {chip("Starred", operations.filter(operation => matches(operation, { starred: true })).length, starred, () => setStarred(!starred))}
      <Toggle variant="outline" pressed={archived} onPressedChange={setArchived}>Include archived</Toggle>
    </div>
    <div className="activity-chipbar">
      <div className="activity-chipgroup"><span>Status</span>{["running", "failed", "rejected", "succeeded", "abandoned"].map(value => <span key={value}>{chip(value[0].toUpperCase() + value.slice(1), operations.filter(operation => matches(operation, { status: [value] })).length, status.includes(value), () => { setStatus(toggle(status, value)); setExpansion({}) })}</span>)}</div>
      <div className="activity-chipgroup"><span>Type</span>{[...new Set(operations.map(operation => operation.operation))].sort().map(value => <span key={value}>{chip(value[0].toUpperCase() + value.slice(1), operations.filter(operation => matches(operation, { kind: [value] })).length, kind.includes(value), () => { setKind(toggle(kind, value)); setExpansion({}) })}</span>)}</div>
      <div className="activity-chipgroup"><span>Marked</span>{chip("Named", operations.filter(operation => matches(operation, { named: true })).length, named, () => { setNamed(!named); setExpansion({}) })}</div>
      {dirty && <Button variant="link" size="sm" onClick={() => { setTime("604800000"); setAgent([]); setCwd([]); setTarget([]); setStatus([]); setKind([]); setText(""); setStarred(false); setArchived(false); setNamed(false); setExpansion({}) }}>Reset filters</Button>}
      <Button variant="link" size="sm" className="ml-auto" onClick={() => setExpansion(Object.fromEntries(groups.map(group => [group.session.id, !allOpen])))}>{allOpen ? "Collapse all" : "Expand all"}</Button>
    </div>
    <div className="border-t">{groups.map(group => <SessionGroup key={group.session.id} {...group} now={now} expanded={isOpen(group)} onExpand={() => setExpansion(current => ({ ...current, [group.session.id]: !isOpen(group) }))} />)}{ungrouped.length > 0 && <ActivityOperations operations={ungrouped} now={now} />}{!filtered.length && !groups.length && <p className="p-10 text-center text-sm text-muted-foreground">Nothing matches these filters.</p>}</div>

  </section>
}
