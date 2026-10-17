import type { OptimizationComparison } from "@/api/types"

export const conditionsDifferMessage = "conditions differ; SQL diff is the available comparison."

function basisLabel(comparison: OptimizationComparison): string {
  switch (comparison.basis) {
    case "MeasuredTogether": return `measured together in #${comparison.operationId}`
    case "DifferentRunsSameConditions": return "different runs, same conditions"
    case "ConditionsDiffer": return "conditions differ"
    case "SameSql": return "same SQL"
  }
}

function number(value: number | null): string {
  return value === null ? "—" : String(value)
}

export function optimizationMarkdown(comparison: OptimizationComparison, subject: string, scope: string): string {
  const selected = comparison.cases.find(option => option.id === comparison.selectedCase)
  const caseLabel = selected ? selected.label : comparison.selectedCase === "all" ? "all cases" : comparison.selectedCase
  const aggregateLabel = comparison.aggregate === "mean" ? "Mean" : "Sum"
  const lines = [
    `# ${subject} — ${scope} — ${comparison.a} → ${comparison.b}`,
    `Case: ${caseLabel} (${aggregateLabel})${comparison.coverageLabel ? ` — ${comparison.coverageLabel}` : ""}`,
    `Basis: ${basisLabel(comparison)}`,
    `Equivalence: ${comparison.equivalenceLabel}`,
    "",
  ]

  if (comparison.mixed) {
    const percent = Math.round(comparison.mixed.worstChangePercent)
    lines.push(`Mixed: ${comparison.mixed.better} better, ${comparison.mixed.worse} worse — ${comparison.mixed.worstCaseLabel}: logical reads ${percent > 0 ? "+" : ""}${percent}%`, "")
  }

  if (comparison.basis === "ConditionsDiffer") {
    lines.push(conditionsDifferMessage)
    return lines.join("\n")
  }

  if (comparison.tableIoUnavailable) {
    lines.push("per-table IO not available for this engine")
    return lines.join("\n")
  }

  lines.push(
    "| Table | Scans before | Scans after | Logical reads before | Logical reads after | Change |",
    "| --- | ---: | ---: | ---: | ---: | --- |",
    ...comparison.tables.map(row => `| ${row.table} | ${number(row.scansBefore)} | ${number(row.scansAfter)} | ${number(row.readsBefore)} | ${number(row.readsAfter)} | ${row.change} |`),
  )
  if (comparison.collapsed) {
    lines.push(`| ${comparison.collapsed.label} |`)
  }
  return lines.join("\n")
}
