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

test("baseline coverage follows selected case and candidate uses the projected counts", () => {
  const outcome = { id: "chosen", label: "Chosen", resultsEquivalent: false, ioOutcome: "unavailable", readsBefore: null, readsAfter: null, elapsedBeforeMs: null, elapsedAfterMs: null, cpuBeforeMs: null, cpuAfterMs: null, policyVerdict: null, policyReasons: [], policyUnavailableReason: "not recorded" }
  const comparison = { a: "v0", b: "v1", basis: "MeasuredTogether", selectedCase: "chosen", caseOutcomes: [{ ...outcome, id: "other", readsBefore: 10 }, outcome], outcomeCounts: { total: 1, results: { equivalent: 0, different: 1, unavailable: 0 }, io: { better: 0, same: 0, worse: 0, unavailable: 1 }, policy: { pass: 0, fail: 0, unavailable: 1 } } } as unknown as import("@/api/types").OptimizationComparison
  render(<VersionTable subject="Selected" sessionId={1} sessionSearch={{}} versions={versions} selectedA="v0" selectedB="v1" comparison={comparison} onSelect={() => {}} />)
  expect(screen.getByText("Baseline · 0/1 measured")).toBeInTheDocument()
  expect(screen.getByText(/1 different/)).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "IO: 0 better, 0 same, 0 worse, 1 unavailable" })).toBeInTheDocument()
})

test("shows selected timing summaries and scans in the version table", () => {
  const comparison = { a: "v0", b: "v1", basis: "MeasuredTogether", selectedCase: "all", aggregate: "sum", elapsed: { beforeMs: 40, afterMs: 30 }, cpuBeforeMs: 40, cpuAfterMs: 30 } as unknown as import("@/api/types").OptimizationComparison
  render(<VersionTable subject="Timing" sessionId={1} sessionSearch={{}} versions={versions} selectedA="v0" selectedB="v1" comparison={comparison} onSelect={() => {}} />)
  expect(screen.getByRole("columnheader", { name: "Elapsed (sum of case medians)" })).toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "CPU (sum of case medians)" })).toBeInTheDocument()
  expect(screen.getByRole("columnheader", { name: "Scans" })).toBeInTheDocument()
  expect(within(screen.getByRole("rowheader", { name: "v0" }).closest("tr")!).getAllByText("40 ms")).toHaveLength(2)
})

test("unpaired rows retain their own case timing while selection changes the active pair", () => {
  const comparison = { a: "v0", b: "v1", basis: "ApproximateAcrossConditions", selectedCase: "chosen", aggregate: "mean", elapsed: { beforeMs: 12, afterMs: 14 }, cpuBeforeMs: 12, cpuAfterMs: 14 } as unknown as import("@/api/types").OptimizationComparison
  const rows = versions.map((v, i) => ({ ...v, group: { fingerprint: i === 2 ? "other" : i === 1 ? "b" : "a", label: "condition", badge: String(i) }, caseTimings: [{ id: "foreign", elapsedMedianMs: 28, cpuMedianMs: 28 }] }))
  render(<VersionTable subject="Selection" sessionId={1} sessionSearch={{}} versions={rows} selectedA="v0" selectedB="v1" groupA="a" groupB="b" comparison={comparison} onSelect={() => {}} />)
  expect(screen.getByRole("columnheader", { name: "Elapsed (median)" })).toBeInTheDocument()
  expect(within(screen.getByRole("rowheader", { name: "v0" }).closest("tr")!).getAllByText("12 ms")).toHaveLength(2)
  const other = within(screen.getByRole("rowheader", { name: "v2" }).closest("tr")!)
  expect(other.getAllByText("28 ms")).toHaveLength(2)
  expect(other.getByText(/different conditions/)).toBeInTheDocument()
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
