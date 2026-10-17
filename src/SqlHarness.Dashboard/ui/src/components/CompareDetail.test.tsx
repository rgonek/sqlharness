import { render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { CompareDetail } from "./CompareDetail"
import { conditions, operation, operationDetail, variant } from "@/test/fixtures"
import { stubFetch } from "@/test/render"

const renderCompare = (detail: ReturnType<typeof operationDetail>) => {
  stubFetch({ "/api/operations/10/repro": { text: null, completeness: "Unavailable", label: "Script not available — enable storeSensitive" } })
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><CompareDetail detail={detail} /></QueryClientProvider>)
}

test("keeps execution, Core regression, result equivalence, and matrix case outcomes separate", () => {
  renderCompare(operationDetail({
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
  }))
  expect(screen.getAllByText(/Execution succeeded/)).toHaveLength(3)
  expect(screen.getAllByText("Regression detected")).toHaveLength(2)
  expect(screen.getByText("Results differ")).toBeInTheDocument()
  const cases = screen.getByRole("tabpanel", { name: "Cases" })
  expect(within(cases).getByText("Cell 0")).toBeInTheDocument()
  expect(within(cases).getByText("Cell 1")).toBeInTheDocument()
  expect(within(cases).getByText("Regression detected")).toBeInTheDocument()
  expect(screen.getByRole("region", { name: "Compare detail" })).toHaveTextContent("v0 → v1")
})

test("shows canonical case labels, truncates long labels, and hides the single-case label", () => {
  const fullLabel = "@ClassType = N'ClientConfigurationModel2'"
  renderCompare(operationDetail({
    operation: operation({ operation: "compare", status: "succeeded" }),
    summary: { kind: "compare-matrix", comparison: "Ordered", cases: 2 },
    conditions: conditions({ cases: [
      { id: "0", index: 0, label: fullLabel, setName: null, varying: [], fingerprint: "a" },
      { id: "1", index: 1, label: "@tenant = N'a very long tenant label that should be clipped in the heading'", setName: null, varying: [], fingerprint: "b" },
    ] }),
    compareCases: [{ index: 0 }, { index: 1 }],
    variants: [
      variant({ ordinal: 0, variant: "baseline", matrixCell: 0 }), variant({ ordinal: 1, variant: "candidate", matrixCell: 0 }),
      variant({ ordinal: 2, variant: "baseline", matrixCell: 1 }), variant({ ordinal: 3, variant: "candidate", matrixCell: 1 }),
    ],
  }))
  const heading = screen.getByTitle(fullLabel)
  expect(heading).toHaveTextContent("@ClassType = N'ClientConfigurationModel2'")
  const longLabel = "@tenant = N'a very long tenant label that should be clipped in the heading'"
  const long = screen.getByTitle(longLabel)
  expect(long).toHaveTextContent(/^@tenant = N'a very long/)
  expect(long).toHaveTextContent(/heading'$/)
  expect(long).not.toHaveTextContent(longLabel)

  const single = renderCompare(operationDetail({
    operation: operation({ operation: "compare" }), conditions: conditions({ cases: [{ id: "0", index: 0, label: "Case 1 of 1", setName: null, varying: [], fingerprint: "single" }] }),
  }))
  expect(single.queryByText("Case 1 of 1")).not.toBeInTheDocument()
})

test("shows missing historic conditions and unrecorded equivalence without borrowing an assessment", async () => {
  renderCompare(operationDetail({
    operation: operation({ operation: "compare" }),
    compareConditions: null, summary: null, compareRegression: null, compareCases: null,
  }))
  expect(screen.getByText("Equivalence unavailable (legacy record)")).toBeInTheDocument()
  expect(screen.getByText("No assessment")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("tab", { name: "Measurement conditions" }))
  expect(screen.getByText("Historical measurement conditions are unavailable.")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("tab", { name: "SQL changes" }))
  expect(screen.getByText(/SQL text is not stored for both sides/)).toBeInTheDocument()
})

test("shows plans and table IO from both compare sides", async () => {
  stubFetch({ "/api/operations/10/repro": { text: null, completeness: "Unavailable", label: "Script not available — enable storeSensitive" } })
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={queryClient}><CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare" }),
    variants: [
      variant({ ordinal: 0, variant: "baseline", tableIo: [{ ...variant().tableIo[0], table: "BeforeRows" }], plans: [{ repetition: 1, ordinal: 0, hash: "A".repeat(64), stored: false }] }),
      variant({ ordinal: 1, variant: "candidate", tableIo: [{ ...variant().tableIo[0], table: "AfterRows" }], plans: [{ repetition: 1, ordinal: 1, hash: "B".repeat(64), stored: false }] }),
    ],
  })} /></QueryClientProvider>)
  await userEvent.click(screen.getByRole("tab", { name: "Plans and IO" }))
  expect(screen.getAllByText("BeforeRows")).toHaveLength(3)
  expect(screen.getAllByText("AfterRows")).toHaveLength(3)
  expect(screen.getAllByText(/plan not stored/)).toHaveLength(2)
})

test("copies compare Markdown without operation metadata or SQL when SQL text is unavailable", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } })
  renderCompare(operationDetail({
    operation: operation({ operation: "compare", engine: "sqlserver", server: "server-secret", database: "database-secret", profile: "profile-secret" }),
    vars: { tenant: "var-secret" }, artifactDirectory: "artifact-secret", sqlText: null, candidateSqlText: null,
    compareSubject: "orders", compareScope: "whole", baselineVersion: "v0", candidateVersion: "v1",
    conditions: conditions({ setup: { hash: "setup", text: "setup-secret" }, fixed: [{ name: "@p", effectiveType: "int", value: "parameter-secret" }] }),
  }))

  await userEvent.click(screen.getByRole("button", { name: "Copy" }))

  await waitFor(() => expect(writeText).toHaveBeenCalledOnce())
  const markdown = writeText.mock.calls[0][0] as string
  expect(markdown.startsWith("### SQL compare: orders / whole (v0 → v1)")).toBe(true)
  expect(markdown).toContain("SQL diff not included (SQL text not stored or over the size limit).")
  for (const secret of ["server-secret", "database-secret", "profile-secret", "var-secret", "setup-secret", "parameter-secret", "artifact-secret"]) {
    expect(markdown).not.toContain(secret)
  }
})

test("shows original setup SQL with an independent copy control beside compare SQL", async () => {
  const setup = "CREATE TABLE #session_data (id int);\nINSERT INTO #session_data VALUES (1);"
  renderCompare(operationDetail({
    operation: operation({ operation: "compare" }),
    sqlText: "SELECT id FROM #session_data",
    candidateSqlText: "SELECT id FROM #session_data WHERE id > 0",
    conditions: conditions({ setup: { hash: "setup", text: setup } }),
  }))
  await userEvent.click(screen.getByRole("tab", { name: "SQL changes" }))
  const panel = screen.getByRole("region", { name: "Setup SQL" })
  expect(within(panel).getByTestId("sql-code").textContent).toBe(setup)
  expect(within(panel).getByRole("button", { name: "Show formatted" })).toBeInTheDocument()
  expect(within(panel).getByRole("button", { name: "Copy" })).toBeInTheDocument()
  expect(within(panel).getByText(/same database session/)).toBeInTheDocument()
  expect(screen.getByRole("tab", { name: "Diff" })).toBeInTheDocument()
})

test("explains when setup SQL is unavailable or omitted by journal limits", async () => {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const { rerender } = render(<QueryClientProvider client={queryClient}><CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare" }),
    conditions: conditions(),
  })} /></QueryClientProvider>)
  await userEvent.click(screen.getByRole("tab", { name: "SQL changes" }))
  expect(screen.getByText(/Setup SQL is not stored/)).toBeInTheDocument()
  rerender(<QueryClientProvider client={queryClient}><CompareDetail detail={operationDetail({
    operation: operation({ operation: "compare" }),
    conditions: conditions(),
  })} /></QueryClientProvider>)
  expect(screen.getByText(/Setup SQL is not stored/)).toBeInTheDocument()
  expect(within(screen.getByRole("region", { name: "Setup SQL" })).queryByRole("button", { name: "Copy" })).not.toBeInTheDocument()
})

test("uses the dedicated setup column even if the bounded conditions omitted setup", async () => {
  const setup = "CREATE TABLE #big_data (id int);"
  renderCompare(operationDetail({
    operation: operation({ operation: "compare" }),
    setupSqlText: setup,
    conditions: conditions(),
  }))
  await userEvent.click(screen.getByRole("tab", { name: "SQL changes" }))
  const section = screen.getByRole("region", { name: "Setup SQL" })
  expect(within(section).getByTestId("sql-code")).toHaveTextContent(setup)
  expect(screen.queryByText(/Setup SQL was omitted/)).not.toBeInTheDocument()
})
