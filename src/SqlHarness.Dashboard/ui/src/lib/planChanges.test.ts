import { expect, test } from "vitest"
import type { DistilledPlan, PlanNode, VariantDetail } from "@/api/types"
import { planChanges, firstPlanHash } from "./planChanges"

const plan = (root: PlanNode, missing = 0): DistilledPlan => ({
  statements: [{ root, missingIndexes: Array.from({ length: missing }, () => ({ table: "Orders", equalityColumns: ["CustomerId"], inequalityColumns: [], includeColumns: [], impact: 90 })) }],
})

test("scan to seek, resolved missing index and implicit conversion", () => {
  const before = plan({ physicalOp: "Hash Match", costFraction: 0.2, children: [
    { physicalOp: "Clustered Index Scan", objectName: "dbo.Orders", indexName: "PK_Orders", actualRows: 400, executions: 2, costFraction: 0.7, warnings: ["Implicit conversion"] },
    { physicalOp: "Index Seek", objectName: "dbo.Customers", indexName: "IX_C", costFraction: 0.1 },
  ] }, 1)
  const after = plan({ physicalOp: "Nested Loops", costFraction: 0.1, children: [
    { physicalOp: "Index Seek", objectName: "dbo.Orders", indexName: "IX_Orders_Customer", actualRows: 12, executions: 1, costFraction: 0.5 },
    { physicalOp: "Index Seek", objectName: "dbo.Customers", indexName: "IX_C", costFraction: 0.4 },
  ] })

  const changes = planChanges(before, after)

  expect(changes.access).toEqual([
    { object: "dbo.Orders.IX_Orders_Customer", baseline: [], candidate: ["Index Seek"] },
    { object: "dbo.Orders.PK_Orders", baseline: ["Clustered Index Scan"], candidate: [] },
  ])
  expect(changes.warnings).toEqual([
    { warning: "Implicit conversion", baseline: 1, candidate: 0 },
    { warning: "Missing index", baseline: 1, candidate: 0 },
  ])
  expect(changes.topBaseline[0]).toEqual({ physicalOp: "Clustered Index Scan", object: "dbo.Orders.PK_Orders", costFraction: 0.7 })
  expect(changes.accessDetails).toEqual([
    { object: "dbo.Orders.IX_Orders_Customer", baseline: [], candidate: [{ physicalOp: "Index Seek", actualRows: 12, executions: 1 }] },
    { object: "dbo.Orders.PK_Orders", baseline: [{ physicalOp: "Clustered Index Scan", actualRows: 400, executions: 2 }], candidate: [] },
  ])
})

test("missing actual row and execution values stay unavailable", () => {
  const changes = planChanges(
    plan({ physicalOp: "Index Scan", objectName: "T", actualRows: 0 }),
    plan({ physicalOp: "Index Seek", objectName: "T", executions: 0 }),
  )
  expect(changes.accessDetails).toEqual([{
    object: "T", baseline: [{ physicalOp: "Index Scan", actualRows: 0, executions: null }],
    candidate: [{ physicalOp: "Index Seek", actualRows: null, executions: 0 }],
  }])
})

test("collects changed access and warnings across every statement", () => {
  const changes = planChanges(
    { statements: [
      { root: { physicalOp: "Index Scan", objectName: "A" }, missingIndexes: [] },
      { root: { physicalOp: "Table Scan", objectName: "B", warnings: ["Spill"] }, missingIndexes: [] },
    ] },
    { statements: [
      { root: { physicalOp: "Index Seek", objectName: "A" }, missingIndexes: [] },
      { root: { physicalOp: "Index Seek", objectName: "B" }, missingIndexes: [] },
    ] },
  )
  expect(changes.access.map(change => change.object)).toEqual(["A", "B"])
  expect(changes.warnings).toEqual([{ warning: "Spill", baseline: 1, candidate: 0 }])
})

test("identical plans have no access or warning changes", () => {
  const p = plan({ physicalOp: "Index Seek", objectName: "T", costFraction: 1 })
  expect(planChanges(p, p)).toMatchObject({ access: [], accessDetails: [], warnings: [] })
})

test("firstPlanHash chooses the earliest stored repetition and ordinal", () => {
  const variant = {
    plans: [
      { repetition: 2, ordinal: 0, hash: "later-repetition", stored: true },
      { repetition: 1, ordinal: 3, hash: "later-ordinal", stored: true },
      { repetition: 1, ordinal: 1, hash: "first", stored: true },
      { repetition: 0, ordinal: 0, hash: "not-stored", stored: false },
    ],
  } as VariantDetail
  expect(firstPlanHash(variant)).toBe("first")
  expect(firstPlanHash({ plans: [] } as VariantDetail)).toBeNull()
})
