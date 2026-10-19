import { Link, useNavigate, useSearch } from "@tanstack/react-router"
import type { OptimizationSubject, ScopeVersions } from "@/api/types"
import { useSessionOptimization, useSessionOptimizationComparison } from "@/api/queries"
import { ComparisonPanel } from "@/components/optimization/ComparisonPanel"
import { VersionTable } from "@/components/optimization/VersionTable"
import { Button } from "@/components/ui/button"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SessionSearch } from "@/router"

function subjectKey(subject: OptimizationSubject): string {
  return `${subject.subject}\u0000${subject.database}`
}

function scopeOrder(scope: ScopeVersions): number {
  return scope.scope === "whole query" ? 0 : 1
}

export function OptimizationSection({ sessionId }: { sessionId: number }) {
  const search = useSearch({ from: "/sessions/$id" })
  const query = useSessionOptimization(sessionId)
  if (query.isPending) return <section aria-labelledby="optimization-heading" className="space-y-3"><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><p role="status">Loading optimization history…</p></section>
  if (query.error) return <section aria-labelledby="optimization-heading" className="space-y-3"><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><div role="alert" className="flex flex-wrap items-center gap-3"><span>Could not load optimization history: {query.error.message}</span><Button variant="outline" onClick={() => void query.refetch()}>Retry</Button></div></section>

  const section = query.data
  return <section aria-labelledby="optimization-heading" className="space-y-4">
    <div><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><p className="text-sm text-muted-foreground">Compare retained SQL versions and review measurements within their conditions and scope.</p></div>
    {section.subjects.map(subject => <SubjectCard key={subjectKey(subject)} sessionId={sessionId} subject={subject} />)}
    {!!section.unassigned.length && <details className="rounded-lg border p-3">
      <summary className="cursor-pointer font-medium">Unassigned compares ({section.unassigned.length})</summary>
      <p className="mt-2 text-sm text-muted-foreground">These compares do not have a complete subject and scope, so they are not included in version comparisons.</p>
      <ol className="divide-y">{section.unassigned.map(item => <li key={item.operationId} className="flex flex-wrap gap-3 py-2"><Link to="/operations/$id" params={{ id: String(item.operationId) }} search={operationReturnSearch(sessionId, { ...search, unassigned: true })}>Compare #{item.operationId}</Link><span>{item.status}</span><time className="text-muted-foreground">{item.startedAt}</time></li>)}</ol>
    </details>}
  </section>
}

function SubjectCard({ sessionId, subject }: { sessionId: number; subject: OptimizationSubject }) {
  const search = useSearch({ from: "/sessions/$id" })
  const navigate = useNavigate({ from: "/sessions/$id" })
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
  const compareQuery = useSessionOptimizationComparison(sessionId, group && scope && selectedA && selectedB ? {
    subject: subject.subject, database: subject.database, group: group.fingerprint, scope: scope.scope,
    a: selectedA, b: selectedB, case: caseId, aggregate, view,
  } : null)

  function update(patch: Record<string, string | undefined>, reset = false) {
    void navigate({ search: current => ({ ...current, subject: subject.subject, db: subject.database, group: group?.fingerprint,
      scope: scope?.scope, ...(reset ? { a: undefined, b: undefined, case: undefined, agg: undefined, view: undefined } : {}), ...patch }), replace: false })
  }

  return <article aria-current={matchesCard ? "true" : undefined} className="space-y-3 rounded-lg border p-3">
    <div className="flex flex-wrap items-center justify-between gap-3"><div><h3 className="font-medium">{subject.subject}</h3><p className="text-sm text-muted-foreground">{subject.database || "(no database)"} · {subject.engine}</p></div>
      {!matchesCard && <Button type="button" variant="outline" size="sm" onClick={() => update({ a: undefined, b: undefined, case: undefined, agg: undefined, view: undefined })}>Select comparison</Button>}
    </div>
    {groups.length > 1 && group && <label className="grid max-w-xl gap-1 text-sm">Conditions group
      <select aria-label={`${subject.subject} conditions group`} value={group.fingerprint} onChange={event => {
        const next = groups.find(item => item.fingerprint === event.target.value)
        if (next) update({ group: next.fingerprint, scope: [...next.scopes].sort((left, right) => scopeOrder(left) - scopeOrder(right))[0]?.scope }, true)
      }}>{groups.map(item => <option key={item.fingerprint} value={item.fingerprint}>{item.label}</option>)}</select>
    </label>}
    {scopes.length > 1 && group && scope && <label className="grid max-w-md gap-1 text-sm">Scope
      <select aria-label={`${subject.subject} scope`} value={scope.scope} onChange={event => {
        const next = scopes.find(item => item.scope === event.target.value)
        if (next) update({ scope: next.scope }, true)
      }}>{scopes.map(item => <option key={item.scope} value={item.scope}>{item.scope}</option>)}</select>
    </label>}
    {group && scope ? <>
      <p className="text-sm font-medium">{scope.scope}</p>
      {versions.length > 0 ? <VersionTable subject={`${subject.subject}-${subject.database}`} sessionId={sessionId} sessionSearch={selectedSearch}
        versions={versions} selectedA={selectedA} selectedB={selectedB || null}
        onSelect={(side, version) => update({ a: selectedA, b: selectedB || undefined, [side]: version })} /> : <p className="text-sm text-muted-foreground">No retained versions for this scope.</p>}
      {!selectedB && <p className="text-sm text-muted-foreground">Select a version for B to compare it with {selectedA}.</p>}
      {compareQuery.isPending && <p role="status" className="mt-3">Loading comparison…</p>}
      {compareQuery.error && <p role="alert" className="mt-3">Could not load comparison: {compareQuery.error.message}</p>}
      {compareQuery.data && <div className="mt-4"><ComparisonPanel comparison={compareQuery.data} sessionId={sessionId} subject={subject.subject} scope={scope.scope} versions={versions} engine={subject.engine}
        sessionSearch={selectedSearch} searchState={{ case: caseId, agg: aggregate, view }} onSelectionChange={patch => update(patch)} /></div>}
    </> : <p className="text-sm text-muted-foreground">No assigned optimization compares in this session.</p>}
  </article>
}
