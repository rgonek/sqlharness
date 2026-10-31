import type { StatementRow } from "@/api/types"

function number(value: number | null): string {
  return value === null ? "—" : value.toLocaleString()
}

export function executionLabel(row: StatementRow, measured = false): string | null {
  if (row.executionsBefore === undefined && row.executionsAfter === undefined)
    return row.executions == null ? null : `${number(row.executions)} ${row.executions === 1 ? "execution" : "executions"}`
  const before = row.executionsBefore ?? null
  const after = row.executionsAfter ?? null
  if (measured) return after == null ? null : `${number(after)} ${after === 1 ? "execution" : "executions"}`
  return before == null && after == null ? null : `${number(before)} → ${number(after)} executions`
}

