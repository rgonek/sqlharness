import { Fragment, useMemo, useState } from "react"
import type { PlanGraph, PlanGraphNode, PlanOccurrenceManifest } from "@/api/types"
import { useOperationPlans, usePlanGraph, usePlanSourceMap, type PlanSourceMapParams } from "@/api/queries"
import type { DistilledPlan, PlanNode } from "@/api/types"
import { PlanErrorState } from "@/components/plan/PlanErrorState"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { ComparePair } from "@/lib/compare"
import { planChanges, type TopOperator } from "@/lib/planChanges"
import { formatNumber } from "@/lib/format"

type Side = "baseline" | "candidate"
type Choice = PlanOccurrenceManifest

const operators = (values: string[]) => values.length ? values.join(", ") : "—"
const metric = (value: number | null, unit: string) => value === null ? `unavailable ${unit}` : `${formatNumber(value)} ${unit}`

function choiceKey(choice: Choice): string {
  return JSON.stringify([choice.variant, choice.caseId, choice.parameterSet, choice.repetition, choice.documentOrdinal, choice.documentSha256, choice.statementIndex])
}

function choicesFor(occurrences: Choice[], side: Side, pair: ComparePair): Choice[] {
  const variant = side === "baseline" ? pair.baseline : pair.candidate
  return occurrences.filter(choice => choice.variant === variant.variant
    && choice.parameterSet === variant.parameterSet
    && choice.caseId === variant.caseId
    && choice.stored && !choice.omitted && choice.documentSha256 !== null)
    .sort((left, right) => left.repetition - right.repetition
      || left.documentOrdinal - right.documentOrdinal
      || left.statementIndex - right.statementIndex
      || (left.documentSha256 ?? "").localeCompare(right.documentSha256 ?? ""))
}

function selectionParams(choice: Choice | null): PlanSourceMapParams | null {
  return choice ? { variant: choice.variant, parameterSet: choice.parameterSet, repetition: choice.repetition, caseId: choice.caseId } : null
}

function objectName(node: PlanGraphNode): string | undefined {
  if (!node.object) return undefined
  const parts = [node.object.database, node.object.schema, node.object.table].filter((part): part is string => Boolean(part))
  return parts.length ? parts.join(".") : undefined
}

/** Adapts the renderer-neutral graph to the existing comparison reducer. */
function graphStatement(graph: PlanGraph | null | undefined, statementIndex: number): DistilledPlan | null {
  if (!graph) return null
  const statement = graph.statements.find(item => item.index === statementIndex)
  if (!statement) return null
  const nodes = new Map(graph.nodes.map(node => [node.id, node]))
  const children = new Map<string, string[]>()
  for (const edge of graph.edges) children.set(edge.source, [...(children.get(edge.source) ?? []), edge.target])
  const build = (id: string, ancestors = new Set<string>()): PlanNode | null => {
    const node = nodes.get(id)
    if (!node || ancestors.has(id)) return null
    const nextAncestors = new Set(ancestors).add(id)
    const childNodes = (children.get(id) ?? []).flatMap(childId => {
      const child = build(childId, nextAncestors)
      return child ? [child] : []
    })
    return {
      physicalOp: node.physicalOperator,
      ...(node.logicalOperator ? { logicalOp: node.logicalOperator } : {}),
      ...(objectName(node) ? { objectName: objectName(node) } : {}),
      ...(node.object?.index ? { indexName: node.object.index } : {}),
      ...(node.estimatedRows !== null ? { estimatedRows: node.estimatedRows } : {}),
      ...(node.actualRows !== null ? { actualRows: node.actualRows } : {}),
      ...(node.executions !== null ? { executions: node.executions } : {}),
      ...(node.costFraction !== null ? { costFraction: node.costFraction } : {}),
      ...(node.warnings.length ? { warnings: node.warnings } : {}),
      ...(childNodes.length ? { children: childNodes } : {}),
    }
  }
  const root = build(statement.rootNodeId)
  return root ? { statements: [{ root }] } : null
}

function TopOperators({ title, nodes }: { title: string; nodes: TopOperator[] }) {
  return <div>
    <h4 className="mb-1 text-sm font-medium">{title}</h4>
    {nodes.length === 0 ? <p className="text-sm text-muted-foreground">No operator cost data.</p> : <ol className="list-inside list-decimal text-sm">
      {nodes.map((node, index) => <li key={`${node.physicalOp}-${node.object ?? ""}-${index}`}>
        {node.physicalOp}{node.object ? ` · ${node.object}` : ""} · {formatNumber(node.costFraction * 100)}%
      </li>)}
    </ol>}
  </div>
}

function ExactOccurrenceSelect({
  side,
  options,
  selectedKey,
  onChange,
}: {
  side: Side
  options: Choice[]
  selectedKey: string
  onChange: (key: string) => void
}) {
  const sideName = side === "baseline" ? "Baseline" : "Candidate"
  const items = options.map(option => ({
    value: choiceKey(option),
    label: `Repetition ${option.repetition} · document ${option.documentOrdinal + 1} · statement ${option.statementIndex + 1} · ${(option.documentSha256 ?? "").slice(0, 8)}`,
  }))
  return <label className="grid gap-1 text-sm">{sideName} occurrence
    <Select items={items} value={selectedKey || null} onValueChange={value => onChange(typeof value === "string" ? value : "")}>
      <SelectTrigger aria-label={`${sideName} occurrence`} className="min-w-64 max-w-[min(32rem,80vw)]">
        <SelectValue placeholder="Choose exact occurrence" />
      </SelectTrigger>
      <SelectContent>{items.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}</SelectContent>
    </Select>
  </label>
}

function selectedChoice(options: Choice[], key: string): Choice | null {
  if (key) return options.find(option => choiceKey(option) === key) ?? null
  return options.length === 1 ? options[0] : null
}

function PlanChangeDetails({ before, after, beforeChoice, afterChoice, beforeSql, afterSql, label }: {
  before: DistilledPlan
  after: DistilledPlan
  beforeChoice: Choice
  afterChoice: Choice
  beforeSql: string | null
  afterSql: string | null
  label: string
}) {
  const changes = planChanges(before, after)
  const detailByObject = new Map(changes.accessDetails.map(detail => [detail.object, detail]))
  const statementLabel = `Statement ${beforeChoice.statementIndex + 1} plan changes`
  return <section role="region" aria-label={statementLabel} className="space-y-4 rounded border p-3">
    <header>
      <h3 className="font-medium">Statement {beforeChoice.statementIndex + 1}</h3>
      <p className="text-sm text-muted-foreground">{label}</p>
      <div className="mt-2 grid gap-2 text-xs sm:grid-cols-2">
        <p>Baseline · repetition {beforeChoice.repetition} · document {beforeChoice.documentOrdinal + 1}</p>
        <p>Candidate · repetition {afterChoice.repetition} · document {afterChoice.documentOrdinal + 1}</p>
      </div>
      <div className="mt-2 grid gap-2 sm:grid-cols-2">
        <pre className="whitespace-pre-wrap break-words rounded bg-muted/50 p-2 font-mono text-xs">{beforeSql ?? "Exact executed SQL span unavailable."}</pre>
        <pre className="whitespace-pre-wrap break-words rounded bg-muted/50 p-2 font-mono text-xs">{afterSql ?? "Exact executed SQL span unavailable."}</pre>
      </div>
    </header>
    <section aria-label={`Statement ${beforeChoice.statementIndex + 1} access changes`}>
      <h4 className="mb-2 text-sm font-medium">Access changes</h4>
      {changes.access.length === 0 ? <p className="text-sm text-muted-foreground">No access changes.</p> : <Table aria-label={`${label} Statement ${beforeChoice.statementIndex + 1} Access changes`}>
        <TableHeader><TableRow><TableHead>Object</TableHead><TableHead>Baseline</TableHead><TableHead>Candidate</TableHead></TableRow></TableHeader>
        <TableBody>{changes.access.map(access => {
          const details = detailByObject.get(access.object)!
          return <Fragment key={access.object}>
            <TableRow>
              <TableCell className="font-mono">{access.object}</TableCell>
              <TableCell>{operators(access.baseline)}</TableCell>
              <TableCell>{operators(access.candidate)}</TableCell>
            </TableRow>
            <TableRow>
              <TableCell className="text-muted-foreground">Actual rows / executions</TableCell>
              <TableCell>{details.baseline.map(node => `${node.physicalOp}: ${metric(node.actualRows, "rows")} / ${metric(node.executions, "executions")}`).join("; ") || "—"}</TableCell>
              <TableCell>{details.candidate.map(node => `${node.physicalOp}: ${metric(node.actualRows, "rows")} / ${metric(node.executions, "executions")}`).join("; ") || "—"}</TableCell>
            </TableRow>
          </Fragment>
        })}</TableBody>
      </Table>}
    </section>
    <section aria-label={`Statement ${beforeChoice.statementIndex + 1} warnings`}>
      <h4 className="mb-2 text-sm font-medium">Warnings</h4>
      {changes.warnings.length === 0 ? <p className="text-sm text-muted-foreground">No warning changes.</p> : <ul className="list-inside list-disc text-sm">
        {changes.warnings.map(warning => {
          const warningStatus = warning.baseline > 0 && warning.candidate === 0 ? "resolved" : warning.baseline === 0 && warning.candidate > 0 ? "introduced" : "changed"
          return <li key={warning.warning}>{warning.warning}: {warningStatus} ({formatNumber(warning.baseline)} → {formatNumber(warning.candidate)})</li>
        })}
      </ul>}
    </section>
    <div className="grid gap-4 sm:grid-cols-2"><TopOperators title="Top baseline operators" nodes={changes.topBaseline} /><TopOperators title="Top candidate operators" nodes={changes.topCandidate} /></div>
  </section>
}

export function PlanChanges({ operationId, pair }: { operationId: number; pair: ComparePair }) {
  const manifest = useOperationPlans(operationId)
  const [baselineKey, setBaselineKey] = useState("")
  const [candidateKey, setCandidateKey] = useState("")
  const beforeOptions = useMemo(() => choicesFor(manifest.data?.occurrences ?? [], "baseline", pair), [manifest.data?.occurrences, pair])
  const afterOptions = useMemo(() => choicesFor(manifest.data?.occurrences ?? [], "candidate", pair), [manifest.data?.occurrences, pair])
  const beforeChoice = selectedChoice(beforeOptions, baselineKey)
  const afterChoice = selectedChoice(afterOptions, candidateKey)
  const sameDocument = beforeChoice !== null && afterChoice !== null
    && beforeChoice.documentSha256 === afterChoice.documentSha256
    && beforeChoice.statementIndex === afterChoice.statementIndex
  const beforeSource = usePlanSourceMap(operationId, sameDocument ? null : selectionParams(beforeChoice))
  const afterSource = usePlanSourceMap(operationId, sameDocument ? null : selectionParams(afterChoice))
  const beforeGraph = usePlanGraph(beforeChoice?.documentSha256 ?? "", beforeChoice !== null && !sameDocument)
  const afterGraph = usePlanGraph(afterChoice?.documentSha256 ?? "", afterChoice !== null && !sameDocument)

  let content
  if (manifest.isPending) content = <Skeleton className="h-28 w-full" />
  else if (manifest.error) content = <PlanErrorState error={manifest.error} />
  else if (manifest.data.state === "plans not stored") content = <p className="text-sm text-muted-foreground">Plans not stored (journal.storeSensitive is off).</p>
  else {
    const baselineUnavailable = beforeOptions.length === 0
    const candidateUnavailable = afterOptions.length === 0
    const beforeGraphError = beforeGraph.error ?? beforeGraph.data?.error
    const afterGraphError = afterGraph.error ?? afterGraph.data?.error
    const beforeSourceError = beforeSource.error
    const afterSourceError = afterSource.error
    const beforePlan = graphStatement(beforeGraph.data?.graph, beforeChoice?.statementIndex ?? -1)
    const afterPlan = graphStatement(afterGraph.data?.graph, afterChoice?.statementIndex ?? -1)
    const beforeSql = beforeChoice && beforeSource.data?.executedText !== null && beforeSource.data?.executedText !== undefined
      && beforeChoice.spanStart !== null && beforeChoice.spanLength !== null
      ? beforeSource.data.executedText.slice(beforeChoice.spanStart, beforeChoice.spanStart + beforeChoice.spanLength)
      : null
    const afterSql = afterChoice && afterSource.data?.executedText !== null && afterSource.data?.executedText !== undefined
      && afterChoice.spanStart !== null && afterChoice.spanLength !== null
      ? afterSource.data.executedText.slice(afterChoice.spanStart, afterChoice.spanStart + afterChoice.spanLength)
      : null

    content = <div className="space-y-4">
      <div className="flex flex-wrap gap-3">
        <ExactOccurrenceSelect side="baseline" options={beforeOptions} selectedKey={beforeChoice ? choiceKey(beforeChoice) : ""} onChange={setBaselineKey} />
        <ExactOccurrenceSelect side="candidate" options={afterOptions} selectedKey={afterChoice ? choiceKey(afterChoice) : ""} onChange={setCandidateKey} />
      </div>
      {baselineUnavailable && <p className="text-sm text-muted-foreground">No stored baseline plan occurrence is available for this variant and case.</p>}
      {candidateUnavailable && <p className="text-sm text-muted-foreground">No stored candidate plan occurrence is available for this variant and case.</p>}
      {!beforeChoice && !baselineUnavailable && beforeOptions.length > 1 && <p className="text-sm text-muted-foreground">Choose an exact baseline occurrence; multiple repetitions or documents are available.</p>}
      {!afterChoice && !candidateUnavailable && afterOptions.length > 1 && <p className="text-sm text-muted-foreground">Choose an exact candidate occurrence; multiple repetitions or documents are available.</p>}
      {beforeChoice && afterChoice && beforeChoice.statementIndex !== afterChoice.statementIndex && <p role="status" className="text-sm text-muted-foreground">Selected occurrences refer to different statements. Choose matching statement indexes to compare them.</p>}
      {sameDocument && <p className="text-sm text-muted-foreground">Same plan document</p>}
      {beforeGraphError && <PlanErrorState error={beforeGraphError} />}
      {!beforeGraphError && afterGraphError && <PlanErrorState error={afterGraphError} />}
      {!beforeGraphError && !afterGraphError && beforeSourceError && <PlanErrorState error={beforeSourceError} />}
      {!beforeGraphError && !afterGraphError && !beforeSourceError && afterSourceError && <PlanErrorState error={afterSourceError} />}
      {beforeChoice && afterChoice && !sameDocument && beforeChoice.statementIndex === afterChoice.statementIndex && (beforeGraph.isPending || afterGraph.isPending || beforeSource.isPending || afterSource.isPending) && <Skeleton className="h-28 w-full" />}
      {beforeChoice && afterChoice && !sameDocument && beforeChoice.statementIndex === afterChoice.statementIndex && !beforeGraphError && !afterGraphError && !beforeSourceError && !afterSourceError && !beforeGraph.isPending && !afterGraph.isPending && (!beforePlan || !afterPlan)
        && <p role="status" className="text-sm text-muted-foreground">A complete graph for the selected statement is unavailable.</p>}
      {beforeChoice && afterChoice && !sameDocument && beforeChoice.statementIndex === afterChoice.statementIndex && !beforeGraphError && !afterGraphError && !beforeSourceError && !afterSourceError && beforePlan && afterPlan
        && <PlanChangeDetails before={beforePlan} after={afterPlan} beforeChoice={beforeChoice} afterChoice={afterChoice} beforeSql={beforeSql} afterSql={afterSql} label={pair.label} />}
    </div>
  }

  return <Card>
    <CardHeader><CardTitle>Plan changes</CardTitle><p className="text-sm text-muted-foreground">{pair.label}</p></CardHeader>
    <CardContent>{content}</CardContent>
  </Card>
}
