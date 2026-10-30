import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { CaseOutcomeDetails } from "./CaseOutcomeDetails"

test("separates IO from recorded policy and opens a failure-first table by keyboard", async () => {
  const row = { id: "a", label: "a very long ".repeat(30), resultsEquivalent: true, ioOutcome: "better", readsBefore: 100, readsAfter: 50, elapsedBeforeMs: 10, elapsedAfterMs: 30, cpuBeforeMs: 4, cpuAfterMs: 8, policyVerdict: "fail", policyReasons: ["elapsed-regressed"], policyUnavailableReason: null }
  render(<CaseOutcomeDetails counts={{ total: 2, results: { equivalent: 2, different: 0, unavailable: 0 }, io: { better: 1, same: 1, worse: 0, unavailable: 0 }, policy: { pass: 1, fail: 1, unavailable: 0 } }} cases={[{ ...row, id: "b", label: "second", policyVerdict: "pass", policyReasons: [], ioOutcome: "same" }, row]} />)
  const trigger = screen.getByRole("button", { name: /IO: 1 better, 1 same, 0 worse, 0 unavailable/ })
  trigger.focus()
  await userEvent.keyboard("{Enter}")
  const dialog = within(screen.getByRole("dialog"))
  expect(dialog.getAllByRole("row")[1]).toHaveTextContent("elapsed-regressed")
  expect(dialog.getAllByRole("rowheader")[0]).toHaveClass("whitespace-normal", "break-all")
  expect(dialog.getAllByRole("row")[1]).toHaveTextContent("10 → 30")
  await userEvent.keyboard("{Escape}")
  expect(trigger).toHaveFocus()
})

test("shows independent counts, unavailable evidence and a zero baseline without a percentage", async () => {
  const base = { id: "0", label: "first", resultsEquivalent: true, ioOutcome: "better", readsBefore: 100, readsAfter: 50, elapsedBeforeMs: 10, elapsedAfterMs: 30, cpuBeforeMs: 1, cpuAfterMs: 2, policyVerdict: "pass", policyReasons: [], policyUnavailableReason: null }
  const cases = [base, { ...base, id: "1", policyVerdict: "fail", policyReasons: ["elapsed-regressed"] }, { ...base, id: "2" }, { ...base, id: "3", ioOutcome: "same" }]
  const { rerender } = render(<CaseOutcomeDetails cases={cases} counts={{ total: 4, results: { equivalent: 4, different: 0, unavailable: 0 }, io: { better: 3, same: 1, worse: 0, unavailable: 0 }, policy: { pass: 3, fail: 1, unavailable: 0 } }} />)
  expect(screen.getByRole("button", { name: "IO: 3 better, 1 same, 0 worse, 0 unavailable" })).toHaveTextContent("↑3")
  expect(screen.getByRole("button", { name: "Policy: 3 pass, 1 fail, 0 unavailable" })).toHaveTextContent("⚠1")
  rerender(<CaseOutcomeDetails counts={{ total: 1, results: { equivalent: 0, different: 1, unavailable: 0 }, io: { better: 0, same: 0, worse: 0, unavailable: 1 }, policy: { pass: 0, fail: 0, unavailable: 1 } }} cases={[{ ...base, readsBefore: 0, readsAfter: 10, resultsEquivalent: false, ioOutcome: "unavailable", policyVerdict: null, policyUnavailableReason: "reversed comparison" }]} />)
  const policy = screen.getByRole("button", { name: "Policy: 0 pass, 0 fail, 1 unavailable" })
  await userEvent.click(policy)
  const dialog = within(screen.getByRole("dialog"))
  expect(dialog.getByText("0 → 10 (—)")).toBeInTheDocument()
  expect(dialog.getByText("different")).toBeInTheDocument()
  expect(dialog.getByText("unavailable: reversed comparison")).toBeInTheDocument()
  await userEvent.keyboard("{Escape}")
  expect(policy).toHaveFocus()
})


test("sorts policy failures, then IO regressions, preserving order within each category", async () => {
  const base = { id: "", label: "", resultsEquivalent: null, ioOutcome: "same", readsBefore: 100, readsAfter: 100, elapsedBeforeMs: null, elapsedAfterMs: null, cpuBeforeMs: null, cpuAfterMs: null, policyVerdict: null, policyReasons: [], policyUnavailableReason: "not recorded" }
  const cases = [{ ...base, id: "a", label: "same first" }, { ...base, id: "b", label: "worse first", ioOutcome: "worse" }, { ...base, id: "c", label: "failure", policyVerdict: "fail" }, { ...base, id: "d", label: "worse second", ioOutcome: "worse" }, { ...base, id: "e", label: "same second" }]
  render(<CaseOutcomeDetails cases={cases} counts={{ total: 5, results: { equivalent: 0, different: 0, unavailable: 5 }, io: { better: 0, same: 3, worse: 2, unavailable: 0 }, policy: { pass: 0, fail: 1, unavailable: 4 } }} />)
  expect(screen.getByText(/5 unavailable/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: /IO:/ }))
  expect(within(screen.getByRole("dialog")).getAllByRole("rowheader").map(row => row.textContent)).toEqual(["failure", "worse first", "worse second", "same first", "same second"])
})
