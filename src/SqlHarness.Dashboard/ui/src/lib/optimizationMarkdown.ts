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

function bufferSummary(value: NonNullable<OptimizationComparison["buffersBefore"]>): string {
  const fields: [string, number | null][] = [
    ["shared hit", value.sharedHit], ["shared read", value.sharedRead], ["shared dirtied", value.sharedDirtied],
    ["shared written", value.sharedWritten], ["temp read", value.tempRead], ["temp written", value.tempWritten],
  ]
  return fields.map(([label, count]) => `${label} ${number(count)}`).join(", ")
}

function viewLabel(view: OptimizationComparison["view"]): string {
  switch (view) {
    case "whole": return "Whole batch"
    case "statements": return "By statement"
    default: return "Changed statements"
  }
}

function hiddenWorkLabel(item: NonNullable<OptimizationComparison["statements"]>[number]["hiddenWork"][number]): string | null {
  if (item.kind === "scalar-udf" && item.sharePercent !== null) return `UDF time ${item.sharePercent}% of statement`
  if (item.kind === "multi-statement-tvf" && item.executions !== null)
    return `multi-statement function: internal reads not visible (${item.executions} executions)`
  return null
}

export function optimizationMetricRows(comparison: OptimizationComparison): { label: string; before: string; after: string }[] {
  const rows: { label: string; before: string; after: string }[] = []
  if (comparison.cpuBeforeMs != null || comparison.cpuAfterMs != null)
    rows.push({ label: "CPU (ms)", before: number(comparison.cpuBeforeMs ?? null), after: number(comparison.cpuAfterMs ?? null) })
  if (comparison.buffersBefore || comparison.buffersAfter)
    rows.push({ label: "Buffers", before: comparison.buffersBefore ? bufferSummary(comparison.buffersBefore) : "—", after: comparison.buffersAfter ? bufferSummary(comparison.buffersAfter) : "—" })
  return rows
}

export function optimizationMarkdown(comparison: OptimizationComparison, subject: string, scope: string): string {
  const selected = comparison.cases.find(option => option.id === comparison.selectedCase)
  const caseLabel = selected ? selected.label : comparison.selectedCase === "all" ? "all cases" : comparison.selectedCase
  const aggregateLabel = comparison.aggregate === "mean" ? "Mean" : "Sum"
  const lines = [
    `# ${subject} — ${scope} — ${comparison.a} → ${comparison.b}`,
    `Case: ${caseLabel} (${aggregateLabel})${comparison.coverageLabel ? ` — ${comparison.coverageLabel}` : ""}`,
    `Basis: ${basisLabel(comparison)}`,
    `View: ${viewLabel(comparison.view)}`,
    `basis: ${comparison.verdictBasis ?? "whole batch"}`,
    `Equivalence: ${comparison.equivalenceLabel}`,
    "",
  ]

  if (comparison.wholeBatch?.label) lines.push(comparison.wholeBatch.label)
  for (const text of [
    (comparison.omittedStatementsBefore ?? 0) > 0 ? `omitted statements: ${comparison.omittedStatementsBefore} (their reads remain in the batch total)` : null,
    (comparison.omittedStatementsAfter ?? 0) > 0 ? `omitted statements: ${comparison.omittedStatementsAfter} (their reads remain in the batch total)` : null,
  ]) if (text) lines.push(text)
  const hidden = [...new Set((comparison.statements ?? []).flatMap(row => row.hiddenWork.map(hiddenWorkLabel).filter((label): label is string => label !== null)))]
  if (hidden.length) lines.push(...hidden)
  if (comparison.fallbackNote) lines.push(comparison.fallbackNote)
  if (comparison.wholeBatch?.label || hidden.length || comparison.fallbackNote
    || comparison.omittedStatementsBefore || comparison.omittedStatementsAfter) lines.push("")

  if (comparison.mixed) {
    const percent = Math.round(comparison.mixed.worstChangePercent)
    lines.push(`Mixed: ${comparison.mixed.better} better, ${comparison.mixed.worse} worse — ${comparison.mixed.worstCaseLabel}: logical reads ${percent > 0 ? "+" : ""}${percent}%`, "")
  }

  if (comparison.basis === "ConditionsDiffer") {
    lines.push(conditionsDifferMessage)
    return lines.join("\n")
  }

  if (comparison.view === "statements" || comparison.view === "changed") {
    lines.push(
      "| Statement | Status | Executions | Reads before | Reads after | Change |",
      "| --- | --- | ---: | ---: | ---: | --- |",
      ...(comparison.statements ?? []).map(row => `| ${row.text.replaceAll("|", "\\|").replaceAll("\n", " ")} | ${row.status} | ${number(row.executions)} | ${number(row.readsBefore)} | ${number(row.readsAfter)} | ${row.change} |`),
    )
    return lines.join("\n")
  }

  if (comparison.tableIoUnavailable) {
    lines.push("per-table IO not available for this engine")
    appendMetrics(lines, comparison)
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
  appendMetrics(lines, comparison)
  return lines.join("\n")
}

function appendMetrics(lines: string[], comparison: OptimizationComparison): void {
  const metrics = optimizationMetricRows(comparison)
  if (metrics.length) lines.push("", "| Metric | Before | After |", "| --- | --- | --- |", ...metrics.map(row => `| ${row.label} | ${row.before} | ${row.after} |`))
}
