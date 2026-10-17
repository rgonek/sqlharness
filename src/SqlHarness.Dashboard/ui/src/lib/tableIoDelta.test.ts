import { expect, test } from "vitest"
import type { TableIoRow } from "@/api/types"
import { tableIoDelta } from "./tableIoDelta"

const io = (table: string, logicalReads: number, scanCount: number | null = 1): TableIoRow => ({
  table, logicalReads, scanCount, scans: scanCount, physicalReads: 0, pageServerReads: null, readAheadReads: 0,
  lobLogicalReads: 0, lobPhysicalReads: 0, lobReadAheadReads: 0, coldRuns: 0, totalLogicalReads: logicalReads,
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

test("uses data plus LOB reads for totals and combines table-variable rows", () => {
  const before = [
    { ...io("#12345678", 10), lobLogicalReads: 4, totalLogicalReads: 14 },
    { ...io("#ABCDEF12", 20), lobLogicalReads: 8, totalLogicalReads: 28 },
  ]
  const after = [{ ...io("#87654321", 10), lobLogicalReads: 4, totalLogicalReads: 14 }]
  const result = tableIoDelta(before, after)
  expect(result.rows.map(row => row.table)).toEqual(["table variables"])
  expect(result.rows[0].baseline).toMatchObject({ logicalReads: 30, lobLogicalReads: 12, totalLogicalReads: 42, scans: 2 })
  expect(result.total).toEqual({ baseline: 42, candidate: 14, delta: -2 / 3 })
})

test("sorts by the displayed total-read change when LOB reads dominate", () => {
  const before = [
    { ...io("Data-only", 100), totalLogicalReads: 100 },
    { ...io("LOB-heavy", 10), lobLogicalReads: 100, totalLogicalReads: 110 },
  ]
  const after = [
    { ...io("Data-only", 0), totalLogicalReads: 0 },
    { ...io("LOB-heavy", 20), lobLogicalReads: 240, totalLogicalReads: 260 },
  ]
  expect(tableIoDelta(before, after).rows.map(row => row.table)).toEqual(["LOB-heavy", "Data-only"])
})
