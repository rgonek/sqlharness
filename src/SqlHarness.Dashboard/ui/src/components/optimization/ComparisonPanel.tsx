import { useState } from "react"
import { Link } from "@tanstack/react-router"
import type { OptimizationActionReference, OptimizationComparison, ScopeVersions } from "@/api/types"
import { CopyButton } from "@/components/CopyButton"
import { ReproScriptButtons } from "@/components/ReproScriptButtons"
import { SessionVersionDiff } from "@/components/SessionVersionDiff"
import { Button } from "@/components/ui/button"
import { conditionsDifferMessage, optimizationMarkdown } from "@/lib/optimizationMarkdown"
import { badgeTier } from "@/lib/flags"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SqlVersionOption } from "@/lib/compare"
import type { SessionSearch } from "@/router"

function basisLabel(comparison: OptimizationComparison): string {
  switch (comparison.basis) {
    case "MeasuredTogether": return `measured together in #${comparison.operationId}`
    case "DifferentRunsSameConditions": return "different runs, same conditions"
    case "ConditionsDiffer": return "conditions differ"
    case "SameSql": return "same SQL"
  }
}

function actionCase(action: OptimizationActionReference | null | undefined, selectedCase: string): string | null {
  if (!action?.caseIds.length) return null
  return action.caseIds.includes(selectedCase) ? selectedCase : action.caseIds[0]
}

function VersionAction({ label, action, selectedCase, sessionId, sessionSearch }: {
  label: string; action: OptimizationActionReference | null | undefined; selectedCase: string; sessionId: number; sessionSearch: SessionSearch
}) {
  const caseId = actionCase(action, selectedCase)
  if (!action || !caseId) return null
  return <section className="space-y-1" aria-label={`${label} actions`}>
    <h3 className="text-sm font-medium">{label} · {action.side}</h3>
    <div className="flex flex-wrap items-center gap-2">
      <Link to="/operations/$id" params={{ id: String(action.operationId) }} search={operationReturnSearch(sessionId, sessionSearch)} className="text-sm underline">Plans for {label}</Link>
      <ReproScriptButtons operationId={action.operationId} side={action.side} caseId={caseId} />
    </div>
  </section>
}

export function ComparisonPanel({ comparison, sessionId, subject, scope, versions = [], engine = null, searchState, sessionSearch = {}, onSelectionChange, hideCaseSelector = false }: {
  comparison: OptimizationComparison
  sessionId: number
  subject: string
  scope: string
  versions?: ScopeVersions["versions"]
  engine?: string | null
  sessionSearch?: SessionSearch
  searchState?: { case?: string; agg?: string }
  onSelectionChange?: (patch: { case?: string; agg?: string }) => void
  hideCaseSelector?: boolean
}) {
  const activeCase = searchState?.case ?? comparison.selectedCase
  const activeAggregate = searchState?.agg ?? comparison.aggregate
  const scriptCase = activeCase === "all" ? comparison.cases[0]?.id ?? "all" : activeCase
  const [showDiff, setShowDiff] = useState(false)
  const sqlOptions: SqlVersionOption[] = versions.map(version => ({ version: version.version, hash: version.hash }))
  function updateSearch(patch: { case?: string; agg?: string }) {
    onSelectionChange?.(patch)
  }

  return <section aria-label="Version comparison" className="space-y-4 rounded-lg border p-4">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="rounded-full border px-2 py-1 text-xs">{basisLabel(comparison)}</span>
        <span className="text-sm">{comparison.equivalenceLabel}</span>
      </div>
      <div className="flex items-center gap-2">
        <CopyButton label="Copy summary" getText={() => optimizationMarkdown(comparison, subject, scope)} />
        <Button type="button" variant="outline" size="sm" disabled={sqlOptions.length < 2} aria-expanded={showDiff} onClick={() => setShowDiff(value => !value)}>{showDiff ? "Hide SQL diff" : "SQL diff"}</Button>
      </div>
    </div>

    <div className="flex flex-wrap items-center gap-2" aria-label="Cases">
      {!hideCaseSelector && <><span className="text-sm font-medium">Case</span>
        <Button type="button" size="sm" variant={activeCase === "all" ? "secondary" : "outline"} aria-pressed={activeCase === "all"} onClick={() => updateSearch({ case: "all" })}>All cases</Button>
        {comparison.cases.filter(option => option.id !== "all").map(option => <Button key={option.id} type="button" size="sm" variant={activeCase === option.id ? "secondary" : "outline"} aria-pressed={activeCase === option.id} onClick={() => updateSearch({ case: option.id })}>{option.label}</Button>)}</>}
      <span className="ml-2 text-sm font-medium">Aggregate</span>
      <Button type="button" size="sm" variant={activeAggregate === "sum" ? "secondary" : "outline"} aria-pressed={activeAggregate === "sum"} onClick={() => updateSearch({ agg: "sum" })}>Sum</Button>
      <Button type="button" size="sm" variant={activeAggregate === "mean" ? "secondary" : "outline"} aria-pressed={activeAggregate === "mean"} onClick={() => updateSearch({ agg: "mean" })}>Mean</Button>
    </div>
    {comparison.coverageLabel && <p className="text-sm text-muted-foreground">{comparison.coverageLabel}</p>}
    {comparison.mixed && <p data-tier={badgeTier("mixed")} className="text-sm text-amber-700 dark:text-amber-300">Mixed: {comparison.mixed.better} better, {comparison.mixed.worse} worse — {comparison.mixed.worstCaseLabel}: logical reads {comparison.mixed.worstChangePercent > 0 ? "+" : ""}{Math.round(comparison.mixed.worstChangePercent)}%</p>}
    {comparison.basis === "ConditionsDiffer" ? <p className="text-sm text-muted-foreground">{conditionsDifferMessage}</p> : <>
      {comparison.tableIoUnavailable ? <p className="text-sm text-muted-foreground">per-table IO not available for this engine</p> : <div className="overflow-x-auto">
        <table className="w-full text-sm"><thead><tr className="border-b text-left"><th className="py-2 pr-3">Table</th><th className="px-3 py-2">Scans before</th><th className="px-3 py-2">Scans after</th><th className="px-3 py-2">Logical reads before</th><th className="px-3 py-2">Logical reads after</th><th className="px-3 py-2">Change</th></tr></thead>
          <tbody>{comparison.tables.map(row => <tr key={row.table} className="border-b"><th scope="row" className="py-2 pr-3 text-left font-normal">{row.table}</th><td className="px-3 py-2">{row.scansBefore ?? "—"}</td><td className="px-3 py-2">{row.scansAfter ?? "—"}</td><td className="px-3 py-2">{row.readsBefore ?? "—"}</td><td className="px-3 py-2">{row.readsAfter ?? "—"}</td><td className="px-3 py-2">{row.change}</td></tr>)}
            {comparison.collapsed && <tr><td colSpan={6} className="py-2 text-muted-foreground">{comparison.collapsed.label}</td></tr>}</tbody></table>
      </div>}
      <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm"><dt className="font-medium">Elapsed (median)</dt><dd>{comparison.elapsed.beforeMs ?? "—"} ms → {comparison.elapsed.afterMs ?? "—"} ms</dd>{comparison.elapsed.policyVerdict && <dd title="Policy v2">{comparison.elapsed.policyVerdict}</dd>}</dl>
    </>}
    {comparison.operationId !== null ? <Link to="/operations/$id" params={{ id: String(comparison.operationId) }} search={operationReturnSearch(sessionId, sessionSearch)} className="text-sm underline">Operation #{comparison.operationId}</Link>
      : <div className="flex flex-wrap gap-3"><VersionOperationLink action={comparison.beforeAction} sessionId={sessionId} sessionSearch={sessionSearch} /><VersionOperationLink action={comparison.afterAction} sessionId={sessionId} sessionSearch={sessionSearch} /></div>}
    <div className="grid gap-3 sm:grid-cols-2"><VersionAction label={comparison.a} action={comparison.beforeAction} selectedCase={scriptCase} sessionId={sessionId} sessionSearch={sessionSearch} /><VersionAction label={comparison.b} action={comparison.afterAction} selectedCase={scriptCase} sessionId={sessionId} sessionSearch={sessionSearch} /></div>
    {showDiff && sqlOptions.length > 1 && <SessionVersionDiff key={`${comparison.a}:${comparison.b}`} sessionId={sessionId} engine={engine} options={sqlOptions} initialFrom={comparison.a} initialTo={comparison.b} />}
  </section>
}

function VersionOperationLink({ action, sessionId, sessionSearch }: { action: OptimizationActionReference | null | undefined; sessionId: number; sessionSearch: SessionSearch }) {
  return action ? <Link to="/operations/$id" params={{ id: String(action.operationId) }} search={operationReturnSearch(sessionId, sessionSearch)} className="text-sm underline">Operation #{action.operationId}</Link> : null
}
