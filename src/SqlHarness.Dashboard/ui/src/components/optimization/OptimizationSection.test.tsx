import { screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { operation, session, sessionDetail } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

const versions = Array.from({ length: 40 }, (_, index) => ({
  version: `v${index}`, hash: `hash-${index}`, logicalReadsSum: index * 10, logicalReadsMean: index * 10,
  scansSum: index, elapsedMedianMs: index, caseOutcome: "1/1 ok", measuredIn: [index + 10], latestOperationId: index + 10,
}))

const section = {
  subjects: [{ subject: "Orders", database: "shop", engine: "SqlServer", groups: [{ fingerprint: "fp-1", label: "1 cases: @CustomerId = 9001",
    subject: "Orders", database: "shop", defaultA: "v0", defaultB: "v39", scopes: [{ scope: "whole query", defaultA: "v0", defaultB: "v39", versions }] }] }],
  unassigned: [{ operationId: 999, startedAt: "2026-01-01T00:00:00Z", status: "succeeded" }],
}
const sectionWithChoices = {
  ...section,
  subjects: [{ ...section.subjects[0]!, groups: [
    ...section.subjects[0]!.groups,
    { ...section.subjects[0]!.groups[0]!, fingerprint: "fp-2", label: "2 cases", scopes: [
      { scope: "fragment A", defaultA: "v4", defaultB: "v9", versions: versions.slice(4, 10) },
      { scope: "whole query", defaultA: "v0", defaultB: "v39", versions },
    ] },
  ] }],
}

const comparison = {
  a: "v0", b: "v39", basis: "MeasuredTogether", operationId: 214, equivalenceLabel: "results equivalent (direct)",
  cases: [{ id: "case-1", label: "@CustomerId = 9001", outcome: "worse" }], selectedCase: "all", aggregate: "sum", coverageLabel: "",
  mixed: null, tables: [{ table: "dbo.Orders", scansBefore: 1, scansAfter: 2, readsBefore: 100, readsAfter: 120, change: "+20%" }],
  collapsed: null, elapsed: { beforeMs: 10, afterMs: 12, policyVerdict: null, differentRun: false },
}

function sessionWithCompare(hasCompare: boolean) {
  return sessionDetail({ session: session({ id: 7, hasCompare }), operations: [operation({ id: 70, operation: "compare" })], unassignedCompareCount: 1 })
}

test("does not request or render optimization when the session has no compare", async () => {
  const calls = stubFetch({ "/api/sessions/7": sessionWithCompare(false) })
  renderApp("/sessions/7")
  expect(await screen.findByRole("heading", { name: "cli:abc" })).toBeInTheDocument()
  expect(screen.queryByRole("heading", { name: "Optimization" })).not.toBeInTheDocument()
  expect(calls.some(call => call.includes("/optimization"))).toBe(false)
})

test("puts the optimization section first, preserves scope defaults and lists unassigned compares", async () => {
  stubFetch({
    "/api/sessions/7": sessionWithCompare(true),
    "/api/sessions/7/optimization": section,
    "/api/sessions/7/optimization/compare": comparison,
  })
  renderApp("/sessions/7")
  expect(await screen.findByRole("heading", { name: "Optimization" })).toBeInTheDocument()
  const allOperations = screen.getByText("All operations")
  expect(screen.getByRole("heading", { name: "Optimization" }).compareDocumentPosition(allOperations) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(await screen.findByRole("radio", { name: "A v0" })).toBeChecked()
  expect(await screen.findByRole("radio", { name: "B v39" })).toBeChecked()
  expect(screen.getByText("Unassigned compares (1)")).toBeInTheDocument()
  await userEvent.click(screen.getByText("Unassigned compares (1)"))
  expect(screen.getByRole("link", { name: "Compare #999" })).toHaveAttribute("href", expect.stringContaining("/operations/999"))
})

test("renders forty versions and updates the URL when a version picker changes", async () => {
  stubFetch({
    "/api/sessions/7": sessionWithCompare(true),
    "/api/sessions/7/optimization": section,
    "/api/sessions/7/optimization/compare": comparison,
  })
  const { router } = renderApp("/sessions/7")
  const table = await screen.findByRole("table", { name: "Orders-shop versions" })
  expect(within(table).getAllByRole("row")).toHaveLength(41)
  expect(screen.getByText("whole query")).toBeInTheDocument()
  await userEvent.click(within(table).getByRole("radio", { name: "A v1" }))
  await screen.findByRole("radio", { name: "A v1", checked: true })
  expect(router.state.location.search).toMatchObject({ a: "v1", b: "v39", subject: "Orders", db: "shop", group: "fp-1" })
})

test("shows group and scope selectors only when the selected group has choices", async () => {
  stubFetch({
    "/api/sessions/7": sessionWithCompare(true),
    "/api/sessions/7/optimization": sectionWithChoices,
    "/api/sessions/7/optimization/compare": comparison,
  })
  const { router } = renderApp("/sessions/7?subject=Orders&db=shop&group=fp-2")
  const group = await screen.findByRole("combobox", { name: "Conditions group" })
  await userEvent.selectOptions(group, "fp-2")
  expect(router.state.location.search.group).toBe("fp-2")
  const scope = await screen.findByRole("combobox", { name: "Scope" })
  await userEvent.selectOptions(scope, "whole query")
  expect(router.state.location.search).toMatchObject({ group: "fp-2", scope: "whole query" })
  expect(router.state.location.search).not.toHaveProperty("a")
  expect(router.state.location.search).not.toHaveProperty("b")
  expect(await screen.findByRole("radio", { name: "A v0" })).toBeChecked()
})
