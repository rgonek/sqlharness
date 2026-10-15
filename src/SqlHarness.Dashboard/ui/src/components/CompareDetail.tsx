import { useState } from "react"
import type { OperationDetail, VariantDetail } from "@/api/types"
import { VariantPanel } from "@/components/VariantPanel"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { comparePairs, compareRows, equivalenceLabel, formatChange, regressionLabel } from "@/lib/compare"
import { formatNumber } from "@/lib/format"
import { SqlDiff } from "@/components/SqlDiff"
import { SqlBlock } from "@/components/SqlBlock"

type JsonObject = Record<string, unknown>
const object = (value: unknown): JsonObject | null => value !== null && typeof value === "object" && !Array.isArray(value) ? value as JsonObject : null
const textValue = (value: unknown) => value === null || value === undefined ? "—" : typeof value === "string" ? value : JSON.stringify(value)

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

function Cases({ detail }: { detail: OperationDetail }) {
  const pairs = comparePairs(detail.variants)
  const cells = detail.compareCases ?? []
  const casePairs = new Map(pairs.filter(pair => pair.baseline.matrixCell !== null).map(pair => [pair.baseline.matrixCell!, pair]))
  if (pairs.length === 0 && cells.length === 0) return <p className="text-sm text-muted-foreground">No complete baseline/candidate pairs are available.</p>
  const cards = cells.length > 0 ? cells.map(matrixCase => ({
    key: `cell ${String(matrixCase.index)}`,
    label: `Cell ${String(matrixCase.index)}`,
    pair: casePairs.get(typeof matrixCase.index === "number" ? matrixCase.index : -1) ?? null,
    equivalence: object(matrixCase.equivalence),
    regression: object(matrixCase.regression),
  })) : pairs.map(pair => ({
    key: pair.label,
    label: pair.baseline.matrixCell === null ? "Compare" : `Cell ${pair.baseline.matrixCell}`,
    pair,
    equivalence: object(detail.summary),
    regression: object(detail.compareRegression),
  }))
  return <div className="space-y-4">{cards.map(card => {
    return <Card key={card.key}>
      <CardHeader><CardTitle className="text-base">{card.label}</CardTitle><CardDescription className="flex flex-wrap gap-2"><span>Execution {detail.operation.status}</span><span>·</span><span>{regressionLabel(card.regression)}</span><span>·</span><Equivalence value={card.equivalence} /></CardDescription></CardHeader>
      {card.pair && <CardContent><PairMetrics baseline={card.pair.baseline} candidate={card.pair.candidate} engine={detail.operation.engine} /></CardContent>}
    </Card>
  })}</div>
}

function SetupSql({ detail }: { detail: OperationDetail }) {
  const conditions = object(detail.compareConditions)
  const recordedSetup = conditions?.setupSql
  // Pre-v12 journals kept setup only inside bounded compare conditions.
  const setup = detail.setupSqlText ?? (typeof recordedSetup === "string" ? recordedSetup : null)
  const omittedFields = conditions?.omittedSensitiveFields
  const omitted = Array.isArray(omittedFields) && omittedFields.includes("setupSql")
  return <section aria-label="Setup SQL" className="space-y-2">
    <h3 className="font-medium">Setup SQL</h3>
    {typeof setup === "string" && setup.trim() !== "" ? (
      <>
        <p className="text-sm text-muted-foreground">Run setup and the chosen SQL in the same database session, with the original parameter values.</p>
        <SqlBlock sql={setup} engine={detail.operation.engine} initiallyFormatted={false} />
      </>
    ) : omitted ? (
      <p className="text-sm text-muted-foreground">Setup SQL was omitted because journal storage limits were reached.</p>
    ) : conditions && "setupSql" in conditions ? (
      <p className="text-sm text-muted-foreground">No setup SQL was supplied.</p>
    ) : (
      <p className="text-sm text-muted-foreground">Setup SQL is not stored (journal.storeSensitive is off or historical data is unavailable).</p>
    )}
  </section>
}

function Conditions({ detail }: { detail: OperationDetail }) {
  const conditions = object(detail.compareConditions)
  if (!conditions) return <p className="text-sm text-muted-foreground">Historical measurement conditions are unavailable.</p>
  const allowed = ["timeoutSeconds", "repeat", "comparisonMode", "compareNullability", "setupSql", "parameters", "typedParameters", "parameterSets", "matrix", "matrixText"]
  const omitted = Array.isArray(conditions.omittedSensitiveFields) ? conditions.omittedSensitiveFields.filter((item): item is string => typeof item === "string") : []
  const fields = allowed.filter(key => key in conditions)
  return <div className="space-y-4">
    <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
      <div><div className="text-sm text-muted-foreground">Profile</div><div>{detail.operation.profile ?? "—"}</div></div>
      <div><div className="text-sm text-muted-foreground">Resolved database</div><div>{detail.operation.database ?? "—"}</div></div>
      <div><div className="text-sm text-muted-foreground">Engine</div><div>{detail.operation.engine ?? "—"}</div></div>
      <div><div className="text-sm text-muted-foreground">Server</div><div>{detail.operation.server ?? "—"}</div></div>
    </div>
    {detail.vars && <div><div className="text-sm text-muted-foreground">Variables</div><pre className="whitespace-pre-wrap font-mono text-xs">{JSON.stringify(detail.vars, null, 2)}</pre></div>}
    <div className="grid gap-3 md:grid-cols-2">
      {fields.map(key => <div key={key}><div className="text-sm text-muted-foreground">{key}</div><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-2 font-mono text-xs">{textValue(conditions[key])}</pre></div>)}
      {omitted.map(key => <div key={key}><div className="text-sm text-muted-foreground">{key}</div><span className="text-sm">Unavailable; omitted by journal storage limits.</span></div>)}
    </div>
  </div>
}

export function CompareDetail({ detail }: { detail: OperationDetail }) {
  const [tab, setTab] = useState("cases")
  const pairs = comparePairs(detail.variants)
  const summary = object(detail.summary)
  const isMatrix = summary?.kind === "compare-matrix"
  const equivalence = summary && !isMatrix ? summary : null
  return <Card role="region" aria-label="Compare detail">
    <CardHeader>
      <div className="flex flex-wrap items-center gap-2"><CardTitle>Compare detail</CardTitle><Badge variant="outline">Execution {detail.operation.status}</Badge><Badge variant="outline">{regressionLabel(object(detail.compareRegression))}</Badge>{isMatrix ? <Badge variant="outline">Equivalence shown per case</Badge> : <Equivalence value={equivalence} />}</div>
      <CardDescription>{detail.compareSubject ?? "Unassigned"} / {detail.compareScope ?? "Unassigned scope"} · {detail.baselineVersion ?? "Unknown"} → {detail.candidateVersion ?? "Unknown"}</CardDescription>
    </CardHeader>
    <CardContent className="space-y-4">
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList aria-label="Compare detail sections" className="compare-detail-tabs grid h-auto w-full grid-cols-2 gap-1 sm:grid-cols-4">
          <TabsTrigger className="min-w-0 whitespace-normal leading-tight" value="cases">Cases</TabsTrigger><TabsTrigger className="min-w-0 whitespace-normal leading-tight" value="sql">SQL changes</TabsTrigger><TabsTrigger className="min-w-0 whitespace-normal leading-tight" value="plans">Plans and IO</TabsTrigger><TabsTrigger className="min-w-0 whitespace-normal leading-tight" value="conditions">Measurement conditions</TabsTrigger>
        </TabsList>
        <TabsContent value="cases"><Cases detail={detail} /></TabsContent>
        <TabsContent value="sql"><div className="space-y-4"><SetupSql detail={detail} /><SqlDiff baseline={detail.sqlText} candidate={detail.candidateSqlText} /></div></TabsContent>
        <TabsContent value="plans"><div className="grid gap-4 xl:grid-cols-2">{pairs.map(pair => <section key={pair.label} aria-label={`${pair.label} plans and IO`} className="min-w-0 space-y-2"><h2 className="font-medium">{pair.label}</h2><div className="grid gap-4 lg:grid-cols-2"><div><h3 className="mb-2 text-sm font-medium">Baseline</h3><VariantPanel variant={pair.baseline} engine={detail.operation.engine} compareMetrics /></div><div><h3 className="mb-2 text-sm font-medium">Candidate</h3><VariantPanel variant={pair.candidate} engine={detail.operation.engine} compareMetrics /></div></div></section>)}</div>{pairs.length === 0 && <p className="text-sm text-muted-foreground">No paired plans or IO are available.</p>}</TabsContent>
        <TabsContent value="conditions"><Conditions detail={detail} /></TabsContent>
      </Tabs>
    </CardContent>
  </Card>
}
