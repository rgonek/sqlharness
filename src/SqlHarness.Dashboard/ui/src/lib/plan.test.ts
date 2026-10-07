import { expect, test } from "vitest"
import type { PlanNode } from "@/api/types"
import { flattenPlan } from "./plan"

test("flattens depth-first with depth", () => {
  const root: PlanNode = {
    physicalOp: "Sort", children: [{ physicalOp: "Hash Match", children: [{ physicalOp: "Index Seek" }] }, { physicalOp: "Scan" }],
  }
  expect(flattenPlan(root).map(row => [row.node.physicalOp, row.depth])).toEqual([
    ["Sort", 0], ["Hash Match", 1], ["Index Seek", 2], ["Scan", 1],
  ])
})
