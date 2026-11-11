import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import type { OptimizationComparison, OptimizationVersion } from "@/api/types"
import { VersionTable } from "./VersionTable"

const counts = { resultsEquivalent: 2, resultsDifferent: 0, resultsUnavailable: 0, ioBetter: 2, ioSame: 0, ioWorse: 0, ioUnavailable: 0, policyPass: 1, policyFail: 1, policyUnavailable: 0 }
const latest: OptimizationVersion[] = ["A", "B", "C"].map(version => ({ version, hash: version, logicalReadsSum: 20, logicalReadsMean: 10, scansSum: 99, elapsedMedianMs: 5, caseOutcome: "ok", measuredIn: [], latestOperationId: 11, verdictBasis: "whole batch", metricCases: ["one", "two"].map(caseKey => ({ caseKey, caseId: caseKey, label: caseKey, logicalReads: 10, scans: 49.5, elapsedMs: 2.5, cpuMs: 1 })) }))
function pair(over: Partial<OptimizationComparison> = {}): OptimizationComparison {
  return { a: "A", b: "B", basis: "MeasuredTogether", operationId: 10, equivalenceLabel: "results equivalent", cases: ["one", "two"].map(id => ({ id, label: id, outcome: "better" })), selectedCase: "all", aggregate: "sum", coverageLabel: "2 of 2 cases", mixed: null, tables: [], collapsed: null, wholeBatch: { before: 100, after: 50, shared: null, label: "", sharedBefore: null, sharedAfter: null, changedBefore: null, changedAfter: null }, elapsed: { beforeMs: 10, afterMs: 20, differentRun: false, policyVerdict: "fail" }, cpuBeforeMs: 8, cpuAfterMs: 16, scansBefore: 3, scansAfter: 2, caseRows: ["one", "two"].map(caseKey => ({ caseKey, labelA: caseKey, labelB: caseKey, rowsA: 4, rowsB: 4, rowsAVaries: false, rowsBVaries: false })), outcomes: counts, caseOutcomes: ["one", "two"].map(caseKey => ({ caseKey, label: caseKey, resultsEquivalent: true, ioOutcome: "better", policyVerdict: "fail", policyUnavailableReason: null, policyReasons: ["elapsed regressed"], readsBefore: 100, readsAfter: 50, elapsedBeforeMs: 10, elapsedAfterMs: 20, cpuBeforeMs: 8, cpuAfterMs: 16 })), ...over }
}
function table(comparison: OptimizationComparison | undefined, search = { case: "all", agg: "sum" }, pending = false) {
  return <VersionTable subject="Exact pair" sessionId={1} sessionSearch={search} versions={latest} selectedA="A" selectedB="B" comparison={comparison} pending={pending} onSelect={() => {}} />
}
function cells(version: string) { return within(screen.getByRole("rowheader", { name: version }).closest("tr")!).getAllByRole("cell") }

test.each([{ selectedCase: "all", aggregate: "sum", reads: 100, elapsed: 10, rows: 8 }, { selectedCase: "all", aggregate: "mean", reads: 50, elapsed: 5, rows: 4 }, { selectedCase: "one", aggregate: "sum", reads: 70, elapsed: 7, rows: 4 }])("active pair uses operation #10 rather than latest A-to-C #11: $selectedCase/$aggregate", ({ selectedCase, aggregate, reads, elapsed, rows }) => {
  const comparison = pair({ selectedCase, aggregate, wholeBatch: { ...pair().wholeBatch!, before: reads, after: reads / 2 }, elapsed: { ...pair().elapsed, beforeMs: elapsed, afterMs: elapsed * 2 } })
  render(table(comparison, { case: selectedCase, agg: aggregate }))
  expect(cells("A")[1]).toHaveTextContent(String(rows))
  expect(cells("A")[2]).toHaveTextContent(String(reads))
  expect(cells("A")[4]).toHaveTextContent("3")
  expect(cells("A")[5]).toHaveTextContent(`${elapsed} ms`)
  expect(cells("A")[6]).toHaveTextContent("8 ms")
  expect(cells("B")[3]).toHaveTextContent("-50%")
  expect(cells("B")[5]).toHaveTextContent(`${elapsed * 2} ms`)
  expect(cells("C")[2]).toHaveTextContent(selectedCase === "one" || aggregate === "mean" ? "10" : "20")
  expect(cells("C")[0]).toHaveTextContent("Latest own observation #11")
})

test("ambiguous or unavailable pair counters never fall back to latest version measurements", () => {
  const comparison = pair({ wholeBatch: { ...pair().wholeBatch!, before: null, after: null }, elapsed: { ...pair().elapsed, beforeMs: null, afterMs: null }, cpuBeforeMs: null, cpuAfterMs: null, scansBefore: null, scansAfter: null, caseRows: [], cases: [] })
  const { rerender } = render(table(comparison))
  for (const version of ["A", "B"]) for (const index of [1, 2, 3, 4, 5, 6]) expect(cells(version)[index]).toHaveTextContent("—")
  rerender(table(undefined))
  expect(cells("A")[2]).toHaveTextContent("—")
  expect(cells("A")[0]).toHaveTextContent("Pair evidence unavailable")
})

test("pending selection hides stale outcomes and closes old case details while A/B controls retain focus", async () => {
  const { rerender } = render(table(pair()))
  await userEvent.click(screen.getByRole("button", { name: /IO: 2 better/ }))
  expect(screen.getByRole("table", { name: "Case outcome details" })).toBeInTheDocument()
  rerender(table(pair(), { case: "one", agg: "sum" }, true))
  expect(screen.queryByRole("table", { name: "Case outcome details" })).not.toBeInTheDocument()
  expect(screen.queryByRole("button", { name: /IO: 2 better/ })).not.toBeInTheDocument()
  expect(screen.queryAllByText("Results 2/2")).toHaveLength(0)
  const a = screen.getByRole("radio", { name: "A A" }); a.focus()
  expect(a).toHaveFocus()
  expect(a).toBeChecked()
  expect(screen.getAllByRole("status").some(status => status.textContent?.includes("Updating comparison"))).toBe(true)
  rerender(table(pair({ selectedCase: "one", outcomes: { ...counts, resultsEquivalent: 1, ioBetter: 1, policyPass: 0 } }), { case: "one", agg: "sum" }))
  expect(a).toHaveFocus()
  expect(screen.getByRole("button", { name: /IO: 1 better/ })).toBeInTheDocument()
})
