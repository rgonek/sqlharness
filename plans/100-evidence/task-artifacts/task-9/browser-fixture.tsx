import React, { useState } from "react"
import { createRoot } from "react-dom/client"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import "../../../../../src/SqlHarness.Dashboard/ui/src/index.css"
import type { DimensionMatrixCell, PlanGraphNode } from "../../../../../src/SqlHarness.Dashboard/ui/src/api/types"
import { Matrix } from "../../../../../src/SqlHarness.Dashboard/ui/src/pages/ProfileDimensionsPanel"
import { PlanGraphView, type PlanGraphSelection } from "../../../../../src/SqlHarness.Dashboard/ui/src/components/plan/PlanGraphView"
import { PlanPropertiesPanel } from "../../../../../src/SqlHarness.Dashboard/ui/src/components/plan/PlanPropertiesPanel"

const hash = "a".repeat(64)
const root: PlanGraphNode = { id: "root", statementIndex: 0, showplanNodeId: 1, physicalOperator: "Nested Loops", logicalOperator: "Inner Join", object: null, tableReferenceId: null, predicates: [], definedValues: [], estimatedRows: 123456789, actualRows: 987654321, executions: 1, costFraction: 0.75, warnings: [] }
const child: PlanGraphNode = { id: "child", statementIndex: 0, showplanNodeId: 2, physicalOperator: "Index Scan", logicalOperator: "Index Scan", object: { database: "very_long_database_name", schema: "very_long_schema_name", table: "very_long_table_name", index: null, alias: null }, tableReferenceId: null, predicates: [], definedValues: [], estimatedRows: 987654321, actualRows: 123456789, executions: 1, costFraction: 0.25, warnings: [] }
const selection: PlanGraphSelection = { key: `${hash}:0:child`, documentSha256: hash, documentOrdinal: 0, node: child }
const dimensionsValue = (name: string, value: string) => ({ name, value, isUnknown: false, source: "recorded", operations: 123456789, percentage: 100, totalDurationMs: 987654321, durationAvailableOperations: 123456789, durationUnavailableOperations: 0, failed: 1234, rejected: 23 })
const matrixCells: DimensionMatrixCell[] = [{ row: dimensionsValue("row", "a_long_dense_row_label"), column: dimensionsValue("column", "b_long_dense_column_label"), metrics: { operations: 123456789, totalDurationMs: 987654321, durationAvailableOperations: 123456789, durationUnavailableOperations: 0, failed: 1234, rejected: 23 } }]

function Fixture() {
  const [selectedNodeKey, setSelectedNodeKey] = useState<string | null>(null)
  const [candidateSelected, setCandidateSelected] = useState(false)
  const document = { documentSha256: hash, documentOrdinal: 0, graph: { engine: "sqlserver", statements: [{ index: 0, statementId: 1, statementCompId: 1, rootNodeId: "root" }], nodes: [root, child], edges: [{ source: "root", target: "child", estimatedRows: 987654321, actualRows: 123456789, estimatedSizeBytes: null }] } }
  return <QueryClientProvider client={new QueryClient()}>
    <main className="space-y-4 p-4">
      <div className="h-[32rem] max-w-5xl"><PlanGraphView documents={[document]} links={[]} selectedNodeKey={selectedNodeKey} highlightedNodeKeys={[]} onSelectNode={node => setSelectedNodeKey(node.key)} /></div>
      <output aria-label="Selected graph node">{selectedNodeKey ?? "none"}</output>
      <PlanPropertiesPanel selected={null} candidates={[{ selection, rule: "A deliberately long source match explanation that should wrap within the panel on a narrow viewport without expanding the whole page horizontally." }]} selectedRules={[]} onSelect={() => setCandidateSelected(true)} />
      <output aria-label="Candidate selected">{String(candidateSelected)}</output>
      <Matrix cells={matrixCells} totals={matrixCells[0].metrics} rowName="Dense row dimension" columnName="Dense column dimension" metric="operations" profile="fixture" range="7d" window={{ from: "2026-10-01T00:00:00.000Z", to: "2026-10-08T00:00:00.000Z" }} unprofiled={false} filters={{}} />
    </main>
  </QueryClientProvider>
}

createRoot(document.getElementById("app")!).render(<Fixture />)
