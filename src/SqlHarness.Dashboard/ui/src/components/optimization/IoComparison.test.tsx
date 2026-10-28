import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import type { OptimizationComparison } from "@/api/types"
import { IoComparison } from "./IoComparison"
import { StatementMetrics } from "./StatementRows"
import { IoDetails } from "./IoDetails"

vi.mock("@tanstack/react-query", () => ({ useQueries: () => [] }))

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

test.each([
  { statements: [], tableIoUnavailable: false },
  { statements: [], tableIoUnavailable: true },
])("shows the all-cases explanation even when statement rows are absent", ({ statements, tableIoUnavailable }) => {
  render(<IoComparison comparison={{ ...comparison, statements, tableIoUnavailable, statementMetricsUnavailableReason: "Select a single case for statement metrics." }} byStatement onViewChange={vi.fn()} />)
  expect(screen.getByText("Select a single case for statement metrics.")).toBeInTheDocument()
})

test("keeps the all-cases explanation visible when a statement is collapsed", async () => {
  render(<IoComparison comparison={{ ...comparison, statementMetricsUnavailableReason: "Select a single case for statement metrics." }} byStatement onViewChange={vi.fn()} />)
  await userEvent.click(screen.getByRole("button", { name: "SELECT * FROM Orders" }))
  expect(screen.getByText("Select a single case for statement metrics.")).toBeInTheDocument()
})

test("shows nullable grant values and tri-state spill with selected-run provenance", () => {
  const statement = { ...comparison.statements![0], grantRequestedKbBefore: 0, grantRequestedKbAfter: 100, grantGrantedKbBefore: null, grantGrantedKbAfter: 512, grantMaxUsedKbBefore: 12, grantMaxUsedKbAfter: null, hasSpillWarningBefore: false, hasSpillWarningAfter: true, planRepetitionBefore: 2, planRepetitionAfter: 3, planLinkBefore: "/before", planLinkAfter: "/after" }
  render(<IoComparison comparison={{ ...comparison, selectedCase: "case1", statements: [statement] }} byStatement onViewChange={vi.fn()} />)
  expect(screen.getByText("Requested grant (KB): 0 → 100")).toBeInTheDocument()
  expect(screen.getByText("Granted grant (KB): — → 512")).toBeInTheDocument()
  expect(screen.getByText("Max used grant (KB): 12 → —")).toBeInTheDocument()
  expect(screen.getByText("Spill warning: No → Yes")).toBeInTheDocument()
  expect(screen.getByText(/before repetition 2; after repetition 3/)).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Plan before" })).toHaveAttribute("href", "/before")
  expect(screen.getByRole("link", { name: "Plan after" })).toHaveAttribute("href", "/after")
})

test("does not turn unknown spill into no warning or sum metrics across cases", () => {
  const statement = { ...comparison.statements![0], hasSpillWarningBefore: null, hasSpillWarningAfter: false, grantRequestedKbAfter: 0 }
  const { rerender } = render(<IoComparison comparison={{ ...comparison, selectedCase: "case1", statements: [statement] }} byStatement onViewChange={vi.fn()} />)
  expect(screen.getByText("Spill warning: — → No")).toBeInTheDocument()
  rerender(<IoComparison comparison={{ ...comparison, statements: [statement], statementMetricsUnavailableReason: "Select a single case for statement metrics." }} byStatement onViewChange={vi.fn()} />)
  expect(screen.queryByText(/Spill warning:/)).not.toBeInTheDocument()
  expect(screen.getByText("Select a single case for statement metrics.")).toBeInTheDocument()
})

test("only exposes supported counters when switching IO views", async () => {
  const onViewChange = vi.fn()
  const extras = { before: { Orders: { physicalReads: 8, readAheadReads: 9, lobPhysicalReads: 10, lobReadAheadReads: 11 } }, after: { Orders: { physicalReads: 0, readAheadReads: 1, lobPhysicalReads: 2, lobReadAheadReads: 3 } } }
  const { rerender } = render(<IoComparison comparison={comparison} byStatement={false} onViewChange={onViewChange} extras={extras} />)
  await userEvent.click(screen.getByText("More columns"))
  for (const name of ["Physical", "Read-ahead", "LOB physical", "LOB read-ahead", "LOB logical"]) await userEvent.click(screen.getByRole("checkbox", { name }))
  expect(within(screen.getByRole("rowheader", { name: "Orders" }).closest("tr")!).getByText("8 →")).toBeInTheDocument()
  rerender(<IoComparison comparison={comparison} byStatement onViewChange={onViewChange} extras={extras} />)
  for (const name of ["Physical", "Read-ahead", "LOB physical", "LOB read-ahead"]) {
    expect(screen.queryByRole("columnheader", { name })).not.toBeInTheDocument()
    expect(screen.queryByRole("checkbox", { name })).not.toBeInTheDocument()
  }
  expect(screen.getByRole("columnheader", { name: "LOB logical" })).toBeInTheDocument()
  rerender(<IoComparison comparison={comparison} byStatement={false} onViewChange={onViewChange} extras={extras} />)
  expect(screen.queryByRole("columnheader", { name: "Physical" })).not.toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "LOB logical" })).toBeInTheDocument()
})


test("measured statement grants and spill show only after values", () => {
  const row = { ...comparison.statements![0], grantRequestedKbBefore: 111, grantRequestedKbAfter: 222, grantGrantedKbBefore: 333, grantGrantedKbAfter: 444, grantMaxUsedKbBefore: 555, grantMaxUsedKbAfter: 666, hasSpillWarningBefore: false, hasSpillWarningAfter: true }
  const { container } = render(<StatementMetrics row={row} measured />)
  expect(screen.getByText("Requested grant (KB): 222")).toBeInTheDocument()
  expect(screen.getByText("Granted grant (KB): 444")).toBeInTheDocument()
  expect(screen.getByText("Max used grant (KB): 666")).toBeInTheDocument()
  expect(screen.getByText("Spill warning: Yes")).toBeInTheDocument()
  expect(container).not.toHaveTextContent(/111|333|555|→|Spill warning: No/)
})


test("batch memory does not claim statement memory is never retained", () => {
  render(<IoDetails comparison={comparison} byStatement onViewChange={vi.fn()} />)
  expect(screen.getByLabelText("Batch memory")).toHaveTextContent("Memory (sum of selected case medians)")
  expect(screen.queryByText("Statement memory is not retained in the journal.")).not.toBeInTheDocument()
})

test("the statement's counter cells toggle expansion without swallowing its plan links", async () => {
  const linked = { ...comparison, statements: comparison.statements!.map(row => ({ ...row, planLinkBefore: "#before" })) }
  render(<IoComparison comparison={linked} byStatement onViewChange={vi.fn()} />)
  const row = screen.getByRole("button", { name: "SELECT * FROM Orders" }).closest("tr")!
  await userEvent.click(within(row).getAllByRole("cell")[0])
  expect(screen.queryByRole("rowheader", { name: "Orders" })).not.toBeInTheDocument()
  await userEvent.click(within(row).getAllByRole("cell")[0])
  expect(screen.getByRole("rowheader", { name: "Orders" })).toBeInTheDocument()
  await userEvent.click(within(row).getByRole("link", { name: "Plan before" }))
  expect(screen.getByRole("rowheader", { name: "Orders" })).toBeInTheDocument()
})
