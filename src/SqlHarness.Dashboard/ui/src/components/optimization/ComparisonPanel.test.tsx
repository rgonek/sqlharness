import { screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import type { OptimizationComparison } from "@/api/types"
import { operation, operationDetail, session, sessionDetail } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

const comparison: OptimizationComparison = {
  a: "v0", b: "v3", basis: "MeasuredTogether", operationId: 214, equivalenceLabel: "results equivalent (direct)",
  cases: [{ id: "customer-9001", label: "@CustomerId = 9001", outcome: "worse" }],
  selectedCase: "all", aggregate: "sum", coverageLabel: "", mixed: { better: 2, worse: 1, worstCaseLabel: "@CustomerId = 9001", worstChangePercent: 35 },
  tables: [{ table: "dbo.Orders", scansBefore: 4, scansAfter: 2, readsBefore: 100, readsAfter: 135, change: "+35%" }],
  collapsed: { count: 2, label: "2 smaller tables" }, elapsed: { beforeMs: 10, afterMs: 11, policyVerdict: null, differentRun: false },
  beforeAction: { operationId: 214, side: "baseline", caseIds: ["0", "customer-9001"] },
  afterAction: { operationId: 214, side: "candidate", caseIds: ["customer-9001", "1"] },
  view: "whole", verdictBasis: "whole batch",
}

function renderPanel(payload = comparison, versions = [{ version: "v0", hash: "h0", logicalReadsSum: 100, logicalReadsMean: 100, scansSum: 4, elapsedMedianMs: 10, caseOutcome: "1/1 ok", measuredIn: [214], latestOperationId: 214 }, { version: "v3", hash: "h3", logicalReadsSum: 135, logicalReadsMean: 135, scansSum: 2, elapsedMedianMs: 11, caseOutcome: "worse", measuredIn: [214], latestOperationId: 214 }], searchUrl = "/sessions/7?subject=Orders&db=shop&group=fp-1&scope=whole%20query&a=v0&b=v3&case=all&agg=sum") {
  const calls = stubFetch({
    "/api/sessions/7": sessionDetail({ session: session({ id: 7, hasCompare: true }), operations: [operation({ id: 70, operation: "compare" })] }),
    "/api/sessions/7/optimization": { subjects: [{ subject: "Orders", database: "shop", engine: "SqlServer", groups: [
      { fingerprint: "fp-1", label: "1 case", subject: "Orders", database: "shop", defaultA: "v0", defaultB: "v3", scopes: [{ scope: "whole query", defaultA: "v0", defaultB: "v3", versions }] },
      { fingerprint: "fp-2", label: "another group", subject: "Orders", database: "shop", defaultA: "v1", defaultB: "v2", scopes: [{ scope: "fragment:customer", defaultA: "v1", defaultB: "v2", versions: [
        { version: "v1", hash: "h1", logicalReadsSum: 120, logicalReadsMean: 120, scansSum: 3, elapsedMedianMs: 9, caseOutcome: "ok", measuredIn: [214], latestOperationId: 214 },
        { version: "v2", hash: "h2", logicalReadsSum: 100, logicalReadsMean: 100, scansSum: 2, elapsedMedianMs: 8, caseOutcome: "better", measuredIn: [214], latestOperationId: 214 },
      ] }] },
    ] }], unassigned: [] },
    "/api/sessions/7/optimization/compare": payload,
    "/api/operations/214": operationDetail({ operation: operation({ id: 214, sessionId: 7, operation: "compare" }), session: session({ id: 7, sessionKey: "mcp:session" }) }),
    "/api/operations/214/optimization": comparison,
    "/api/operations/214/optimization?case=all&aggregate=sum": comparison,
    "/api/operations/214/optimization?case=customer-9001&aggregate=mean": comparison,
  })
  return { ...renderApp(searchUrl), calls }
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
  expect(within(screen.getByRole("region", { name: "Version comparison" })).queryByRole("link", { name: /Operation #/ })).not.toBeInTheDocument()
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
    "View: Whole batch",
    "basis: whole batch",
    "Equivalence: results equivalent (direct)",
    "",
    "Mixed: 2 better, 1 worse — @CustomerId = 9001: logical reads +35%",
    "",
    "per-table IO not available for this engine",
  ].join("\n"))
})

test("SQL diff starts at the selected non-default A/B pair", async () => {
  const versions = ["v0", "v1", "v2", "v3"].map((version, index) => ({ version, hash: `h${index}`, logicalReadsSum: 10, logicalReadsMean: 10, scansSum: 1, elapsedMedianMs: 1, caseOutcome: "ok", measuredIn: [214], latestOperationId: 214 }))
  renderPanel({ ...comparison, a: "v1", b: "v2" }, versions, "/sessions/7?subject=Orders&db=shop&group=fp-1&scope=whole%20query&a=v1&b=v2&case=all&agg=sum")
  await userEvent.click(await screen.findByRole("button", { name: "SQL diff" }))
  expect(await screen.findByRole("combobox", { name: "From version" })).toHaveTextContent("v1")
  expect(screen.getByRole("combobox", { name: "To version" })).toHaveTextContent("v2")
})

test("reproduction actions resolve a selected fingerprint to each side's raw case ID", async () => {
  const cases = [
    { id: "fingerprint-a", label: "@CustomerId = 1", outcome: "same" },
    { id: "fingerprint-b", label: "@CustomerId = 2", outcome: "better" },
  ]
  const payload: OptimizationComparison = {
    ...comparison,
    cases,
    beforeAction: { operationId: 214, side: "baseline", caseIds: ["baseline-raw-a", "baseline-raw-b"],
      caseIdsByFingerprint: { "fingerprint-a": "baseline-raw-a", "fingerprint-b": "baseline-raw-b" } },
    afterAction: { operationId: 214, side: "candidate", caseIds: ["candidate-raw-a", "candidate-raw-b"],
      caseIdsByFingerprint: { "fingerprint-a": "candidate-raw-a", "fingerprint-b": "candidate-raw-b" } },
  }
  const { calls } = renderPanel(payload)

  await userEvent.click(await screen.findByRole("button", { name: "@CustomerId = 2" }))

  await expect.poll(() => calls.some(call => call.includes("/repro?") && call.includes("case=baseline-raw-b"))).toBe(true)
  await expect.poll(() => calls.some(call => call.includes("/repro?") && call.includes("case=candidate-raw-b"))).toBe(true)
  expect(calls.some(call => call.includes("/repro?") && call.includes("case=baseline-raw-a"))).toBe(true)
  expect(calls.some(call => call.includes("/repro?") && call.includes("case=candidate-raw-a"))).toBe(true)
})

test("operation navigation round-trips the selected multi-group session search", async () => {
  const { router } = renderPanel(comparison, undefined, "/sessions/7?subject=Orders&db=shop&group=fp-2&scope=fragment%3Acustomer&a=v1&b=v2&case=customer-9001&agg=mean")
  const planLink = await screen.findByRole("link", { name: "Plans for v0" })
  const planTarget = new URL(planLink.getAttribute("href")!, "http://dashboard")
  expect(Object.fromEntries(planTarget.searchParams)).toMatchObject({ returnSession: "7", returnSubject: "Orders", returnDb: "shop", returnGroup: "fp-2", returnScope: "fragment:customer", returnA: "v1", returnB: "v2", returnCase: "customer-9001", returnAgg: "mean" })
  await userEvent.click((await screen.findAllByRole("link", { name: "Operation #214" }))[0]!)
  const back = await screen.findByRole("link", { name: /mcp:session/ })
  const target = new URL(back.getAttribute("href")!, "http://dashboard")
  expect(target.pathname).toBe("/sessions/7")
  expect(Object.fromEntries(target.searchParams)).toMatchObject({ subject: "Orders", db: "shop", group: "fp-2", scope: "fragment:customer", a: "v1", b: "v2", case: "customer-9001", agg: "mean" })
  expect(router.state.location.pathname).toBe("/operations/214")
})

test("conditions-differ wording and collapsed row match their copied Markdown", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } })
  const { unmount } = renderPanel({ ...comparison, basis: "ConditionsDiffer", operationId: null, beforeAction: null, afterAction: null, mixed: null, tables: [], collapsed: null })
  expect(await screen.findByText("conditions differ; SQL diff is the available comparison.")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Copy summary" }))
  await expect.poll(() => writeText.mock.calls[0]?.[0]).toContain("conditions differ; SQL diff is the available comparison.")
  unmount()
  renderPanel({ ...comparison, mixed: null, tables: [{ table: "dbo.Orders", scansBefore: 4, scansAfter: 2, readsBefore: 100, readsAfter: 135, change: "+35%" }], collapsed: { count: 2, label: "2 smaller tables" } })
  expect(await screen.findByRole("row", { name: "2 smaller tables" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Copy summary" }))
  await expect.poll(() => writeText.mock.calls[1]?.[0]).toContain("| 2 smaller tables |")
  expect(writeText.mock.calls[1]?.[0]).not.toContain("collapsed")
})

test("shows PostgreSQL CPU and buffer totals in the panel and copied summary when reads are unavailable", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } })
  renderPanel({ ...comparison, tableIoUnavailable: true, tables: [], cpuBeforeMs: 12, cpuAfterMs: 8,
    buffersBefore: { sharedHit: 7, sharedRead: 2, sharedDirtied: 1, sharedWritten: 0, tempRead: 0, tempWritten: 3 },
    buffersAfter: { sharedHit: 5, sharedRead: 1, sharedDirtied: 0, sharedWritten: 0, tempRead: 0, tempWritten: 1 } })
  expect(await screen.findByText("12 → 8", { exact: false })).toBeInTheDocument()
  expect(screen.getByText(/shared hit 7, shared read 2/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Copy summary" }))
  await expect.poll(() => writeText.mock.calls[0]?.[0]).toContain("| CPU (ms) | 12 | 8 |")
  expect(writeText.mock.calls[0]?.[0]).toContain("| Buffers | shared hit 7, shared read 2, shared dirtied 1, shared written 0, temp read 0, temp written 3 | shared hit 5, shared read 1, shared dirtied 0, shared written 0, temp read 0, temp written 1 |")
})

test("view selection is URL-backed and the query receives the selected view", async () => {
  const payload = {
    ...comparison,
    view: "changed" as const,
    verdictBasis: "changed statements" as const,
    wholeBatch: { before: 500, after: 650, shared: 100, label: "whole batch: 500 → 650 reads (shared statements: 100)",
      sharedBefore: 100, sharedAfter: 100, changedBefore: 400, changedAfter: 550 },
    statements: [],
  }
  const { calls, router } = renderPanel(payload, undefined,
    "/sessions/7?subject=Orders&db=shop&group=fp-1&scope=whole%20query&a=v0&b=v3&case=all&agg=sum")

  expect(await screen.findByRole("button", { name: "Changed statements" })).toHaveAttribute("aria-pressed", "true")
  expect(screen.getByText("whole batch: 500 → 650 reads (shared statements: 100)")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Whole batch" }))
  expect(router.state.location.search).toMatchObject({ view: "whole" })
  await expect.poll(() => calls.some(call => new URL(call, "http://dashboard").searchParams.get("view") === "whole")).toBe(true)
  await userEvent.click(screen.getByRole("button", { name: "By statement" }))
  expect(router.state.location.search).toMatchObject({ view: "statements" })
})

test("incomplete attribution visibly falls back to Whole batch", async () => {
  renderPanel({ ...comparison, view: "whole", verdictBasis: "whole batch", fallbackNote: "per-statement attribution incomplete",
    wholeBatch: { before: 500, after: 650, shared: null, label: "whole batch: 500 → 650 reads (shared statements: unavailable)",
      sharedBefore: null, sharedAfter: null, changedBefore: null, changedAfter: null }, statements: [] })

  expect(await screen.findByText("per-statement attribution incomplete")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Whole batch" })).toHaveAttribute("aria-pressed", "true")
  expect(screen.getByText("basis: whole batch")).toBeInTheDocument()
})
