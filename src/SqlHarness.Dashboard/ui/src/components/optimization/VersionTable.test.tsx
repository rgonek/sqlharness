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
