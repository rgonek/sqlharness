import type { DistilledPlan, PlanNode, VariantDetail } from "@/api/types"
import { flattenPlan } from "@/lib/plan"
import { shortHash } from "@/lib/format"

export type AccessChange = { object: string; baseline: string[]; candidate: string[] }
export type WarningChange = { warning: string; baseline: number; candidate: number }
export type TopOperator = { physicalOp: string; object: string | null; costFraction: number }
export type AccessNodeDetail = { physicalOp: string; actualRows: number | null; executions: number | null }
export type AccessObjectDetail = { object: string; baseline: AccessNodeDetail[]; candidate: AccessNodeDetail[] }
export type PlanChangesResult = {
  access: AccessChange[]
  accessDetails: AccessObjectDetail[]
  warnings: WarningChange[]
  topBaseline: TopOperator[]
  topCandidate: TopOperator[]
}

export type PlanStatementChange = { ordinal: number; statement: string }

export function storedPlanStatements(variant: VariantDetail) {
  const stored = variant.plans.filter(plan => plan.stored)
  const firstRepetition = Math.min(...stored.map(plan => plan.repetition))
  return Number.isFinite(firstRepetition) ? stored.filter(plan => plan.repetition === firstRepetition).sort((a, b) => a.ordinal - b.ordinal) : []
}

export function planChangeStatus(baseline: VariantDetail, candidate: VariantDetail): { same: boolean; changed: PlanStatementChange[] } {
  const before = storedPlanStatements(baseline)
  const after = storedPlanStatements(candidate)
  const beforeByOrdinal = new Map(before.map(plan => [plan.ordinal, plan]))
  const afterByOrdinal = new Map(after.map(plan => [plan.ordinal, plan]))
  const ordinals = [...new Set([...beforeByOrdinal.keys(), ...afterByOrdinal.keys()])].sort((a, b) => a - b)
  const changed = ordinals.flatMap(ordinal => {
    const previous = beforeByOrdinal.get(ordinal)
    const next = afterByOrdinal.get(ordinal)
    if (previous && next && previous.hash === next.hash) return []
    const hash = next?.hash ?? previous?.hash ?? ""
    return [{ ordinal, statement: `statement ${ordinal} (${shortHash(hash)})` }]
  })
  return { same: before.length > 0 && before.length === after.length && changed.length === 0, changed }
}

const objectFor = (node: PlanNode): string | null => node.objectName
  ? `${node.objectName}${node.indexName ? `.${node.indexName}` : ""}`
  : null
const compareOrdinal = (a: string, b: string) => a < b ? -1 : a > b ? 1 : 0

function nodesByObject(plan: DistilledPlan): Map<string, PlanNode[]> {
  const result = new Map<string, PlanNode[]>()
  for (const statement of plan.statements) {
    for (const { node } of flattenPlan(statement.root)) {
      const object = objectFor(node)
      if (object === null) continue
      const nodes = result.get(object) ?? []
      nodes.push(node)
      result.set(object, nodes)
    }
  }
  return result
}

function warningCounts(plan: DistilledPlan): Map<string, number> {
  const counts = new Map<string, number>()
  let missingIndexes = 0
  for (const statement of plan.statements) {
    missingIndexes += statement.missingIndexes?.length ?? 0
    for (const { node } of flattenPlan(statement.root)) {
      for (const warning of node.warnings ?? []) counts.set(warning, (counts.get(warning) ?? 0) + 1)
    }
  }
  if (missingIndexes > 0) counts.set("Missing index", (counts.get("Missing index") ?? 0) + missingIndexes)
  return counts
}

function topOperators(plan: DistilledPlan): TopOperator[] {
  return plan.statements.flatMap(statement => flattenPlan(statement.root).flatMap(({ node }) =>
    typeof node.costFraction === "number"
      ? [{ physicalOp: node.physicalOp, object: objectFor(node), costFraction: node.costFraction }]
      : [],
  )).sort((a, b) => b.costFraction - a.costFraction).slice(0, 5)
}

const nodeDetails = (nodes: PlanNode[] = []): AccessNodeDetail[] => nodes.map(node => ({
  physicalOp: node.physicalOp,
  actualRows: node.actualRows ?? null,
  executions: node.executions ?? null,
}))

export function planChanges(baseline: DistilledPlan, candidate: DistilledPlan): PlanChangesResult {
  const baselineNodes = nodesByObject(baseline)
  const candidateNodes = nodesByObject(candidate)
  const objects = [...new Set([...baselineNodes.keys(), ...candidateNodes.keys()])].sort(compareOrdinal)
  const access: AccessChange[] = []
  const accessDetails: AccessObjectDetail[] = []

  for (const object of objects) {
    const beforeNodes = baselineNodes.get(object) ?? []
    const afterNodes = candidateNodes.get(object) ?? []
    const before = [...new Set(beforeNodes.map(node => node.physicalOp))]
    const after = [...new Set(afterNodes.map(node => node.physicalOp))]
    if (JSON.stringify(before) === JSON.stringify(after)) continue
    access.push({ object, baseline: before, candidate: after })
    accessDetails.push({ object, baseline: nodeDetails(beforeNodes), candidate: nodeDetails(afterNodes) })
  }

  const beforeWarnings = warningCounts(baseline)
  const afterWarnings = warningCounts(candidate)
  const warnings = [...new Set([...beforeWarnings.keys(), ...afterWarnings.keys()])]
    .sort(compareOrdinal)
    .flatMap(warning => {
      const before = beforeWarnings.get(warning) ?? 0
      const after = afterWarnings.get(warning) ?? 0
      return before === after ? [] : [{ warning, baseline: before, candidate: after }]
    })

  return { access, accessDetails, warnings, topBaseline: topOperators(baseline), topCandidate: topOperators(candidate) }
}
