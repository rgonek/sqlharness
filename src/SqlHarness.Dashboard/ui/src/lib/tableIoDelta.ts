import type { TableIoRow } from "@/api/types"

export type TableIoDeltaRow = {
  table: string
  presence: "both" | "baseline-only" | "candidate-only"
  baseline: TableIoRow | null
  candidate: TableIoRow | null
  logicalDelta: number | null
}

export function tableIoDelta(baseline: TableIoRow[], candidate: TableIoRow[]): {
  rows: TableIoDeltaRow[]
  total: { baseline: number; candidate: number; delta: number | null }
} {
  const baselineByTable = new Map(baseline.map(row => [row.table, row]))
  const candidateByTable = new Map(candidate.map(row => [row.table, row]))
  const tableNames = new Set([...baselineByTable.keys(), ...candidateByTable.keys()])
  const rows = [...tableNames].map(table => {
    const baselineRow = baselineByTable.get(table) ?? null
    const candidateRow = candidateByTable.get(table) ?? null
    const baselineReads = baselineRow?.logicalReads ?? null
    const candidateReads = candidateRow?.logicalReads ?? null
    return {
      table,
      presence: baselineRow && candidateRow ? "both" as const : baselineRow ? "baseline-only" as const : "candidate-only" as const,
      baseline: baselineRow,
      candidate: candidateRow,
      logicalDelta: baselineReads === null || candidateReads === null || baselineReads === 0
        ? null
        : (candidateReads - baselineReads) / baselineReads,
    }
  }).sort((left, right) => {
    const leftChange = Math.abs((left.candidate?.logicalReads ?? 0) - (left.baseline?.logicalReads ?? 0))
    const rightChange = Math.abs((right.candidate?.logicalReads ?? 0) - (right.baseline?.logicalReads ?? 0))
    if (leftChange !== rightChange) return rightChange - leftChange
    return left.table < right.table ? -1 : left.table > right.table ? 1 : 0
  })
  const baselineTotal = baseline.reduce((total, row) => total + row.logicalReads, 0)
  const candidateTotal = candidate.reduce((total, row) => total + row.logicalReads, 0)
  return {
    rows,
    total: {
      baseline: baselineTotal,
      candidate: candidateTotal,
      delta: baselineTotal === 0 ? null : (candidateTotal - baselineTotal) / baselineTotal,
    },
  }
}
