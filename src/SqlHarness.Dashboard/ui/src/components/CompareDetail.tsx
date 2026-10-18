import { useState } from "react"
import type { OperationDetail } from "@/api/types"
import { VariantPanel } from "@/components/VariantPanel"
import { comparePairs, equivalenceLabel, regressionLabel } from "@/lib/compare"
import { SqlDiff } from "@/components/SqlDiff"
import { TableIoComparison } from "@/components/TableIoComparison"
import { PlanChanges } from "@/components/PlanChanges"
import { SqlBlock } from "@/components/SqlBlock"
import { ConditionsList } from "@/components/ConditionsList"
import { ReproScriptButtons } from "@/components/ReproScriptButtons"
import { badgeTier } from "@/lib/flags"

type JsonObject = Record<string, unknown>
const object = (value: unknown): JsonObject | null => value !== null && typeof value === "object" && !Array.isArray(value) ? value as JsonObject : null

type CompareCard = { key: string; label: string; pair: ReturnType<typeof comparePairs>[number] | null; equivalence: JsonObject | null; regression: JsonObject | null }

function compareCards(detail: OperationDetail): CompareCard[] {
  const pairs = comparePairs(detail.variants)
  const cells = detail.compareCases ?? []
  const casesByIndex = new Map((detail.conditions?.cases ?? []).map(item => [item.index, item]))
  const casePairs = new Map(pairs.filter(pair => pair.baseline.matrixCell !== null).map(pair => [pair.baseline.matrixCell!, pair]))
  const availableCaseLabel = (index: number, pair?: ReturnType<typeof comparePairs>[number]) => {
    const candidates = [casesByIndex.get(index)?.label, pair?.baseline.caseLabel, pair?.candidate.caseLabel]
    return candidates.find(label => label && label !== "Case 1 of 1") ?? null
  }
  return cells.length > 0 ? cells.map(matrixCase => {
    const index = typeof matrixCase.index === "number" ? matrixCase.index : -1
    const caseLabel = availableCaseLabel(index, casePairs.get(index) ?? undefined)
    return {
      key: `cell ${String(matrixCase.index)}`,
      label: caseLabel && caseLabel !== "Case 1 of 1" ? caseLabel : `Case ${String(matrixCase.index)}`,
      pair: casePairs.get(index) ?? null,
      equivalence: object(matrixCase.equivalence),
      regression: object(matrixCase.regression),
    }
  }) : pairs.map(pair => {
    const index = pair.baseline.matrixCell
    return {
      key: pair.label,
      label: index === null ? "Compare" : availableCaseLabel(index, pair) ?? `Case ${index}`,
      pair,
      equivalence: object(detail.summary),
      regression: object(detail.compareRegression),
    }
  })
}

function Cases({ detail, selectedCase, onCaseChange }: { detail: OperationDetail; selectedCase: string; onCaseChange?: (value: string) => void }) {
  const [side, setSide] = useState<"baseline" | "candidate">("baseline")
  const pairs = comparePairs(detail.variants)
  const cells = detail.compareCases ?? []
  const cards = compareCards(detail)
  if (pairs.length === 0 && cells.length === 0) return <p className="text-sm text-muted-foreground">No complete baseline/candidate pairs are available.</p>
  const card = cards.find(item => item.pair?.baseline.caseId === selectedCase) ?? cards[0]
  const pair = card?.pair
  const selected = pair ? (side === "baseline" ? pair.baseline : pair.candidate) : null
  return <div className="space-y-3">
    <div className="flex flex-wrap items-center gap-3">
      <label className="text-sm">Case <select aria-label="Case" value={card?.pair?.baseline.caseId ?? selectedCase} onChange={event => onCaseChange?.(event.target.value)} className="ml-2 rounded-md border bg-background px-2 py-1">{cards.filter(item => item.pair).map(item => <option key={item.pair!.baseline.caseId} value={item.pair!.baseline.caseId}>{item.label}</option>)}</select></label>
      <label className="text-sm">Side <select aria-label="Side" value={side} onChange={event => setSide(event.target.value as typeof side)} className="ml-2 rounded-md border bg-background px-2 py-1"><option value="baseline">Baseline</option><option value="candidate">Candidate</option></select></label>
      {card && <span className="text-sm text-muted-foreground">Execution {detail.operation.status} · <span data-tier={card.regression?.verdict === "fail" ? badgeTier("regression") : undefined} title={regressionLabel(card.regression) === "No assessment" ? "No regression assessment is available for this comparison." : typeof card.regression?.policyVersion === "number" ? `Policy v${card.regression.policyVersion}` : undefined}>{regressionLabel(card.regression)}</span> · <span data-tier={card.equivalence?.equivalent === false ? badgeTier("results-differ") : undefined}>{equivalenceLabel(card.equivalence)}</span></span>}
    </div>
    {selected ? <><VariantPanel operationId={detail.operation.id} variant={selected} engine={detail.operation.engine} compareMetrics />
      <ReproScriptButtons operationId={detail.operation.id} side={selected.variant as "baseline" | "candidate"} caseId={selected.caseId} />
    </> : <p className="text-sm text-muted-foreground">No complete baseline/candidate pair is available for this case.</p>}
  </div>
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

export function CompareDetail({ detail, selectedCase = "all", onCaseChange }: { detail: OperationDetail; selectedCase?: string; onCaseChange?: (value: string) => void }) {
  const cards = compareCards(detail)
  const activeCase = selectedCase === "all" ? cards[0]?.pair?.baseline.caseId ?? "all" : selectedCase
  const activePair = cards.find(card => card.pair?.baseline.caseId === activeCase)?.pair ?? cards[0]?.pair
  return <section className="space-y-3" aria-label="Compare detail">
    <details className="rounded-lg border p-4"><summary className="cursor-pointer font-medium">SQL diff &amp; conditions</summary><div className="mt-4 space-y-4"><SetupSql detail={detail} /><SqlDiff baseline={detail.sqlText} candidate={detail.candidateSqlText} engine={detail.operation.engine} /><Conditions detail={detail} /></div></details>
    <details className="rounded-lg border p-4"><summary className="cursor-pointer font-medium">Plans</summary><div className="mt-4 space-y-4">{activePair ? <><TableIoComparison pairs={[activePair]} engine={detail.operation.engine} /><PlanChanges operationId={detail.operation.id} pair={activePair} /><section aria-label="Selected variant plan" className="space-y-2"><VariantPanel operationId={detail.operation.id} variant={activePair.baseline} engine={detail.operation.engine} compareMetrics /><VariantPanel operationId={detail.operation.id} variant={activePair.candidate} engine={detail.operation.engine} compareMetrics /></section></> : <p className="text-sm text-muted-foreground">No paired plans or IO are available.</p>}</div></details>
    <details className="rounded-lg border p-4"><summary className="cursor-pointer font-medium">Per-case &amp; per-run details</summary><div className="mt-4"><Cases detail={detail} selectedCase={activeCase} onCaseChange={onCaseChange} /></div></details>
  </section>
}
