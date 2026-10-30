import { Link, useNavigate, useSearch } from "@tanstack/react-router"
import { Fragment, useEffect, useId, useState } from "react"
import { NotFoundError } from "@/api/client"
import type { OperationSummary, OptimizationSubject, ScopeVersions } from "@/api/types"
import type { AgentSessionKey } from "@/api/types"
import { useAgentSessionOptimization, useAgentSessionOptimizationComparison, useSessionOptimization, useSessionOptimizationComparison } from "@/api/queries"
import { ComparisonPanel } from "@/components/optimization/ComparisonPanel"
import { VersionTable } from "@/components/optimization/VersionTable"
import { Button } from "@/components/ui/button"
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SessionSearch } from "@/router"

function subjectKey(subject: OptimizationSubject): string {
  return `${subject.subject}\u0000${subject.database}`
}

function scopeOrder(scope: ScopeVersions): number {
  return scope.scope === "whole query" ? 0 : 1
}

export function OptimizationSection({ sessionId, agentSessionKey, operations = [] }: { sessionId: number; agentSessionKey?: AgentSessionKey; operations?: OperationSummary[] }) {
  const search = useSearch({ strict: false }) as SessionSearch
  const query = useSessionOptimization(sessionId, !agentSessionKey)
  const agentQuery = useAgentSessionOptimization(agentSessionKey ?? { agentKind: "", conversationId: "" }, !!agentSessionKey)
  const activeQuery = agentSessionKey ? agentQuery : query
  if (activeQuery.isPending) return <section aria-labelledby="optimization-heading" className="space-y-3"><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><p role="status">Loading optimization history…</p></section>
  if (activeQuery.error) return <section aria-labelledby="optimization-heading" className="space-y-3"><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><div role="alert" className="flex flex-wrap items-center gap-3"><span>Could not load optimization history: {activeQuery.error.message}</span><Button variant="outline" onClick={() => void activeQuery.refetch()}>Retry</Button></div></section>

  const section = activeQuery.data
  if (!section) return null
  return <section aria-labelledby="optimization-heading" className="space-y-4">
    <h2 id="optimization-heading" className="sr-only">Optimization</h2>
    {section.subjects.map(subject => <SubjectCard key={subjectKey(subject)} sessionId={sessionId} agentSessionKey={agentSessionKey} search={search} subject={subject} operations={operations} refetchOptimization={activeQuery.refetch} />)}
    {!!section.unassigned.length && <Collapsible className="rounded-lg border p-3">
      <CollapsibleTrigger className="cursor-pointer font-medium">Unassigned compares ({section.unassigned.length})</CollapsibleTrigger>
      <CollapsibleContent keepMounted><p className="mt-2 text-sm text-muted-foreground">These compares do not have a complete subject and scope, so they are not included in version comparisons.</p>
      <ol className="divide-y">{section.unassigned.map(item => <li key={item.operationId} className="flex flex-wrap gap-3 py-2"><Link to="/operations/$id" params={{ id: String(item.operationId) }} search={operationReturnSearch(sessionId, { ...search, unassigned: true }, agentSessionKey)}>Compare #{item.operationId}</Link><span>{item.status}</span><time className="text-muted-foreground">{item.startedAt}</time></li>)}</ol></CollapsibleContent>
    </Collapsible>}
  </section>
}

function SubjectCard({ sessionId, agentSessionKey, search, subject, operations, refetchOptimization }: { refetchOptimization: () => unknown; sessionId: number; agentSessionKey?: AgentSessionKey; search: SessionSearch; subject: OptimizationSubject; operations: OperationSummary[] }) {
  const navigate = useNavigate({ from: agentSessionKey ? "/agent-sessions/$kind/$id" : "/sessions/$id" } as never)
  const controlId = useId()
  const [comparisonTab, setComparisonTab] = useState("diff")
  const [toolbarTarget, setToolbarTarget] = useState<HTMLDivElement | null>(null)
  const matchesSubject = search.subject === subject.subject && (search.db ?? "") === subject.database
  const groups = subject.groups
  const group = (matchesSubject && groups.find(item => item.fingerprint === search.group)) || groups[0]
  const scopes = [...new Map(groups.flatMap(item => item.scopes).map(item => [item.scope, item])).values()].sort((left, right) => scopeOrder(left) - scopeOrder(right) || left.scope.localeCompare(right.scope))
  const scope = (matchesSubject && scopes.find(item => item.scope === search.scope)) || group?.scopes.slice().sort((left, right) => scopeOrder(left) - scopeOrder(right) || left.scope.localeCompare(right.scope))[0] || scopes[0]
  const scopeOf = (item: typeof groups[number]) => item.scopes.find(value => value.scope === scope?.scope)
  const versions = groups.flatMap((item, index) => (scopeOf(item)?.versions ?? []).map(row => ({ ...row, group: { fingerprint: item.fingerprint, label: item.label, badge: String.fromCharCode(65 + index) } }))).sort((left, right) => left.version.localeCompare(right.version, undefined, { numeric: true }))
  const matchesCard = matchesSubject && (!search.group || search.group === group?.fingerprint) && (!search.scope || search.scope === scope?.scope)
  const groupScope = group && scopeOf(group)
  const aGroup = group && groupScope ? group : groups.find(item => scopeOf(item)?.versions.length)
  const aScope = aGroup && scopeOf(aGroup)
  const hasVersion = (fingerprint: string | undefined, version: string | undefined) => !!fingerprint && !!version && versions.some(item => item.group.fingerprint === fingerprint && item.version === version)
  const selectedA = hasVersion(aGroup?.fingerprint, matchesCard ? search.a : undefined) ? search.a! : aScope?.defaultA ?? aScope?.versions[0]?.version ?? ""
  const requestedBGroup = matchesCard && search.groupB ? groups.find(item => item.fingerprint === search.groupB) : aGroup
  const validB = hasVersion(requestedBGroup?.fingerprint, matchesCard ? search.b : undefined)
  const bGroup = validB ? requestedBGroup : aGroup
  const selectedB = validB ? search.b! : aScope?.defaultB ?? ""
  const caseId = matchesCard ? search.case ?? "all" : "all"
  const aggregate = matchesCard ? search.agg ?? "sum" : "sum"
  const view = matchesCard ? search.view ?? "statements" : "statements"
  const selectedSearch: SessionSearch = { subject: subject.subject, db: subject.database, group: aGroup?.fingerprint, groupB: bGroup?.fingerprint !== aGroup?.fingerprint ? bGroup?.fingerprint : undefined, scope: scope?.scope, a: selectedA || undefined, b: selectedB || undefined, case: caseId, agg: aggregate, view }
  const compareParams = aGroup && scope && selectedA && selectedB ? {
    subject: subject.subject, database: subject.database, group: aGroup.fingerprint, groupB: selectedSearch.groupB, scope: scope.scope,
    a: selectedA, b: selectedB, case: caseId, aggregate, view,
  } : null
  const compareQuery = useSessionOptimizationComparison(sessionId, agentSessionKey ? null : compareParams)
  const agentCompareQuery = useAgentSessionOptimizationComparison(agentSessionKey ?? { agentKind: "", conversationId: "" }, agentSessionKey ? compareParams : null)
  const activeCompareQuery = agentSessionKey ? agentCompareQuery : compareQuery
  const activeQueryRefetch = refetchOptimization

  function update(patch: Record<string, string | undefined>, reset = false) {
    void navigate({ to: agentSessionKey ? "/agent-sessions/$kind/$id" : "/sessions/$id", params: agentSessionKey ? { kind: agentSessionKey.agentKind, id: agentSessionKey.conversationId } : { id: String(sessionId) }, search: (current: Record<string, unknown>) => ({ ...current, subject: subject.subject, db: subject.database, group: aGroup?.fingerprint, groupB: selectedSearch.groupB,
      scope: scope?.scope, ...(reset ? { a: undefined, b: undefined, groupB: undefined, case: undefined, agg: undefined, view: undefined } : {}), ...patch }), replace: false, resetScroll: false })
  }

  useEffect(() => {
    if (!matchesCard || (!search.groupB && !search.b) || validB) return
    update({ groupB: undefined, a: hasVersion(aGroup?.fingerprint, search.a) ? search.a : undefined, b: undefined, case: undefined, agg: undefined, view: undefined })
  }, [matchesCard, search.groupB, search.b, validB])

  useEffect(() => {
    if (!(activeCompareQuery.error instanceof NotFoundError) || !matchesCard || (!search.groupB && !search.b)) return
    void activeQueryRefetch()
    update({ groupB: undefined, a: hasVersion(aGroup?.fingerprint, search.a) ? search.a : undefined, b: undefined, case: undefined, agg: undefined, view: undefined })
  }, [activeCompareQuery.error])

  return <article aria-current={matchesCard ? "true" : undefined} className="optimization-card">
    <div className="flex flex-wrap items-center justify-between gap-3"><div><div className="text-base font-semibold">Optimization</div><p className="mt-1 text-sm text-muted-foreground">{[subject.subject, subject.database, subject.engine, scope?.scope].map(value => value?.trim()).filter((value): value is string => !!value).map((value, index) => <Fragment key={index}>{index > 0 && " · "}{index === 0 && subject.subject.trim() ? <span role="heading" aria-level={3}>{value}</span> : <span>{value}</span>}</Fragment>)}</p></div>
      <div className="flex max-w-full flex-wrap items-center gap-2">
        <div ref={setToolbarTarget} />
      </div>
    </div>
    {scopes.length > 1 && group && scope && <div className="grid max-w-md gap-1 text-sm">
      <Label htmlFor={`${controlId}-scope`}>Scope</Label>
      <Select items={scopes.map(item => ({ value: item.scope, label: item.scope }))} value={scope.scope} onValueChange={selectedScope => {
        const next = scopes.find(item => item.scope === selectedScope)
        if (next) update({ scope: next.scope }, true)
      }}>
        <SelectTrigger id={`${controlId}-scope`} aria-label={`${subject.subject} scope`} className="w-full">
          <SelectValue />
        </SelectTrigger>
        <SelectContent><SelectGroup>{scopes.map(item => <SelectItem key={item.scope} value={item.scope}>{item.scope}</SelectItem>)}</SelectGroup></SelectContent>
      </Select>
    </div>}
    {group && scope ? <>

      {versions.length > 0 ? <VersionTable subject={`${subject.subject}-${subject.database}`} sessionId={sessionId} agentSessionKey={agentSessionKey} sessionSearch={selectedSearch}
        versions={versions} comparison={activeCompareQuery.isPlaceholderData ? undefined : activeCompareQuery.data} operations={operations} selectedA={selectedA} selectedB={selectedB || null} groupA={aGroup?.fingerprint} groupB={bGroup?.fingerprint}
        onSelect={(side, version, fingerprint) => side === "a"
          ? update({ group: fingerprint, groupB: selectedB && bGroup?.fingerprint !== fingerprint ? bGroup?.fingerprint : undefined, a: version, b: selectedB || undefined })
          : update({ group: aGroup?.fingerprint, groupB: fingerprint !== aGroup?.fingerprint ? fingerprint : undefined, a: selectedA, b: version })} /> : <p className="text-sm text-muted-foreground">No retained versions for this scope.</p>}
      {!selectedB && <p className="text-sm text-muted-foreground">Select a version for B to compare it with {selectedA}.</p>}
      {activeCompareQuery.isPending && <p role="status" className="mt-3">Loading comparison…</p>}
      {activeCompareQuery.error && <p role="alert" className="mt-3">Could not load comparison: {activeCompareQuery.error.message}</p>}
      {activeCompareQuery.data && <div className="optimization-comparison"><ComparisonPanel toolbarTarget={toolbarTarget} pending={activeCompareQuery.isPlaceholderData} activeTab={comparisonTab} onTabChange={setComparisonTab} comparison={activeCompareQuery.data} sessionId={sessionId} agentSessionKey={agentSessionKey} subject={subject.subject} scope={scope.scope} versions={[versions.find(item => item.group.fingerprint === aGroup?.fingerprint && item.version === selectedA), versions.find(item => item.group.fingerprint === bGroup?.fingerprint && item.version === selectedB)].filter((item): item is typeof versions[number] => !!item)} engine={subject.engine}
        sessionSearch={selectedSearch} searchState={{ case: caseId, agg: aggregate, view }} onSelectionChange={patch => update(patch)} /></div>}
    </> : <p className="text-sm text-muted-foreground">No assigned optimization compares in this session.</p>}
  </article>
}
