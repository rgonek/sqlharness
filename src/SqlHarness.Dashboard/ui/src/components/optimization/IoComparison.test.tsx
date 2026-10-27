import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import type { OptimizationComparison } from "@/api/types"
import { IoComparison } from "./IoComparison"

const comparison: OptimizationComparison = {
  a: "v0", b: "v1", basis: "MeasuredTogether", operationId: 10, equivalenceLabel: "equivalent",
  cases: [], selectedCase: "all", aggregate: "sum", coverageLabel: "", mixed: null,
  tables: [{ table: "Orders", scansBefore: 1, scansAfter: 2, readsBefore: 100, readsAfter: 25, change: "−75%" }],
  collapsed: null, elapsed: { beforeMs: null, afterMs: null, differentRun: false, policyVerdict: null },
  wholeBatch: { before: 100, after: 25, shared: null, label: "", sharedBefore: null, sharedAfter: null, changedBefore: null, changedAfter: null },
  statements: [{ key: "s", occurrence: 0, text: "SELECT * FROM Orders", status: "different-work", readsBefore: 100, readsAfter: 25, scansBefore: 1, scansAfter: 2, executions: 1, change: "−75%", attribution: "confirmed", hiddenWork: [], objects: [{ table: "Orders", scansBefore: 1, scansAfter: 2, readsBefore: 100, readsAfter: 25, change: "−75%" }], planHashBefore: null, planHashAfter: null, planLinkBefore: null, planLinkAfter: null, udfElapsedMilliseconds: null }],
}

test("IO expands statements, switches scope and toggles optional counters", async () => {
  const onViewChange = vi.fn()
  const { rerender } = render(<IoComparison comparison={comparison} byStatement onViewChange={onViewChange} />)
  expect(screen.getByRole("rowheader", { name: "Orders" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "SELECT * FROM Orders" }))
  expect(screen.queryByRole("rowheader", { name: "Orders" })).not.toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "Scans" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Scans" }))
  expect(screen.queryByRole("columnheader", { name: "Scans" })).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Scans" }))
  await userEvent.click(screen.getByRole("radio", { name: "Whole batch" }))
  expect(onViewChange).toHaveBeenCalledWith("whole")
  rerender(<IoComparison comparison={comparison} byStatement={false} onViewChange={onViewChange} extras={{ before: { Orders: { physicalReads: 8 } }, after: { Orders: { physicalReads: 0 } } }} />)
  await userEvent.click(screen.getByText("More columns"))
  await userEvent.click(screen.getByRole("checkbox", { name: "Physical" }))
  const row = screen.getByRole("rowheader", { name: "Orders" }).closest("tr")!
  expect(within(row).getByText("8 →")).toBeInTheDocument()
  expect(within(row).getByText("0")).toBeInTheDocument()
  expect(screen.getByRole("rowheader", { name: "Total (whole batch)" })).toBeInTheDocument()
})

test("shows selected-run metrics and object LOB without replacing missing values", async () => {
  const statement = { ...comparison.statements![0], cpuBeforeMs: null, cpuAfterMs: 0, elapsedBeforeMs: null, elapsedAfterMs: 42, planRepetitionBefore: null, planRepetitionAfter: 3, planLinkAfter: "/plan?occurrence=2", objects: [{ ...comparison.statements![0].objects[0], lobLogicalReadsBefore: null, lobLogicalReadsAfter: 7 }] }
  render(<IoComparison comparison={{ ...comparison, selectedCase: "case1", statements: [statement] }} byStatement onViewChange={vi.fn()} />)
  expect(screen.getByText(/CPU \(ms\).*—.*0/)).toBeInTheDocument()
  expect(screen.getByText(/Elapsed \(ms\).*—.*42/)).toBeInTheDocument()
  expect(screen.getByText(/repetition 3/)).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Plan after" })).toHaveAttribute("href", "/plan?occurrence=2")
  await userEvent.click(screen.getByText("More columns"))
  await userEvent.click(screen.getByRole("checkbox", { name: "LOB logical" }))
  expect(within(screen.getByRole("rowheader", { name: "Orders" }).closest("tr")!).getByText("7")).toBeInTheDocument()
})

test("explains unavailable statement metrics for all cases", () => {
  render(<IoComparison comparison={{ ...comparison, statementMetricsUnavailableReason: "Select a single case for statement metrics." }} byStatement onViewChange={vi.fn()} />)
  expect(screen.getByText("Select a single case for statement metrics.")).toBeInTheDocument()
})
