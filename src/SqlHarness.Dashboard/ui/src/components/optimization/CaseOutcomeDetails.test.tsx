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
