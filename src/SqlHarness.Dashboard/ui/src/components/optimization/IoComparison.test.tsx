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
  await userEvent.click(screen.getByRole("button", { name: "Scans" }))
  expect(screen.getByRole("columnheader", { name: "Scans" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Whole batch" }))
  expect(onViewChange).toHaveBeenCalledWith("whole")
  rerender(<IoComparison comparison={comparison} byStatement={false} onViewChange={onViewChange} extras={{ before: { Orders: { physicalReads: 8 } }, after: { Orders: { physicalReads: 0 } } }} />)
  await userEvent.click(screen.getByText("More columns"))
  await userEvent.click(screen.getByRole("checkbox", { name: "Physical" }))
  const row = screen.getByRole("rowheader", { name: "Orders" }).closest("tr")!
  expect(within(row).getByText("8 →")).toBeInTheDocument()
  expect(within(row).getByText("0")).toBeInTheDocument()
  expect(screen.getByRole("rowheader", { name: "Total (whole batch)" })).toBeInTheDocument()
})
