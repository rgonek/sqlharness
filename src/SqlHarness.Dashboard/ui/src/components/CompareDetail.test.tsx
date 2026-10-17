import { fireEvent, render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { CompareDetail } from "./CompareDetail"
import { conditions, operation, operationDetail, variant } from "@/test/fixtures"
import { stubFetch } from "@/test/render"

const renderCompare = (detail: ReturnType<typeof operationDetail>) => {
  const calls = stubFetch({ "/api/operations/10/repro": { text: null, completeness: "Unavailable", label: "Script not available — enable storeSensitive" } })
  return { ...render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><CompareDetail detail={detail} /></QueryClientProvider>), calls }
}

test("compare detail disclosures start collapsed", () => {
  renderCompare(operationDetail({ operation: operation({ operation: "compare" }) }))
  for (const label of ["SQL diff & conditions", "Plans", "Per-case & per-run details"]) {
    expect(screen.getByText(label).closest("details")).not.toHaveAttribute("open")
  }
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
  await userEvent.click(screen.getByText("Per-case & per-run details"))
  const section = screen.getByText("Per-case & per-run details").closest("details")!
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
  await userEvent.click(screen.getByText("SQL diff & conditions"))
  const sql = screen.getByText("SQL diff & conditions").closest("details")!
  expect(within(sql).getByText("CREATE TABLE #work (id int);")).toBeInTheDocument()
  expect(within(sql).getByText("Timeout")).toBeInTheDocument()
  await userEvent.click(screen.getAllByText("Plans")[0])
  expect(within(screen.getAllByText("Plans")[0].closest("details")!).getAllByRole("region", { name: "Key metrics" })).toHaveLength(2)
})

test("plans and reproduction scripts use the selected operation's actual baseline and candidate variants", async () => {
  const before = "C".repeat(64)
  const after = "D".repeat(64)
  const { calls } = renderCompare(operationDetail({
    operation: operation({ operation: "compare" }),
    variants: [
      variant({ ordinal: 0, variant: "baseline", caseId: "0", plans: [{ repetition: 1, ordinal: 0, hash: before, stored: true }] }),
      variant({ ordinal: 1, variant: "candidate", caseId: "0", plans: [{ repetition: 1, ordinal: 0, hash: after, stored: true }] }),
    ],
  }))
  await userEvent.click(screen.getAllByText("Plans").find(element => element.tagName.toLowerCase() === "summary")!)
  const planHrefs = screen.getAllByRole("link", { name: "Download plan" }).map(link => link.getAttribute("href"))
  expect(planHrefs).toContain(`/api/plans/${before}`)
  expect(planHrefs).toContain(`/api/plans/${after}`)
  await userEvent.click(screen.getByText("Per-case & per-run details"))
  await userEvent.selectOptions(screen.getByRole("combobox", { name: "Side" }), "candidate")
  await waitFor(() => expect(calls.some(call => {
    const url = new URL(call, "http://dashboard")
    return url.pathname === "/api/operations/10/repro" && url.searchParams.get("side") === "candidate" && url.searchParams.get("case") === "0"
  })).toBe(true))
})
