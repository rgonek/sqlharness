import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import type { PlanGraph, PlanGraphNode } from "@/api/types"
import { variant } from "@/test/fixtures"
import { stubFetch } from "@/test/render"
import { PlanWorkbench } from "./PlanWorkbench"

const documentHash = "a".repeat(64)
const secondDocumentHash = "b".repeat(64)
const updatedDocumentHash = "c".repeat(64)
const node: PlanGraphNode = {
  id: "0:0", statementIndex: 0, showplanNodeId: 1, physicalOperator: "Index Seek", logicalOperator: "Index Seek",
  object: { database: null, schema: "dbo", table: "Orders", index: null, alias: null }, tableReferenceId: null,
  predicates: [], definedValues: [], estimatedRows: 10, actualRows: 5, executions: 1, costFraction: 1, warnings: [],
}
const secondNode: PlanGraphNode = { ...node, id: "1:0", statementIndex: 1, physicalOperator: "Clustered Scan" }
const graph: PlanGraph = {
  engine: "sqlserver", statements: [
    { index: 0, statementId: 1, statementCompId: 1, rootNodeId: node.id },
    { index: 1, statementId: 1, statementCompId: 1, rootNodeId: secondNode.id },
  ], nodes: [node, secondNode], edges: [],
}

vi.mock("./PlanGraphView", () => ({
  PlanGraphView: ({ documents, selectedNodeKey, onSelectNode }: {
    documents: { documentSha256: string; documentOrdinal: number; graph: PlanGraph }[]
    selectedNodeKey: string | null
    onSelectNode: (selection: { key: string; documentSha256: string; documentOrdinal: number; node: PlanGraphNode }) => void
  }) => {
    const document = documents[0]
    const selectedNode = document?.graph.nodes[0]
    const key = document && selectedNode ? `${document.documentSha256}:${document.documentOrdinal}:${selectedNode.id}` : ""
    return <div>
      <button type="button" onClick={() => document && selectedNode && onSelectNode({
        key, documentSha256: document.documentSha256, documentOrdinal: document.documentOrdinal, node: selectedNode,
      })}>Select operator</button>
      <output aria-label="Selected plan node">{selectedNodeKey ?? "No operator selected"}</output>
      <output aria-label="Visible plan graph nodes">{documents.flatMap(item => item.graph.nodes.map(graphNode => `${item.documentSha256}:${graphNode.statementIndex}`)).join(",")}</output>
    </div>
  },
}))

vi.mock("./PlanSourceView", () => ({
  PlanSourceView: ({ revealSpan, onSelectionChange }: {
    revealSpan: { start: number; length: number } | null
    onSelectionChange: (selection: { start: number; end: number } | null) => void
  }) => <div>
    <button type="button" onClick={() => onSelectionChange({ start: 0, end: 8 })}>Select SQL span</button>
    <output aria-label="Revealed SQL span">{revealSpan ? `${revealSpan.start}:${revealSpan.length}` : "No revealed span"}</output>
  </div>,
}))

const occurrence = (caseId: string, caseLabel: string, repetition: number, documentOrdinal: number, statementIndex: number, hash: string | null, omitted = false) => ({
  variant: "measure", parameterSet: null, caseId, caseLabel, repetition, documentOrdinal, statementIndex,
  spanStart: 0, spanLength: 8, statementId: 1, statementCompId: 1, documentSha256: hash,
  shapeHash: "shape", format: "showplan-xml", matchKind: "exact", omitted, stored: !omitted,
})

function renderWorkbench() {
  stubFetch({
    "/api/operations/10/plans": { operationId: 10, state: "stored", occurrences: [
      ...[1, 2].flatMap(repetition => [
        occurrence("case-a", "A very long initial case label that must remain readable", repetition, 0, 0, documentHash),
        occurrence("case-a", "A very long initial case label that must remain readable", repetition, 1, 0, secondDocumentHash),
        occurrence("case-a", "A very long initial case label that must remain readable", repetition, 1, 1, secondDocumentHash),
        occurrence("case-b", "Another very long case label after the update", repetition, 0, 1, updatedDocumentHash),
      ]),
      occurrence("case-c", "Omitted plan case", 1, 2, 0, null, true),
    ] },
    "/api/operations/10/source-map": (url: URL) => new Response(JSON.stringify({
      operationId: 10, state: "stored", engine: "sqlserver", variant: "measure", parameterSet: null,
      caseId: url.searchParams.get("caseId"), caseLabel: url.searchParams.get("caseId"),
      repetition: Number(url.searchParams.get("repetition")), executedText: "SELECT 1;", algorithmVersion: 1,
      links: [
        { documentSha256: documentHash, documentOrdinal: 0, link: { nodeId: node.id, spanStart: 0, spanLength: 8, kind: "statement", state: "exact", rule: "statement" } },
        { documentSha256: secondDocumentHash, documentOrdinal: 1, link: { nodeId: node.id, spanStart: 0, spanLength: 8, kind: "statement", state: "exact", rule: "statement" } },
        { documentSha256: secondDocumentHash, documentOrdinal: 1, link: { nodeId: secondNode.id, spanStart: 0, spanLength: 8, kind: "statement", state: "exact", rule: "statement" } },
        { documentSha256: updatedDocumentHash, documentOrdinal: 0, link: { nodeId: secondNode.id, spanStart: 0, spanLength: 8, kind: "statement", state: "exact", rule: "statement" } },
      ], omittedDocuments: [], errors: [],
    }), { status: 200, headers: { "Content-Type": "application/json" } }),
    [`/api/plan-documents/${documentHash}/graph`]: { engine: "sqlserver", graph, error: null },
    [`/api/plan-documents/${secondDocumentHash}/graph`]: { engine: "sqlserver", graph, error: null },
    [`/api/plan-documents/${updatedDocumentHash}/graph`]: { engine: "sqlserver", graph, error: null },
    [`/api/plan-documents/${documentHash}/nodes/${encodeURIComponent(node.id)}/properties`]: {
      engine: "sqlserver", nodeId: node.id, tree: null, error: null,
    },
    [`/api/plan-documents/${secondDocumentHash}/nodes/${encodeURIComponent(secondNode.id)}/properties`]: {
      engine: "sqlserver", nodeId: secondNode.id, tree: null, error: null,
    },
  })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><PlanWorkbench
    operationId={10} variant={variant({ variant: "measure", caseId: "case-a", caseLabel: "A very long initial case label that must remain readable" })}
    engine="sqlserver"
  /></QueryClientProvider>)
}

test("case, repetition, document and statement selections preserve their distinct reset paths", async () => {
  const user = userEvent.setup()
  renderWorkbench()

  const casePicker = await screen.findByRole("combobox", { name: "Case" })
  expect(casePicker).toHaveTextContent("A very long initial case label that must remain readable")
  expect(screen.queryByRole("combobox", { name: "Repetition" })).not.toBeInTheDocument()
  await user.click(screen.getByRole("button", { name: "Advanced" }))
  const repetitionPicker = await screen.findByRole("combobox", { name: "Repetition" })
  expect(repetitionPicker).toHaveTextContent("Repetition 2")
  const documentPicker = screen.getByRole("combobox", { name: "Plan document" })
  expect(documentPicker).toHaveTextContent("All plan documents")
  const statementPicker = screen.getByRole("combobox", { name: "Statement" })
  expect(statementPicker).toHaveTextContent("Statement 1")

  await user.click(await screen.findByRole("button", { name: "Select operator" }))
  expect(screen.getByLabelText("Selected plan node")).not.toHaveTextContent("No operator selected")
  expect(screen.getByLabelText("Revealed SQL span")).toHaveTextContent("0:8")

  await user.click(documentPicker)
  await user.click(await screen.findByRole("option", { name: `Document 2 · ${secondDocumentHash.slice(0, 8)}` }))
  expect(documentPicker).toHaveTextContent(`Document 2 · ${secondDocumentHash.slice(0, 8)}`)
  expect(screen.getByLabelText("Visible plan graph nodes")).toHaveTextContent(`${secondDocumentHash}:0`)
  expect(screen.getByLabelText("Selected plan node")).toHaveTextContent("No operator selected")
  expect(screen.getByLabelText("Revealed SQL span")).toHaveTextContent("0:8")

  await user.click(statementPicker)
  await user.click(await screen.findByRole("option", { name: "Statement 2" }))
  expect(statementPicker).toHaveTextContent("Statement 2")
  expect(screen.getByLabelText("Visible plan graph nodes")).toHaveTextContent(`${secondDocumentHash}:1`)
  expect(screen.getByLabelText("Revealed SQL span")).toHaveTextContent("No revealed span")

  await user.click(screen.getByRole("button", { name: "Select operator" }))
  expect(screen.getByLabelText("Selected plan node")).not.toHaveTextContent("No operator selected")
  await user.click(casePicker)
  await user.click(await screen.findByRole("option", { name: "Another very long case label after the update" }))
  expect(casePicker).toHaveTextContent("Another very long case label after the update")
  expect(screen.getByLabelText("Selected plan node")).toHaveTextContent("No operator selected")
  expect(documentPicker).toHaveTextContent("All plan documents")
  expect(statementPicker).toHaveTextContent("Statement 2")

  await user.click(screen.getByRole("button", { name: "Select SQL span" }))
  expect(screen.getByText("Plan graph fitted to SQL selection")).toBeInTheDocument()
  await user.click(repetitionPicker)
  await user.click(await screen.findByRole("option", { name: "Repetition 1" }))
  expect(repetitionPicker).toHaveTextContent("Repetition 1")
  expect(screen.getByLabelText("Selected plan node")).toHaveTextContent("No operator selected")
  expect(screen.getByLabelText("Revealed SQL span")).toHaveTextContent("No revealed span")
  expect(screen.queryByText("Plan graph fitted to SQL selection")).not.toBeInTheDocument()

  await user.click(casePicker)
  await user.click(await screen.findByRole("option", { name: "Omitted plan case" }))
  expect(screen.queryByRole("combobox", { name: "Plan document" })).not.toBeInTheDocument()
  expect(screen.getByText("Incomplete plan evidence: 1 fragment")).toBeVisible()
  expect(screen.queryByText(/this plan occurrence was omitted or not stored/)).not.toBeInTheDocument()
  await user.click(screen.getByRole("button", { name: "Missing evidence details" }))
  expect(screen.getByText(/this plan occurrence was omitted or not stored/)).toBeVisible()
})
