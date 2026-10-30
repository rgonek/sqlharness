import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { CaseOutcomeDetails } from "./CaseOutcomeDetails"

test("separates IO from recorded policy and opens a failure-first table by keyboard", async () => {
  const row = { id: "a", label: "a very long ".repeat(30), resultsEquivalent: true, ioOutcome: "better", readsBefore: 100, readsAfter: 50, elapsedBeforeMs: 10, elapsedAfterMs: 30, cpuBeforeMs: 4, cpuAfterMs: 8, policyVerdict: "fail", policyReasons: ["elapsed-regressed"], policyUnavailableReason: null }
  render(<CaseOutcomeDetails cases={[{ ...row, id: "b", label: "second", policyVerdict: "pass", policyReasons: [], ioOutcome: "same" }, row]} />)
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
  const { rerender } = render(<CaseOutcomeDetails cases={cases} />)
  expect(screen.getByRole("button", { name: "IO: 3 better, 1 same, 0 worse, 0 unavailable" })).toHaveTextContent("↑3")
  expect(screen.getByRole("button", { name: "Policy: 3 pass, 1 fail, 0 unavailable" })).toHaveTextContent("⚠1")
  rerender(<CaseOutcomeDetails cases={[{ ...base, readsBefore: 0, readsAfter: 10, resultsEquivalent: false, ioOutcome: "unavailable", policyVerdict: null, policyUnavailableReason: "reversed comparison" }]} />)
  const policy = screen.getByRole("button", { name: "Policy: 0 pass, 0 fail, 1 unavailable" })
  await userEvent.click(policy)
  const dialog = within(screen.getByRole("dialog"))
  expect(dialog.getByText("0 → 10 (—)")).toBeInTheDocument()
  expect(dialog.getByText("different")).toBeInTheDocument()
  expect(dialog.getByText("unavailable: reversed comparison")).toBeInTheDocument()
  await userEvent.keyboard("{Escape}")
  expect(policy).toHaveFocus()
})

