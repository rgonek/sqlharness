import type { ComparePair } from "@/lib/compare"
import { compareRows, equivalenceLabel, formatChange, regressionLabel } from "@/lib/compare"
import { formatNumber } from "@/lib/format"
import { tableIoDelta } from "@/lib/tableIoDelta"
import type { planChanges } from "@/lib/planChanges"

type PlanChanges = ReturnType<typeof planChanges>

export type PrMarkdownInput = {
  subject: string | null
  scope: string | null
  baselineVersion: string | null
  candidateVersion: string | null
  engine: string | null
  status: string
  regression: Record<string, unknown> | null
  equivalence: Record<string, unknown> | null
  isMatrix: boolean
  cases: { label: string; pair: ComparePair | null; regression: Record<string, unknown> | null; equivalence: Record<string, unknown> | null }[]
  plans: Map<string, PlanChanges | "same" | "unavailable">
  sqlDiff: string | null
}

const text = (value: string | null, fallback: string): string => value === null || value.length === 0 ? fallback : value
const safeText = (value: string): string => value.replace(/\\/g, "\\\\").replace(/\|/g, "\\|").replace(/`/g, "\\`").replace(/[\r\n]+/g, " ")
const tableCell = (value: string): string => safeText(value)
const metricLabel = (value: string): string => value === "Logical reads (median)" ? "Logical reads" : value
const displayEngine = (engine: string | null): string => {
  switch (engine?.toLowerCase()) {
    case "sqlserver":
    case "sql-server": return "SQL Server"
    case "postgres":
    case "postgresql": return "PostgreSQL"
    default: return engine ? safeText(engine) : "Unknown"
  }
}
const accessTypes = (values: string[]): string => values.length ? values.map(safeText).join(", ") : "—"

function planMarkdown(value: PlanChanges | "same" | "unavailable" | undefined): string[] {
  if (!value || value === "unavailable") return ["Plan changes unavailable."]
  if (value === "same") return ["Same plan."]
  const lines: string[] = []
  if (value.access.length === 0) lines.push("No access changes.")
  else for (const change of value.access) {
    lines.push(`${safeText(change.object)}: ${accessTypes(change.baseline)} → ${accessTypes(change.candidate)}`)
  }
  for (const warning of value.warnings) {
    lines.push(`${safeText(warning.warning)}: ${formatNumber(warning.baseline)} → ${formatNumber(warning.candidate)}`)
  }
  return lines
}

function tableIoMarkdown(pair: ComparePair): string[] {
  const io = tableIoDelta(pair.baseline.tableIo, pair.candidate.tableIo)
  const lines = [
    "",
    "Table IO headline: median of run totals (data + LOB)",
    "",
    "| Table | Logical (baseline) | Logical (candidate) | LOB logical (baseline) | LOB logical (candidate) | Total (baseline) | Total (candidate) | Scans (baseline) | Scans (candidate) | Change |",
    "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
  ]
  for (const row of io.rows) lines.push(`| ${tableCell(row.table)} | ${formatNumber(row.baseline?.logicalReads)} | ${formatNumber(row.candidate?.logicalReads)} | ${formatNumber(row.baseline?.lobLogicalReads)} | ${formatNumber(row.candidate?.lobLogicalReads)} | ${formatNumber(row.baseline?.totalLogicalReads)} | ${formatNumber(row.candidate?.totalLogicalReads)} | ${formatNumber(row.baseline?.scans ?? row.baseline?.scanCount)} | ${formatNumber(row.candidate?.scans ?? row.candidate?.scanCount)} | ${formatChange(row.logicalDelta)} |`)
  lines.push(`| Total | ${formatNumber(pair.baseline.tableIo.reduce((total, row) => total + row.logicalReads, 0))} | ${formatNumber(pair.candidate.tableIo.reduce((total, row) => total + row.logicalReads, 0))} | ${formatNumber(pair.baseline.tableIo.reduce((total, row) => total + (row.lobLogicalReads ?? 0), 0))} | ${formatNumber(pair.candidate.tableIo.reduce((total, row) => total + (row.lobLogicalReads ?? 0), 0))} | ${formatNumber(io.total.baseline)} | ${formatNumber(io.total.candidate)} | ${formatNumber(pair.baseline.tableIo.reduce((total, row) => total + (row.scans ?? row.scanCount ?? 0), 0))} | ${formatNumber(pair.candidate.tableIo.reduce((total, row) => total + (row.scans ?? row.scanCount ?? 0), 0))} | ${formatChange(io.total.delta)} |`)
  return lines
}

export function buildPrMarkdown(input: PrMarkdownInput): string {
  const subject = safeText(text(input.subject, "Unassigned"))
  const scope = safeText(text(input.scope, "Unassigned scope"))
  const baselineVersion = safeText(text(input.baselineVersion, "Unknown"))
  const candidateVersion = safeText(text(input.candidateVersion, "Unknown"))
  const lines = [
    `### SQL compare: ${subject} / ${scope} (${baselineVersion} → ${candidateVersion})`,
    "",
    `Engine: ${displayEngine(input.engine)}`,
    `Execution: ${safeText(input.status)}`,
    `Regression: ${regressionLabel(input.regression)}`,
    `Equivalence: ${equivalenceLabel(input.equivalence)}`,
  ]

  for (const item of input.cases) {
    if (input.isMatrix && item.pair) {
      lines.push("", `#### ${safeText(item.label)}`, `Execution: ${safeText(input.status)}`, `Regression: ${regressionLabel(item.regression)}`, `Equivalence: ${equivalenceLabel(item.equivalence)}`)
      lines.push(`Runs: ${formatNumber(item.pair.baseline.runs)} → ${formatNumber(item.pair.candidate.runs)}`, "", "| Metric | Baseline | Candidate | Change |", "| --- | ---: | ---: | ---: |")
      const metrics = compareRows(item.pair.baseline, item.pair.candidate, input.engine)
      lines.push(...metrics.map(row => `| ${tableCell(metricLabel(row.metric))} | ${formatNumber(row.baseline)} | ${formatNumber(row.candidate)} | ${formatChange(row.delta)} |`))
      if (input.engine?.toLowerCase() === "sqlserver" || input.engine?.toLowerCase() === "sql-server") {
        lines.push(...tableIoMarkdown(item.pair))
      }
    }
    else if (item.pair) {
      lines.push("", `Runs: ${formatNumber(item.pair.baseline.runs)} → ${formatNumber(item.pair.candidate.runs)}`, "", "| Metric | Baseline | Candidate | Change |", "| --- | ---: | ---: | ---: |")
      const metrics = compareRows(item.pair.baseline, item.pair.candidate, input.engine)
      lines.push(...metrics.map(row => `| ${tableCell(metricLabel(row.metric))} | ${formatNumber(row.baseline)} | ${formatNumber(row.candidate)} | ${formatChange(row.delta)} |`))
      if (input.engine?.toLowerCase() === "sqlserver" || input.engine?.toLowerCase() === "sql-server") {
        lines.push(...tableIoMarkdown(item.pair))
      }
    } else lines.push("", `#### ${safeText(item.label)}`, "No complete baseline/candidate pair is available.")

    if (item.pair) {
      lines.push("", "**Plan changes**", ...planMarkdown(input.plans.get(item.pair.label)))
    }
  }

  if (input.sqlDiff === null) lines.push("", "SQL diff not included (SQL text not stored or over the size limit).")
  else {
    const fenceLength = Math.max(3, ...[...input.sqlDiff.matchAll(/`+/g)].map(match => match[0].length + 1))
    const fence = "`".repeat(fenceLength)
    lines.push("", `${fence}diff`, input.sqlDiff, fence)
  }
  return lines.join("\n")
}
