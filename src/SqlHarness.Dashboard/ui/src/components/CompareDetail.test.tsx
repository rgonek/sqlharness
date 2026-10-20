import { fireEvent, render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { CompareDetail } from "./CompareDetail"
import { conditions, operation, operationDetail, variant } from "@/test/fixtures"
import { stubFetch } from "@/test/render"

const renderCompare = (detail: ReturnType<typeof operationDetail>, routes: Record<string, unknown | ((url: URL, init?: RequestInit) => Response)> = {}) => {
  const calls = stubFetch({ "/api/operations/10/repro": { text: null, completeness: "Unavailable", label: "Script not available — enable storeSensitive" }, ...routes })
  return { ...render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><CompareDetail detail={detail} /></QueryClientProvider>), calls }
}

test("compare panels start closed and can remain open together", async () => {
  renderCompare(operationDetail({ operation: operation({ operation: "compare" }) }))
  for (const label of ["SQL diff & conditions", "Plans", "Per-case & per-run details"]) {
    expect(screen.getByRole("button", { name: label })).toHaveAttribute("aria-expanded", "false")
    expect(screen.queryByRole("region", { name: label })).not.toBeInTheDocument()
  }
  for (const label of ["SQL diff & conditions", "Plans", "Per-case & per-run details"])
    await userEvent.click(screen.getByRole("button", { name: label }))
  for (const label of ["SQL diff & conditions", "Plans", "Per-case & per-run details"])
    expect(screen.getByRole("button", { name: label })).toHaveAttribute("aria-expanded", "true")
})

test("no assessment explains its meaning and keeps policy version in a tooltip", () => {
  renderCompare(operationDetail({
    operation: operation({ operation: "compare" }),
    compareCases: [{ index: 0, equivalence: { mode: "Ordered", equivalent: false }, regression: { policyVersion: 2 } }],
    variants: [variant({ ordinal: 0, variant: "baseline", caseId: "0", matrixCell: 0 }), variant({ ordinal: 1, variant: "candidate", caseId: "0", matrixCell: 0 })],
  }))
  expect(screen.getByText("No assessment")).toHaveAttribute("title", "No regression assessment is available for this comparison.")
  expect(screen.getByText("Results differ").closest("[data-tier='warning']")).not.toBeNull()
})

test("policy version appears only in the assessment tooltip", () => {
  renderCompare(operationDetail({
    operation: operation({ operation: "compare" }),
    compareCases: [{ index: 0, equivalence: { mode: "Ordered", equivalent: true }, regression: { verdict: "fail", policyVersion: 2 } }],
    variants: [variant({ ordinal: 0, variant: "baseline", caseId: "0", matrixCell: 0 }), variant({ ordinal: 1, variant: "candidate", caseId: "0", matrixCell: 0 })],
  }))
  expect(screen.getByText("Regression detected")).toHaveAttribute("title", "Policy v2")
  expect(screen.queryByText("policy v2", { exact: false })).toBeNull()
})

test("per-run details select one case and one side at a time", async () => {
  const onCaseChange = vi.fn()
  const detail = operationDetail({
    operation: operation({ operation: "compare" }),
    conditions: conditions({ cases: [
      { id: "0", index: 0, label: "@tenant = N'gold'", setName: null, varying: [], fingerprint: "a" },
      { id: "1", index: 1, label: "@tenant = N'silver'", setName: null, varying: [], fingerprint: "b" },
    ] }),
    compareCases: [
      { index: 0, equivalence: { mode: "Ordered", equivalent: true }, regression: { verdict: "pass" } },
      { index: 1, equivalence: { mode: "Ordered", equivalent: false }, regression: { verdict: "fail" } },
    ],
    variants: [
      variant({ ordinal: 0, variant: "baseline", caseId: "0", matrixCell: 0, elapsedMs: { min: 1, median: 11, max: 12 } }),
      variant({ ordinal: 1, variant: "candidate", caseId: "0", matrixCell: 0, elapsedMs: { min: 1, median: 12, max: 13 } }),
      variant({ ordinal: 2, variant: "baseline", caseId: "1", matrixCell: 1, elapsedMs: { min: 1, median: 21, max: 22 } }),
      variant({ ordinal: 3, variant: "candidate", caseId: "1", matrixCell: 1, elapsedMs: { min: 1, median: 22, max: 23 } }),
    ],
  })
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><CompareDetail detail={detail} selectedCase="1" onCaseChange={onCaseChange} /></QueryClientProvider>)
  await userEvent.click(screen.getByRole("button", { name: "Per-case & per-run details" }))
  const section = screen.getByRole("region", { name: "Per-case & per-run details" })
  const caseSelect = within(section).getByRole("combobox", { name: "Case" })
  expect(caseSelect).toHaveValue("1")
  fireEvent.change(caseSelect, { target: { value: "0" } })
  expect(onCaseChange).toHaveBeenCalledWith("0")
  await userEvent.selectOptions(within(section).getByRole("combobox", { name: "Side" }), "candidate")
  expect(within(section).getAllByRole("region", { name: "Key metrics" })).toHaveLength(1)
  expect(within(section).getByText("22 ms")).toBeInTheDocument()
  expect(within(section).queryByText("21 ms")).not.toBeInTheDocument()
  expect(within(section).getByText(/Results differ/)).toBeInTheDocument()
  expect(within(section).getByText(/Regression detected/)).toBeInTheDocument()
})

test("SQL, conditions, and plans stay within their disclosures", async () => {
  const detail = operationDetail({
    operation: operation({ operation: "compare" }),
    sqlText: "SELECT id FROM Orders", candidateSqlText: "SELECT id FROM Orders WHERE id > 0",
    conditions: conditions({ setup: { hash: "setup", text: "CREATE TABLE #work (id int);" } }),
    variants: [variant({ ordinal: 0, variant: "baseline" }), variant({ ordinal: 1, variant: "candidate" })],
  })
  renderCompare(detail)
  await userEvent.click(screen.getByRole("button", { name: "SQL diff & conditions" }))
  const sql = screen.getByRole("region", { name: "SQL diff & conditions" })
  expect(within(sql).getByText("CREATE TABLE #work (id int);")).toBeInTheDocument()
  expect(within(sql).getByText("Timeout")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Plans" }))
  const plans = screen.getByRole("region", { name: "Plans" })
  expect(within(plans).getAllByRole("region", { name: "Key metrics" })).toHaveLength(2)
  const tableIo = within(plans).getByRole("region", { name: "compare table IO" })
  expect(within(tableIo).getAllByRole("table")).toHaveLength(1)
  const more = within(tableIo).getByRole("button", { name: "More" })
  await userEvent.click(more)
  expect(within(tableIo).getAllByRole("table")).toHaveLength(2)
  expect(screen.getByRole("button", { name: "Plans" })).toHaveAttribute("aria-expanded", "true")
  await userEvent.click(more)
  expect(within(tableIo).getAllByRole("table")).toHaveLength(1)
  await userEvent.click(more)
  await userEvent.click(screen.getByRole("button", { name: "Plans" }))
  expect(screen.queryByRole("region", { name: "compare table IO" })).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Plans" }))
  expect(within(screen.getByRole("region", { name: "compare table IO" })).getAllByRole("table")).toHaveLength(2)
})

test("SQL diff mode survives closing and reopening its compare panel", async () => {
  const detail = operationDetail({
    operation: operation({ operation: "compare" }),
    sqlText: "SELECT id FROM Orders", candidateSqlText: "SELECT id FROM Orders WHERE id > 0",
  })
  renderCompare(detail)
  const trigger = screen.getByRole("button", { name: "SQL diff & conditions" })
  expect(screen.queryByRole("tab", { name: "Baseline" })).not.toBeInTheDocument()
  await userEvent.click(trigger)
  await userEvent.click(await screen.findByRole("tab", { name: "Baseline" }))
  expect(screen.getByRole("tab", { name: "Baseline" })).toHaveAttribute("aria-selected", "true")
  await userEvent.click(trigger)
  expect(screen.queryByRole("tab", { name: "Baseline" })).not.toBeInTheDocument()
  await userEvent.click(trigger)
  expect(screen.getByRole("tab", { name: "Baseline" })).toHaveAttribute("aria-selected", "true")
})

test("plans and reproduction scripts use the selected operation's actual baseline and candidate variants", async () => {
  const before = "C".repeat(64)
  const after = "D".repeat(64)
  const shapeHash = "E".repeat(64)
  const { calls } = renderCompare(operationDetail({
    operation: operation({ operation: "compare" }),
    variants: [
      variant({ ordinal: 0, variant: "baseline", caseId: "0", plans: [{ repetition: 1, ordinal: 0, hash: before, stored: true }] }),
      variant({ ordinal: 1, variant: "candidate", caseId: "0", plans: [{ repetition: 1, ordinal: 0, hash: after, stored: true }] }),
    ],
  }), {
    "/api/operations/10/plans": {
      operationId: 10,
      state: "stored",
      occurrences: [before, after].map((documentSha256, index) => ({
        variant: index === 0 ? "baseline" : "candidate", parameterSet: null, caseId: "0", caseLabel: "Case 1",
        repetition: 1, documentOrdinal: 0, statementIndex: 0, spanStart: 0, spanLength: 8,
        statementId: 1, statementCompId: 1, documentSha256, shapeHash, format: "showplan-xml",
        matchKind: "exact", omitted: false, stored: true,
      })),
    },
    "/api/operations/10/source-map": (url: URL) => new Response(JSON.stringify({
      operationId: 10, state: "stored", engine: "sqlserver", variant: url.searchParams.get("variant"),
      parameterSet: null, caseId: "0", caseLabel: "Case 1", repetition: 1,
      executedText: url.searchParams.get("variant") === "baseline" ? "SELECT 1" : "SELECT 2",
      algorithmVersion: 1, links: [], omittedDocuments: [], errors: [],
    }), { status: 200 }),
    [`/api/plan-documents/${before}/graph`]: {
      engine: "sqlserver", error: null, graph: { engine: "sqlserver", statements: [{ index: 0, statementId: 1, statementCompId: 1, rootNodeId: "0:0" }], nodes: [{ id: "0:0", statementIndex: 0, showplanNodeId: 1, physicalOperator: "Index Scan", logicalOperator: "Index Scan", object: null, tableReferenceId: null, predicates: [], definedValues: [], estimatedRows: null, actualRows: null, executions: null, costFraction: null, warnings: [] }], edges: [] },
    },
    [`/api/plan-documents/${after}/graph`]: {
      engine: "sqlserver", error: null, graph: { engine: "sqlserver", statements: [{ index: 0, statementId: 1, statementCompId: 1, rootNodeId: "0:0" }], nodes: [{ id: "0:0", statementIndex: 0, showplanNodeId: 1, physicalOperator: "Index Seek", logicalOperator: "Index Seek", object: null, tableReferenceId: null, predicates: [], definedValues: [], estimatedRows: null, actualRows: null, executions: null, costFraction: null, warnings: [] }], edges: [] },
    },
  })
  await userEvent.click(screen.getByRole("button", { name: "Plans" }))
  const documentSelectors = await screen.findAllByRole("combobox", { name: "Plan document" })
  await userEvent.selectOptions(documentSelectors[0], `${before}:0`)
  await userEvent.selectOptions(documentSelectors[1], `${after}:0`)
  const planHrefs = screen.getAllByRole("link", { name: "Download selected plan" }).map(link => link.getAttribute("href"))
  expect(planHrefs).toContain(`/api/plan-documents/${before}`)
  expect(planHrefs).toContain(`/api/plan-documents/${after}`)
  await userEvent.click(screen.getByRole("button", { name: "Per-case & per-run details" }))
  await userEvent.selectOptions(screen.getByRole("combobox", { name: "Side" }), "candidate")
  await waitFor(() => expect(calls.some(call => {
    const url = new URL(call, "http://dashboard")
    return url.pathname === "/api/operations/10/repro" && url.searchParams.get("side") === "candidate" && url.searchParams.get("case") === "0"
  })).toBe(true))
})
