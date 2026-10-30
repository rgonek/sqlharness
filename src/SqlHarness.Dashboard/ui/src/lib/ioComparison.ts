import type { OperationDetail, OptimizationActionReference, OptimizationComparison, VariantDetail } from "@/api/types"

export type IoExtraMetric = "physicalReads" | "pageServerReads" | "readAheadReads" | "lobLogicalReads" | "lobPhysicalReads" | "lobReadAheadReads"
export type IoExtras = { before: Record<string, Partial<Record<IoExtraMetric, number | null>>>; after: Record<string, Partial<Record<IoExtraMetric, number | null>>> }

export function selectedVariants(detail: OperationDetail | undefined, action: OptimizationActionReference | null | undefined, comparison: OptimizationComparison): VariantDetail[] {
  if (!detail || !action) return []
  const selected = comparison.selectedCase === "all" ? comparison.cases.map(item => item.id) : [comparison.selectedCase]
  const caseIds = new Set(selected.map(id => action.caseIdsByFingerprint?.[id] ?? (action.caseIds.includes(id) ? id : null)).filter((id): id is string => id !== null))
  // A missing case mapping must not turn an all-case comparison into a partial total.
  if (!selected.length || selected.some(id => !action.caseIdsByFingerprint?.[id] && !action.caseIds.includes(id))) return []
  const variants = detail.variants.filter(variant => caseIds.has(variant.caseId) && (variant.variant === action.side || (action.side === "measure" && variant.variant === "query")))
  return [...caseIds].every(id => variants.filter(variant => variant.caseId === id).length === 1) ? variants : []
}
export function aggregate(values: (number | null)[], mean: boolean): number | null {
  if (!values.length || values.some(value => value === null)) return null
  const sum = values.reduce<number>((total, value) => total + value!, 0)
  return mean ? Math.round(sum / values.length) : sum
}

export function tableExtras(variants: VariantDetail[], mean: boolean): IoExtras["before"] {
  const names = [...new Set(variants.flatMap(variant => variant.tableIo.map(row => row.table)))]
  const metrics: IoExtraMetric[] = ["physicalReads", "pageServerReads", "readAheadReads", "lobLogicalReads", "lobPhysicalReads", "lobReadAheadReads"]
  return Object.fromEntries(names.map(name => [name, Object.fromEntries(metrics.map(metric => [metric, aggregate(variants.map(variant => {
    const rows = variant.tableIo.filter(row => row.table === name)
    return rows.length ? aggregate(rows.map(row => row[metric]), false) : null
  }), mean)]))]))
}
