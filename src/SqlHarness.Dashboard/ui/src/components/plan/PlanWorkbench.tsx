import { lazy, Suspense, useCallback, useMemo, useState } from "react"
import { useQueries } from "@tanstack/react-query"
import type { PlanGraph, PlanOccurrenceManifest, PlanSourceMapLink } from "@/api/types"
import { fetchPlanGraph, queryKeys, useOperationPlans, usePlanSourceMap, type PlanSourceMapParams } from "@/api/queries"
import type { VariantDetail } from "@/api/types"
import type { SqlSourceSelection, SqlSourceSpan } from "@/lib/sqlDiff"
import { PlanErrorState } from "./PlanErrorState"
import type { PlanGraphDocument, PlanGraphLink, PlanGraphSelection } from "./PlanGraphView"
import { PlanPropertiesPanel } from "./PlanPropertiesPanel"
import { PlanSourceView } from "./PlanSourceView"
import { PlanStatementSwitcher } from "./PlanStatementSwitcher"

const LazyPlanGraphView = lazy(() => import("./PlanGraphView").then(module => ({ default: module.PlanGraphView })))

type SourceLinkRow = PlanGraphLink & { key: string }
type DocumentChoice = { documentSha256: string; documentOrdinal: number }
const documentKey = (document: DocumentChoice) => `${document.documentSha256}:${document.documentOrdinal}`

function uniqueOccurrences(occurrences: PlanOccurrenceManifest[]): PlanOccurrenceManifest[] {
  const seen = new Set<string>()
  return occurrences.filter(occurrence => {
    const key = `${occurrence.documentSha256 ?? "missing"}:${occurrence.documentOrdinal}:${occurrence.statementIndex}`
    if (seen.has(key)) return false
    seen.add(key)
    return true
  })
}

function uniqueDocuments(occurrences: PlanOccurrenceManifest[]): DocumentChoice[] {
  const documents = new Map<string, DocumentChoice>()
  for (const occurrence of occurrences) {
    if (!occurrence.stored || occurrence.omitted || !occurrence.documentSha256) continue
    const key = `${occurrence.documentSha256}:${occurrence.documentOrdinal}`
    documents.set(key, { documentSha256: occurrence.documentSha256, documentOrdinal: occurrence.documentOrdinal })
  }
  return [...documents.values()].sort((left, right) => left.documentOrdinal - right.documentOrdinal)
}

function nodeKey(documentSha256: string, documentOrdinal: number, nodeId: string): string {
  return `${documentSha256}:${documentOrdinal}:${nodeId}`
}

function statementIndexOf(graphNodeId: string | null): number | null {
  if (!graphNodeId) return null
  const separator = graphNodeId.indexOf(":")
  if (separator <= 0) return null
  const index = Number(graphNodeId.slice(0, separator))
  return Number.isInteger(index) && index >= 0 ? index : null
}

function statementGraph(graph: PlanGraph, statementIndex: number | null): PlanGraph {
  if (statementIndex === null) return graph
  const nodes = graph.nodes.filter(node => node.statementIndex === statementIndex)
  const nodeIds = new Set(nodes.map(node => node.id))
  return {
    ...graph,
    statements: graph.statements.filter(statement => statement.index === statementIndex),
    nodes,
    edges: graph.edges.filter(edge => nodeIds.has(edge.source) && nodeIds.has(edge.target)),
  }
}

function overlaps(link: PlanSourceMapLink["link"], selection: SqlSourceSelection): boolean {
  if (!selection || link.spanStart === null || link.spanLength === null || link.spanLength <= 0) return false
  return link.spanStart < selection.end && link.spanStart + link.spanLength > selection.start
}

function span(link: PlanSourceMapLink["link"]): SqlSourceSpan | null {
  return link.spanStart === null || link.spanLength === null || link.spanLength <= 0
    ? null
    : { start: link.spanStart, length: link.spanLength }
}

function matchingOccurrences(occurrences: PlanOccurrenceManifest[], variant: VariantDetail): PlanOccurrenceManifest[] {
  return occurrences.filter(occurrence => occurrence.variant === variant.variant && occurrence.parameterSet === variant.parameterSet)
}

export function PlanWorkbench({ operationId, variant, engine }: { operationId: number; variant: VariantDetail; engine: string | null }) {
  const manifest = useOperationPlans(operationId)
  const [selectedCase, setSelectedCase] = useState(variant.caseId)
  const [selectedRepetition, setSelectedRepetition] = useState<number | null>(null)
  const [selectedDocumentKey, setSelectedDocumentKey] = useState<string | "all">("all")
  const [selectedStatementIndex, setSelectedStatementIndex] = useState<number | null>(null)
  const [selectedNodeKey, setSelectedNodeKey] = useState<string | null>(null)
  const [sqlSelection, setSqlSelection] = useState<SqlSourceSelection>(null)
  const [revealSpan, setRevealSpan] = useState<SqlSourceSpan | null>(null)

  const variantOccurrences = useMemo(() => matchingOccurrences(manifest.data?.occurrences ?? [], variant), [manifest.data?.occurrences, variant])
  const caseOptions = useMemo(() => [...new Map(variantOccurrences.map(occurrence => [occurrence.caseId, occurrence.caseLabel])).entries()], [variantOccurrences])
  const caseId = caseOptions.some(([id]) => id === selectedCase) ? selectedCase : caseOptions[0]?.[0] ?? variant.caseId
  const caseOccurrences = useMemo(() => variantOccurrences.filter(occurrence => occurrence.caseId === caseId), [caseId, variantOccurrences])
  const repetitions = useMemo(() => [...new Set(caseOccurrences.map(occurrence => occurrence.repetition))].sort((left, right) => left - right), [caseOccurrences])
  const defaultRepetition = repetitions.length ? repetitions[repetitions.length - 1] : null
  const repetition = selectedRepetition !== null && repetitions.includes(selectedRepetition) ? selectedRepetition : defaultRepetition
  const occurrences = useMemo(() => uniqueOccurrences(caseOccurrences.filter(occurrence => occurrence.repetition === repetition)), [caseOccurrences, repetition])
  const statementOptions = useMemo(() => [...new Set(occurrences.map(occurrence => occurrence.statementIndex))].sort((left, right) => left - right), [occurrences])
  const statementIndex = selectedStatementIndex !== null && statementOptions.includes(selectedStatementIndex) ? selectedStatementIndex : statementOptions[0] ?? null
  const documentOptions = useMemo(() => uniqueDocuments(occurrences), [occurrences])
  const documentSelection = selectedDocumentKey === "all" || documentOptions.some(option => documentKey(option) === selectedDocumentKey)
    ? selectedDocumentKey
    : "all"
  const sourceSelection: PlanSourceMapParams | null = repetition === null || !caseId
    ? null
    : { variant: variant.variant, parameterSet: variant.parameterSet, repetition, caseId }
  const sourceMap = usePlanSourceMap(operationId, sourceSelection)

  const statementDocuments = documentOptions.filter(document => occurrences.some(occurrence =>
    occurrence.documentSha256 === document.documentSha256
    && occurrence.documentOrdinal === document.documentOrdinal
    && occurrence.statementIndex === statementIndex))
  const visibleDocuments = documentSelection === "all"
    ? statementDocuments
    : statementDocuments.filter(option => documentKey(option) === documentSelection)
  const graphQueries = useQueries({
    queries: visibleDocuments.map(document => ({
      queryKey: queryKeys.planGraph(document.documentSha256),
      queryFn: () => fetchPlanGraph(document.documentSha256),
      staleTime: Infinity,
    })),
  })
  const graphDocuments = useMemo(() => visibleDocuments.flatMap((document, index) => {
    const result = graphQueries[index]?.data
    return result?.graph ? [{ ...document, graph: statementGraph(result.graph, statementIndex) } satisfies PlanGraphDocument] : []
  }), [graphQueries, statementIndex, visibleDocuments])
  const graphError = graphQueries.find(query => query.isError)?.error
    ?? graphQueries.map(query => query.data?.error).find(error => error !== null && error !== undefined)
    ?? graphQueries.map(query => query.data?.graph === null ? new Error("The plan graph response did not include a graph.") : null).find(Boolean)
  const isGraphLoading = graphQueries.some(query => query.isPending)

  const links: SourceLinkRow[] = useMemo(() => (sourceMap.data?.links ?? []).flatMap(item => {
    const documentId = `${item.documentSha256}:${item.documentOrdinal}`
    const candidates = occurrences.filter(occurrence => occurrence.documentSha256 === item.documentSha256 && occurrence.documentOrdinal === item.documentOrdinal)
    const linkStatementIndex = statementIndexOf(item.link.nodeId)
    const occurrence = linkStatementIndex === null
      ? candidates.length === 1 ? candidates[0] : null
      : candidates.find(candidate => candidate.statementIndex === linkStatementIndex)
    if (!occurrence) return []
    const key = item.link.nodeId
      ? nodeKey(item.documentSha256, item.documentOrdinal, item.link.nodeId)
      : `${documentId}:none:${item.link.spanStart ?? "null"}:${item.link.spanLength ?? "null"}`
    return [{ ...item, statementIndex: occurrence.statementIndex, key }]
  }), [occurrences, sourceMap.data?.links])
  const activeStatementLinks = links.filter(item => item.statementIndex === statementIndex)
  const selectedNodeLinks = selectedNodeKey ? links.filter(item => item.key === selectedNodeKey) : []
  const sqlMatchedLinks = sqlSelection ? links.filter(item => overlaps(item.link, sqlSelection)) : []
  const highlightedNodeKeys = sqlSelection
    ? [...new Set(sqlMatchedLinks.flatMap(item => item.link.nodeId ? [item.key] : []))]
    : selectedNodeKey ? [selectedNodeKey] : []
  const linksToDecorate = sqlSelection ? sqlMatchedLinks : selectedNodeLinks.length ? selectedNodeLinks : activeStatementLinks
  const highlightedSpans = useMemo(() => {
    const seen = new Set<string>()
    return linksToDecorate.flatMap(item => {
      const value = span(item.link)
      if (!value) return []
      const key = `${value.start}:${value.length}`
      if (seen.has(key)) return []
      seen.add(key)
      return [value]
    })
  }, [linksToDecorate])

  const selectionByKey = useMemo(() => {
    const map = new Map<string, PlanGraphSelection>()
    for (const document of graphDocuments) {
      for (const node of document.graph.nodes) {
        const key = nodeKey(document.documentSha256, document.documentOrdinal, node.id)
        map.set(key, { key, documentSha256: document.documentSha256, documentOrdinal: document.documentOrdinal, node })
      }
    }
    return map
  }, [graphDocuments])
  const selectedNode = selectedNodeKey ? selectionByKey.get(selectedNodeKey) ?? null : null
  const ambiguousCandidates = links.flatMap(item => {
    if (item.link.state !== "ambiguous" || !item.link.nodeId) return []
    const selection = selectionByKey.get(item.key)
    return selection ? [{ selection, rule: item.link.rule }] : []
  })
  const selectedRules = selectedNodeLinks.map(item => item.link.rule).filter(Boolean)

  const handleSelectNode = useCallback((selection: PlanGraphSelection) => {
    setSelectedNodeKey(selection.key)
    setSqlSelection(null)
    const first = links.find(item => item.key === selection.key && span(item.link) !== null)
    setRevealSpan(first ? span(first.link) : null)
  }, [links])

  const handleSqlSelection = useCallback((selection: SqlSourceSelection) => {
    setSqlSelection(selection)
    setSelectedNodeKey(null)
    setRevealSpan(null)
    if (!selection) return
    const match = links.filter(item => overlaps(item.link, selection)).sort((left, right) => left.statementIndex - right.statementIndex)[0]
    if (match) setSelectedStatementIndex(match.statementIndex)
  }, [links])

  if (manifest.isPending) return <section aria-label="Plans" className="rounded-lg border p-3"><p role="status">Loading plan documents…</p></section>
  if (manifest.error) return <PlanErrorState error={manifest.error} onRetry={() => void manifest.refetch()} />
  if (manifest.data.state === "plans not stored") return <section aria-label="Plans" className="rounded-lg border p-3"><h3 className="font-medium">Plans not stored</h3><p className="text-sm text-muted-foreground">Enable journal.storeSensitive to retain executed SQL, plan documents, and source links for this operation.</p></section>
  if (variantOccurrences.length === 0) return <section aria-label="Plans" className="rounded-lg border p-3"><h3 className="font-medium">No plans for this variant</h3><p className="text-sm text-muted-foreground">The operation manifest has no plan occurrence for {variant.variant}{variant.parameterSet ? ` · ${variant.parameterSet}` : ""}.</p></section>
  if (repetition === null || occurrences.length === 0) return <section aria-label="Plans" className="rounded-lg border p-3"><h3 className="font-medium">No plan occurrence for this case</h3></section>
  if (sourceMap.error) return <PlanErrorState error={sourceMap.error} onRetry={() => void sourceMap.refetch()} />
  if (sourceMap.data?.state === "plans not stored") return <section aria-label="Plans" className="rounded-lg border p-3"><h3 className="font-medium">Plans not stored</h3><p className="text-sm text-muted-foreground">Enable journal.storeSensitive to retain executed SQL, plan documents, and source links for this operation.</p></section>

  const source = sourceMap.data
  const selectedOccurrence = occurrences.find(item => item.statementIndex === statementIndex)
  const isUnmatched = selectedOccurrence?.matchKind === "unmatched" && !activeStatementLinks.some(item => item.link.state !== "none")
  const isOmitted = selectedOccurrence?.omitted === true || selectedOccurrence?.stored === false
  const selectedDocument = documentSelection === "all" ? null : documentOptions.find(item => documentKey(item) === documentSelection) ?? null
  const sourceLinks = source?.links ?? []
  const sourceMapErrors = source?.errors ?? []
  const sourceMapOmissions = source?.omittedDocuments ?? []
  const graphLinkItems: PlanGraphLink[] = links.map(item => ({
    documentSha256: item.documentSha256,
    documentOrdinal: item.documentOrdinal,
    statementIndex: item.statementIndex,
    link: item.link,
  }))

  return <section aria-label="Plans" className="space-y-3 rounded-lg border border-border p-3">
    <div className="flex flex-wrap items-end gap-3">
      <label className="grid gap-1 text-sm">Case
        <select aria-label="Case" className="rounded-md border bg-background px-2 py-1" value={caseId} onChange={event => {
          setSelectedCase(event.target.value)
          setSelectedRepetition(null)
          setSelectedDocumentKey("all")
          setSelectedStatementIndex(null)
          setSelectedNodeKey(null)
        }}>{caseOptions.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select>
      </label>
      {repetitions.length > 1 && <label className="grid gap-1 text-sm">Repetition
        <select aria-label="Repetition" className="rounded-md border bg-background px-2 py-1" value={repetition} onChange={event => {
          setSelectedRepetition(Number(event.target.value))
          setSelectedDocumentKey("all")
          setSelectedStatementIndex(null)
          setSelectedNodeKey(null)
          setSqlSelection(null)
        }}>{repetitions.map(value => <option key={value} value={value}>Repetition {value}</option>)}</select>
      </label>}
      {documentOptions.length > 0 && <label className="grid gap-1 text-sm">Plan document
        <select aria-label="Plan document" className="rounded-md border bg-background px-2 py-1" value={documentSelection} onChange={event => {
          setSelectedDocumentKey(event.target.value)
          setSelectedNodeKey(null)
          setSqlSelection(null)
        }}><option value="all">All plan documents</option>{documentOptions.map(document => <option key={documentKey(document)} value={documentKey(document)}>Document {document.documentOrdinal + 1} · {document.documentSha256.slice(0, 8)}</option>)}</select>
      </label>}
      <PlanStatementSwitcher occurrences={occurrences} selectedIndex={statementIndex} onChange={value => {
        setSelectedStatementIndex(value)
        setSelectedNodeKey(null)
        setSqlSelection(null)
        setRevealSpan(null)
      }} />
      {selectedDocument && <a className="rounded border px-2 py-1 text-sm underline" href={`/api/plan-documents/${selectedDocument.documentSha256}`} download>Download selected plan</a>}
    </div>

    {sourceMap.isPending && <p role="status" className="text-sm text-muted-foreground">Loading source links for repetition {repetition}…</p>}
    {isOmitted && <p className="rounded border border-amber-600/50 bg-amber-500/5 p-2 text-sm">This plan occurrence was omitted by the operation plan budget.</p>}
    {sourceMapOmissions.length > 0 && <p className="rounded border border-amber-600/50 bg-amber-500/5 p-2 text-sm">{sourceMapOmissions.length} plan occurrence(s) were omitted or not stored.</p>}
    {sourceMapErrors.map(error => <div key={`${error.documentOrdinal}:${error.code}`} className="rounded border border-amber-600/50 p-2 text-sm" role="status">Document {error.documentOrdinal + 1}: {error.code} — {error.message}</div>)}
    {isUnmatched && <p className="rounded border border-border bg-muted/50 p-2 text-sm">Unmatched SQL statement</p>}
    {!isUnmatched && !isOmitted && source && sourceLinks.length === 0 && <p className="rounded border border-border bg-muted/50 p-2 text-sm">No operator source links were produced for this statement.</p>}

    {source && <div className="grid gap-3 xl:grid-cols-2">
      <PlanSourceView executedText={source.executedText} engine={source.engine ?? engine} highlightedSpans={highlightedSpans} revealSpan={revealSpan} onSelectionChange={handleSqlSelection} />
      <div className="space-y-3">
        {graphError ? <PlanErrorState error={graphError} onRetry={() => { for (const query of graphQueries) void query.refetch() }} />
          : isGraphLoading ? <p role="status" className="rounded border p-3 text-sm">Loading plan graph…</p>
          : graphDocuments.length > 0 ? <Suspense fallback={<p role="status" className="rounded border p-3 text-sm">Loading graphical plan viewer…</p>}>
            <LazyPlanGraphView documents={graphDocuments} links={graphLinkItems} selectedNodeKey={selectedNodeKey} highlightedNodeKeys={highlightedNodeKeys} onSelectNode={handleSelectNode} />
          </Suspense>
          : !isUnmatched && !isOmitted && documentOptions.length > 0 ? <p role="status" className="rounded border p-3 text-sm text-muted-foreground">No operators are available for the selected plan document.</p> : null}
        {sqlSelection && highlightedNodeKeys.length > 0 && <p role="status" className="text-sm text-muted-foreground">Plan graph fitted to SQL selection</p>}
        <PlanPropertiesPanel selected={selectedNode} candidates={ambiguousCandidates} selectedRules={selectedRules} onSelect={handleSelectNode} />
      </div>
    </div>}
  </section>
}
