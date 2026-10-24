import type { TableIoRow } from "@/api/types"

export type TableIoDeltaRow = {
  table: string
  presence: "both" | "baseline-only" | "candidate-only"
  baseline: TableIoRow | null
  candidate: TableIoRow | null
  logicalDelta: number | null
}

function grouped(rows: TableIoRow[]): Map<string, TableIoRow> {
  const result = new Map<string, TableIoRow>()
  for (const row of rows) {
    // Core owns object classification and collision-safe aggregate labels.
    // Preserve exact persisted names here, including legacy summaries without provenance.
    const table = row.table
    const previous = result.get(table)
    if (!previous) {
      result.set(table, { ...row, table })
      continue
    }
    const sum = (left: number | null, right: number | null): number | null => left === null && right === null ? null : (left ?? 0) + (right ?? 0)
    result.set(table, {
      ...previous,
      logicalReads: previous.logicalReads + row.logicalReads,
      scanCount: sum(previous.scanCount, row.scanCount), scans: sum(previous.scans, row.scans),
      physicalReads: sum(previous.physicalReads, row.physicalReads), pageServerReads: sum(previous.pageServerReads, row.pageServerReads),
      readAheadReads: sum(previous.readAheadReads, row.readAheadReads), lobLogicalReads: sum(previous.lobLogicalReads, row.lobLogicalReads),
      lobPhysicalReads: sum(previous.lobPhysicalReads, row.lobPhysicalReads), lobReadAheadReads: sum(previous.lobReadAheadReads, row.lobReadAheadReads),
      coldRuns: previous.coldRuns + row.coldRuns, totalLogicalReads: previous.totalLogicalReads + row.totalLogicalReads,
    })
  }
  return result
}

export function tableIoDelta(baseline: TableIoRow[], candidate: TableIoRow[]): {
  rows: TableIoDeltaRow[]
  total: { baseline: number; candidate: number; delta: number | null }
} {
  const baselineByTable = grouped(baseline)
  const candidateByTable = grouped(candidate)
  const tableNames = new Set([...baselineByTable.keys(), ...candidateByTable.keys()])
  const rows = [...tableNames].map(table => {
    const baselineRow = baselineByTable.get(table) ?? null
    const candidateRow = candidateByTable.get(table) ?? null
    const baselineReads = baselineRow?.totalLogicalReads ?? null
    const candidateReads = candidateRow?.totalLogicalReads ?? null
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
    const leftChange = Math.abs((left.candidate?.totalLogicalReads ?? 0) - (left.baseline?.totalLogicalReads ?? 0))
    const rightChange = Math.abs((right.candidate?.totalLogicalReads ?? 0) - (right.baseline?.totalLogicalReads ?? 0))
    if (leftChange !== rightChange) return rightChange - leftChange
    return left.table < right.table ? -1 : left.table > right.table ? 1 : 0
  })
  const baselineTotal = baseline.reduce((total, row) => total + row.totalLogicalReads, 0)
  const candidateTotal = candidate.reduce((total, row) => total + row.totalLogicalReads, 0)
  return {
    rows,
    total: {
      baseline: baselineTotal,
      candidate: candidateTotal,
      delta: baselineTotal === 0 ? null : (candidateTotal - baselineTotal) / baselineTotal,
    },
  }
}
