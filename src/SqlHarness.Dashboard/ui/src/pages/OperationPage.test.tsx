import { screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { operation, operationDetail, session, variant } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

test("measure shows facts, KPIs, table IO, waits and the sql-not-stored note", async () => {
  stubFetch({ "/api/operations/10": operationDetail() })
  renderApp("/operations/10")

  expect(await screen.findByRole("heading", { level: 1, name: "Operation #10" })).toBeInTheDocument()
  expect(screen.getByText("acme")).toBeInTheDocument()
  const kpis = screen.getByRole("region", { name: "Key metrics" })
  // Elapsed median (with its 10–14 range) and compile time are both 12 ms in the fixture.
  expect(within(kpis).getAllByText("12 ms")).toHaveLength(2)
  expect(within(kpis).getByText("10–14")).toBeInTheDocument()
  expect(within(kpis).getByText("512 KB / 8.0 MB")).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "Orders" })).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "PAGEIOLATCH_SH" })).toBeInTheDocument()
  expect(screen.getByText("SQL text is not stored (journal.storeSensitive is off).")).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "Download plan" })).not.toBeInTheDocument()
})

test("compare shows baseline versus candidate and stored plans with operators", async () => {
  const hash = "B".repeat(64)
  stubFetch({
    "/api/operations/10": operationDetail({
      operation: operation({ operation: "compare" }),
      sqlText: "SELECT 1",
      candidateSqlText: "SELECT 2",
      summary: { kind: "compare", comparison: "Ordered", resultsEquivalent: true, baselineOperators: [], candidateOperators: [] },
      variants: [
        variant({ ordinal: 0, variant: "baseline", plans: [{ repetition: 1, ordinal: 0, hash, stored: true }] }),
        variant({ ordinal: 1, variant: "candidate", elapsedMs: { min: 1, median: 6, max: 9 } }),
      ],
    }),
    // The server writes the statement text as "sql" and omits empty missing-index lists.
    [`/api/plans/${hash}?view=distilled`]: { statements: [{ sql: "SELECT 1 /* plan */", root: { physicalOp: "Clustered Index Scan", objectName: "Orders" } }] },
  })
  renderApp("/operations/10")

  const comparison = await screen.findByRole("region", { name: "Baseline vs candidate" })
  expect(within(comparison).getByText("-50%")).toBeInTheDocument()
  expect(screen.getByText("equivalent")).toBeInTheDocument()
  expect(screen.getByText("SELECT 2")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Download plan" })).toHaveAttribute("href", `/api/plans/${hash}`)

  await userEvent.click(screen.getByRole("button", { name: "Show operators" }))
  expect(await screen.findByText("Clustered Index Scan")).toBeInTheDocument()
  expect(screen.getByText("SELECT 1 /* plan */")).toBeInTheDocument()
})

test("Operation_without_metrics_or_target_renders", async () => {
  stubFetch({
    "/api/operations/10": operationDetail({
      operation: operation({ operation: "gain", profile: null, engine: null, server: null, database: null, sqlHash: null, durationMs: null, exitCode: null }),
      session: session(), vars: null, rawTokens: null, emittedTokens: null, artifactDirectory: null, summary: null, variants: [],
    }),
  })
  renderApp("/operations/10")
  expect(await screen.findByRole("heading", { level: 1, name: "Operation #10" })).toBeInTheDocument()
  expect(screen.queryByRole("region", { name: "Key metrics" })).not.toBeInTheDocument()
})

test("failed operation with a variant that has no metrics renders", async () => {
  stubFetch({
    "/api/operations/10": operationDetail({
      operation: operation({ operation: "measure", status: "failed", exitCode: 5, errorKind: "SqlExecution", finishedAt: null, rowsReturned: null }),
      summary: null,
      variants: [variant({
        elapsedMs: null, cpuMs: null, logicalReads: null, grantRequestedKb: null, grantGrantedKb: null, grantMaxUsedKb: null,
        dop: null, compileTimeMs: null, compileCpuMs: null, spillCount: 0, hasWarnings: false, hasImplicitConversion: false,
        missingIndexCount: 0, waits: null, postgres: null, tableIo: [], plans: [],
      })],
    }),
  })
  renderApp("/operations/10")
  expect(await screen.findByRole("heading", { level: 1, name: "Operation #10" })).toBeInTheDocument()
  expect(screen.getByText("5 (SqlExecution)")).toBeInTheDocument()
  expect(within(screen.getByRole("region", { name: "Key metrics" })).getByText("— / —")).toBeInTheDocument()
})

test("a non-numeric operation id shows not found without calling the api", async () => {
  const calls = stubFetch({})
  renderApp("/operations/abc")
  expect(await screen.findByText("Not found")).toBeInTheDocument()
  expect(calls).toEqual([])
})

test("measure-sets summary shows set stability", async () => {
  stubFetch({ "/api/operations/10": operationDetail({ summary: { kind: "measure-sets", sets: 3, stableSets: 2 } }) })
  renderApp("/operations/10")
  expect(await screen.findByText("2/3 sets stable")).toBeInTheDocument()
  expect(screen.getByText("unstable results")).toBeInTheDocument()
})

test("stable measure-sets summary has no unstable badge", async () => {
  stubFetch({ "/api/operations/10": operationDetail({ summary: { kind: "measure-sets", sets: 2, stableSets: 2 } }) })
  renderApp("/operations/10")
  expect(await screen.findByText("2/2 sets stable")).toBeInTheDocument()
  expect(screen.queryByText("unstable results")).not.toBeInTheDocument()
})

test("compare-matrix summary shows the matrix parameter and equivalent cells", async () => {
  stubFetch({
    "/api/operations/10": operationDetail({
      summary: { kind: "compare-matrix", parameterName: "BatchSize", parameterType: "int", cells: 3, equivalentCells: 2 },
    }),
  })
  renderApp("/operations/10")
  expect(await screen.findByText("parameter: BatchSize (int)")).toBeInTheDocument()
  expect(screen.getByText("2/3 cells equivalent")).toBeInTheDocument()
})
