import { useState } from "react"
import { useQueryClient } from "@tanstack/react-query"
import type { OperationDetail, VariantDetail } from "@/api/types"
import { fetchDistilledPlan, queryKeys } from "@/api/queries"
import { VariantPanel } from "@/components/VariantPanel"
import { CopyButton } from "@/components/CopyButton"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { comparePairs, compareRows, equivalenceLabel, formatChange, regressionLabel } from "@/lib/compare"
import { formatNumber } from "@/lib/format"
import { SqlDiff } from "@/components/SqlDiff"
import { TableIoComparison } from "@/components/TableIoComparison"
import { PlanChanges } from "@/components/PlanChanges"
import { loadUnifiedDiffText } from "@/components/SqlEditors"
import { buildPrMarkdown } from "@/lib/prMarkdown"
import { planChangeStatus, planChanges, storedPlanStatements } from "@/lib/planChanges"
import { diffTooLarge, normalizeForDiff } from "@/lib/sqlDiff"
import { SqlBlock } from "@/components/SqlBlock"
import { ConditionsList } from "@/components/ConditionsList"
import { ReproScriptButtons } from "@/components/ReproScriptButtons"
import { displayCaseLabel } from "@/lib/caseLabel"

type JsonObject = Record<string, unknown>
const object = (value: unknown): JsonObject | null => value !== null && typeof value === "object" && !Array.isArray(value) ? value as JsonObject : null

function Equivalence({ value }: { value: JsonObject | null }) {
  return <Badge variant="outline">{equivalenceLabel(value)}</Badge>
}

function PairMetrics({ baseline, candidate, engine }: { baseline: VariantDetail; candidate: VariantDetail; engine: string | null }) {
  const rows = compareRows(baseline, candidate, engine)
  const spread = (value: VariantDetail["elapsedMs"]) => value ? `${formatNumber(value.min)}–${formatNumber(value.max)}` : "—"
  const cells = (variant: VariantDetail) => `Runs: ${formatNumber(variant.runs)} · elapsed ${spread(variant.elapsedMs)} ms · CPU ${spread(variant.cpuMs)} ms${engine?.toLowerCase() === "sqlserver" ? ` · logical reads ${spread(variant.logicalReads)}` : ""}`
  return (
    <div className="space-y-2">
      <div className="grid grid-cols-2 gap-2 text-xs text-muted-foreground"><span>Baseline — {cells(baseline)}</span><span>Candidate — {cells(candidate)}</span></div>
      <div className="overflow-x-auto">
        <Table>
          <TableHeader><TableRow><TableHead>Metric</TableHead><TableHead className="text-right">Baseline</TableHead><TableHead className="text-right">Candidate</TableHead><TableHead className="text-right">Change</TableHead></TableRow></TableHeader>
          <TableBody>{rows.map(row => <TableRow key={row.metric}><TableCell>{row.metric}</TableCell><TableCell className="text-right">{formatNumber(row.baseline)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate)}</TableCell><TableCell className="text-right">{formatChange(row.delta)}</TableCell></TableRow>)}</TableBody>
        </Table>
      </div>
    </div>
  )
}

type CompareCard = { key: string; label: string; pair: ReturnType<typeof comparePairs>[number] | null; equivalence: JsonObject | null; regression: JsonObject | null }

function compareCards(detail: OperationDetail): CompareCard[] {
  const pairs = comparePairs(detail.variants)
  const cells = detail.compareCases ?? []
  const casesByIndex = new Map((detail.conditions?.cases ?? []).map(item => [item.index, item]))
  const casePairs = new Map(pairs.filter(pair => pair.baseline.matrixCell !== null).map(pair => [pair.baseline.matrixCell!, pair]))
  return cells.length > 0 ? cells.map(matrixCase => {
    const index = typeof matrixCase.index === "number" ? matrixCase.index : -1
    const caseLabel = casesByIndex.get(index)?.label
    return {
      key: `cell ${String(matrixCase.index)}`,
      label: caseLabel && caseLabel !== "Case 1 of 1" ? caseLabel : `Cell ${String(matrixCase.index)}`,
      pair: casePairs.get(index) ?? null,
      equivalence: object(matrixCase.equivalence),
      regression: object(matrixCase.regression),
    }
  }) : pairs.map(pair => ({
    key: pair.label,
    label: pair.baseline.matrixCell === null ? "Compare" : `Cell ${pair.baseline.matrixCell}`,
    pair,
    equivalence: object(detail.summary),
    regression: object(detail.compareRegression),
  }))
}

function Cases({ detail }: { detail: OperationDetail }) {
  const pairs = comparePairs(detail.variants)
  const cells = detail.compareCases ?? []
  const cards = compareCards(detail)
  if (pairs.length === 0 && cells.length === 0) return <p className="text-sm text-muted-foreground">No complete baseline/candidate pairs are available.</p>
  return <div className="space-y-4">{cards.map(card => {
    return <Card key={card.key}>
      <CardHeader><CardTitle className="min-w-0 text-base"><span className="block" title={card.label}>{displayCaseLabel(card.label)}</span></CardTitle><CardDescription className="flex flex-wrap gap-2"><span>Execution {detail.operation.status}</span><span>·</span><span>{regressionLabel(card.regression)}</span><span>·</span><Equivalence value={card.equivalence} /></CardDescription></CardHeader>
      {card.pair && <CardContent className="space-y-3"><PairMetrics baseline={card.pair.baseline} candidate={card.pair.candidate} engine={detail.operation.engine} /><div className="grid gap-3 sm:grid-cols-2"><section><h3 className="mb-1 text-sm font-medium">Baseline reproduction</h3><ReproScriptButtons operationId={detail.operation.id} side="baseline" caseId={card.pair.baseline.caseId} /></section><section><h3 className="mb-1 text-sm font-medium">Candidate reproduction</h3><ReproScriptButtons operationId={detail.operation.id} side="candidate" caseId={card.pair.candidate.caseId} /></section></div></CardContent>}
    </Card>
  })}</div>
}

function SetupSql({ detail }: { detail: OperationDetail }) {
  const setup = detail.setupSqlText ?? detail.conditions?.setup?.text ?? null
  return <section aria-label="Setup SQL" className="space-y-2">
    <h3 className="font-medium">Setup SQL</h3>
    {typeof setup === "string" && setup.trim() !== "" ? (
      <>
        <p className="text-sm text-muted-foreground">Run setup and the chosen SQL in the same database session, with the original parameter values.</p>
        <SqlBlock sql={setup} engine={detail.operation.engine} initiallyFormatted={false} />
      </>
    ) : (
      <p className="text-sm text-muted-foreground">Setup SQL is not stored (journal.storeSensitive is off or historical data is unavailable).</p>
    )}
  </section>
}

function Conditions({ detail }: { detail: OperationDetail }) {
  return <ConditionsList conditions={detail.conditions} />
}

export function CompareDetail({ detail }: { detail: OperationDetail }) {
  const [tab, setTab] = useState("cases")
  const queryClient = useQueryClient()
  const pairs = comparePairs(detail.variants)
  const cards = compareCards(detail)
  const summary = object(detail.summary)
  const isMatrix = summary?.kind === "compare-matrix"
  const equivalence = summary && !isMatrix ? summary : null

  async function copyMarkdown(): Promise<string> {
    const plans = new Map<string, ReturnType<typeof planChanges> | "same" | "unavailable">()
    await Promise.all(cards.flatMap(card => {
      const pair = card.pair
      if (!pair) return []
      const status = planChangeStatus(pair.baseline, pair.candidate)
      if (status.same) {
        plans.set(pair.label, "same")
        return []
      }
      const change = status.changed[0]
      if (!change) {
        plans.set(pair.label, "unavailable")
        return []
      }
      const baselineHash = storedPlanStatements(pair.baseline).find(plan => plan.ordinal === change.ordinal)?.hash
      const candidateHash = storedPlanStatements(pair.candidate).find(plan => plan.ordinal === change.ordinal)?.hash
      if (!baselineHash || !candidateHash) { plans.set(pair.label, "unavailable"); return [] }
      return [queryClient.fetchQuery({ queryKey: queryKeys.plan(baselineHash), queryFn: () => fetchDistilledPlan(baselineHash), staleTime: Infinity })
        .then(baseline => queryClient.fetchQuery({ queryKey: queryKeys.plan(candidateHash), queryFn: () => fetchDistilledPlan(candidateHash), staleTime: Infinity })
          .then(candidate => plans.set(pair.label, planChanges(baseline, candidate))))
        .catch(() => plans.set(pair.label, "unavailable"))]
    }))

    let sqlDiff: string | null = null
    const baselineSql = detail.sqlText
    const candidateSql = detail.candidateSqlText
    if (baselineSql !== null && candidateSql !== null && !diffTooLarge(baselineSql, candidateSql)) {
      const baseline = normalizeForDiff(baselineSql, detail.operation.engine, false).text
      const candidate = normalizeForDiff(candidateSql, detail.operation.engine, false).text
      if (!diffTooLarge(baseline, candidate)) {
        try {
          sqlDiff = await (await loadUnifiedDiffText())(baseline, candidate)
        } catch {
          sqlDiff = null
        }
      }
    }

    return buildPrMarkdown({
      subject: detail.compareSubject,
      scope: detail.compareScope,
      baselineVersion: detail.baselineVersion,
      candidateVersion: detail.candidateVersion,
      engine: detail.operation.engine,
      status: detail.operation.status,
      regression: object(detail.compareRegression),
      equivalence,
      isMatrix,
      cases: cards.map(card => ({ label: card.label, pair: card.pair, regression: card.regression, equivalence: card.equivalence })),
      plans,
      sqlDiff,
    })
  }

  return <Card role="region" aria-label="Compare detail">
    <CardHeader>
      <div className="flex flex-wrap items-center gap-2"><CardTitle>Compare detail</CardTitle><Badge variant="outline">Execution {detail.operation.status}</Badge><Badge variant="outline">{regressionLabel(object(detail.compareRegression))}</Badge>{isMatrix ? <Badge variant="outline">Equivalence shown per case</Badge> : <Equivalence value={equivalence} />}<CopyButton label="Copy" getText={copyMarkdown} /></div>
      <CardDescription>{detail.compareSubject ?? "Unassigned"} / {detail.compareScope ?? "Unassigned scope"} · {detail.baselineVersion ?? "Unknown"} → {detail.candidateVersion ?? "Unknown"}</CardDescription>
    </CardHeader>
    <CardContent className="space-y-4">
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList aria-label="Compare detail sections" className="compare-detail-tabs grid h-auto w-full grid-cols-2 gap-1 sm:grid-cols-4">
          <TabsTrigger className="min-w-0 whitespace-normal leading-tight" value="cases">Cases</TabsTrigger><TabsTrigger className="min-w-0 whitespace-normal leading-tight" value="sql">SQL changes</TabsTrigger><TabsTrigger className="min-w-0 whitespace-normal leading-tight" value="plans">Plans and IO</TabsTrigger><TabsTrigger className="min-w-0 whitespace-normal leading-tight" value="conditions">Measurement conditions</TabsTrigger>
        </TabsList>
        <TabsContent value="cases"><Cases detail={detail} /></TabsContent>
        <TabsContent value="sql"><div className="space-y-4"><SetupSql detail={detail} /><SqlDiff baseline={detail.sqlText} candidate={detail.candidateSqlText} engine={detail.operation.engine} /></div></TabsContent>
        <TabsContent value="plans"><div className="space-y-4"><TableIoComparison pairs={pairs} engine={detail.operation.engine} />{pairs.map(pair => <PlanChanges key={`${pair.label}-plan-changes`} pair={pair} />)}<div className="grid gap-4 xl:grid-cols-2">{pairs.map(pair => <section key={pair.label} aria-label={`${pair.label} plans and IO`} className="min-w-0 space-y-2"><h2 className="font-medium">{pair.label}</h2><div className="grid gap-4 lg:grid-cols-2"><div><h3 className="mb-2 text-sm font-medium">Baseline</h3><VariantPanel variant={pair.baseline} engine={detail.operation.engine} compareMetrics /></div><div><h3 className="mb-2 text-sm font-medium">Candidate</h3><VariantPanel variant={pair.candidate} engine={detail.operation.engine} compareMetrics /></div></div></section>)}</div></div>{pairs.length === 0 && <p className="text-sm text-muted-foreground">No paired plans or IO are available.</p>}</TabsContent>
        <TabsContent value="conditions"><Conditions detail={detail} /></TabsContent>
      </Tabs>
    </CardContent>
  </Card>
}
