import { ComparisonPending } from "./ComparisonPending"
import { Separator } from "@/components/ui/separator"
import { Popover, PopoverTrigger, PopoverContent, PopoverTitle } from "@/components/ui/popover"
import { SegmentedRadioGroup } from "@/components/SegmentedRadioGroup"
import { Fragment, useState, type ReactNode } from "react"
import { Link } from "@tanstack/react-router"
import type { AgentSessionKey, OptimizationActionReference, OptimizationComparison, ScopeVersions } from "@/api/types"
import { CopyButton } from "@/components/CopyButton"
import { ReproScriptButtons } from "@/components/ReproScriptButtons"
import { SessionVersionDiff } from "@/components/SessionVersionDiff"
import { Button } from "@/components/ui/button"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { IoDetails } from "./IoDetails"
import { VersionPlan } from "./VersionPlan"
import { conditionsDifferMessage, optimizationMarkdown, optimizationMetricRows } from "@/lib/optimizationMarkdown"
import { badgeTier } from "@/lib/flags"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SqlVersionOption } from "@/lib/compare"
import type { SessionSearch } from "@/router"
import { StatementHiddenWork } from "./StatementRows"
import { HugeiconsIcon } from "@hugeicons/react";
import { ArrowDown01Icon } from "@hugeicons/core-free-icons";

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

export function ComparisonPanel({ pending = false, comparison, sessionId, agentSessionKey, subject, scope, versions = [], engine = null, searchState, sessionSearch = {}, onSelectionChange, hideCaseSelector = false, diff, timing, details, activeTab, onTabChange, navigation, headerContent }: {
  comparison: OptimizationComparison
  pending?: boolean
  navigation?: ReactNode
  headerContent?: ReactNode
  activeTab?: string
  onTabChange?: (value: string) => void
  diff?: ReactNode
  timing?: ReactNode
  details?: ReactNode
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
  const [selectorsOpen, setSelectorsOpen] = useState(false)
  const activeCase = searchState?.case ?? comparison.selectedCase
  const activeAggregate = searchState?.agg ?? comparison.aggregate
  const activeView = comparison.fallbackNote ? comparison.view ?? "whole" : searchState?.view ?? comparison.view ?? "statements"
  const scriptCase = activeCase === "all" ? comparison.cases[0]?.id ?? "all" : activeCase
  const sqlOptions: SqlVersionOption[] = versions.map(version => ({ version: version.version, hash: version.hash }))
  function updateSearch(patch: { case?: string; agg?: string; view?: "changed" | "whole" | "statements" }) {
    setSelectorsOpen(false)
    onSelectionChange?.(patch)
  }

  const copy = <div className="comparison-copy"><ComparisonPending pending={pending}><CopyButton label="Copy summary" getText={() => optimizationMarkdown(comparison, subject, scope)} /></ComparisonPending></div>
  const selectors = <Popover open={selectorsOpen && !pending} onOpenChange={open => setSelectorsOpen(pending ? false : open)}><PopoverTrigger render={<Button variant="outline" size="sm" />}>{activeCase === "all" ? "All cases" : comparison.cases.find(item => item.id === activeCase)?.label ?? activeCase} · {activeAggregate === "mean" ? "Mean" : "Sum"}<HugeiconsIcon icon={ArrowDown01Icon} strokeWidth={2} data-icon="inline-end" /></PopoverTrigger><PopoverContent align="end" className="max-h-80 max-w-[calc(100vw-2rem)] overflow-auto"><PopoverTitle>Comparison options</PopoverTitle><div className="flex flex-col gap-2" aria-label="Cases">
      {!hideCaseSelector && <><span className="text-sm font-medium">Case</span>
        <SegmentedRadioGroup label="Comparison case" value={activeCase} options={[{ value: "all", label: "All cases" }, ...comparison.cases.filter(option => option.id !== "all").map(option => ({ value: option.id, label: option.label }))]} onValueChange={value => updateSearch({ case: value })} /></>}
      <span className="ml-2 text-sm font-medium">Aggregate</span>
      <SegmentedRadioGroup label="Comparison aggregate" value={activeAggregate} options={[{ value: "sum", label: "Sum" }, { value: "mean", label: "Mean" }]} onValueChange={value => updateSearch({ agg: value })} />
    </div>
    </PopoverContent></Popover>
  const context = <div className="comparison-context"><span>{comparison.a} → {comparison.b}</span><span> · </span><span>{basisLabel(comparison)}</span><span> · </span><span>{comparison.equivalenceLabel}</span></div>

  return <section aria-label="Version comparison" className="comparison-panel" aria-busy={pending}>
    {!navigation && <div className="comparison-toolbar">{selectors}{copy}</div>}


    <ComparisonPending pending={pending} className="comparison-notices"><StatementHiddenWork rows={comparison.statements ?? []} />
    {comparison.fallbackNote && <p className="text-sm text-muted-foreground">{comparison.fallbackNote}</p>}
    {((comparison.omittedStatementsBefore ?? 0) > 0 || (comparison.omittedStatementsAfter ?? 0) > 0) && <div className="space-y-1 text-sm text-muted-foreground">
      {(comparison.omittedStatementsBefore ?? 0) > 0 && <p>omitted statements: {comparison.omittedStatementsBefore} (their reads remain in the batch total)</p>}
      {(comparison.omittedStatementsAfter ?? 0) > 0 && <p>omitted statements: {comparison.omittedStatementsAfter} (their reads remain in the batch total)</p>}
    </div>}


    {comparison.coverageLabel && <p className="text-sm text-muted-foreground">{comparison.coverageLabel}</p>}
    {comparison.versionLabelsProvisional && comparison.versionLabelNote && <p role="note" className="text-sm text-muted-foreground">{comparison.versionLabelNote}</p>}
    {comparison.mixed && <p data-tier={badgeTier("mixed")} className="text-sm text-amber-700 dark:text-amber-300">Mixed: {comparison.mixed.better} better, {comparison.mixed.worse} worse — {comparison.mixed.worstCaseLabel}: logical reads {comparison.mixed.worstChangePercent > 0 ? "+" : ""}{Math.round(comparison.mixed.worstChangePercent)}%</p>}
    {comparison.basis === "ConditionsDiffer" && <p className="text-sm text-muted-foreground">{conditionsDifferMessage}</p>}
    </ComparisonPending>
      <Tabs className="comparison-tabs" value={activeTab} onValueChange={value => onTabChange?.(String(value))} defaultValue="diff">
        <Separator />
        <TabsList variant="line"><TabsTrigger value="diff">SQL diff</TabsTrigger><TabsTrigger value="io">IO</TabsTrigger><TabsTrigger value="plan">Plan</TabsTrigger>{timing && <TabsTrigger value="timing">Timing</TabsTrigger>}{details && <TabsTrigger value="details">Details</TabsTrigger>}</TabsList><Separator />{navigation ?? <ComparisonPending pending={pending} className="mx-4">{context}</ComparisonPending>}
        {headerContent && <div className="comparison-header-content">{headerContent}</div>}
        {navigation && <div className="comparison-operation-context"><ComparisonPending pending={pending}>{context}</ComparisonPending><div className="comparison-operation-actions">{selectors}{copy}</div></div>}
        <TabsContent value="diff"><ComparisonPending pending={pending} announce>{diff ?? (sqlOptions.length > 1 ? <SessionVersionDiff key={`${comparison.a}:${comparison.b}`} sessionId={sessionId} agentSessionKey={agentSessionKey} engine={engine} options={sqlOptions} initialFrom={comparison.a} initialTo={comparison.b} selectionControls={false} /> : <p className="text-sm text-muted-foreground">SQL diff requires two retained SQL versions.</p>)}</ComparisonPending></TabsContent>
        <TabsContent value="io">{comparison.basis === "ConditionsDiffer" ? <p className="text-sm text-muted-foreground">{conditionsDifferMessage}</p> : <IoDetails pending={pending} comparison={comparison} byStatement={activeView !== "whole"} onViewChange={view => updateSearch({ view })} />}</TabsContent>
        <TabsContent value="plan"><ComparisonPending pending={pending} announce><VersionPlan a={comparison.a} b={comparison.b} before={comparison.beforeAction} after={comparison.afterAction} engine={engine} selectedCase={scriptCase} /></ComparisonPending></TabsContent>
        {timing && <TabsContent value="timing">{timing}</TabsContent>}
        {details && <TabsContent value="details">{details}</TabsContent>}
      </Tabs>
      <ComparisonPending pending={pending}><details className="comparison-evidence"><summary>Comparison evidence and scripts</summary><div className="space-y-3 pt-3"><p className="text-sm">basis: {comparison.verdictBasis ?? "whole batch"}</p>{comparison.wholeBatch?.label && <p className="text-sm">{comparison.wholeBatch.label}</p>}
      {comparison.basis !== "ConditionsDiffer" && <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm"><dt className="font-medium">Elapsed (median)</dt><dd>{comparison.elapsed.beforeMs ?? "—"} ms → {comparison.elapsed.afterMs ?? "—"} ms</dd>{comparison.elapsed.policyVerdict && <dd title="Policy v2">{comparison.elapsed.policyVerdict}</dd>}
        {optimizationMetricRows(comparison).map(row => <Fragment key={row.label}><dt className="font-medium">{row.label}</dt><dd>{row.before} → {row.after}</dd></Fragment>)}
      </dl>}
    {comparison.operationId !== null ? <Link to="/operations/$id" params={{ id: String(comparison.operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="text-sm underline">Operation #{comparison.operationId}</Link>
      : <div className="flex flex-wrap gap-3"><VersionOperationLink action={comparison.beforeAction} sessionId={sessionId} sessionSearch={sessionSearch} agentSessionKey={agentSessionKey} /><VersionOperationLink action={comparison.afterAction} sessionId={sessionId} sessionSearch={sessionSearch} agentSessionKey={agentSessionKey} /></div>}
    <div className="grid gap-3 sm:grid-cols-2"><VersionAction label={comparison.a} action={comparison.beforeAction} selectedCase={scriptCase} sessionId={sessionId} sessionSearch={sessionSearch} agentSessionKey={agentSessionKey} /><VersionAction label={comparison.b} action={comparison.afterAction} selectedCase={scriptCase} sessionId={sessionId} sessionSearch={sessionSearch} agentSessionKey={agentSessionKey} /></div>

    </div></details></ComparisonPending>
  </section>
}

function VersionOperationLink({ action, sessionId, sessionSearch, agentSessionKey }: { action: OptimizationActionReference | null | undefined; sessionId: number; sessionSearch: SessionSearch; agentSessionKey?: AgentSessionKey }) {
  return action ? <Link to="/operations/$id" params={{ id: String(action.operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="text-sm underline">Operation #{action.operationId}</Link> : null
}
