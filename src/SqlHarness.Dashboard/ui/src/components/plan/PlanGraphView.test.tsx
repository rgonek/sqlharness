import { describe, expect, test } from "vitest"
import type { PlanGraph, PlanGraphNode } from "@/api/types"
import { layoutPlanDocuments } from "./planGraphLayout"

function node(id: string, statementIndex: number): PlanGraphNode {
  return {
    id, statementIndex, showplanNodeId: null, physicalOperator: "Index Scan", logicalOperator: null,
    object: null, tableReferenceId: null, predicates: [], definedValues: [], estimatedRows: null,
    actualRows: null, executions: null, costFraction: null, warnings: [],
  }
}

function document(sha: string, ordinal: number, prefix: string, nodeCount: number) {
  const nodes = Array.from({ length: nodeCount }, (_, index) => node(`${prefix}-${index}`, 0))
  const edges = nodes.slice(1).map((item, index) => ({
    source: nodes[index].id,
    target: item.id,
    estimatedRows: null,
    actualRows: null,
    estimatedSizeBytes: null,
  }))
  const graph = {
    engine: "sqlserver",
    statements: [{ index: 0, statementId: null, statementCompId: null, rootNodeId: nodes[0].id }],
    nodes,
    edges,
  } satisfies PlanGraph
  return { documentSha256: sha, documentOrdinal: ordinal, graph }
}

describe("layoutPlanDocuments", () => {
  test("places every document in a disjoint canvas region using its actual node coordinates", () => {
    const layouts = layoutPlanDocuments([
      document("a".repeat(64), 0, "first", 4),
      document("b".repeat(64), 1, "second", 3),
    ], new Set())
    const firstDocumentNodes = layouts[0].flowNodes
    const secondDocumentNodes = layouts[1].flowNodes
    const firstBottom = Math.max(...firstDocumentNodes.map(item => item.position.y + 128))
    const secondTop = Math.min(...secondDocumentNodes.map(item => item.position.y))

    expect(firstDocumentNodes).toHaveLength(4)
    expect(secondDocumentNodes).toHaveLength(3)
    expect(secondTop).toBeGreaterThan(firstBottom)
  })
})
