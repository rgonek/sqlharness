import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import type { ComparePair } from "@/lib/compare"
import { variant } from "@/test/fixtures"
import { stubFetch } from "@/test/render"
import { PlanChanges } from "./PlanChanges"

const baselineA = "a".repeat(64)
const baselineB = "b".repeat(64)
const baselineC = "f".repeat(64)
const candidateA = "c".repeat(64)
const candidateB = "d".repeat(64)
const shapeHash = "e".repeat(64)

const pair = (before = variant({ variant: "baseline", plans: [] }), after = variant({ variant: "candidate", plans: [] })): ComparePair => ({
  label: "compare · Case 1",
  baseline: before,
  candidate: after,
})

const occurrence = (variantName: string, documentSha256: string, repetition: number, operator: string, actualRows: number, documentOrdinal = 0) => ({
  variant: variantName,
  parameterSet: null,
  caseId: "0",
  caseLabel: "Case 1",
  repetition,
  documentOrdinal,
  statementIndex: 0,
  spanStart: 0,
  spanLength: 26,
  statementId: 1,
  statementCompId: 1,
  documentSha256,
  shapeHash,
  format: "showplan-xml",
  matchKind: "exact",
  omitted: false,
  stored: true,
  operator,
  actualRows,
})

const graph = (operator: string, actualRows: number) => ({
  engine: "sqlserver",
  error: null,
  graph: {
    engine: "sqlserver",
    statements: [{ index: 0, statementId: 1, statementCompId: 1, rootNodeId: "0:0" }],
    nodes: [{
      id: "0:0", statementIndex: 0, showplanNodeId: 1, physicalOperator: operator, logicalOperator: operator,
      object: { database: null, schema: "dbo", table: "Orders", index: null, alias: null }, tableReferenceId: null,
      predicates: [], definedValues: [], estimatedRows: 10, actualRows, executions: 1, costFraction: 1,
      warnings: actualRows > 20 ? ["Spill"] : [],
    }],
    edges: [],
  },
})

const manifest = (occurrences: unknown[]) => ({ operationId: 10, state: "stored", occurrences })
const sourceMap = (variantName: string, repetition: number) => ({
  operationId: 10, state: "stored", engine: "sqlserver", variant: variantName, parameterSet: null,
  caseId: "0", caseLabel: "Case 1", repetition, executedText: "SELECT * FROM dbo.Orders;",
  algorithmVersion: 1, links: [], omittedDocuments: [], errors: [],
})

const renderPlanChanges = (value: ComparePair = pair()) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><PlanChanges operationId={10} pair={value} /></QueryClientProvider>)
}

test("requires exact occurrence selection when same-shape actuals have multiple documents and repetitions", async () => {
  const calls = stubFetch({
    "/api/operations/10/plans": manifest([
      occurrence("baseline", baselineA, 1, "Index Scan", 40),
      occurrence("baseline", baselineB, 2, "Clustered Scan", 80),
      occurrence("baseline", baselineC, 2, "Hash Match", 90, 1),
      occurrence("candidate", candidateA, 1, "Index Seek", 5),
      occurrence("candidate", candidateB, 2, "Index Seek", 8),
    ]),
    "/api/operations/10/source-map": (url: URL) => new Response(JSON.stringify(sourceMap(url.searchParams.get("variant")!, Number(url.searchParams.get("repetition")))), { status: 200 }),
    [`/api/plan-documents/${baselineB}/graph`]: graph("Clustered Scan", 80),
    [`/api/plan-documents/${baselineC}/graph`]: graph("Hash Match", 90),
    [`/api/plan-documents/${candidateB}/graph`]: graph("Index Seek", 8),
  })
  renderPlanChanges()
  const before = await screen.findByRole("combobox", { name: "Baseline occurrence" }) as HTMLSelectElement
  const after = screen.getByRole("combobox", { name: "Candidate occurrence" }) as HTMLSelectElement
  expect(before).toHaveTextContent("Choose exact occurrence")
  expect(after).toHaveTextContent("Choose exact occurrence")
  expect(screen.getByText("Choose an exact baseline occurrence; multiple repetitions or documents are available.")).toBeInTheDocument()
  expect(calls.filter(url => url.endsWith("/graph"))).toEqual([])

  const baselineOptionLabel = `Repetition 2 · document 2 · statement 1 · ${baselineC.slice(0, 8)}`
  const candidateOptionLabel = `Repetition 2 · document 1 · statement 1 · ${candidateB.slice(0, 8)}`
  await userEvent.click(before)
  await userEvent.click(await screen.findByRole("option", { name: baselineOptionLabel }))
  await userEvent.click(after)
  await userEvent.click(await screen.findByRole("option", { name: candidateOptionLabel }))
  expect(before).toHaveTextContent(baselineOptionLabel)
  expect(after).toHaveTextContent(candidateOptionLabel)
  expect(await screen.findByRole("region", { name: "Statement 1 plan changes" })).toBeInTheDocument()
  expect(screen.getByText("Hash Match")).toBeInTheDocument()
  expect(screen.getByText("Index Seek")).toBeInTheDocument()
  expect(screen.getByText("Hash Match: 90 rows / 1 executions")).toBeInTheDocument()
  expect(calls).toContain(`/api/plan-documents/${baselineC}/graph`)
  expect(calls).toContain(`/api/plan-documents/${candidateB}/graph`)
  expect(calls.some(url => url.includes("source-map?variant=baseline&repetition=2&caseId=0"))).toBe(true)
  expect(calls.some(url => url.includes("source-map?variant=candidate&repetition=2&caseId=0"))).toBe(true)
})

test("compares a unique selected occurrence through renderer-neutral graph data and exact SQL spans", async () => {
  const calls = stubFetch({
    "/api/operations/10/plans": manifest([
      occurrence("baseline", baselineA, 2, "Index Scan", 40),
      occurrence("candidate", candidateA, 2, "Index Seek", 5),
    ]),
    "/api/operations/10/source-map": (url: URL) => {
      const variantName = url.searchParams.get("variant")!
      const repetition = Number(url.searchParams.get("repetition"))
      return new Response(JSON.stringify(sourceMap(variantName, repetition)), { status: 200 })
    },
    [`/api/plan-documents/${baselineA}/graph`]: graph("Index Scan", 40),
    [`/api/plan-documents/${candidateA}/graph`]: graph("Index Seek", 5),
  })
  renderPlanChanges()
  const region = await screen.findByRole("region", { name: "Statement 1 plan changes" })
  expect(within(region).getByText("dbo.Orders")).toBeInTheDocument()
  expect(within(region).getAllByText("SELECT * FROM dbo.Orders;")).toHaveLength(2)
  expect(screen.getByText("Index Scan: 40 rows / 1 executions")).toBeInTheDocument()
  expect(screen.getByText("Index Seek: 5 rows / 1 executions")).toBeInTheDocument()
  expect(calls).toContain(`/api/plan-documents/${baselineA}/graph`)
  expect(calls).toContain(`/api/plan-documents/${candidateA}/graph`)
})

test("reports identical raw documents without requesting or choosing an arbitrary occurrence", async () => {
  const calls = stubFetch({
    "/api/operations/10/plans": manifest([
      occurrence("baseline", baselineA, 1, "Index Scan", 40),
      occurrence("candidate", baselineA, 1, "Index Scan", 40),
    ]),
  })
  renderPlanChanges()
  expect(await screen.findByText("Same plan document")).toBeInTheDocument()
  expect(calls.filter(url => url.includes("/graph"))).toEqual([])
})

test("shows plans-not-stored state and surfaces graph 422 errors", async () => {
  stubFetch({ "/api/operations/10/plans": { operationId: 10, state: "plans not stored", occurrences: [] } })
  const firstRender = renderPlanChanges()
  expect(await screen.findByText(/Plans not stored/)).toBeInTheDocument()
  firstRender.unmount()

  stubFetch({
    "/api/operations/10/plans": manifest([
      occurrence("baseline", baselineA, 1, "Index Scan", 40),
      occurrence("candidate", candidateA, 1, "Index Seek", 5),
    ]),
    [`/api/plan-documents/${baselineA}/graph`]: () => new Response(JSON.stringify({ engine: "sqlserver", graph: null, error: { code: "invalid_plan_graph", message: "Plan document could not be parsed." } }), { status: 422, headers: { "Content-Type": "application/json" } }),
    [`/api/plan-documents/${candidateA}/graph`]: graph("Index Seek", 5),
  })
  renderPlanChanges()
  expect(await screen.findByRole("alert")).toHaveTextContent("422")
  expect(screen.getByRole("alert")).toHaveTextContent("invalid_plan_graph")
})
