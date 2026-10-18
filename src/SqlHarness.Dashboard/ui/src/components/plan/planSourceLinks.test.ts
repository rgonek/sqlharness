import { describe, expect, test } from "vitest"
import type { PlanGraph, PlanGraphNode, PlanOccurrenceManifest, PlanSourceMapLink } from "@/api/types"
import { resolvePlanSourceLinks } from "./planSourceLinks"

function occurrence(documentSha256: string, documentOrdinal: number, statementIndex: number): PlanOccurrenceManifest {
  return {
    variant: "baseline", parameterSet: null, caseId: "case-a", caseLabel: "Case A", repetition: 1,
    documentOrdinal, statementIndex, spanStart: 0, spanLength: 40, statementId: null,
    statementCompId: null, documentSha256, shapeHash: null, format: "showplan-xml",
    matchKind: "exact", omitted: false, stored: true,
  }
}

function graphNode(id: string, statementIndex: number): PlanGraphNode {
  return {
    id, statementIndex, showplanNodeId: null, physicalOperator: "Index Scan", logicalOperator: null,
    object: null, tableReferenceId: null, predicates: [], definedValues: [], estimatedRows: null,
    actualRows: null, executions: null, costFraction: null, warnings: [],
  }
}

function sourceLink(documentSha256: string, documentOrdinal: number, nodeId: string | null): PlanSourceMapLink {
  return {
    documentSha256, documentOrdinal,
    link: { nodeId, spanStart: 10, spanLength: 5, kind: "table_reference", state: "exact", rule: "fixture" },
  }
}

function graph(documentSha256: string, documentOrdinal: number, nodes: PlanGraphNode[]) {
  return { documentSha256, documentOrdinal, graph: { engine: "sqlserver", statements: [], nodes, edges: [] } satisfies PlanGraph }
}

describe("resolvePlanSourceLinks", () => {
  test("uses graph node statement indexes with opaque IDs and document-scoped identity", () => {
    const firstSha = "a".repeat(64)
    const secondSha = "b".repeat(64)
    const occurrences = [
      occurrence(firstSha, 0, 1),
      occurrence(firstSha, 0, 4),
      occurrence(secondSha, 1, 0),
    ]
    const resolved = resolvePlanSourceLinks(occurrences, [
      sourceLink(firstSha, 0, "root-node-without-statement-prefix"),
      sourceLink(firstSha, 0, "scan/path:variant"),
      sourceLink(secondSha, 1, "root-node-without-statement-prefix"),
    ], [
      graph(firstSha, 0, [graphNode("root-node-without-statement-prefix", 4), graphNode("scan/path:variant", 1)]),
      graph(secondSha, 1, [graphNode("root-node-without-statement-prefix", 0)]),
    ])

    expect(resolved.map(item => item.statementIndex)).toEqual([4, 1, 0])
    expect(resolved.map(item => item.key)).toEqual([
      `${firstSha}:0:root-node-without-statement-prefix`,
      `${firstSha}:0:scan/path:variant`,
      `${secondSha}:1:root-node-without-statement-prefix`,
    ])
  })

  test("does not guess statement ownership for an unresolved node with multiple candidate occurrences", () => {
    const sha = "c".repeat(64)
    const resolved = resolvePlanSourceLinks(
      [occurrence(sha, 0, 1), occurrence(sha, 0, 3)],
      [sourceLink(sha, 0, "node-not-in-the-loaded-graph")],
      [graph(sha, 0, [graphNode("other-node", 1)])],
    )

    expect(resolved).toEqual([])
  })
})
