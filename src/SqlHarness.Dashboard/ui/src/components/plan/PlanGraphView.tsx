import { Background, Controls, Handle, MarkerType, Position, ReactFlow, ReactFlowProvider, useReactFlow, type Edge, type Node, type NodeProps } from "@xyflow/react"
import { useEffect, useMemo, useState } from "react"
import "@xyflow/react/dist/style.css"
import type { PlanGraph, PlanGraphNode, SourceMapLink } from "@/api/types"
import { Button } from "@/components/ui/button"
import { formatNumber, formatPercent } from "@/lib/format"
import { layoutPlanDocuments, PLAN_NODE_WIDTH } from "./planGraphLayout"

const NODE_WIDTH = PLAN_NODE_WIDTH

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
    <article className={`w-[170px] rounded-lg ${confidenceClass(confidence)} ${highlighted ? "ring-4 ring-primary/50" : ""} ${selected ? "outline outline-2 outline-primary" : ""}`} data-confidence={confidence} style={{ pointerEvents: "auto" }}>
      <Handle type="source" position={Position.Left} isConnectable={false} aria-hidden style={{ width: 1, height: 1, opacity: 0, border: 0, pointerEvents: "none" }} />
      <Handle type="target" position={Position.Right} isConnectable={false} aria-hidden style={{ width: 1, height: 1, opacity: 0, border: 0, pointerEvents: "none" }} />
      {/* Native by design: the dense multi-line operator card is a React Flow domain surface. */}
      <button
        type="button"
        className="block w-full rounded-t-md px-2.5 py-2 text-left focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
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
        <span className="mt-1 flex flex-wrap gap-x-1 text-xs text-muted-foreground">
          <span>Est. {formatNumber(node.estimatedRows)}</span>
          <span>Act. {formatNumber(node.actualRows)}</span>
        </span>
        <span className="mt-1 flex items-center justify-between text-xs">
          <span>Cost {formatPercent(node.costFraction)}</span>
          <span className="capitalize">{confidence}</span>
        </span>
      </button>
      {hasChildren && <div className="flex justify-end border-t border-border/70 px-2 py-1">
        <Button
          type="button"
          variant="ghost"
          size="xs"
          className="underline"
          aria-label={`${collapsed ? "Expand" : "Collapse"} subtree for ${name}`}
          onClick={event => { event.stopPropagation(); onToggleCollapse(selection.key) }}
        >{collapsed ? "Expand" : "Collapse"}</Button>
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
    if (targets.length) void flow.fitView({ nodes: targets, padding: 0.22, maxZoom: 1, duration: 250 })
  }, [fitTargets, fitVersion, flow, nodes])
  return <ReactFlow<FlowNode, FlowEdge>
    nodes={nodes}
    edges={edges}
    nodeTypes={nodeTypes}
    fitView
    fitViewOptions={{ padding: 0.06, maxZoom: 1 }}
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
    for (const { document, flowNodes, flowEdges } of layoutPlanDocuments(documents, collapsed)) {
      const linksByNode = new Map<string, PlanGraphLink[]>()
      for (const link of links) {
        if (link.documentSha256 !== document.documentSha256 || link.documentOrdinal !== document.documentOrdinal || !link.link.nodeId) continue
        linksByNode.set(link.link.nodeId, [...(linksByNode.get(link.link.nodeId) ?? []), link])
      }
      for (const item of flowNodes) {
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
      for (const edge of flowEdges) {
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
    <section aria-label="Graphical execution plan" className="plan-graph space-y-2 rounded-lg border border-border bg-card text-card-foreground dark:border-border">
      <h3 className="sr-only">Graphical execution plan</h3>
      {documents.length === 0 || graph.nodes.length === 0
        ? <p className="px-3 pb-3 text-sm text-muted-foreground">No plan operators are available for this selection.</p>
        : <div className="plan-graph-canvas h-[250px] overflow-hidden rounded-b-lg border-t border-border bg-background">
          <ReactFlowProvider>
            <FlowCanvas nodes={graph.nodes} edges={graph.edges} fitTargets={fitTargets} fitVersion={fitVersion} />
          </ReactFlowProvider>
        </div>}
      <div className="plan-graph-footer"><span>Select SQL or an operator to highlight its match.</span><span>Solid = exact link · dashed = inferred or ambiguous</span><Button type="button" variant="ghost" size="sm" disabled={fitTargets.length === 0} onClick={() => setFitVersion(value => value + 1)}>Fit selection</Button></div>
    </section>
  )
}
