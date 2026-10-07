import type { PlanNode } from "@/api/types"

export type PlanRow = { node: PlanNode; depth: number; key: string }

/** Depth-first rows of a distilled plan tree, with each node's depth for indentation. */
export function flattenPlan(node: PlanNode, depth = 0, key = "0"): PlanRow[] {
  return [
    { node, depth, key },
    ...(node.children ?? []).flatMap((child, index) => flattenPlan(child, depth + 1, `${key}.${index}`)),
  ]
}
