import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { test, expect } from "vitest"
import { OutcomeDimensions } from "./CaseOutcomeDetails"

const outcomes = { resultsEquivalent: 4, resultsDifferent: 0, resultsUnavailable: 0, ioBetter: 3, ioSame: 1, ioWorse: 0, ioUnavailable: 0, policyPass: 3, policyFail: 1, policyUnavailable: 0 }
const base = { caseKey: "one", label: "One", resultsEquivalent: true, ioOutcome: "better", policyVerdict: "pass", policyUnavailableReason: null, policyReasons: [], readsBefore: 100, readsAfter: 50, elapsedBeforeMs: 10, elapsedAfterMs: 8, cpuBeforeMs: 4, cpuAfterMs: 2 }

test("IO improvements and recorded policy failures remain independent in shared case details", async () => {
  render(<OutcomeDimensions outcomes={outcomes} cases={[base, { ...base, caseKey: "two", label: "Lower reads but slower elapsed", policyVerdict: "fail", policyReasons: ["elapsed-regressed"], elapsedAfterMs: 30 }]} />)
  expect(screen.getByText("Results 4/4")).toBeInTheDocument()
  const io = screen.getByRole("button", { name: /IO: 3 better, 1 same, 0 worse, 0 unavailable/ })
  expect(io).toHaveTextContent("IO ↑3 · =1")
  io.focus()
  await userEvent.keyboard("{Enter}")
  const table = screen.getByRole("table", { name: "Case outcome details" })
  const rows = within(table).getAllByRole("row")
  expect(rows[1]).toHaveTextContent("Lower reads but slower elapsed")
  expect(rows[1]).toHaveTextContent("elapsed-regressed")
  expect(rows[1]).toHaveTextContent("10 → 30")
  await userEvent.keyboard("{Escape}")
  expect(io).toHaveFocus()
  await userEvent.click(screen.getByRole("button", { name: /Policy: 3 pass, 1 fail, 0 unavailable/ }))
  expect(screen.getByRole("table", { name: "Case outcome details" })).toBeInTheDocument()
})

test("separates IO from recorded policy and opens a failure-first table by keyboard", async () => {
  const row = { caseKey: "a", label: "a very long ".repeat(30), resultsEquivalent: true, ioOutcome: "better", readsBefore: 100, readsAfter: 50, elapsedBeforeMs: 10, elapsedAfterMs: 30, cpuBeforeMs: 4, cpuAfterMs: 8, policyVerdict: "fail", policyReasons: ["elapsed-regressed"], policyUnavailableReason: null }
  render(<OutcomeDimensions outcomes={{ resultsEquivalent: 2, resultsDifferent: 0, resultsUnavailable: 0, ioBetter: 1, ioSame: 1, ioWorse: 0, ioUnavailable: 0, policyPass: 1, policyFail: 1, policyUnavailable: 0 }} cases={[{ ...row, caseKey: "b", label: "second", policyVerdict: "pass", policyReasons: [], ioOutcome: "same" }, row]} />)
  const trigger = screen.getByRole("button", { name: /IO: 1 better, 1 same, 0 worse, 0 unavailable/ })
  trigger.focus()
  await userEvent.keyboard("{Enter}")
  const dialog = within(screen.getByRole("dialog"))
  expect(dialog.getAllByRole("row")[1]).toHaveTextContent("elapsed-regressed")
  expect(dialog.getAllByRole("rowheader")[0]).toHaveClass("whitespace-normal", "break-words")
  expect(dialog.getAllByRole("row")[1]).toHaveTextContent("10 → 30")
  await userEvent.keyboard("{Escape}")
  expect(trigger).toHaveFocus()
})

test("shows independent counts, unavailable evidence and a zero baseline without a percentage", async () => {
  const base = { caseKey: "0", label: "first", resultsEquivalent: true, ioOutcome: "better", readsBefore: 100, readsAfter: 50, elapsedBeforeMs: 10, elapsedAfterMs: 30, cpuBeforeMs: 1, cpuAfterMs: 2, policyVerdict: "pass", policyReasons: [], policyUnavailableReason: null }
  const cases = [base, { ...base, caseKey: "1", policyVerdict: "fail", policyReasons: ["elapsed-regressed"] }, { ...base, caseKey: "2" }, { ...base, caseKey: "3", ioOutcome: "same" }]
  const { rerender } = render(<OutcomeDimensions cases={cases} outcomes={outcomes} />)
  expect(screen.getByRole("button", { name: /IO: 3 better, 1 same, 0 worse, 0 unavailable/ })).toHaveTextContent("↑3")
  expect(screen.getByRole("button", { name: "Policy: 3 pass, 1 fail, 0 unavailable" })).toHaveTextContent("⚠1")
  rerender(<OutcomeDimensions outcomes={{ resultsEquivalent: 0, resultsDifferent: 1, resultsUnavailable: 0, ioBetter: 0, ioSame: 0, ioWorse: 0, ioUnavailable: 1, policyPass: 0, policyFail: 0, policyUnavailable: 1 }} cases={[{ ...base, readsBefore: 0, readsAfter: 10, resultsEquivalent: false, ioOutcome: "unavailable", policyVerdict: null, policyUnavailableReason: "reversed comparison" }]} />)
  const policy = screen.getByRole("button", { name: "Policy: 0 pass, 0 fail, 1 unavailable" })
  await userEvent.click(policy)
  const dialog = within(screen.getByRole("dialog"))
  expect(dialog.getByText("baseline zero")).toBeInTheDocument()
  expect(dialog.getByText("Different")).toBeInTheDocument()
  expect(dialog.getByText("reversed comparison")).toBeInTheDocument()
  await userEvent.keyboard("{Escape}")
  expect(policy).toHaveFocus()
})


test("sorts policy failures, then IO regressions, preserving order within each category", async () => {
  const base = { caseKey: "", label: "", resultsEquivalent: null, ioOutcome: "same", readsBefore: 100, readsAfter: 100, elapsedBeforeMs: null, elapsedAfterMs: null, cpuBeforeMs: null, cpuAfterMs: null, policyVerdict: null, policyReasons: [], policyUnavailableReason: "not recorded" }
  const cases = [{ ...base, caseKey: "a", label: "same first" }, { ...base, caseKey: "b", label: "worse first", ioOutcome: "worse" }, { ...base, caseKey: "c", label: "failure", policyVerdict: "fail" }, { ...base, caseKey: "d", label: "worse second", ioOutcome: "worse" }, { ...base, caseKey: "e", label: "same second" }]
  render(<OutcomeDimensions cases={cases} outcomes={{ resultsEquivalent: 0, resultsDifferent: 0, resultsUnavailable: 5, ioBetter: 0, ioSame: 3, ioWorse: 2, ioUnavailable: 0, policyPass: 0, policyFail: 1, policyUnavailable: 4 }} />)
  expect(screen.getByLabelText(/Results: 0 equivalent, 0 different, 5 unavailable/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: /IO:/ }))
  expect(within(screen.getByRole("dialog")).getAllByRole("rowheader").map(row => row.textContent)).toEqual(["failure", "worse first", "worse second", "same first", "same second"])
})
