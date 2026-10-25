import type { ReactNode } from "react"
import type { PlanGraphPropertyNode } from "@/api/types"
import { Button } from "@/components/ui/button"
import { usePlanProperties } from "@/api/queries"
import { formatNumber, formatPercent } from "@/lib/format"
import { PlanErrorState } from "./PlanErrorState"
import type { PlanGraphSelection } from "./PlanGraphView"

type Candidate = { selection: PlanGraphSelection; rule: string }

function PropertyNode({ node }: { node: PlanGraphPropertyNode }) {
  return <li className="ml-3 border-l border-border pl-3">
    <span className="font-medium">{node.name}</span>
    {node.attributes.length > 0 && <dl className="ml-2 text-xs text-muted-foreground">{node.attributes.map((attribute, index) => <div key={`${attribute.name}:${index}`}><dt className="inline">{attribute.name}: </dt><dd className="inline break-all">{attribute.value}</dd></div>)}</dl>}
    {node.text && <p className="break-all text-xs text-muted-foreground">{node.text}</p>}
    {node.isTruncationMarker && <p className="text-xs text-muted-foreground">{node.omittedNodeCount} properties omitted</p>}
    {node.children.length > 0 && <ul className="mt-1 space-y-1">{node.children.map((child, index) => <PropertyNode key={`${child.name}:${index}`} node={child} />)}</ul>}
  </li>
}

function Value({ label, children }: { label: string; children: ReactNode }) {
  return <div><dt className="text-xs text-muted-foreground">{label}</dt><dd>{children}</dd></div>
}

export function PlanPropertiesPanel({
  selected,
  candidates,
  selectedRules,
  onSelect,
}: {
  selected: PlanGraphSelection | null
  candidates: Candidate[]
  selectedRules: string[]
  onSelect: (selection: PlanGraphSelection) => void
}) {
  const properties = usePlanProperties(selected?.documentSha256 ?? "", selected?.node.id ?? "", selected !== null)
  const node = selected?.node

  if (!selected && candidates.length === 0) return null

  return <aside aria-label="Plan operator properties" className="space-y-3 rounded-lg border border-border bg-card p-3 text-card-foreground">
    <div>
      <h3 className="font-medium">Operator properties</h3>
      {selected ? <p className="text-sm text-muted-foreground">{node?.physicalOperator}{node?.logicalOperator && node.logicalOperator !== node.physicalOperator ? ` · ${node.logicalOperator}` : ""}</p>
        : <p className="text-sm text-muted-foreground">Select an operator to inspect its properties.</p>}
    </div>

    {selected && node && <>
      <dl className="grid grid-cols-2 gap-2 text-sm sm:grid-cols-3">
        <Value label="Object">{node.object ? [node.object.database, node.object.schema, node.object.table].filter(Boolean).join(".") || "—" : "—"}</Value>
        <Value label="Index">{node.object?.index ?? "—"}</Value>
        <Value label="Alias">{node.object?.alias ?? "—"}</Value>
        <Value label="Estimated rows">{formatNumber(node.estimatedRows)}</Value>
        <Value label="Actual rows">{formatNumber(node.actualRows)}</Value>
        <Value label="Executions">{formatNumber(node.executions)}</Value>
        <Value label="Cost">{formatPercent(node.costFraction)}</Value>
        <Value label="Engine node id">{formatNumber(node.showplanNodeId)}</Value>
        <Value label="Table reference id">{formatNumber(node.tableReferenceId)}</Value>
      </dl>
      {selectedRules.length > 0 && <section aria-label="Source match rule" className="rounded bg-muted/60 p-2 text-xs">
        <h4 className="font-medium">Source match rule</h4>
        <ul className="mt-1 list-inside list-disc">{selectedRules.map((rule, index) => <li key={`${index}:${rule}`}>{rule}</li>)}</ul>
      </section>}
      {node.predicates.length > 0 && <section><h4 className="text-sm font-medium">Predicates</h4><ul className="list-inside list-disc break-words font-mono text-xs">{node.predicates.map((predicate, index) => <li key={`${index}:${predicate}`}>{predicate}</li>)}</ul></section>}
      {node.definedValues.length > 0 && <section><h4 className="text-sm font-medium">Defined values</h4><ul className="list-inside list-disc break-words font-mono text-xs">{node.definedValues.flatMap((value, index) => [
        ...value.columns.map((column, columnIndex) => <li key={`${index}:column:${columnIndex}`}>{[column.alias, column.table, column.column].filter(Boolean).join(".")}</li>),
        ...value.expressions.map((expression, expressionIndex) => <li key={`${index}:expression:${expressionIndex}`}>{expression}</li>),
      ])}</ul></section>}
      {node.warnings.length > 0 && <section><h4 className="text-sm font-medium">Warnings</h4><ul className="list-inside list-disc text-sm">{node.warnings.map((warning, index) => <li key={`${index}:${warning}`}>{warning}</li>)}</ul></section>}
      {properties.data?.error && <PlanErrorState error={properties.data.error} />}
      {!properties.data?.error && properties.data?.tree && <section aria-label="Complete plan properties">
        <h4 className="text-sm font-medium">Plan properties</h4>
        {properties.data.tree.isTruncated && <p className="text-xs text-muted-foreground">Property tree truncated; {properties.data.tree.omittedNodeCount} properties omitted.</p>}
        <ul className="mt-1 max-h-64 space-y-1 overflow-auto text-sm">{properties.data.tree.properties.map((property, index) => <PropertyNode key={`${property.name}:${index}`} node={property} />)}</ul>
      </section>}
      {!properties.data?.error && properties.isPending && <p role="status" className="text-xs text-muted-foreground">Loading complete properties…</p>}
      {properties.isError && <PlanErrorState error={properties.error} onRetry={() => void properties.refetch()} />}
    </>}

    {candidates.length > 0 && <section aria-label="Ambiguous candidates" className="space-y-1">
      <h4 className="text-sm font-medium">Ambiguous candidates</h4>
      <p className="text-xs text-muted-foreground">The source match has more than one possible operator. Choose a candidate to inspect it.</p>
      <ul className="space-y-1">{candidates.map(({ selection, rule }) => <li key={selection.key}>
        <Button type="button" variant="outline" className="h-auto min-h-8 w-full justify-start whitespace-normal border-dashed border-amber-600/60 px-2 py-1 text-left text-sm" title={rule} onClick={() => onSelect(selection)}>
          {selection.node.physicalOperator}{selection.node.object?.table ? ` · ${selection.node.object.table}` : ""}
        </Button>
      </li>)}</ul>
    </section>}
  </aside>
}
