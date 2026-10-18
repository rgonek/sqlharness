import type { PlanGraph, PlanOccurrenceManifest, PlanSourceMapLink } from "@/api/types"

export type PlanGraphDocumentData = { documentSha256: string; documentOrdinal: number; graph: PlanGraph }
export type ResolvedPlanSourceLink = PlanSourceMapLink & { statementIndex: number; key: string }

export function planNodeKey(documentSha256: string, documentOrdinal: number, nodeId: string): string {
  return `${documentSha256}:${documentOrdinal}:${nodeId}`
}

/** Resolves link ownership from renderer-neutral graph metadata, never from a node-id spelling convention. */
export function resolvePlanSourceLinks(
  occurrences: PlanOccurrenceManifest[],
  sourceLinks: PlanSourceMapLink[],
  graphDocuments: PlanGraphDocumentData[],
): ResolvedPlanSourceLink[] {
  const statementByNode = new Map<string, number>()
  for (const document of graphDocuments) {
    for (const node of document.graph.nodes) {
      statementByNode.set(planNodeKey(document.documentSha256, document.documentOrdinal, node.id), node.statementIndex)
    }
  }

  return sourceLinks.flatMap(item => {
    const documentId = `${item.documentSha256}:${item.documentOrdinal}`
    const candidates = occurrences.filter(occurrence => occurrence.documentSha256 === item.documentSha256
      && occurrence.documentOrdinal === item.documentOrdinal)
    const statementIndex = item.link.nodeId
      ? statementByNode.get(planNodeKey(item.documentSha256, item.documentOrdinal, item.link.nodeId))
      : undefined
    const occurrence = statementIndex !== undefined
      ? candidates.find(candidate => candidate.statementIndex === statementIndex)
      : candidates.length === 1 ? candidates[0] : null
    if (!occurrence) return []
    const key = item.link.nodeId
      ? planNodeKey(item.documentSha256, item.documentOrdinal, item.link.nodeId)
      : `${documentId}:none:${item.link.spanStart ?? "null"}:${item.link.spanLength ?? "null"}`
    return [{ ...item, statementIndex: occurrence.statementIndex, key }]
  })
}
