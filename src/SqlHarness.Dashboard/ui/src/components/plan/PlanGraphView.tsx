import { Background, Controls, MarkerType, ReactFlow, ReactFlowProvider, useReactFlow, type Edge, type Node, type NodeProps } from "@xyflow/react"
import { useEffect, useMemo, useState } from "react"
import "@xyflow/react/dist/style.css"
import type { PlanGraph, PlanGraphNode, SourceMapLink } from "@/api/types"
import { formatNumber, formatPercent } from "@/lib/format"

const NODE_WIDTH = 250
const HORIZONTAL_GAP = 92
const ROW_HEIGHT = 154
const TREE_GAP = 72

export type PlanGraphDocument = { documentSha256: string; documentOrdinal: number; graph: PlanGraph }
export type PlanGraphSelection = { key: string; documentSha256: string; documentOrdinal: number; node: PlanGraphNode }
export type PlanGraphLink = { documentSha256: string; documentOrdinal: number; statementIndex: number; link: SourceMapLink }

type Confidence = SourceMapLink["state"]
type OperatorData = {
  selection: PlanGraphSelection
  confidence: Confidence
  rules: string[]
  highlighted: boolean
  selected: boolean
  collapsed: boolean
  hasChildren: boolean
  onSelect: (selection: PlanGraphSelection) => void
  onToggleCollapse: (key: string) => void
}
type FlowNode = Node<OperatorData, "operator">
type FlowEdge = Edge<{ rows: number | null }, "smoothstep">

function objectLabel(node: PlanGraphNode): string | null {
  const object = node.object
  if (!object) return null
  const name = [object.database, object.schema, object.table].filter(Boolean).join(".")
  return name ? `${name}${object.index ? ` · ${object.index}` : ""}${object.alias ? ` (${object.alias})` : ""}` : null
}

function nodeName(selection: PlanGraphSelection): string {
  return [selection.node.physicalOperator, objectLabel(selection.node)].filter(Boolean).join(" · ")
}

function confidenceClass(state: Confidence): string {
  switch (state) {
    case "exact": return "border-2 border-emerald-600 bg-card"
    case "inferred": return "border-2 border-dashed border-sky-600 bg-sky-500/5"
    case "ambiguous": return "border-2 border-dashed border-amber-600 bg-amber-500/5"
    case "none": return "border border-border bg-muted/60"
  }
}

function confidenceRank(state: Confidence): number {
  return state === "exact" ? 4 : state === "inferred" ? 3 : state === "ambiguous" ? 2 : 1
}

function NodeCard({ data }: NodeProps<FlowNode>) {
  const { selection, confidence, rules, highlighted, selected, collapsed, hasChildren, onSelect, onToggleCollapse } = data
  const name = nodeName(selection)
  const node = selection.node
  const title = rules.length ? rules.join("\n") : `No source-map rule for ${name}`
  return (
    <article className={`min-w-[230px] max-w-[250px] rounded-lg ${confidenceClass(confidence)} ${highlighted ? "ring-4 ring-primary/50" : ""} ${selected ? "outline outline-2 outline-primary" : ""}`} data-confidence={confidence}>
      <button
        type="button"
        className="block w-full rounded-t-md px-3 py-2 text-left focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
        aria-label={`${name}; ${confidence} source match`}
        aria-pressed={selected || highlighted}
        title={title}
        onClick={event => { event.stopPropagation(); onSelect(selection) }}
        onKeyDown={event => {
          if (event.key !== "Enter" && event.key !== " ") return
          event.preventDefault()
          event.stopPropagation()
          onSelect(selection)
        }}
      >
        <span className="flex items-start justify-between gap-2">
          <span className="font-medium">{node.physicalOperator}</span>
          {node.warnings.length > 0 && <span className="rounded-full border border-amber-600/50 px-1.5 text-xs text-amber-800 dark:text-amber-200" title={node.warnings.join("; ")}>{node.warnings.length} warning{node.warnings.length === 1 ? "" : "s"}</span>}
        </span>
        {node.logicalOperator && node.logicalOperator !== node.physicalOperator && <span className="block text-xs text-muted-foreground">{node.logicalOperator}</span>}
        {objectLabel(node) && <span className="mt-1 block truncate font-mono text-xs" title={objectLabel(node) ?? undefined}>{objectLabel(node)}</span>}
        <span className="mt-2 flex flex-wrap gap-x-3 text-xs text-muted-foreground">
          <span>Est. {formatNumber(node.estimatedRows)}</span>
          <span>Actual {formatNumber(node.actualRows)}</span>
        </span>
        <span className="mt-1 flex items-center justify-between text-xs">
          <span>Cost {formatPercent(node.costFraction)}</span>
          <span className="capitalize">{confidence}</span>
        </span>
      </button>
      {hasChildren && <div className="flex justify-end border-t border-border/70 px-2 py-1">
        <button
          type="button"
          className="rounded px-2 py-1 text-xs underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
          aria-label={`${collapsed ? "Expand" : "Collapse"} subtree for ${name}`}
          onClick={event => { event.stopPropagation(); onToggleCollapse(selection.key) }}
        >{collapsed ? "Expand" : "Collapse"}</button>
      </div>}
    </article>
  )
}

const nodeTypes = { operator: NodeCard }

function nodeKey(document: PlanGraphDocument, nodeId: string): string {
  return `${document.documentSha256}:${document.documentOrdinal}:${nodeId}`
}

function edgeWidth(rows: number | null): number {
  if (rows === null || !Number.isFinite(rows) || rows <= 0) return 1.5
  return Math.min(8, 1.5 + Math.log10(rows + 1))
}

function visibleTreeLayout(document: PlanGraphDocument, collapsed: Set<string>) {
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
    positions.set(nodeKey(document, id), { x: (deepest - depth) * (NODE_WIDTH + HORIZONTAL_GAP), y })
    return y
  }
  for (const root of rootIds) {
    place(root, 0, row, maxDepth(root), new Set())
    row += height(root) + Math.max(0.5, TREE_GAP / ROW_HEIGHT)
  }

  const visible = new Set(positions.keys())
  const flowNodes = graph.nodes.flatMap(node => {
    const key = nodeKey(document, node.id)
    const position = positions.get(key)
    return position ? [{ node, key, position, children: children.get(node.id) ?? [] }] : []
  })
  const flowEdges = graph.edges.filter(edge => visible.has(nodeKey(document, edge.source)) && visible.has(nodeKey(document, edge.target)))
  return { flowNodes, flowEdges }
}

function FlowCanvas({
  nodes,
  edges,
  fitTargets,
  fitVersion,
}: {
  nodes: FlowNode[]
  edges: FlowEdge[]
  fitTargets: string[]
  fitVersion: number
}) {
  const flow = useReactFlow<FlowNode, FlowEdge>()
  useEffect(() => {
    if (fitTargets.length === 0) return
    const targets = nodes.filter(node => fitTargets.includes(node.id))
    if (targets.length) void flow.fitView({ nodes: targets, padding: 0.22, duration: 250 })
  }, [fitTargets, fitVersion, flow, nodes])
  return <ReactFlow<FlowNode, FlowEdge>
    nodes={nodes}
    edges={edges}
    nodeTypes={nodeTypes}
    fitView
    fitViewOptions={{ padding: 0.2 }}
    nodesConnectable={false}
    nodesDraggable={false}
    elementsSelectable={false}
    panOnDrag
    zoomOnScroll
    aria-label="Plan graph canvas"
  ><Background color="var(--border)" gap={24} /><Controls showInteractive={false} /></ReactFlow>
}

export function PlanGraphView({
  documents,
  links,
  selectedNodeKey,
  highlightedNodeKeys,
  onSelectNode,
}: {
  documents: PlanGraphDocument[]
  links: PlanGraphLink[]
  selectedNodeKey: string | null
  highlightedNodeKeys: string[]
  onSelectNode: (selection: PlanGraphSelection) => void
}) {
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set())
  const [fitVersion, setFitVersion] = useState(0)
  const graph = useMemo(() => {
    const allNodes: FlowNode[] = []
    const allEdges: FlowEdge[] = []
    const selectable = new Map<string, PlanGraphSelection>()
    for (const document of documents) {
      const layout = visibleTreeLayout(document, collapsed)
      const linksByNode = new Map<string, PlanGraphLink[]>()
      for (const link of links) {
        if (link.documentSha256 !== document.documentSha256 || link.documentOrdinal !== document.documentOrdinal || !link.link.nodeId) continue
        linksByNode.set(link.link.nodeId, [...(linksByNode.get(link.link.nodeId) ?? []), link])
      }
      for (const item of layout.flowNodes) {
        const key = item.key
        const selection = { key, documentSha256: document.documentSha256, documentOrdinal: document.documentOrdinal, node: item.node }
        selectable.set(key, selection)
        const nodeLinks = linksByNode.get(item.node.id) ?? []
        const state = nodeLinks.map(link => link.link.state).sort((a, b) => confidenceRank(b) - confidenceRank(a))[0] ?? "none"
        const rules = [...new Set(nodeLinks.map(link => link.link.rule).filter(Boolean))]
        const hasChildren = item.children.length > 0
        allNodes.push({
          id: key,
          type: "operator",
          position: item.position,
          style: { width: NODE_WIDTH, height: 128 },
          data: {
            selection,
            confidence: state,
            rules,
            selected: selectedNodeKey === key,
            highlighted: highlightedNodeKeys.includes(key),
            collapsed: collapsed.has(key),
            hasChildren,
            onSelect: onSelectNode,
            onToggleCollapse: collapseKey => setCollapsed(current => {
              const next = new Set(current)
              if (next.has(collapseKey)) next.delete(collapseKey)
              else next.add(collapseKey)
              return next
            }),
          },
          draggable: false,
          selectable: false,
        })
      }
      for (const edge of layout.flowEdges) {
        const rows = edge.actualRows ?? edge.estimatedRows
        allEdges.push({
          id: `${nodeKey(document, edge.source)}→${nodeKey(document, edge.target)}`,
          source: nodeKey(document, edge.source),
          target: nodeKey(document, edge.target),
          type: "smoothstep",
          markerEnd: { type: MarkerType.ArrowClosed, color: "var(--muted-foreground)" },
          style: { stroke: "var(--muted-foreground)", strokeWidth: edgeWidth(rows) },
          label: rows === null ? undefined : formatNumber(rows),
          labelStyle: { fill: "var(--muted-foreground)", fontSize: 10 },
          data: { rows },
        })
      }
    }
    return { nodes: allNodes, edges: allEdges, selectable }
  }, [collapsed, documents, highlightedNodeKeys, links, onSelectNode, selectedNodeKey])
  const fitTargets = useMemo(() => {
    if (highlightedNodeKeys.length) return highlightedNodeKeys
    return selectedNodeKey ? [selectedNodeKey] : []
  }, [highlightedNodeKeys, selectedNodeKey])

  return (
    <section aria-label="Graphical execution plan" className="space-y-2 rounded-lg border border-border bg-card text-card-foreground dark:border-border">
      <header className="flex flex-wrap items-center justify-between gap-2 px-3 pt-3">
        <div>
          <h3 className="font-medium">Graphical execution plan</h3>
          <p className="text-xs text-muted-foreground">Operators flow from left to right toward the root. Solid links are exact; dashed links are inferred or ambiguous.</p>
        </div>
        <button type="button" className="rounded border px-2 py-1 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring" disabled={fitTargets.length === 0} onClick={() => setFitVersion(value => value + 1)}>Fit selection</button>
      </header>
      <div className="flex flex-wrap gap-3 px-3 text-xs" aria-label="Source match confidence">
        <span data-confidence="exact">Exact · solid</span><span data-confidence="inferred">Inferred · dashed</span><span data-confidence="ambiguous">Ambiguous candidates</span><span data-confidence="none">No source link</span>
      </div>
      {documents.length === 0 || graph.nodes.length === 0
        ? <p className="px-3 pb-3 text-sm text-muted-foreground">No plan operators are available for this selection.</p>
        : <div className="h-[38rem] min-h-[30rem] overflow-hidden rounded-b-lg border-t border-border bg-background">
          <ReactFlowProvider>
            <FlowCanvas nodes={graph.nodes} edges={graph.edges} fitTargets={fitTargets} fitVersion={fitVersion} />
          </ReactFlowProvider>
        </div>}
    </section>
  )
}
