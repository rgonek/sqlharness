import type { VariantDetail } from "@/api/types"
import { formatPercent } from "@/lib/format"

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

export function compareRows(baseline: VariantDetail, candidate: VariantDetail, engine?: string | null): CompareRow[] {
  const row = (metric: string, pick: (v: VariantDetail) => number | null | undefined): CompareRow => {
    const b = pick(baseline) ?? null
    const c = pick(candidate) ?? null
    return { metric, baseline: b, candidate: c, delta: b === null || c === null || b === 0 ? null : (c - b) / b }
  }
  const rows = [
    row("Elapsed (median ms)", v => v.elapsedMs?.median),
    row("CPU (median ms)", v => v.cpuMs?.median),
  ]
  const normalizedEngine = engine?.toLowerCase()
  if (normalizedEngine === "sqlserver" || normalizedEngine === "sql-server") {
    rows.push(
      row("Logical reads (median)", v => v.logicalReads?.median),
      row("Memory grant used (KB)", v => v.grantMaxUsedKb),
      row("Memory granted (KB)", v => v.grantGrantedKb),
      row("Spills", v => v.spillCount),
    )
  } else if (normalizedEngine === "postgres" || normalizedEngine === "postgresql") {
    rows.push(
      row("Shared hits (median buffers)", v => v.postgres?.sharedHit),
      row("Shared reads (median buffers)", v => v.postgres?.sharedRead),
      row("Shared dirtied (median buffers)", v => v.postgres?.sharedDirtied),
      row("Shared written (median buffers)", v => v.postgres?.sharedWritten),
      row("Temp reads (median buffers)", v => v.postgres?.tempRead),
      row("Temp written (median buffers)", v => v.postgres?.tempWritten),
    )
  }
  return rows
}

export function regressionLabel(regression: Record<string, unknown> | null | undefined): string {
  switch (regression?.verdict) {
    case "pass": return "No regression detected"
    case "fail": return "Regression detected"
    case "inconclusive": return "Inconclusive"
    default: return "No assessment"
  }
}

export function equivalenceLabel(equivalence: Record<string, unknown> | null | undefined): string {
  if (!equivalence) return "Equivalence unavailable (legacy record)"
  const mode = equivalence.mode ?? equivalence.comparison
  if (mode === "Off" || mode === "off" || mode === 3) return "Equivalence check off"
  const result = equivalence.equivalent ?? equivalence.resultsEquivalent
  if (result === true) return "Results equivalent"
  if (result === false) return "Results differ"
  return mode !== null && mode !== undefined ? "Equivalence check incomplete" : "Equivalence unavailable (legacy record)"
}

/** Relative change as a percent; "+" only when the rounded percent is above zero (never "+0%"). */
export function formatChange(delta: number | null): string {
  if (delta === null) return "—"
  return `${Math.round(delta * 100) > 0 ? "+" : ""}${formatPercent(delta)}`
}
