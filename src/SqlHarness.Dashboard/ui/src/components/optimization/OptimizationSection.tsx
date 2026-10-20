import { Link, useNavigate, useSearch } from "@tanstack/react-router"
import { useId } from "react"
import type { OptimizationSubject, ScopeVersions } from "@/api/types"
import type { AgentSessionKey } from "@/api/types"
import { useAgentSessionOptimization, useAgentSessionOptimizationComparison, useSessionOptimization, useSessionOptimizationComparison } from "@/api/queries"
import { ComparisonPanel } from "@/components/optimization/ComparisonPanel"
import { VersionTable } from "@/components/optimization/VersionTable"
import { Button } from "@/components/ui/button"
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SessionSearch } from "@/router"

function subjectKey(subject: OptimizationSubject): string {
  return `${subject.subject}\u0000${subject.database}`
}

function scopeOrder(scope: ScopeVersions): number {
  return scope.scope === "whole query" ? 0 : 1
}

export function OptimizationSection({ sessionId, agentSessionKey }: { sessionId: number; agentSessionKey?: AgentSessionKey }) {
  const search = useSearch({ strict: false }) as SessionSearch
  const query = useSessionOptimization(sessionId, !agentSessionKey)
  const agentQuery = useAgentSessionOptimization(agentSessionKey ?? { agentKind: "", conversationId: "" }, !!agentSessionKey)
  const activeQuery = agentSessionKey ? agentQuery : query
  if (activeQuery.isPending) return <section aria-labelledby="optimization-heading" className="space-y-3"><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><p role="status">Loading optimization history…</p></section>
  if (activeQuery.error) return <section aria-labelledby="optimization-heading" className="space-y-3"><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><div role="alert" className="flex flex-wrap items-center gap-3"><span>Could not load optimization history: {activeQuery.error.message}</span><Button variant="outline" onClick={() => void activeQuery.refetch()}>Retry</Button></div></section>

  const section = activeQuery.data
  if (!section) return null
  return <section aria-labelledby="optimization-heading" className="space-y-4">
    <div><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><p className="text-sm text-muted-foreground">Compare retained SQL versions and review measurements within their conditions and scope.</p></div>
    {section.subjects.map(subject => <SubjectCard key={subjectKey(subject)} sessionId={sessionId} agentSessionKey={agentSessionKey} search={search} subject={subject} />)}
    {!!section.unassigned.length && <Collapsible className="rounded-lg border p-3">
      <CollapsibleTrigger className="cursor-pointer font-medium">Unassigned compares ({section.unassigned.length})</CollapsibleTrigger>
      <CollapsibleContent keepMounted><p className="mt-2 text-sm text-muted-foreground">These compares do not have a complete subject and scope, so they are not included in version comparisons.</p>
      <ol className="divide-y">{section.unassigned.map(item => <li key={item.operationId} className="flex flex-wrap gap-3 py-2"><Link to="/operations/$id" params={{ id: String(item.operationId) }} search={operationReturnSearch(sessionId, { ...search, unassigned: true }, agentSessionKey)}>Compare #{item.operationId}</Link><span>{item.status}</span><time className="text-muted-foreground">{item.startedAt}</time></li>)}</ol></CollapsibleContent>
    </Collapsible>}
  </section>
}

function SubjectCard({ sessionId, agentSessionKey, search, subject }: { sessionId: number; agentSessionKey?: AgentSessionKey; search: SessionSearch; subject: OptimizationSubject }) {
  const navigate = useNavigate({ from: agentSessionKey ? "/agent-sessions/$kind/$id" : "/sessions/$id" } as never)
  const controlId = useId()
  const matchesSubject = search.subject === subject.subject && (search.db ?? "") === subject.database
  const groups = subject.groups
  const group = (matchesSubject && groups.find(item => item.fingerprint === search.group)) || groups[0]
  const scopes = [...(group?.scopes ?? [])].sort((left, right) => scopeOrder(left) - scopeOrder(right) || left.scope.localeCompare(right.scope))
  const scope = (matchesSubject && scopes.find(item => item.scope === search.scope)) || scopes[0]
  const matchesCard = matchesSubject && (!search.group || search.group === group?.fingerprint) && (!search.scope || search.scope === scope?.scope)
  const versions = scope?.versions ?? []
  const hasVersion = (version: string | undefined) => version !== undefined && versions.some(item => item.version === version)
  const selectedA = hasVersion(matchesCard ? search.a : undefined) ? search.a! : scope?.defaultA ?? versions[0]?.version ?? ""
  const selectedB = hasVersion(matchesCard ? search.b : undefined) ? search.b! : scope?.defaultB ?? ""
  const caseId = matchesCard ? search.case ?? "all" : "all"
  const aggregate = matchesCard ? search.agg ?? "sum" : "sum"
  const view = matchesCard ? search.view ?? "changed" : "changed"
  const selectedSearch: SessionSearch = { subject: subject.subject, db: subject.database, group: group?.fingerprint, scope: scope?.scope, a: selectedA || undefined, b: selectedB || undefined, case: caseId, agg: aggregate, view }
  const compareParams = group && scope && selectedA && selectedB ? {
    subject: subject.subject, database: subject.database, group: group.fingerprint, scope: scope.scope,
    a: selectedA, b: selectedB, case: caseId, aggregate, view,
  } : null
  const compareQuery = useSessionOptimizationComparison(sessionId, agentSessionKey ? null : compareParams)
  const agentCompareQuery = useAgentSessionOptimizationComparison(agentSessionKey ?? { agentKind: "", conversationId: "" }, agentSessionKey ? compareParams : null)
  const activeCompareQuery = agentSessionKey ? agentCompareQuery : compareQuery

  function update(patch: Record<string, string | undefined>, reset = false) {
    void navigate({ to: agentSessionKey ? "/agent-sessions/$kind/$id" : "/sessions/$id", params: agentSessionKey ? { kind: agentSessionKey.agentKind, id: agentSessionKey.conversationId } : { id: String(sessionId) }, search: (current: Record<string, unknown>) => ({ ...current, subject: subject.subject, db: subject.database, group: group?.fingerprint,
      scope: scope?.scope, ...(reset ? { a: undefined, b: undefined, case: undefined, agg: undefined, view: undefined } : {}), ...patch }), replace: false })
  }

  return <article aria-current={matchesCard ? "true" : undefined} className="space-y-3 rounded-lg border p-3">
    <div className="flex flex-wrap items-center justify-between gap-3"><div><h3 className="font-medium">{subject.subject}</h3><p className="text-sm text-muted-foreground">{subject.database || "(no database)"} · {subject.engine}</p></div>
      {!matchesCard && <Button type="button" variant="outline" size="sm" onClick={() => update({ a: undefined, b: undefined, case: undefined, agg: undefined, view: undefined })}>Select comparison</Button>}
    </div>
    {groups.length > 1 && group && <div className="grid max-w-xl gap-1 text-sm">
      <Label htmlFor={`${controlId}-group`}>Conditions group</Label>
      <Select items={groups.map(item => ({ value: item.fingerprint, label: item.label }))} value={group.fingerprint} onValueChange={fingerprint => {
        const next = groups.find(item => item.fingerprint === fingerprint)
        if (next) update({ group: next.fingerprint, scope: [...next.scopes].sort((left, right) => scopeOrder(left) - scopeOrder(right))[0]?.scope }, true)
      }}>
        <SelectTrigger id={`${controlId}-group`} aria-label={`${subject.subject} conditions group`} className="w-full">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>{groups.map(item => <SelectItem key={item.fingerprint} value={item.fingerprint}>{item.label}</SelectItem>)}</SelectContent>
      </Select>
    </div>}
    {scopes.length > 1 && group && scope && <div className="grid max-w-md gap-1 text-sm">
      <Label htmlFor={`${controlId}-scope`}>Scope</Label>
      <Select items={scopes.map(item => ({ value: item.scope, label: item.scope }))} value={scope.scope} onValueChange={selectedScope => {
        const next = scopes.find(item => item.scope === selectedScope)
        if (next) update({ scope: next.scope }, true)
      }}>
        <SelectTrigger id={`${controlId}-scope`} aria-label={`${subject.subject} scope`} className="w-full">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>{scopes.map(item => <SelectItem key={item.scope} value={item.scope}>{item.scope}</SelectItem>)}</SelectContent>
      </Select>
    </div>}
    {group && scope ? <>
      <p className="text-sm font-medium">{scope.scope}</p>
      {versions.length > 0 ? <VersionTable subject={`${subject.subject}-${subject.database}`} sessionId={sessionId} agentSessionKey={agentSessionKey} sessionSearch={selectedSearch}
        versions={versions} selectedA={selectedA} selectedB={selectedB || null}
        onSelect={(side, version) => update({ a: selectedA, b: selectedB || undefined, [side]: version })} /> : <p className="text-sm text-muted-foreground">No retained versions for this scope.</p>}
      {!selectedB && <p className="text-sm text-muted-foreground">Select a version for B to compare it with {selectedA}.</p>}
      {activeCompareQuery.isPending && <p role="status" className="mt-3">Loading comparison…</p>}
      {activeCompareQuery.error && <p role="alert" className="mt-3">Could not load comparison: {activeCompareQuery.error.message}</p>}
      {activeCompareQuery.data && <div className="mt-4"><ComparisonPanel comparison={activeCompareQuery.data} sessionId={sessionId} agentSessionKey={agentSessionKey} subject={subject.subject} scope={scope.scope} versions={versions} engine={subject.engine}
        sessionSearch={selectedSearch} searchState={{ case: caseId, agg: aggregate, view }} onSelectionChange={patch => update(patch)} /></div>}
    </> : <p className="text-sm text-muted-foreground">No assigned optimization compares in this session.</p>}
  </article>
}
