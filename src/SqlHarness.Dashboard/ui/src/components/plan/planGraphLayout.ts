import type { PlanGraph, PlanGraphEdge, PlanGraphNode } from "@/api/types"

export const PLAN_NODE_WIDTH = 250

const HORIZONTAL_GAP = 92
const ROW_HEIGHT = 154
const TREE_GAP = 72

type LayoutDocument = { documentSha256: string; documentOrdinal: number; graph: PlanGraph }
type PositionedNode = { node: PlanGraphNode; key: string; position: { x: number; y: number }; children: string[] }

function nodeKey(document: LayoutDocument, nodeId: string): string {
  return `${document.documentSha256}:${document.documentOrdinal}:${nodeId}`
}

function visibleTreeLayout(document: LayoutDocument, collapsed: Set<string>) {
  const graph = document.graph
  const graphNodes = new Map(graph.nodes.map(node => [node.id, node]))
  const children = new Map<string, string[]>()
  const parents = new Set<string>()
  for (const edge of graph.edges) {
    if (!graphNodes.has(edge.source) || !graphNodes.has(edge.target)) continue
    children.set(edge.source, [...(children.get(edge.source) ?? []), edge.target])
    parents.add(edge.target)
  }
  const roots = graph.statements.map(statement => statement.rootNodeId).filter(id => graphNodes.has(id))
  const rootIds = roots.length ? roots : graph.nodes.filter(node => !parents.has(node.id)).map(node => node.id)
  const heightCache = new Map<string, number>()
  const depthCache = new Map<string, number>()
  const getChildren = (id: string) => collapsed.has(nodeKey(document, id)) ? [] : children.get(id) ?? []
  const height = (id: string, visited = new Set<string>()): number => {
    if (heightCache.has(id)) return heightCache.get(id)!
    if (visited.has(id)) return 1
    const nextVisited = new Set(visited).add(id)
    const value = getChildren(id).reduce((sum, child) => sum + height(child, nextVisited), 0) || 1
    heightCache.set(id, value)
    return value
  }
  const maxDepth = (id: string, visited = new Set<string>()): number => {
    if (depthCache.has(id)) return depthCache.get(id)!
    if (visited.has(id)) return 0
    const nextVisited = new Set(visited).add(id)
    const value = getChildren(id).reduce((maximum, child) => Math.max(maximum, 1 + maxDepth(child, nextVisited)), 0)
    depthCache.set(id, value)
    return value
  }

  const positions = new Map<string, { x: number; y: number }>()
  let row = 0
  const place = (id: string, depth: number, startRow: number, deepest: number, visited = new Set<string>()): number => {
    if (visited.has(id)) return (startRow + 0.5) * ROW_HEIGHT
    const nextVisited = new Set(visited).add(id)
    const nextChildren = getChildren(id)
    let y: number
    if (nextChildren.length === 0) y = (startRow + 0.5) * ROW_HEIGHT
    else {
      let childRow = startRow
      const childYs = nextChildren.map(child => {
        const childY = place(child, depth + 1, childRow, deepest, nextVisited)
        childRow += height(child, nextVisited)
        return childY
      })
      y = childYs.reduce((sum, value) => sum + value, 0) / childYs.length
    }
    positions.set(nodeKey(document, id), { x: (deepest - depth) * (PLAN_NODE_WIDTH + HORIZONTAL_GAP), y })
    return y
  }
  for (const root of rootIds) {
    place(root, 0, row, maxDepth(root), new Set())
    row += height(root) + Math.max(0.5, TREE_GAP / ROW_HEIGHT)
  }

  const visible = new Set(positions.keys())
  const flowNodes: PositionedNode[] = graph.nodes.flatMap(node => {
    const key = nodeKey(document, node.id)
    const position = positions.get(key)
    return position ? [{ node, key, position, children: children.get(node.id) ?? [] }] : []
  })
  const flowEdges: PlanGraphEdge[] = graph.edges.filter(edge => visible.has(nodeKey(document, edge.source)) && visible.has(nodeKey(document, edge.target)))
  return { flowNodes, flowEdges, height: row * ROW_HEIGHT }
}

/** Places document trees in separate vertical regions of one React Flow canvas. */
export function layoutPlanDocuments(documents: LayoutDocument[], collapsed: Set<string>) {
  let yOffset = 0
  return documents.map(document => {
    const layout = visibleTreeLayout(document, collapsed)
    const flowNodes = layout.flowNodes.map(item => ({
      ...item,
      position: { ...item.position, y: item.position.y + yOffset },
    }))
    yOffset += layout.height + TREE_GAP
    return { document, flowNodes, flowEdges: layout.flowEdges }
  })
}
