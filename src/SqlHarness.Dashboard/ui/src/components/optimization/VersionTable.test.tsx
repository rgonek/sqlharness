import { useState } from "react"
import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { operation } from "@/test/fixtures"
import { VersionTable } from "./VersionTable"

const versions = ["v0", "v1", "v2"].map(version => ({ version, hash: version, logicalReadsSum: 10, logicalReadsMean: 10, scansSum: 1, elapsedMedianMs: 1, caseOutcome: "ok", measuredIn: [], latestOperationId: 1, verdictBasis: "whole batch" }))
function Choices({ subject }: { subject: string }) {
  const [a, setA] = useState("v0")
  const [b, setB] = useState<string | null>(null)
  return <VersionTable subject={subject} sessionId={1} sessionSearch={{}} versions={versions} selectedA={a} selectedB={b} onSelect={(side, version) => side === "a" ? setA(version) : setB(version)} />
}

test("A and B are independent exclusive native groups, isolated per table instance", async () => {
  render(<><Choices subject="First" /><Choices subject="Second" /></>)
  const first = within(screen.getByRole("table", { name: "First versions" }))
  const second = within(screen.getByRole("table", { name: "Second versions" }))
  expect(first.getByRole("radio", { name: "A v0" })).toBeChecked()
  expect(first.getByRole("radio", { name: "A v0" })).toHaveAccessibleDescription("Baseline A")
  expect(first.getByRole("radio", { name: "B v0" })).toHaveAccessibleDescription("Candidate B")
  await userEvent.click(first.getByRole("radio", { name: "A v1" }))
  first.getByRole("radio", { name: "B v2" }).focus()
  await userEvent.keyboard(" ")
  expect(first.getByRole("radio", { name: "A v1" })).toBeChecked()
  expect(first.getByRole("radio", { name: "A v0" })).not.toBeChecked()
  expect(first.getByRole("radio", { name: "B v2" })).toBeChecked()
  expect(second.getByRole("radio", { name: "A v0" })).toBeChecked()
  expect(first.getByRole("radio", { name: "A v0" }).getAttribute("name")).not.toBe(second.getByRole("radio", { name: "A v0" }).getAttribute("name"))
  expect(first.getAllByRole("radio").filter(radio => (radio as HTMLInputElement).checked)).toHaveLength(2)
})


test("version labels share the version line and retain their full title", () => {
  render(<VersionTable subject="Orders" sessionId={1} sessionSearch={{}} versions={[{ ...versions[0], measuredIn: [10] }]} operations={[operation({ id: 10, agentLabel: "Candidate with a complete label" })]} selectedA="v0" selectedB={null} onSelect={() => {}} />)
  const label = screen.getByText("Candidate with a complete label")
  expect(label).toHaveAttribute("title", "Candidate with a complete label")
  expect(label.parentElement).toHaveTextContent("v0")
  expect(screen.getByRole("radio", { name: "A v0" }).nextElementSibling).toHaveTextContent("A")
  expect(screen.getByRole("radio", { name: "B v0" }).nextElementSibling).toHaveTextContent("B")
})

test("shows rows column and dash for missing", () => {
  render(<VersionTable subject="Rows" sessionId={1} sessionSearch={{}} versions={[{ ...versions[0], rowsReturnedSum: 5 }, { ...versions[1], rowsReturnedSum: null }]} selectedA="v0" selectedB={null} onSelect={() => {}} />)
  expect(screen.getByRole("columnheader", { name: "Rows" })).toBeInTheDocument()
  expect(screen.getByText("5")).toBeInTheDocument()
  const missingRow = screen.getByRole("rowheader", { name: "v1" }).closest("tr")!
  expect(within(missingRow).getAllByRole("cell")[1]).toHaveTextContent("—")
})

test("shows explicit row count variation separately from missing", () => {
  render(<VersionTable subject="Rows" sessionId={1} sessionSearch={{}} versions={[{ ...versions[0], rowsReturnedSum: null, rowsReturnedVaries: true }]} selectedA="v0" selectedB={null} onSelect={() => {}} />)
  expect(screen.getByText("varies")).toBeInTheDocument()
})


test("selected-case sum and mean use per-case medians, missing contributors remain unavailable", async () => {
  const { versionMetrics } = await import("./versionMetrics")
  const row = { ...versions[0], metricCases: [
    { caseKey: "one", caseId: "1", label: "One", logicalReads: 100, scans: 2, elapsedMs: 12, cpuMs: 8 },
    { caseKey: "two", caseId: "2", label: "Two", logicalReads: 200, scans: 4, elapsedMs: 28, cpuMs: 12 },
  ] }
  expect(versionMetrics(row, "all", "sum").elapsed).toBe(40)
  expect(versionMetrics(row, "all", "mean").elapsed).toBe(20)
  expect(versionMetrics(row, "one", "sum").elapsed).toBe(12)
  expect(versionMetrics({ ...row, metricCases: [row.metricCases[0], { ...row.metricCases[1], elapsedMs: null }] }, "all", "sum").elapsed).toBeNull()
})

test("timing headings identify the aggregation and scans and CPU are visible", () => {
  render(<VersionTable subject="Timing" sessionId={1} sessionSearch={{ case: "all", agg: "mean" }} versions={versions} selectedA="v0" selectedB="v1" onSelect={() => {}} />)
  expect(screen.getByRole("columnheader", { name: "Elapsed (mean of case medians)" })).toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "CPU (mean of case medians)" })).toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "Scans" })).toBeInTheDocument()
  expect(screen.queryByRole("columnheader", { name: "Outcome" })).not.toBeInTheDocument()
})


test("foreign case IDs never silently select another condition group's own cases", async () => {
  const { versionMetrics } = await import("./versionMetrics")
  const row = { ...versions[0], metricCases: [
    { caseKey: "own-1", caseId: "1", label: "One", varyingFingerprint: "paired-1", logicalReads: 10, scans: 1, elapsedMs: 12, cpuMs: 3 },
    { caseKey: "own-2", caseId: "2", label: "Two", varyingFingerprint: "paired-2", logicalReads: 20, scans: 2, elapsedMs: 28, cpuMs: 7 },
  ] }
  expect(versionMetrics(row, "all", "sum", ["paired-1"], true).elapsed).toBe(12)
  expect(versionMetrics(row, "foreign-case", "sum").elapsed).toBeNull()
  expect(versionMetrics(row, "all", "sum", ["paired-1", "missing"], true).elapsed).toBeNull()
  expect(versionMetrics(row, "all", "sum").cpu).toBe(10)
  expect(versionMetrics(row, "all", "mean").cpu).toBe(5)
})

test("inactive version rows cannot borrow a recorded policy from another directed pair", () => {
  const counts = { resultsEquivalent: 1, resultsDifferent: 0, resultsUnavailable: 0, ioBetter: 1, ioSame: 0, ioWorse: 0, ioUnavailable: 0, policyPass: 1, policyFail: 0, policyUnavailable: 0 }
  render(<VersionTable subject="No borrowed policy" sessionId={1} sessionSearch={{}} versions={[{ ...versions[0], outcomes: counts }, { ...versions[1], outcomes: counts }]} selectedA="v0" selectedB="v1" onSelect={() => {}} />)
  expect(screen.queryByText("Policy ✓1")).not.toBeInTheDocument()
  expect(screen.getAllByText("Policy unavailable")).toHaveLength(2)
})


test("baseline result coverage follows the active selected-case comparison", () => {
  const whole = { resultsEquivalent: 4, resultsDifferent: 0, resultsUnavailable: 0, ioBetter: 3, ioSame: 1, ioWorse: 0, ioUnavailable: 0, policyPass: 4, policyFail: 0, policyUnavailable: 0 }
  const selected = { ...whole, resultsEquivalent: 1, ioBetter: 1, ioSame: 0, policyPass: 1 }
  const comparison: import("@/api/types").OptimizationComparison = { a: "v0", b: "v1", selectedCase: "one", aggregate: "sum", cases: [], outcomes: selected, basis: "MeasuredTogether", operationId: 1, equivalenceLabel: "equivalent", coverageLabel: "", mixed: null, tables: [], collapsed: null, elapsed: { beforeMs: null, afterMs: null, differentRun: false, policyVerdict: null } }
  render(<VersionTable subject="Selected coverage" sessionId={1} sessionSearch={{ case: "one" }} versions={[{ ...versions[0], outcomes: whole }, { ...versions[1], outcomes: whole }]} comparison={comparison} selectedA="v0" selectedB="v1" onSelect={() => {}} />)
  const baseline = screen.getByRole("rowheader", { name: "v0" }).closest("tr")!
  expect(within(baseline).getByText("Results 1/1")).toBeInTheDocument()
  expect(within(baseline).queryByText("Policy ✓1")).not.toBeInTheDocument()
})

test("baseline coverage follows selected case and candidate uses the projected counts", () => {
  const outcome = { caseKey: "chosen", id: "chosen", label: "Chosen", resultsEquivalent: false, ioOutcome: "unavailable", readsBefore: null, readsAfter: null, elapsedBeforeMs: null, elapsedAfterMs: null, cpuBeforeMs: null, cpuAfterMs: null, policyVerdict: null, policyReasons: [], policyUnavailableReason: "not recorded" }
  const comparison = { a: "v0", b: "v1", basis: "MeasuredTogether", selectedCase: "chosen", aggregate: "sum", elapsed: { beforeMs: null, afterMs: null, policyVerdict: null, differentRun: false }, outcomes: { resultsEquivalent: 0, resultsDifferent: 1, resultsUnavailable: 0, ioBetter: 0, ioSame: 0, ioWorse: 0, ioUnavailable: 1, policyPass: 0, policyFail: 0, policyUnavailable: 1 }, caseOutcomes: [outcome], outcomeCounts: { total: 1, results: { equivalent: 0, different: 1, unavailable: 0 }, io: { better: 0, same: 0, worse: 0, unavailable: 1 }, policy: { pass: 0, fail: 0, unavailable: 1 } } } as unknown as import("@/api/types").OptimizationComparison
  render(<VersionTable subject="Selected" sessionId={1} sessionSearch={{ case: comparison.selectedCase, agg: comparison.aggregate }} versions={versions} selectedA="v0" selectedB="v1" comparison={comparison} onSelect={() => {}} />)
  expect(within(screen.getByRole("rowheader", { name: "v0" }).closest("tr")!).getByText("IO 0/1 measured")).toBeInTheDocument()
  expect(screen.getAllByLabelText(/Results: 0 equivalent, 1 different/).length).toBeGreaterThan(0)
  expect(screen.getByRole("button", { name: /IO: 0 better, 0 same, 0 worse, 1 unavailable/ })).toBeInTheDocument()
})

test("shows selected timing summaries and scans in the version table", () => {
  const comparison = { a: "v0", b: "v1", basis: "MeasuredTogether", selectedCase: "all", aggregate: "sum", elapsed: { beforeMs: 40, afterMs: 30 }, cpuBeforeMs: 40, cpuAfterMs: 30 } as unknown as import("@/api/types").OptimizationComparison
  render(<VersionTable subject="Timing" sessionId={1} sessionSearch={{ case: comparison.selectedCase, agg: comparison.aggregate }} versions={versions} selectedA="v0" selectedB="v1" comparison={comparison} onSelect={() => {}} />)
  expect(screen.getByRole("columnheader", { name: "Elapsed (sum of case medians)" })).toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "CPU (sum of case medians)" })).toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "Scans" })).toBeInTheDocument()
  expect(within(screen.getByRole("rowheader", { name: "v0" }).closest("tr")!).getAllByText("40 ms")).toHaveLength(2)
})

test("unpaired rows retain their own case timing while selection changes the active pair", () => {
  const comparison = { a: "v0", b: "v1", basis: "ApproximateAcrossConditions", selectedCase: "chosen", aggregate: "mean", elapsed: { beforeMs: 12, afterMs: 14 }, cpuBeforeMs: 12, cpuAfterMs: 14 } as unknown as import("@/api/types").OptimizationComparison
  const rows = versions.map((v, i) => ({ ...v, group: { fingerprint: i === 2 ? "other" : i === 1 ? "b" : "a", label: "condition", badge: String(i) }, caseTimings: [{ id: "foreign", elapsedMedianMs: 28, cpuMedianMs: 28 }] }))
  render(<VersionTable subject="Selection" sessionId={1} sessionSearch={{ case: comparison.selectedCase, agg: comparison.aggregate }} versions={rows} selectedA="v0" selectedB="v1" groupA="a" groupB="b" comparison={comparison} onSelect={() => {}} />)
  expect(screen.getByRole("columnheader", { name: "Elapsed (median)" })).toBeInTheDocument()
  expect(within(screen.getByRole("rowheader", { name: "v0" }).closest("tr")!).getAllByText("12 ms")).toHaveLength(2)
  const other = within(screen.getByRole("rowheader", { name: "v2" }).closest("tr")!)
  expect(other.getAllByText("28 ms")).toHaveLength(2)
  expect(other.getByText(/Different conditions/)).toBeInTheDocument()
})

test("without a pair uses all cases and shows missing timing coverage in scrollable table", () => {
  const row = { ...versions[0], caseTimings: [{ id: "a", elapsedMedianMs: 12, cpuMedianMs: 12 }, { id: "b", elapsedMedianMs: null, cpuMedianMs: null }] }
  render(<VersionTable subject="Coverage" sessionId={1} sessionSearch={{ case: "a", agg: "mean" }} versions={[row]} selectedA="v0" selectedB={null} onSelect={() => {}} />)
  expect(screen.getByRole("columnheader", { name: "Elapsed (sum of case medians)" })).toBeInTheDocument()
  expect(screen.getAllByText("1/2 cases")).toHaveLength(2)
  expect(screen.getByRole("table").parentElement).toHaveClass("overflow-x-auto")
  expect(screen.getByRole("columnheader", { name: "Results" })).toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "IO" })).toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "Policy" })).toBeInTheDocument()
})

test("uses full timing contributors for other rows and per-side metric coverage", () => {
  const comparison = { a: "v0", b: "v1", basis: "MeasuredTogether", selectedCase: "all", aggregate: "sum", cases: [{ id: "a" }], caseOutcomes: [{ id: "a", elapsedBeforeMs: 12, elapsedAfterMs: 12, cpuBeforeMs: 12, cpuAfterMs: null }], elapsed: { beforeMs: 40, afterMs: null }, cpuBeforeMs: null, cpuAfterMs: null, timingContributors: [{ id: "a", elapsedBeforeMs: 12, elapsedAfterMs: 12, cpuBeforeMs: 12, cpuAfterMs: null }, { id: "b", elapsedBeforeMs: 28, elapsedAfterMs: null, cpuBeforeMs: null, cpuAfterMs: null }] } as unknown as import("@/api/types").OptimizationComparison
  const rows = versions.map(v => ({ ...v, group: { fingerprint: "g", label: "same", badge: "A" }, caseTimings: [{ id: "a", elapsedMedianMs: 12, cpuMedianMs: 12 }, { id: "b", elapsedMedianMs: 28, cpuMedianMs: 28 }] }))
  render(<VersionTable subject="Asymmetric" sessionId={1} sessionSearch={{ case: comparison.selectedCase, agg: comparison.aggregate }} versions={rows} selectedA="v0" selectedB="v1" groupA="g" groupB="g" comparison={comparison} onSelect={() => {}} />)
  const other = within(screen.getByRole("rowheader", { name: "v2" }).closest("tr")!)
  expect(other.getAllByText("40 ms")).toHaveLength(2)
  const a = within(screen.getByRole("rowheader", { name: "v0" }).closest("tr")!)
  const b = within(screen.getByRole("rowheader", { name: "v1" }).closest("tr")!)
  expect(a.getByText("1/2 cases")).toBeInTheDocument()
  expect(b.getByText("1/2 cases")).toBeInTheDocument()
  expect(b.getByText("0/2 cases")).toBeInTheDocument()
})

test("cross-condition contributors use varying identities only for group A and preserve other groups", () => {
  const comparison = { a: "v0", b: "v1", basis: "ApproximateAcrossConditions", selectedCase: "all", aggregate: "sum", elapsed: { beforeMs: 12, afterMs: 14 }, cpuBeforeMs: 12, cpuAfterMs: 14, timingContributors: [{ id: "matched", elapsedBeforeMs: 12, elapsedAfterMs: 14, cpuBeforeMs: 12, cpuAfterMs: 14 }] } as unknown as import("@/api/types").OptimizationComparison
  const rows = [
    { ...versions[0], group: { fingerprint: "a", label: "A", badge: "A" } },
    { ...versions[1], group: { fingerprint: "b", label: "B", badge: "B" } },
    { ...versions[2], group: { fingerprint: "a", label: "A", badge: "A" }, caseTimings: [{ id: "full-fingerprint", varyingId: "matched", elapsedMedianMs: 28, cpuMedianMs: 28 }, { id: "other", varyingId: "unmatched", elapsedMedianMs: 50, cpuMedianMs: 50 }] },
    { ...versions[2], version: "v3", group: { fingerprint: "other", label: "Other", badge: "C" }, caseTimings: [{ id: "foreign", elapsedMedianMs: 9, cpuMedianMs: 9 }] },
  ]
  render(<VersionTable subject="Cross" sessionId={1} sessionSearch={{ case: comparison.selectedCase, agg: comparison.aggregate }} versions={rows} selectedA="v0" selectedB="v1" groupA="a" groupB="b" comparison={comparison} onSelect={() => {}} />)
  expect(within(screen.getByRole("rowheader", { name: "v2" }).closest("tr")!).getAllByText("28 ms")).toHaveLength(2)
  expect(within(screen.getByRole("rowheader", { name: "v3" }).closest("tr")!).getAllByText("9 ms")).toHaveLength(2)
})

test("an unpaired row missing a selected contributor is unavailable rather than a partial sum", () => {
  const comparison = { a: "v0", b: "v1", basis: "MeasuredTogether", selectedCase: "all", aggregate: "sum", cases: [{ id: "a" }], elapsed: { beforeMs: 40, afterMs: null }, cpuBeforeMs: 40, cpuAfterMs: null, timingContributors: [{ id: "a", elapsedBeforeMs: 12, elapsedAfterMs: 12, cpuBeforeMs: 12, cpuAfterMs: 12 }, { id: "b", elapsedBeforeMs: 28, elapsedAfterMs: null, cpuBeforeMs: 28, cpuAfterMs: null }] } as unknown as import("@/api/types").OptimizationComparison
  const rows = versions.map(v => ({ ...v, group: { fingerprint: "g", label: "same", badge: "A" }, caseTimings: [{ id: "a", elapsedMedianMs: 12, cpuMedianMs: 12 }] }))
  render(<VersionTable subject="Missing" sessionId={1} sessionSearch={{ case: comparison.selectedCase, agg: comparison.aggregate }} versions={rows} selectedA="v0" selectedB="v1" groupA="g" groupB="g" comparison={comparison} onSelect={() => {}} />)
  const row = within(screen.getByRole("rowheader", { name: "v2" }).closest("tr")!)
  expect(row.getAllByText("1/2 cases")).toHaveLength(2)
  expect(row.queryByText("12 ms")).not.toBeInTheDocument()
})
