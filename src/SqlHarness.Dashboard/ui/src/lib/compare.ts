import type { VariantDetail } from "@/api/types"

export function variantLabel(variant: VariantDetail): string {
  const parts = [variant.variant]
  if (variant.parameterSet) parts.push(variant.parameterSet)
  if (variant.matrixCell !== null) parts.push(`cell ${variant.matrixCell}`)
  return parts.join(" · ")
}

export type ComparePair = { label: string; baseline: VariantDetail; candidate: VariantDetail }

/** Baseline/candidate pairs, one per matrix cell (or one for a plain compare). */
export function comparePairs(variants: VariantDetail[]): ComparePair[] {
  const pairs: ComparePair[] = []
  const cells = [...new Set(variants.map(v => v.matrixCell))]
  for (const cell of cells) {
    const baseline = variants.find(v => v.matrixCell === cell && v.variant === "baseline")
    const candidate = variants.find(v => v.matrixCell === cell && v.variant === "candidate")
    if (baseline && candidate) pairs.push({ label: cell === null ? "compare" : `cell ${cell}`, baseline, candidate })
  }
  return pairs
}

export type CompareRow = { metric: string; baseline: number | null; candidate: number | null; delta: number | null }

export function compareRows(baseline: VariantDetail, candidate: VariantDetail): CompareRow[] {
  const row = (metric: string, pick: (v: VariantDetail) => number | null | undefined): CompareRow => {
    const b = pick(baseline) ?? null
    const c = pick(candidate) ?? null
    return { metric, baseline: b, candidate: c, delta: b === null || c === null || b === 0 ? null : (c - b) / b }
  }
  return [
    row("Elapsed (median ms)", v => v.elapsedMs?.median),
    row("CPU (median ms)", v => v.cpuMs?.median),
    row("Logical reads (median)", v => v.logicalReads?.median),
    row("Memory grant used (KB)", v => v.grantMaxUsedKb),
    row("Memory granted (KB)", v => v.grantGrantedKb),
    row("Spills", v => v.spillCount),
    row("Compile (ms)", v => v.compileTimeMs),
  ]
}
