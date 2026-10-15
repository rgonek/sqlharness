import { expect, test } from "vitest"
import type { TableIoRow } from "@/api/types"
import { tableIoDelta } from "./tableIoDelta"

const io = (table: string, logicalReads: number, scanCount: number | null = 1): TableIoRow => ({
  table, logicalReads, scanCount, physicalReads: 0, pageServerReads: null, readAheadReads: 0,
  lobLogicalReads: 0, lobPhysicalReads: 0, lobReadAheadReads: 0, coldRuns: 0,
})

test("joins tables, labels one-sided rows, sorts by absolute change and totals", () => {
  const result = tableIoDelta([io("Orders", 1000), io("Customers", 10), io("Temp", 50)], [io("Orders", 100), io("Customers", 12), io("Lines", 30)])
  expect(result.rows.map(r => [r.table, r.presence])).toEqual([
    ["Orders", "both"], ["Temp", "baseline-only"], ["Lines", "candidate-only"], ["Customers", "both"],
  ])
  expect(result.rows[0].logicalDelta).toBeCloseTo(-0.9)
  expect(result.rows[1].logicalDelta).toBeNull()
  expect(result.total).toEqual({ baseline: 1060, candidate: 142, delta: (142 - 1060) / 1060 })
})

test("zero baseline gives no percentage", () => {
  expect(tableIoDelta([io("T", 0)], [io("T", 5)]).rows[0].logicalDelta).toBeNull()
})
