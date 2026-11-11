import { ComparisonPending } from "./ComparisonPending"
import { versionMetrics } from "./versionMetrics"
import { OutcomeDimensions } from "./CaseOutcomeDetails"
import { Badge } from "@/components/ui/badge"
import { cn } from "@/lib/utils"
import { useId, type ReactNode } from "react"
import { buttonVariants, Button } from "@/components/ui/button"
import { Popover, PopoverTrigger, PopoverContent, PopoverTitle } from "@/components/ui/popover"
import type { AgentSessionKey, OperationSummary, OptimizationVersion, OptimizationComparison } from "@/api/types"
import { Link } from "@tanstack/react-router"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SessionSearch } from "@/router"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { HugeiconsIcon } from "@hugeicons/react";
import { MoreHorizontalCircle01Icon, SparklesIcon } from "@hugeicons/core-free-icons";

function metric(value: number | null): string {
  return value === null ? "—" : value.toLocaleString("en-US")
}

export function VersionTable({ subject, sessionId, agentSessionKey, sessionSearch, versions, selectedA, selectedB, onSelect, operations = [], groupA, groupB, comparison, pending = false }: {
  comparison?: OptimizationComparison; pending?: boolean; operations?: OperationSummary[]; subject: string; sessionId: number; agentSessionKey?: AgentSessionKey; sessionSearch: SessionSearch; versions: (OptimizationVersion & { group?: { fingerprint: string; label: string; badge: string } })[]; groupA?: string; groupB?: string; selectedA: string; selectedB: string | null; onSelect: (side: "a" | "b", version: string, group?: string) => void
}) {
  const groupId = useId()
  const isA = (version: typeof versions[number]) => version.version === selectedA && (!groupA || version.group?.fingerprint === groupA)
  const isB = (version: typeof versions[number]) => version.version === selectedB && (!groupB || version.group?.fingerprint === groupB)
  const accessibleName = (side: string, version: typeof versions[number]) => `${side} ${version.version}${versions.filter(item => item.version === version.version).length > 1 ? ` (${version.group?.badge}: ${version.group?.label})` : ""}`
  const selectedCase = selectedB ? sessionSearch.case ?? "all" : "all"
  const aggregate = selectedB ? sessionSearch.agg ?? "sum" : "sum"
  const timing = selectedCase === "all" ? `${aggregate === "mean" ? "mean" : "sum"} of case medians` : "median"
  const matchingComparison = !!comparison && comparison.a === selectedA && comparison.b === selectedB
    && (!comparison.groupA || comparison.groupA === groupA) && (!comparison.groupB || comparison.groupB === groupB)
    && comparison.selectedCase === selectedCase && comparison.aggregate === aggregate
  const updating = pending || (!!comparison && !matchingComparison)
  const activeComparison = matchingComparison && !updating ? comparison : undefined
  const metricsFor = (version: typeof versions[number]) => {
    if (selectedB && (isA(version) || isB(version))) {
      const before = isA(version)
      const selectedKeys = activeComparison ? activeComparison.selectedCase === "all" ? activeComparison.cases.filter(item => item.id !== "all").map(item => item.id) : [activeComparison.selectedCase] : []
      const caseRows = (activeComparison?.caseRows ?? []).filter(row => selectedKeys.includes(row.caseKey))
      const complete = selectedKeys.length > 0 && selectedKeys.every(key => caseRows.filter(row => row.caseKey === key).length === 1)
      const rowsVaries = complete && caseRows.some(row => before ? row.rowsAVaries : row.rowsBVaries)
      const rowValues = caseRows.map(row => before ? row.rowsA : row.rowsB)
      const rows = !complete || rowsVaries || rowValues.some(value => value == null) ? null : rowValues.reduce<number>((sum, value) => sum + value!, 0) / (aggregate === "mean" ? caseRows.length : 1)
      return { reads: (before ? activeComparison?.wholeBatch?.before : activeComparison?.wholeBatch?.after) ?? null, scans: (before ? activeComparison?.scansBefore : activeComparison?.scansAfter) ?? null,
        elapsed: (before ? activeComparison?.elapsed.beforeMs : activeComparison?.elapsed.afterMs) ?? null, cpu: (before ? activeComparison?.cpuBeforeMs : activeComparison?.cpuAfterMs) ?? null,
        rows, rowsVaries, coverage: activeComparison ? `Selected pair${activeComparison.operationId != null ? ` #${activeComparison.operationId}` : " · separate observations"}${activeComparison.coverageLabel ? ` · ${activeComparison.coverageLabel}` : ""}` : updating ? "Updating comparison…" : "Pair evidence unavailable" }
    }
    const otherConditions = !!groupA && version.group?.fingerprint !== groupA
    const own = versionMetrics(version, otherConditions ? "all" : selectedCase, otherConditions ? "sum" : aggregate)
    return { ...own, rows: selectedCase === "all" || otherConditions ? version.rowsReturnedSum ?? null : null, rowsVaries: (selectedCase === "all" || otherConditions) && !!version.rowsReturnedVaries,
      coverage: `Latest own observation #${version.latestOperationId} · ${own.coverage}` }
  }
  const baseline = versions.find(isA)
  const baselineMetrics = baseline ? metricsFor(baseline) : undefined
  const delta = (value: number | null, reference: number | null | undefined) => value === null || reference === null || reference === undefined || reference === 0 ? null : `${value > reference ? "+" : ""}${Math.round((value - reference) * 100 / reference)}%`
  return <Table className="version-table" aria-label={`${subject} versions`}>
    <TableHeader><TableRow><TableHead>Version</TableHead><TableHead>Conditions</TableHead><TableHead className="text-right">Rows</TableHead><TableHead className="text-right">Logical reads</TableHead><TableHead className="text-right">vs A</TableHead><TableHead className="text-right">Scans</TableHead><TableHead className="text-right">Elapsed ({timing})</TableHead><TableHead className="text-right">CPU ({timing})</TableHead><TableHead>Results</TableHead><TableHead>IO</TableHead><TableHead>Policy</TableHead><TableHead id={`${groupId}-a-label`}><span aria-hidden="true">A</span><span className="sr-only">Baseline A</span></TableHead><TableHead id={`${groupId}-b-label`}><span aria-hidden="true">B</span><span className="sr-only">Candidate B</span></TableHead><TableHead className="w-10"><span className="sr-only">Observed in</span></TableHead></TableRow></TableHeader>
    <TableBody>{versions.map(version => {
      const values = metricsFor(version)
      const differentConditions = !!groupA && version.group?.fingerprint !== groupA
      const pairRow = !!selectedB && (isA(version) || isB(version))
      const rowPending = pairRow && updating
      const activePair = isB(version) && !!activeComparison
      const outcomes = pairRow ? activeComparison?.outcomes : version.outcomes
      const caseOutcomes = activePair ? activeComparison?.caseOutcomes : []
      const coverageCases = pairRow ? activeComparison?.caseOutcomes : undefined
      const coverage = pairRow ? coverageCases ? { available: coverageCases.filter(row => (isA(version) ? row.readsBefore : row.readsAfter) != null).length, total: coverageCases.length } : undefined : version.metricCases ? { available: version.metricCases.filter(row => row.logicalReads != null).length, total: version.metricCases.length } : undefined
      const evidence = (children: ReactNode, announce = false) => <ComparisonPending pending={rowPending} announce={announce}>{children}</ComparisonPending>
      return <TableRow key={`${version.group?.fingerprint ?? ""}:${version.version}`} className={isA(version) || isB(version) ? "bg-muted/40" : undefined}>
      <TableHead scope="row" className="max-w-80"><div className="flex items-center gap-2">{version.version}{operations.some(operation => version.measuredIn.includes(operation.id) && operation.agentKeep) && <HugeiconsIcon icon={SparklesIcon} strokeWidth={2} className="size-3.5 text-muted-foreground" aria-label="Kept by agent" />}{operations.filter(operation => version.measuredIn.includes(operation.id) && operation.agentLabel).map(operation => <span key={operation.id} className="truncate text-xs font-normal text-muted-foreground" title={operation.agentLabel ?? undefined}>{operation.agentLabel}</span>)}</div></TableHead>
      <TableCell><Badge variant="secondary" title={version.group?.label}>Conditions {version.group?.badge ?? "1"}</Badge>{differentConditions && <span className="block text-xs text-muted-foreground">Different conditions{!isB(version) && "; own cases (sum)"}</span>}<span className="block text-xs text-muted-foreground">{values.coverage}</span></TableCell>
      <TableCell className="text-right tabular-nums">{evidence(values.rowsVaries ? "varies" : metric(values.rows), true)}</TableCell><TableCell className="text-right tabular-nums">{evidence(metric(values.reads))}</TableCell><TableCell className="text-right tabular-nums" title="↓ fewer reads; ↑ in IO outcomes means improvement">{evidence(!activePair || differentConditions ? "—" : delta(values.reads, baselineMetrics?.reads) ?? "—")}</TableCell>
      <TableCell className="text-right tabular-nums">{evidence(metric(values.scans))}</TableCell><TableCell className="text-right tabular-nums">{evidence(<>{metric(values.elapsed)}{values.elapsed !== null && " ms"}</>)}</TableCell><TableCell className="text-right tabular-nums">{evidence(<>{metric(values.cpu)}{values.cpu !== null && " ms"}</>)}</TableCell>
      {(["results", "io", "policy"] as const).map(dimension => <TableCell key={dimension}>{evidence(<OutcomeDimensions outcomes={rowPending ? undefined : outcomes} cases={rowPending ? [] : caseOutcomes} baseline={isA(version)} coverageOnly={!activePair} coverage={rowPending ? undefined : coverage} dimension={dimension} />)}</TableCell>)}

      <TableCell><label className="relative inline-flex"><input type="radio" className="peer sr-only" value={version.version} name={`${groupId}-a`} aria-describedby={`${groupId}-a-label`} aria-label={accessibleName("A", version)} checked={isA(version)} onChange={() => onSelect("a", version.version, version.group?.fingerprint)} /><span aria-hidden="true" className={cn(buttonVariants({ variant: isA(version) ? "secondary" : "outline", size: "icon-sm" }), "rounded-full peer-focus-visible:ring-2 peer-focus-visible:ring-ring")}>A</span></label></TableCell>
      <TableCell><label className="relative inline-flex"><input type="radio" className="peer sr-only" value={version.version} name={`${groupId}-b`} aria-describedby={`${groupId}-b-label`} aria-label={accessibleName("B", version)} checked={isB(version)} onChange={() => onSelect("b", version.version, version.group?.fingerprint)} /><span aria-hidden="true" className={cn(buttonVariants({ variant: isB(version) ? "default" : "outline", size: "icon-sm" }), "rounded-full peer-focus-visible:ring-2 peer-focus-visible:ring-ring")}>B</span></label></TableCell>
      <TableCell><Popover><PopoverTrigger render={<Button variant="ghost" size="icon-sm" />} aria-label={`Details for ${version.version}`}><HugeiconsIcon icon={MoreHorizontalCircle01Icon} strokeWidth={2} /></PopoverTrigger><PopoverContent align="end"><PopoverTitle>{version.version}</PopoverTitle><p>basis: {version.verdictBasis}</p><p>Scans: {metric(version.scansSum)}</p>{version.measuredIn.map(operationId => <Link key={operationId} to="/operations/$id" params={{ id: String(operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="block underline">Operation #{operationId}</Link>)}</PopoverContent></Popover></TableCell>
    </TableRow>})}</TableBody>
  </Table>
}
