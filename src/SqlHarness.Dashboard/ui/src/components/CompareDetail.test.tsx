import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { CompareDetail } from "./CompareDetail"
import { operation, operationDetail, variant } from "@/test/fixtures"

test("keeps execution, Core regression, result equivalence, and matrix case outcomes separate", () => {
  render(<CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare", status: "succeeded", engine: "sqlserver" }),
    compareSubject: "orders", compareScope: "whole", baselineVersion: "v0", candidateVersion: "v1",
    compareRegression: { verdict: "fail", reasons: ["elapsed-regressed"] },
    summary: { kind: "compare-matrix", comparison: "Ordered", cases: 2 },
    compareCases: [
      { index: 0, equivalence: { mode: "Ordered", equivalent: true }, regression: { verdict: "pass" } },
      { index: 1, equivalence: { mode: "Ordered", equivalent: false }, regression: { verdict: "fail" } },
    ],
    variants: [
      variant({ ordinal: 0, variant: "baseline", matrixCell: 0, elapsedMs: { min: 10, median: 12, max: 14 } }),
      variant({ ordinal: 1, variant: "candidate", matrixCell: 0, elapsedMs: { min: 8, median: 9, max: 11 } }),
      variant({ ordinal: 2, variant: "baseline", matrixCell: 1, elapsedMs: { min: 10, median: 12, max: 14 } }),
      variant({ ordinal: 3, variant: "candidate", matrixCell: 1, elapsedMs: { min: 16, median: 18, max: 20 } }),
    ],
  })} />)
  expect(screen.getAllByText(/Execution succeeded/)).toHaveLength(3)
  expect(screen.getAllByText("Regression detected")).toHaveLength(2)
  expect(screen.getByText("Results differ")).toBeInTheDocument()
  const cases = screen.getByRole("tabpanel", { name: "Cases" })
  expect(within(cases).getByText("Cell 0")).toBeInTheDocument()
  expect(within(cases).getByText("Cell 1")).toBeInTheDocument()
  expect(within(cases).getByText("Regression detected")).toBeInTheDocument()
  expect(screen.getByRole("region", { name: "Compare detail" })).toHaveTextContent("v0 → v1")
})

test("shows missing historic conditions and unrecorded equivalence without borrowing an assessment", async () => {
  render(<CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare" }),
    compareConditions: null, summary: null, compareRegression: null, compareCases: null,
  })} />)
  expect(screen.getByText("Equivalence unavailable (legacy record)")).toBeInTheDocument()
  expect(screen.getByText("No assessment")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("tab", { name: "Measurement conditions" }))
  expect(screen.getByText("Historical measurement conditions are unavailable.")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("tab", { name: "SQL changes" }))
  expect(screen.getByText(/SQL text is not stored for both sides/)).toBeInTheDocument()
})

test("shows plans and table IO from both compare sides", async () => {
  render(<CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare" }),
    variants: [
      variant({ ordinal: 0, variant: "baseline", tableIo: [{ ...variant().tableIo[0], table: "BeforeRows" }], plans: [{ repetition: 1, ordinal: 0, hash: "A".repeat(64), stored: false }] }),
      variant({ ordinal: 1, variant: "candidate", tableIo: [{ ...variant().tableIo[0], table: "AfterRows" }], plans: [{ repetition: 1, ordinal: 1, hash: "B".repeat(64), stored: false }] }),
    ],
  })} />)
  await userEvent.click(screen.getByRole("tab", { name: "Plans and IO" }))
  expect(screen.getByText("BeforeRows")).toBeInTheDocument()
  expect(screen.getByText("AfterRows")).toBeInTheDocument()
  expect(screen.getAllByText(/plan not stored/)).toHaveLength(2)
})

test("shows original setup SQL with an independent copy control beside compare SQL", async () => {
  const setup = "CREATE TABLE #session_data (id int);\nINSERT INTO #session_data VALUES (1);"
  render(<CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare" }),
    sqlText: "SELECT id FROM #session_data",
    candidateSqlText: "SELECT id FROM #session_data WHERE id > 0",
    compareConditions: { setupSql: setup, timeoutSeconds: 30 },
  })} />)
  await userEvent.click(screen.getByRole("tab", { name: "SQL changes" }))
  const panel = screen.getByRole("region", { name: "Setup SQL" })
  expect(within(panel).getByTestId("sql-code").textContent).toBe(setup)
  expect(within(panel).getByRole("button", { name: "Show formatted" })).toBeInTheDocument()
  expect(within(panel).getByRole("button", { name: "Copy" })).toBeInTheDocument()
  expect(within(panel).getByText(/same database session/)).toBeInTheDocument()
  expect(screen.getByLabelText("SQL diff")).toBeInTheDocument()
})

test("explains when setup SQL is unavailable or omitted by journal limits", async () => {
  const { rerender } = render(<CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare" }),
    compareConditions: { timeoutSeconds: 30 },
  })} />)
  await userEvent.click(screen.getByRole("tab", { name: "SQL changes" }))
  expect(screen.getByText(/Setup SQL is not stored/)).toBeInTheDocument()
  rerender(<CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare" }),
    compareConditions: { omittedSensitiveFields: ["setupSql"], timeoutSeconds: 30 },
  })} />)
  expect(screen.getByText(/Setup SQL was omitted/)).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: "Copy" })).not.toBeInTheDocument()
})

test("uses the dedicated setup column even if the bounded conditions omitted setup", async () => {
  const setup = "CREATE TABLE #big_data (id int);"
  render(<CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare" }),
    setupSqlText: setup,
    compareConditions: { omittedSensitiveFields: ["setupSql"] },
  })} />)
  await userEvent.click(screen.getByRole("tab", { name: "SQL changes" }))
  const section = screen.getByRole("region", { name: "Setup SQL" })
  expect(within(section).getByTestId("sql-code")).toHaveTextContent(setup)
  expect(screen.queryByText(/Setup SQL was omitted/)).not.toBeInTheDocument()
})
