import { screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import type { OptimizationComparison } from "@/api/types"
import { operation, session, sessionDetail } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

const comparison: OptimizationComparison = {
  a: "v0", b: "v3", basis: "MeasuredTogether", operationId: 214, equivalenceLabel: "results equivalent (direct)",
  cases: [{ id: "customer-9001", label: "@CustomerId = 9001", outcome: "worse" }],
  selectedCase: "all", aggregate: "sum", coverageLabel: "", mixed: { better: 2, worse: 1, worstCaseLabel: "@CustomerId = 9001", worstChangePercent: 35 },
  tables: [{ table: "dbo.Orders", scansBefore: 4, scansAfter: 2, readsBefore: 100, readsAfter: 135, change: "+35%" }],
  collapsed: { count: 2, label: "2 smaller tables" }, elapsed: { beforeMs: 10, afterMs: 11, policyVerdict: null, differentRun: false },
  beforeAction: { operationId: 214, side: "baseline", caseIds: ["0", "customer-9001"] },
  afterAction: { operationId: 214, side: "candidate", caseIds: ["customer-9001", "1"] },
}

function renderPanel(payload = comparison) {
  stubFetch({
    "/api/sessions/7": sessionDetail({ session: session({ id: 7, hasCompare: true }), operations: [operation({ id: 70, operation: "compare" })] }),
    "/api/sessions/7/optimization": { subjects: [{ subject: "Orders", database: "shop", engine: "SqlServer", groups: [{ fingerprint: "fp-1", label: "1 case", subject: "Orders", database: "shop", defaultA: "v0", defaultB: "v3", scopes: [{ scope: "whole query", defaultA: "v0", defaultB: "v3", versions: [{ version: "v0", hash: "h0", logicalReadsSum: 100, logicalReadsMean: 100, scansSum: 4, elapsedMedianMs: 10, caseOutcome: "1/1 ok", measuredIn: [214], latestOperationId: 214 }, { version: "v3", hash: "h3", logicalReadsSum: 135, logicalReadsMean: 135, scansSum: 2, elapsedMedianMs: 11, caseOutcome: "worse", measuredIn: [214], latestOperationId: 214 }] }] }] }], unassigned: [] },
    "/api/sessions/7/optimization/compare": payload,
  })
  return renderApp("/sessions/7?subject=Orders&db=shop&group=fp-1&scope=whole%20query&a=v0&b=v3&case=all&agg=sum")
}

test("shows basis, case chips, mixed warning, collapsed rows and URL-backed selectors", async () => {
  const { router } = renderPanel()
  expect(await screen.findByText("measured together in #214")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "@CustomerId = 9001" })).toBeInTheDocument()
  expect(screen.getByText("Mixed: 2 better, 1 worse — @CustomerId = 9001: logical reads +35%")).toBeInTheDocument()
  expect(screen.getByText("2 smaller tables")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "@CustomerId = 9001" }))
  expect(router.state.location.search).toMatchObject({ case: "customer-9001" })
  await userEvent.click(screen.getByRole("button", { name: "Mean" }))
  expect(router.state.location.search).toMatchObject({ agg: "mean" })
})

test("shows different-run and conditions-differ badges without making up an operation link", async () => {
  const { unmount } = renderPanel({ ...comparison, basis: "DifferentRunsSameConditions", operationId: null, beforeAction: null, afterAction: null })
  expect(await screen.findByText("different runs, same conditions")).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: /Operation #/ })).not.toBeInTheDocument()
  unmount()

  renderPanel({ ...comparison, basis: "ConditionsDiffer", operationId: null, beforeAction: null, afterAction: null })
  expect(await screen.findByText("conditions differ")).toBeInTheDocument()
})

test("shows the engine IO message and action links and copies the panel Markdown", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } })
  const calls = stubFetch({
    "/api/sessions/7": sessionDetail({ session: session({ id: 7, hasCompare: true }), operations: [operation({ id: 70, operation: "compare" })] }),
    "/api/sessions/7/optimization": { subjects: [{ subject: "Orders", database: "shop", engine: "PostgreSql", groups: [{ fingerprint: "fp-1", label: "1 cases", subject: "Orders", database: "shop", defaultA: "v0", defaultB: "v3", scopes: [{ scope: "whole query", defaultA: "v0", defaultB: "v3", versions: [{ version: "v0", hash: "h0", logicalReadsSum: 100, logicalReadsMean: 100, scansSum: 4, elapsedMedianMs: 10, caseOutcome: "1/1 ok", measuredIn: [214], latestOperationId: 214 }, { version: "v3", hash: "h3", logicalReadsSum: 135, logicalReadsMean: 135, scansSum: 2, elapsedMedianMs: 11, caseOutcome: "worse", measuredIn: [214], latestOperationId: 214 }] }] }] }], unassigned: [] },
    "/api/sessions/7/optimization/compare": { ...comparison, tableIoUnavailable: true, tables: [], collapsed: null },
    "/api/operations/214/repro": { text: "SELECT 1", completeness: "Complete", label: "Ready" },
  })
  renderApp("/sessions/7?subject=Orders&db=shop&group=fp-1&scope=whole%20query&a=v0&b=v3&case=all&agg=sum")
  expect(await screen.findByText("per-table IO not available for this engine")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "SQL diff" })).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Plans for v0" })).toHaveAttribute("href", expect.stringContaining("/operations/214"))
  expect(screen.getAllByRole("link", { name: "Operation #214" }).every(link => link.getAttribute("href")?.includes("/operations/214"))).toBe(true)
  expect(await screen.findAllByRole("button", { name: "Copy script (as executed)" })).toHaveLength(2)
  await screen.findAllByRole("button", { name: "Copy script (local variables)" })
  expect(calls.filter(call => call.includes("/repro?")).every(call => call.includes("case=customer-9001"))).toBe(true)
  await userEvent.click(screen.getByRole("button", { name: "Copy summary" }))
  await expect.poll(() => writeText.mock.calls[0]?.[0]).toBe([
    "# Orders — whole query — v0 → v3",
    "Case: all cases (Sum)",
    "Basis: measured together in #214",
    "Equivalence: results equivalent (direct)",
    "",
    "Mixed: 2 better, 1 worse — @CustomerId = 9001: logical reads +35%",
    "",
    "per-table IO not available for this engine",
  ].join("\n"))
})
