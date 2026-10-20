import { Fragment, useState } from "react"
import { Link } from "@tanstack/react-router"
import type { AgentSessionKey, OptimizationActionReference, OptimizationComparison, ScopeVersions } from "@/api/types"
import { CopyButton } from "@/components/CopyButton"
import { ReproScriptButtons } from "@/components/ReproScriptButtons"
import { SessionVersionDiff } from "@/components/SessionVersionDiff"
import { Button } from "@/components/ui/button"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { conditionsDifferMessage, optimizationMarkdown, optimizationMetricRows } from "@/lib/optimizationMarkdown"
import { badgeTier } from "@/lib/flags"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SqlVersionOption } from "@/lib/compare"
import type { SessionSearch } from "@/router"
import { StatementHiddenWork, StatementRows } from "./StatementRows"

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
  const mappedCaseId = action.caseIdsByFingerprint?.[selectedCase]
  if (mappedCaseId) return mappedCaseId
  return action.caseIds.includes(selectedCase) ? selectedCase : action.caseIds[0]
}

function VersionAction({ label, action, selectedCase, sessionId, sessionSearch, agentSessionKey }: {
  label: string; action: OptimizationActionReference | null | undefined; selectedCase: string; sessionId: number; sessionSearch: SessionSearch; agentSessionKey?: AgentSessionKey
}) {
  const caseId = actionCase(action, selectedCase)
  if (!action || !caseId) return null
  return <section className="space-y-1" aria-label={`${label} actions`}>
    <h3 className="text-sm font-medium">{label} · {action.side}</h3>
    <div className="flex flex-wrap items-center gap-2">
      <Link to="/operations/$id" params={{ id: String(action.operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="text-sm underline">{action.side === "measure" ? `Measure for ${label}` : `Plans for ${label}`}</Link>
      <ReproScriptButtons operationId={action.operationId} side={action.side === "measure" ? "query" : action.side} caseId={caseId} />
    </div>
  </section>
}

export function ComparisonPanel({ comparison, sessionId, agentSessionKey, subject, scope, versions = [], engine = null, searchState, sessionSearch = {}, onSelectionChange, hideCaseSelector = false }: {
  comparison: OptimizationComparison
  sessionId: number
  agentSessionKey?: AgentSessionKey
  subject: string
  scope: string
  versions?: ScopeVersions["versions"]
  engine?: string | null
  sessionSearch?: SessionSearch
  searchState?: { case?: string; agg?: string; view?: "changed" | "whole" | "statements" }
  onSelectionChange?: (patch: { case?: string; agg?: string; view?: "changed" | "whole" | "statements" }) => void
  hideCaseSelector?: boolean
}) {
  const activeCase = searchState?.case ?? comparison.selectedCase
  const activeAggregate = searchState?.agg ?? comparison.aggregate
  const activeView = comparison.view ?? searchState?.view ?? "changed"
  const scriptCase = activeCase === "all" ? comparison.cases[0]?.id ?? "all" : activeCase
  const [showDiff, setShowDiff] = useState(false)
  const sqlOptions: SqlVersionOption[] = versions.map(version => ({ version: version.version, hash: version.hash }))
  function updateSearch(patch: { case?: string; agg?: string; view?: "changed" | "whole" | "statements" }) {
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

    <div className="flex flex-wrap items-center gap-2" aria-label="Statement views">
      <Button type="button" size="sm" variant={activeView === "changed" ? "secondary" : "outline"} aria-pressed={activeView === "changed"} onClick={() => updateSearch({ view: "changed" })}>Changed statements</Button>
      <Button type="button" size="sm" variant={activeView === "whole" ? "secondary" : "outline"} aria-pressed={activeView === "whole"} onClick={() => updateSearch({ view: "whole" })}>Whole batch</Button>
      <Button type="button" size="sm" variant={activeView === "statements" ? "secondary" : "outline"} aria-pressed={activeView === "statements"} onClick={() => updateSearch({ view: "statements" })}>By statement</Button>
    </div>
    {comparison.wholeBatch?.label && <div className="space-y-2 rounded-md bg-muted/40 p-3">
      <p className="text-sm font-medium">{comparison.wholeBatch.label}</p>
      <StatementHiddenWork rows={comparison.statements ?? []} />
    </div>}
    <p className="text-sm">basis: {comparison.verdictBasis ?? "whole batch"}</p>
    {comparison.fallbackNote && <p className="text-sm text-muted-foreground">{comparison.fallbackNote}</p>}
    {((comparison.omittedStatementsBefore ?? 0) > 0 || (comparison.omittedStatementsAfter ?? 0) > 0) && <div className="space-y-1 text-sm text-muted-foreground">
      {(comparison.omittedStatementsBefore ?? 0) > 0 && <p>omitted statements: {comparison.omittedStatementsBefore} (their reads remain in the batch total)</p>}
      {(comparison.omittedStatementsAfter ?? 0) > 0 && <p>omitted statements: {comparison.omittedStatementsAfter} (their reads remain in the batch total)</p>}
    </div>}

    <div className="flex flex-wrap items-center gap-2" aria-label="Cases">
      {!hideCaseSelector && <><span className="text-sm font-medium">Case</span>
        <Button type="button" size="sm" variant={activeCase === "all" ? "secondary" : "outline"} aria-pressed={activeCase === "all"} onClick={() => updateSearch({ case: "all" })}>All cases</Button>
        {comparison.cases.filter(option => option.id !== "all").map(option => <Button key={option.id} type="button" size="sm" variant={activeCase === option.id ? "secondary" : "outline"} aria-pressed={activeCase === option.id} onClick={() => updateSearch({ case: option.id })}>{option.label}</Button>)}</>}
      <span className="ml-2 text-sm font-medium">Aggregate</span>
      <Button type="button" size="sm" variant={activeAggregate === "sum" ? "secondary" : "outline"} aria-pressed={activeAggregate === "sum"} onClick={() => updateSearch({ agg: "sum" })}>Sum</Button>
      <Button type="button" size="sm" variant={activeAggregate === "mean" ? "secondary" : "outline"} aria-pressed={activeAggregate === "mean"} onClick={() => updateSearch({ agg: "mean" })}>Mean</Button>
    </div>
    {comparison.coverageLabel && <p className="text-sm text-muted-foreground">{comparison.coverageLabel}</p>}
    {comparison.versionLabelsProvisional && comparison.versionLabelNote && <p role="note" className="text-sm text-muted-foreground">{comparison.versionLabelNote}</p>}
    {comparison.mixed && <p data-tier={badgeTier("mixed")} className="text-sm text-amber-700 dark:text-amber-300">Mixed: {comparison.mixed.better} better, {comparison.mixed.worse} worse — {comparison.mixed.worstCaseLabel}: logical reads {comparison.mixed.worstChangePercent > 0 ? "+" : ""}{Math.round(comparison.mixed.worstChangePercent)}%</p>}
    {comparison.basis === "ConditionsDiffer" ? <p className="text-sm text-muted-foreground">{conditionsDifferMessage}</p> : <>
      {activeView === "statements" || activeView === "changed"
        ? <StatementRows rows={comparison.statements ?? []} postgres={comparison.tableIoUnavailable ?? false} />
        : comparison.tableIoUnavailable ? <p className="text-sm text-muted-foreground">per-table IO not available for this engine</p> : <Table>
        <TableHeader><TableRow><TableHead>Table</TableHead><TableHead>Scans before</TableHead><TableHead>Scans after</TableHead><TableHead>Logical reads before</TableHead><TableHead>Logical reads after</TableHead><TableHead>Change</TableHead></TableRow></TableHeader>
        <TableBody>{comparison.tables.map(row => <TableRow key={row.table}><TableHead scope="row" className="whitespace-normal">{row.table}</TableHead><TableCell>{row.scansBefore ?? "—"}</TableCell><TableCell>{row.scansAfter ?? "—"}</TableCell><TableCell>{row.readsBefore ?? "—"}</TableCell><TableCell>{row.readsAfter ?? "—"}</TableCell><TableCell className="whitespace-normal">{row.change}</TableCell></TableRow>)}
          {comparison.collapsed && <TableRow><TableCell colSpan={6} className="whitespace-normal text-muted-foreground">{comparison.collapsed.label}</TableCell></TableRow>}</TableBody>
      </Table>}
      <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm"><dt className="font-medium">Elapsed (median)</dt><dd>{comparison.elapsed.beforeMs ?? "—"} ms → {comparison.elapsed.afterMs ?? "—"} ms</dd>{comparison.elapsed.policyVerdict && <dd title="Policy v2">{comparison.elapsed.policyVerdict}</dd>}
        {optimizationMetricRows(comparison).map(row => <Fragment key={row.label}><dt className="font-medium">{row.label}</dt><dd>{row.before} → {row.after}</dd></Fragment>)}
      </dl>
    </>}
    {comparison.operationId !== null ? <Link to="/operations/$id" params={{ id: String(comparison.operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="text-sm underline">Operation #{comparison.operationId}</Link>
      : <div className="flex flex-wrap gap-3"><VersionOperationLink action={comparison.beforeAction} sessionId={sessionId} sessionSearch={sessionSearch} agentSessionKey={agentSessionKey} /><VersionOperationLink action={comparison.afterAction} sessionId={sessionId} sessionSearch={sessionSearch} agentSessionKey={agentSessionKey} /></div>}
    <div className="grid gap-3 sm:grid-cols-2"><VersionAction label={comparison.a} action={comparison.beforeAction} selectedCase={scriptCase} sessionId={sessionId} sessionSearch={sessionSearch} agentSessionKey={agentSessionKey} /><VersionAction label={comparison.b} action={comparison.afterAction} selectedCase={scriptCase} sessionId={sessionId} sessionSearch={sessionSearch} agentSessionKey={agentSessionKey} /></div>
    {showDiff && sqlOptions.length > 1 && <SessionVersionDiff key={`${comparison.a}:${comparison.b}`} sessionId={sessionId} agentSessionKey={agentSessionKey} engine={engine} options={sqlOptions} initialFrom={comparison.a} initialTo={comparison.b} />}
  </section>
}

function VersionOperationLink({ action, sessionId, sessionSearch, agentSessionKey }: { action: OptimizationActionReference | null | undefined; sessionId: number; sessionSearch: SessionSearch; agentSessionKey?: AgentSessionKey }) {
  return action ? <Link to="/operations/$id" params={{ id: String(action.operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="text-sm underline">Operation #{action.operationId}</Link> : null
}
