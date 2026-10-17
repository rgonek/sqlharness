import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, within } from "@testing-library/react"
import { expect, test } from "vitest"
import type { DistilledPlan } from "@/api/types"
import type { ComparePair } from "@/lib/compare"
import { variant } from "@/test/fixtures"
import { PlanChanges } from "./PlanChanges"
import { stubFetch } from "@/test/render"

const renderPlanChanges = (pair: ComparePair) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><PlanChanges pair={pair} /></QueryClientProvider>)
}

const plan: DistilledPlan = { statements: [{ root: { physicalOp: "Index Seek", objectName: "dbo.Orders", actualRows: 12, executions: 3 }, missingIndexes: [] }] }

test("same stored hash is stated explicitly without fetching plan details", async () => {
  const calls = stubFetch({ "/api/plans/shared?view=distilled": plan })
  renderPlanChanges({ label: "compare", baseline: variant({ plans: [{ repetition: 0, ordinal: 0, hash: "shared", stored: true }] }), candidate: variant({ variant: "candidate", plans: [{ repetition: 0, ordinal: 0, hash: "shared", stored: true }] }) })
  expect(await screen.findByText("Same plan")).toBeInTheDocument()
  expect(calls).toEqual([])
})

test("missing candidate plan is unavailable before hash equality", () => {
  renderPlanChanges({ label: "compare", baseline: variant({ plans: [{ repetition: 0, ordinal: 0, hash: "same", stored: true }] }), candidate: variant({ variant: "candidate", plans: [{ repetition: 0, ordinal: 1, hash: "same", stored: false }] }) })
  expect(screen.getByText("Plan not stored for the candidate.")).toBeInTheDocument()
})

test("renders access details and marks omitted actual rows and executions unavailable", async () => {
  stubFetch({
    "/api/plans/unchanged?view=distilled": plan,
    "/api/plans/baseline?view=distilled": { statements: [{ root: { physicalOp: "Clustered Index Scan", objectName: "dbo.Orders", actualRows: 40, executions: 2, warnings: ["Implicit conversion"] }, missingIndexes: [] }] },
    "/api/plans/candidate?view=distilled": { statements: [{ root: { physicalOp: "Index Seek", objectName: "dbo.Orders", actualRows: 5 }, missingIndexes: [{ table: "Orders", equalityColumns: ["CustomerId"], inequalityColumns: [], includeColumns: [], impact: 90 }] }] },
  })
  renderPlanChanges({ label: "compare", baseline: variant({ plans: [{ repetition: 0, ordinal: 0, hash: "baseline", stored: true }] }), candidate: variant({ variant: "candidate", plans: [{ repetition: 0, ordinal: 0, hash: "candidate", stored: true }] }) })
  const table = await screen.findByRole("table", { name: /Access changes/ })
  expect(within(within(table).getByRole("row", { name: /dbo.Orders/ })).getByText("Clustered Index Scan")).toBeInTheDocument()
  expect(within(within(table).getByRole("row", { name: /dbo.Orders/ })).getByText("Index Seek")).toBeInTheDocument()
  expect(screen.getByText("Clustered Index Scan: 40 rows / 2 executions")).toBeInTheDocument()
  expect(screen.getByText("Index Seek: 5 rows / unavailable executions")).toBeInTheDocument()
  expect(screen.getByText("Implicit conversion: resolved (1 → 0)")).toBeInTheDocument()
  expect(screen.getByText("Missing index: introduced (0 → 1)")).toBeInTheDocument()
})

test("renders access and warning deltas against each changed statement plan", async () => {
  const statementPlan = (physicalOp: string, objectName: string, warnings: string[] = []) => ({
    statements: [{ root: { physicalOp, objectName, warnings, actualRows: 10, executions: 1 }, missingIndexes: [] }],
  })
  stubFetch({
    "/api/plans/before1?view=distilled": statementPlan("Index Scan", "dbo.First"),
    "/api/plans/after1?view=distilled": statementPlan("Index Seek", "dbo.First"),
    "/api/plans/before2?view=distilled": statementPlan("Table Scan", "dbo.Second", ["Spill"]),
    "/api/plans/after2?view=distilled": statementPlan("Index Seek", "dbo.Second"),
  })
  renderPlanChanges({ label: "compare", baseline: variant({ plans: [
    { repetition: 0, ordinal: 0, hash: "unchanged", stored: true },
    { repetition: 0, ordinal: 1, hash: "before1", stored: true },
    { repetition: 0, ordinal: 2, hash: "before2", stored: true },
  ] }), candidate: variant({ variant: "candidate", plans: [
    { repetition: 0, ordinal: 0, hash: "unchanged", stored: true },
    { repetition: 0, ordinal: 1, hash: "after1", stored: true },
    { repetition: 0, ordinal: 2, hash: "after2", stored: true },
  ] }) })

  const first = await screen.findByRole("region", { name: "Statement 1 plan changes" })
  const second = await screen.findByRole("region", { name: "Statement 2 plan changes" })
  expect(within(first).getByRole("cell", { name: "dbo.First" })).toBeInTheDocument()
  expect(within(first).getByText("Index Scan")).toBeInTheDocument()
  expect(within(first).getByText("Index Seek")).toBeInTheDocument()
  expect(within(first).queryByText("dbo.Second")).not.toBeInTheDocument()
  expect(within(second).getByRole("cell", { name: "dbo.Second" })).toBeInTheDocument()
  expect(within(second).getByText("Table Scan")).toBeInTheDocument()
  expect(within(second).getByText("Index Seek")).toBeInTheDocument()
  expect(within(second).getByText("Spill: resolved (1 → 0)")).toBeInTheDocument()
})

test("shows plan request failures through ErrorState", async () => {
  stubFetch({
    "/api/plans/baseline?view=distilled": () => new Response("broken", { status: 500 }),
    "/api/plans/candidate?view=distilled": plan,
  })
  renderPlanChanges({ label: "compare", baseline: variant({ plans: [{ repetition: 0, ordinal: 0, hash: "baseline", stored: true }] }), candidate: variant({ variant: "candidate", plans: [{ repetition: 0, ordinal: 0, hash: "candidate", stored: true }] }) })
  expect(await screen.findByText("Request failed")).toBeInTheDocument()
  expect(screen.getByText(/500/)).toBeInTheDocument()
})

test("uses the statement text stored in plan XML and falls back to ordinal and hash when absent", async () => {
  stubFetch({
    "/api/plans/baseline?view=distilled": { statements: [{ root: { physicalOp: "Index Scan" } }] },
    "/api/plans/candidate?view=distilled": { statements: [{ root: { physicalOp: "Index Seek" } }] },
  })
  renderPlanChanges({ label: "compare", baseline: variant({ plans: [{ repetition: 0, ordinal: 0, hash: "baseline", stored: true }] }), candidate: variant({ variant: "candidate", plans: [{ repetition: 0, ordinal: 0, hash: "candidate", stored: true }] }) })
  expect(await screen.findByText("statement 0 (candidate)")).toBeInTheDocument()
})
