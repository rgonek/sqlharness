import { Link, useNavigate, useSearch } from "@tanstack/react-router"
import type { OptimizationSubject, ScopeVersions } from "@/api/types"
import { useSessionOptimization, useSessionOptimizationComparison } from "@/api/queries"
import { ComparisonPanel } from "@/components/optimization/ComparisonPanel"
import { VersionTable } from "@/components/optimization/VersionTable"
import { Button } from "@/components/ui/button"
import { operationReturnSearch } from "@/lib/sessionSearch"

function subjectKey(subject: OptimizationSubject): string {
  return `${subject.subject}\u0000${subject.database}`
}

function scopeOrder(scope: ScopeVersions): number {
  return scope.scope === "whole query" ? 0 : 1
}

export function OptimizationSection({ sessionId }: { sessionId: number }) {
  const search = useSearch({ from: "/sessions/$id" })
  const navigate = useNavigate({ from: "/sessions/$id" })
  const query = useSessionOptimization(sessionId)
  const section = query.data
  const subjects = section?.subjects ?? []
  const selectedSubject = subjects.find(subject => subject.subject === search.subject && subject.database === (search.db ?? "")) ?? subjects[0]
  const groups = selectedSubject?.groups ?? []
  const group = groups.find(item => item.fingerprint === search.group) ?? groups[0]
  const scopes = [...(group?.scopes ?? [])].sort((left, right) => scopeOrder(left) - scopeOrder(right) || left.scope.localeCompare(right.scope))
  const scope = scopes.find(item => item.scope === search.scope) ?? scopes[0]
  const versions = scope?.versions ?? []
  const hasVersion = (version: string | undefined) => version !== undefined && versions.some(item => item.version === version)
  const selectedA = hasVersion(search.a) ? search.a! : scope?.defaultA ?? group?.defaultA ?? versions[0]?.version ?? ""
  const selectedB = hasVersion(search.b) ? search.b! : scope?.defaultB ?? group?.defaultB ?? versions.at(-1)?.version ?? selectedA
  const caseId = search.case ?? "all"
  const aggregate = search.agg ?? "sum"
  const compareQuery = useSessionOptimizationComparison(sessionId, selectedSubject && group && scope && selectedA && selectedB ? {
    subject: selectedSubject.subject, database: selectedSubject.database, group: group.fingerprint, scope: scope.scope,
    a: selectedA, b: selectedB, case: caseId, aggregate,
  } : null)

  function update(patch: Record<string, string | undefined>, reset = false) {
    void navigate({ search: current => ({ ...current, ...(reset ? { a: undefined, b: undefined, case: undefined, agg: undefined } : {}), ...patch }), replace: false })
  }

  if (query.isPending) return <section aria-labelledby="optimization-heading" className="space-y-3"><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><p role="status">Loading optimization history…</p></section>
  if (query.error) return <section aria-labelledby="optimization-heading" className="space-y-3"><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><div role="alert" className="flex flex-wrap items-center gap-3"><span>Could not load optimization history: {query.error.message}</span><Button variant="outline" onClick={() => void query.refetch()}>Retry</Button></div></section>

  return <section aria-labelledby="optimization-heading" className="space-y-4">
    <div><h2 id="optimization-heading" className="font-heading text-lg font-medium">Optimization</h2><p className="text-sm text-muted-foreground">Compare retained SQL versions within their conditions and scope.</p></div>
    {subjects.length > 1 && <label className="grid max-w-lg gap-1 text-sm">Subject and database
      <select aria-label="Subject and database" value={selectedSubject ? subjectKey(selectedSubject) : ""} onChange={event => {
        const next = subjects.find(item => subjectKey(item) === event.target.value)
        if (next) update({ subject: next.subject, db: next.database, group: next.groups[0]?.fingerprint, scope: next.groups[0]?.scopes[0]?.scope }, true)
      }}>{subjects.map(item => <option key={subjectKey(item)} value={subjectKey(item)}>{item.subject} · {item.database || "(no database)"}</option>)}</select>
    </label>}
    {selectedSubject && group ? <div className="rounded-lg border p-3">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-3"><div><h3 className="font-medium">{selectedSubject.subject}</h3><p className="text-sm text-muted-foreground">{selectedSubject.database || "(no database)"} · {selectedSubject.engine}</p></div></div>
      {groups.length > 1 && <label className="mb-3 grid max-w-xl gap-1 text-sm">Conditions group
        <select aria-label="Conditions group" value={group.fingerprint} onChange={event => {
          const next = groups.find(item => item.fingerprint === event.target.value)
          if (next) update({ subject: selectedSubject.subject, db: selectedSubject.database, group: next.fingerprint, scope: next.scopes[0]?.scope }, true)
        }}>{groups.map(item => <option key={item.fingerprint} value={item.fingerprint}>{item.label}</option>)}</select>
      </label>}
      {scopes.length > 1 && <label className="mb-3 grid max-w-md gap-1 text-sm">Scope
        <select aria-label="Scope" value={scope?.scope ?? ""} onChange={event => {
          const next = scopes.find(item => item.scope === event.target.value)
          if (next) update({ subject: selectedSubject.subject, db: selectedSubject.database, group: group.fingerprint, scope: next.scope }, true)
        }}>{scopes.map(item => <option key={item.scope} value={item.scope}>{item.scope}</option>)}</select>
      </label>}
      <p className="mb-2 text-sm font-medium">{scope?.scope ?? "Scope"}</p>
      {versions.length > 0 ? <VersionTable subject={`${selectedSubject.subject}-${selectedSubject.database}`} versions={versions} selectedA={selectedA} selectedB={selectedB}
        onSelect={(side, version) => update({ subject: selectedSubject.subject, db: selectedSubject.database, group: group.fingerprint, scope: scope?.scope, a: selectedA, b: selectedB, [side]: version })} /> : <p className="text-sm text-muted-foreground">No retained versions for this scope.</p>}
      {compareQuery.isPending && <p role="status" className="mt-3">Loading comparison…</p>}
      {compareQuery.error && <p role="alert" className="mt-3">Could not load comparison: {compareQuery.error.message}</p>}
      {compareQuery.data && scope && <div className="mt-4"><ComparisonPanel comparison={compareQuery.data} sessionId={sessionId} subject={selectedSubject.subject} scope={scope.scope} versions={versions} engine={selectedSubject.engine}
        sessionSearch={search} searchState={{ case: search.case, agg: search.agg }} onSelectionChange={patch => update(patch)} /></div>}
    </div> : <p className="text-sm text-muted-foreground">No assigned optimization compares in this session.</p>}
    {!!section?.unassigned.length && <details className="rounded-lg border p-3" open={search.unassigned}>
      <summary className="cursor-pointer font-medium">Unassigned compares ({section.unassigned.length})</summary>
      <p className="mt-2 text-sm text-muted-foreground">These compares do not have a complete subject and scope, so they are not included in version comparisons.</p>
      <ol className="divide-y">{section.unassigned.map(item => <li key={item.operationId} className="flex flex-wrap gap-3 py-2"><Link to="/operations/$id" params={{ id: String(item.operationId) }} search={operationReturnSearch(sessionId, { ...search, unassigned: true })}>Compare #{item.operationId}</Link><span>{item.status}</span><time className="text-muted-foreground">{item.startedAt}</time></li>)}</ol>
    </details>}
  </section>
}
