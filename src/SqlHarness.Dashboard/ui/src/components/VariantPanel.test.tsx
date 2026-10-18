import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { afterEach, expect, test, vi } from "vitest"
import type { VariantDetail } from "@/api/types"
import { variant as fixtureVariant } from "@/test/fixtures"
import { stubFetch } from "@/test/render"
import { VariantPanel } from "./VariantPanel"

const mockReactFlow = vi.hoisted(() => ({ fitView: vi.fn(async () => undefined) }))

vi.mock("@xyflow/react", async () => {
  const React = await import("react")
  return {
    Background: () => null,
    Controls: () => null,
    Handle: () => null,
    MarkerType: { ArrowClosed: "arrowclosed" },
    Position: { Left: "left", Right: "right" },
    ReactFlowProvider: ({ children }: { children: React.ReactNode }) => React.createElement("div", null, children),
    ReactFlow: ({ nodes, nodeTypes }: { nodes: { id: string; type?: string; data: unknown; position: { x: number; y: number } }[]; nodeTypes: Record<string, React.ComponentType<any>> }) => React.createElement("div", { "data-testid": "plan-flow", "data-highlighted-keys": nodes.filter(node => (node.data as { highlighted?: boolean }).highlighted).map(node => node.id).join("|") }, ...nodes.map(node => {
      const Node = nodeTypes[node.type ?? "operator"]
      return React.createElement(Node, { key: node.id, id: node.id, data: node.data, selected: false, dragging: false, type: node.type ?? "operator", zIndex: 0, xPos: node.position.x, yPos: node.position.y, isConnectable: false })
    })),
    useReactFlow: () => ({ fitView: mockReactFlow.fitView }),
  }
})

vi.mock("@/components/SqlEditors", async () => {
  const React = await import("react")
  return ({
  LazyPlanSqlViewer: ({
    sql,
    highlightedSpans,
    revealSpan,
    onSelectionChange,
  }: {
    sql: string
    highlightedSpans: { start: number; length: number }[]
    revealSpan: { start: number; length: number } | null
    onSelectionChange: (selection: { start: number; end: number }) => void
  }) => {
    const [selection, setSelection] = React.useState<{ start: number; end: number } | null>(null)
    return <div
      data-testid="monaco-contract"
      data-highlighted-spans={JSON.stringify(highlightedSpans)}
      data-revealed-span={JSON.stringify(revealSpan)}
      data-selection={JSON.stringify(selection)}
    >
      <textarea
        aria-label="Executed SQL"
        readOnly
        value={sql}
        onSelect={event => {
          const next = { start: event.currentTarget.selectionStart, end: event.currentTarget.selectionEnd }
          setSelection(next)
          onSelectionChange(next)
        }}
      />
    </div>
  },
  })
})

afterEach(() => document.documentElement.classList.remove("dark"))

const documentA = "a".repeat(64)
const documentB = "b".repeat(64)
const shapeHash = "c".repeat(64)
const batchSql = "SELECT id FROM dbo.Orders;\nSELECT id FROM dbo.Customers;"

const occurrence = (over: Record<string, unknown> = {}) => ({
  variant: "measure",
  parameterSet: "set-a",
  caseId: "case-a",
  caseLabel: "Gold customers",
  repetition: 2,
  documentOrdinal: 0,
  statementIndex: 0,
  spanStart: 0,
  spanLength: 27,
  statementId: 1,
  statementCompId: 1,
  documentSha256: documentA,
  shapeHash,
  format: "showplan-xml",
  matchKind: "exact",
  omitted: false,
  stored: true,
  ...over,
})

const sourceMap = {
  operationId: 10,
  state: "stored",
  engine: "sqlserver",
  variant: "measure",
  parameterSet: "set-a",
  caseId: "case-a",
  caseLabel: "Gold customers",
  repetition: 2,
  executedText: batchSql,
  algorithmVersion: 1,
  links: [
    { documentSha256: documentA, documentOrdinal: 0, link: { nodeId: "0:0", spanStart: 15, spanLength: 10, kind: "table_reference", state: "exact", rule: "alias o → dbo.Orders" } },
    { documentSha256: documentA, documentOrdinal: 0, link: { nodeId: "0:0/0", spanStart: 15, spanLength: 10, kind: "table_reference", state: "inferred", rule: "inferred through view dbo.OrderView" } },
    { documentSha256: documentA, documentOrdinal: 0, link: { nodeId: "0:0/1", spanStart: 15, spanLength: 10, kind: "table_reference", state: "ambiguous", rule: "candidate aliases o and orders" } },
    { documentSha256: documentA, documentOrdinal: 0, link: { nodeId: "0:0/2", spanStart: null, spanLength: null, kind: "table_reference", state: "none", rule: "no source reference survives" } },
    { documentSha256: documentB, documentOrdinal: 1, link: { nodeId: "1:0/0", spanStart: 42, spanLength: 13, kind: "table_reference", state: "exact", rule: "alias c → dbo.Customers" } },
  ],
  omittedDocuments: [],
  errors: [],
}

const graph = (objectName: string, statementIndex = 0) => ({
  engine: "sqlserver",
  graph: {
    engine: "sqlserver",
    statements: [{ index: statementIndex, statementId: 1, statementCompId: 1, rootNodeId: `${statementIndex}:0` }],
    nodes: [
      { id: `${statementIndex}:0`, statementIndex, showplanNodeId: 1, physicalOperator: "Nested Loops", logicalOperator: "Inner Join", object: null, tableReferenceId: null, predicates: ["o.OrderId = c.OrderId"], definedValues: [], estimatedRows: 1, actualRows: 1, executions: 1, costFraction: 0.4, warnings: [] },
      { id: `${statementIndex}:0/0`, statementIndex, showplanNodeId: 2, physicalOperator: "Index Seek", logicalOperator: "Index Seek", object: { database: "App", schema: "dbo", table: objectName, index: "IX_Orders", alias: "o" }, tableReferenceId: 1, predicates: ["OrderId > 0"], definedValues: [], estimatedRows: 8, actualRows: 11, executions: 1, costFraction: 0.3, warnings: ["Residual predicate"] },
      { id: `${statementIndex}:0/1`, statementIndex, showplanNodeId: 3, physicalOperator: "Table Scan", logicalOperator: "Table Scan", object: { database: "App", schema: "dbo", table: "OrdersArchive", index: null, alias: "orders" }, tableReferenceId: 2, predicates: [], definedValues: [], estimatedRows: 4, actualRows: 4, executions: 1, costFraction: 0.2, warnings: [] },
      { id: `${statementIndex}:0/2`, statementIndex, showplanNodeId: 4, physicalOperator: "Compute Scalar", logicalOperator: "Compute Scalar", object: null, tableReferenceId: null, predicates: [], definedValues: [], estimatedRows: 1, actualRows: 1, executions: 1, costFraction: 0.1, warnings: [] },
    ],
    edges: [
      { source: `${statementIndex}:0`, target: `${statementIndex}:0/0`, estimatedRows: 8, actualRows: 11, estimatedSizeBytes: 512 },
      { source: `${statementIndex}:0`, target: `${statementIndex}:0/1`, estimatedRows: 4, actualRows: 4, estimatedSizeBytes: 256 },
      { source: `${statementIndex}:0`, target: `${statementIndex}:0/2`, estimatedRows: 1, actualRows: 1, estimatedSizeBytes: 64 },
    ],
  },
  error: null,
})

function renderVariantPanel(value: VariantDetail = fixtureVariant({
  variant: "measure",
  parameterSet: "set-a",
  caseId: "case-a",
  caseLabel: "Gold customers",
  plans: [{ repetition: 1, ordinal: 0, hash: shapeHash, stored: true }, { repetition: 2, ordinal: 0, hash: shapeHash, stored: true }],
})) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><VariantPanel operationId={10} variant={value} engine="sqlserver" /></QueryClientProvider>)
}

test("uses occurrence identity and keeps graph selection linked to the exact batch SQL", async () => {
  const calls = stubFetch({
    "/api/operations/10/plans": {
      operationId: 10,
      state: "stored",
      occurrences: [
        occurrence({ repetition: 1, documentOrdinal: 0, documentSha256: documentB }),
        occurrence(),
        occurrence({ documentOrdinal: 1, statementIndex: 1, spanStart: 27, spanLength: 29, documentSha256: documentB }),
      ],
    },
    "/api/operations/10/source-map": (url: URL) => {
      const repetition = Number(url.searchParams.get("repetition"))
      return new Response(JSON.stringify({ ...sourceMap, repetition, executedText: repetition === 1 ? "SELECT id FROM dbo.OldOrders;" : batchSql }), { status: 200 })
    },
    [`/api/plan-documents/${documentA}/graph`]: graph("Orders"),
    [`/api/plan-documents/${documentB}/graph`]: graph("Customers", 1),
    [`/api/plan-documents/${documentA}/nodes/0%3A0%2F0/properties`]: {
      engine: "sqlserver", nodeId: "0:0/0", error: null,
      tree: { properties: [{ name: "Index Seek", attributes: [{ name: "Ordered", value: "true" }], children: [], text: null, isTruncationMarker: false, omittedNodeCount: 0 }], isTruncated: false, omittedNodeCount: 0 },
    },
  })

  renderVariantPanel()

  const repetition = await screen.findByRole("combobox", { name: "Repetition" })
  expect(repetition).toHaveValue("2")
  expect(screen.getByRole("combobox", { name: "Case" })).toHaveValue("case-a")
  expect(await screen.findByRole("textbox", { name: "Executed SQL" })).toHaveValue(batchSql)
  expect(calls).toContain(`/api/operations/10/source-map?variant=measure&parameterSet=set-a&repetition=2&caseId=case-a`)

  const seek = await screen.findByRole("button", { name: /Index Seek/ })
  expect(seek.closest("article")).toHaveAttribute("data-confidence", "inferred")
  expect(seek.closest("article")).toHaveClass("border-dashed", "border-sky-600")
  expect(seek).toHaveAttribute("title", expect.stringContaining("inferred through view"))
  await userEvent.click(seek)
  expect(screen.getByTestId("monaco-contract")).toHaveAttribute("data-highlighted-spans", JSON.stringify([{ start: 15, length: 10 }]))
  expect(screen.getByTestId("monaco-contract")).toHaveAttribute("data-revealed-span", JSON.stringify({ start: 15, length: 10 }))
  expect(await screen.findByText("11")).toBeInTheDocument()

  await userEvent.selectOptions(screen.getByRole("combobox", { name: "Statement" }), "1")
  expect(screen.getByRole("textbox", { name: "Executed SQL" })).toHaveValue(batchSql)

  const sqlEditor = screen.getByRole("textbox", { name: "Executed SQL" }) as HTMLTextAreaElement
  sqlEditor.setSelectionRange(42, 55)
  fireEvent.select(sqlEditor)
  await waitFor(() => expect(screen.getByTestId("monaco-contract")).toHaveAttribute("data-selection", JSON.stringify({ start: 42, end: 55 })))
  await waitFor(() => expect(screen.getByTestId("monaco-contract")).toHaveAttribute("data-highlighted-spans", JSON.stringify([{ start: 42, length: 13 }])))
  await waitFor(() => expect(screen.getByTestId("plan-flow")).toHaveAttribute("data-highlighted-keys", `${documentB}:1:1:0/0`))
  await waitFor(() => expect(screen.getByRole("button", { name: /Customers/ })).toHaveAttribute("aria-pressed", "true"))
  expect(screen.getByText("Plan graph fitted to SQL selection")).toBeInTheDocument()

  await userEvent.selectOptions(repetition, "1")
  expect(await screen.findByRole("textbox", { name: "Executed SQL" })).toHaveValue("SELECT id FROM dbo.OldOrders;")
  expect(calls).toContain(`/api/operations/10/source-map?variant=measure&parameterSet=set-a&repetition=1&caseId=case-a`)
  expect(screen.getByRole("textbox", { name: "Executed SQL" })).toHaveValue("SELECT id FROM dbo.OldOrders;")
})

test("labels plans not stored and unmatched occurrences without rendering an empty graph", async () => {
  stubFetch({
    "/api/operations/10/plans": { operationId: 10, state: "plans not stored", occurrences: [] },
  })
  const firstRender = renderVariantPanel(fixtureVariant({ plans: [] }))
  expect(await screen.findByText("Plans not stored")).toBeInTheDocument()
  expect(screen.queryByRole("region", { name: "Graphical execution plan" })).toBeNull()
  firstRender.unmount()

  stubFetch({
    "/api/operations/10/plans": { operationId: 10, state: "stored", occurrences: [occurrence({ matchKind: "unmatched", spanStart: null, spanLength: null })] },
    "/api/operations/10/source-map": { ...sourceMap, links: [], executedText: batchSql },
  })
  renderVariantPanel(fixtureVariant({ variant: "measure", parameterSet: "set-a", caseId: "case-a", caseLabel: "Gold customers", plans: [{ repetition: 2, ordinal: 0, hash: shapeHash, stored: true }] }))
  expect(await screen.findByText("Document 1, statement 1: unmatched SQL statement.")).toBeInTheDocument()
  expect(screen.queryByRole("region", { name: "Graphical execution plan" })).toBeNull()
})

test("uses theme tokens for the graph in dark mode and supports keyboard node selection", async () => {
  mockReactFlow.fitView.mockClear()
  document.documentElement.classList.add("dark")
  stubFetch({
    "/api/operations/10/plans": { operationId: 10, state: "stored", occurrences: [occurrence()] },
    "/api/operations/10/source-map": sourceMap,
    [`/api/plan-documents/${documentA}/graph`]: graph("Orders"),
    [`/api/plan-documents/${documentA}/nodes/0%3A0%2F0/properties`]: {
      engine: "sqlserver", nodeId: "0:0/0", error: null, tree: { properties: [], isTruncated: false, omittedNodeCount: 0 },
    },
  })
  renderVariantPanel()
  const canvas = await screen.findByRole("region", { name: "Graphical execution plan" })
  expect(canvas).toHaveClass("bg-card", "text-card-foreground")
  expect(canvas).toHaveClass("dark:border-border")

  const exact = await screen.findByRole("button", { name: /Nested Loops; exact source match/ })
  expect(exact.closest("article")).toHaveAttribute("data-confidence", "exact")
  expect(exact.closest("article")).toHaveClass("border-emerald-600")
  exact.focus()
  await userEvent.keyboard("{Enter}")
  expect(exact).toHaveAttribute("aria-pressed", "true")
  const fitButton = screen.getByRole("button", { name: "Fit selection" })
  expect(fitButton).toBeEnabled()
  await userEvent.click(fitButton)
  await waitFor(() => expect(mockReactFlow.fitView).toHaveBeenCalled())

  await userEvent.click(screen.getByRole("button", { name: "Collapse subtree for Nested Loops" }))
  expect(within(canvas).queryByRole("button", { name: /Index Seek/ })).toBeNull()
  await userEvent.click(screen.getByRole("button", { name: "Expand subtree for Nested Loops" }))
  expect(await within(canvas).findByRole("button", { name: /Index Seek/ })).toBeInTheDocument()

  const inferred = screen.getByRole("button", { name: /Index Seek/ })
  expect(inferred.closest("article")).toHaveAttribute("data-confidence", "inferred")
  expect(inferred.closest("article")).toHaveClass("border-dashed", "border-sky-600")
  const ambiguous = within(canvas).getByRole("button", { name: /Table Scan.*ambiguous source match/ })
  expect(ambiguous.closest("article")).toHaveAttribute("data-confidence", "ambiguous")
  expect(ambiguous.closest("article")).toHaveClass("border-dashed", "border-amber-600")
  expect(ambiguous).toHaveAttribute("title", expect.stringContaining("candidate aliases"))
  const none = screen.getByRole("button", { name: /Compute Scalar/ })
  expect(none.closest("article")).toHaveAttribute("data-confidence", "none")
  expect(none.closest("article")).toHaveClass("border-border")
})

test("surfaces a bounded graph response error instead of an empty plan", async () => {
  stubFetch({
    "/api/operations/10/plans": { operationId: 10, state: "stored", occurrences: [occurrence()] },
    "/api/operations/10/source-map": sourceMap,
    [`/api/plan-documents/${documentA}/graph`]: () => new Response(JSON.stringify({ engine: "sqlserver", graph: null, error: { code: "graph_response_size_limit_exceeded", message: "The complete plan graph response exceeds the response size limit." } }), { status: 413, headers: { "Content-Type": "application/json" } }),
  })
  renderVariantPanel()
  expect(await screen.findByRole("alert")).toHaveTextContent("413")
  expect(screen.getByRole("alert")).toHaveTextContent("graph_response_size_limit_exceeded")
  expect(screen.queryByRole("region", { name: "Graphical execution plan" })).toBeNull()
})
