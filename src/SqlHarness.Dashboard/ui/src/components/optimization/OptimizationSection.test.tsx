import { screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { operation, session, sessionDetail } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

const versions = Array.from({ length: 40 }, (_, index) => ({
  version: `v${index}`, hash: `hash-${index}`, logicalReadsSum: index * 10, logicalReadsMean: index * 10,
  scansSum: index, elapsedMedianMs: index, caseOutcome: "1/1 ok", measuredIn: [index + 10], latestOperationId: index + 10,
  verdictBasis: "whole batch",
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

function stubSession(routes: Parameters<typeof stubFetch>[0]) {
  return stubFetch({
    "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false },
    "/api/sessions/7/operations": { items: [operation({ id: 70, operation: "compare" })], nextCursor: null },
    ...routes,
  })
}

test("does not request or render optimization when the session has no compare", async () => {
  const calls = stubSession({ "/api/sessions/7": sessionWithCompare(false) })
  renderApp("/sessions/7")
  expect(await screen.findByRole("heading", { name: "cli:abc" })).toBeInTheDocument()
  expect(screen.queryByRole("heading", { name: "Optimization" })).not.toBeInTheDocument()
  expect(calls.some(call => call.includes("/optimization"))).toBe(false)
})

test("puts the optimization section first, preserves scope defaults and lists unassigned compares", async () => {
  stubSession({
    "/api/sessions/7": sessionWithCompare(true),
    "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false },
    "/api/sessions/7/optimization": section,
    "/api/sessions/7/optimization/compare": comparison,
  })
  renderApp("/sessions/7")
  expect(await screen.findByRole("heading", { name: "Optimization" })).toBeInTheDocument()
  const allOperations = screen.getByText("All operations")
  expect(screen.getByRole("heading", { name: "Optimization" }).compareDocumentPosition(allOperations) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(await screen.findByRole("radio", { name: "A v0" })).toBeChecked()
  expect(await screen.findByRole("radio", { name: "B v39" })).toBeChecked()
  expect(screen.getAllByText("basis: whole batch").length).toBeGreaterThan(0)
  expect(screen.getByText("Unassigned compares (1)")).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "Compare #999" })).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Unassigned compares (1)" }))
  const unassignedLink = screen.getByRole("link", { name: "Compare #999" })
  expect(unassignedLink).toHaveAttribute("href", expect.stringContaining("/operations/999"))
  expect(Object.fromEntries(new URL(unassignedLink.getAttribute("href")!, "http://dashboard").searchParams)).toMatchObject({ returnSession: "7", returnUnassigned: "true" })
})

test.each(["", "Orders"])("omits blank metadata and adjacent separators for subject %s", async subject => {
  stubSession({
    "/api/sessions/7": sessionWithCompare(true),
    "/api/sessions/7/optimization": { ...section, subjects: [{ ...section.subjects[0]!, subject, database: " ", engine: "" }] },
    "/api/sessions/7/optimization/compare": comparison,
  })
  renderApp("/sessions/7")
  await waitFor(() => expect(document.querySelector(".optimization-card > div p")?.textContent).toBe(subject ? "Orders · whole query" : "whole query"))
})

test("renders forty versions and updates the URL when a version picker changes", async () => {
  stubSession({
    "/api/sessions/7": sessionWithCompare(true),
    "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false },
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
  await userEvent.click(within(table).getByRole("radio", { name: "B v2" }))
  expect(within(table).getByRole("radio", { name: "A v1" })).toBeChecked()
  expect(within(table).getByRole("radio", { name: "B v2" })).toBeChecked()
  within(table).getByRole("radio", { name: "A v2" }).focus()
  await userEvent.keyboard(" ")
  expect(within(table).getByRole("radio", { name: "A v2" })).toBeChecked()
  expect(within(table).getByRole("radio", { name: "B v2" })).toBeChecked()
  expect(within(table).getByRole("rowheader", { name: "v1" })).toBeInTheDocument()
  expect(within(table).getByRole("radio", { name: "B v1" })).not.toBeChecked()
})

test("version table shows measure-only outcomes and unverified comparison label", async () => {
  const measured = { ...section, unassigned: [], subjects: [{ ...section.subjects[0]!, groups: [{
    ...section.subjects[0]!.groups[0]!, defaultA: "v0", defaultB: "v1", scopes: [{ scope: "whole query", defaultA: "v0", defaultB: "v1", versions: [
      { ...versions[0]!, version: "v0", caseOutcome: "measured only", measuredIn: [215], latestOperationId: 215 },
      { ...versions[1]!, version: "v1", caseOutcome: "measured only", measuredIn: [216], latestOperationId: 216 },
    ] }],
  }] }] }
  stubSession({
    "/api/sessions/7": sessionWithCompare(true),
    "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false },
    "/api/sessions/7/optimization": measured,
    "/api/sessions/7/optimization/compare": { ...comparison, basis: "DifferentRunsSameConditions", operationId: null, equivalenceLabel: "results not compared" },
  })
  renderApp("/sessions/7")

  expect(await screen.findAllByText("measured only")).toHaveLength(2)
  expect(await screen.findByText("results not compared")).toBeInTheDocument()
})

test("shows group and scope selectors only when the selected group has choices", async () => {
  stubSession({
    "/api/sessions/7": sessionWithCompare(true),
    "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false },
    "/api/sessions/7/optimization": sectionWithChoices,
    "/api/sessions/7/optimization/compare": comparison,
  })
  const { router } = renderApp("/sessions/7?subject=Orders&db=shop&group=fp-2&scope=fragment%20A&a=v4&b=v9&case=case-1&agg=mean&view=whole")
  expect(await screen.findByRole("combobox", { name: "Orders conditions group" })).toBeInTheDocument()
  const scope = await screen.findByRole("combobox", { name: "Orders scope" })
  await userEvent.click(scope)
  await userEvent.click(await screen.findByRole("option", { name: "whole query" }))
  expect(router.state.location.search).toMatchObject({ group: "fp-2", scope: "whole query" })
  expect(screen.getByRole("combobox", { name: "Orders scope" })).toHaveTextContent("whole query")
  for (const key of ["a", "b", "case", "agg", "view"]) {
    expect(router.state.location.search).not.toHaveProperty(key)
  }
  expect(await screen.findByRole("radio", { name: "A v0" })).toBeChecked()
  expect(screen.getByRole("radio", { name: "B v39" })).toBeChecked()
})

test("changing conditions group selects its first scope and clears all dependent selections", async () => {
  stubSession({
    "/api/sessions/7": sessionWithCompare(true),
    "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false },
    "/api/sessions/7/optimization": sectionWithChoices,
    "/api/sessions/7/optimization/compare": comparison,
  })
  const { router } = renderApp("/sessions/7?subject=Orders&db=shop&group=fp-1&scope=whole%20query&a=v1&b=v2&case=case-1&agg=mean&view=whole")

  const group = await screen.findByRole("combobox", { name: "Orders conditions group" })
  await userEvent.click(group)
  await userEvent.click(await screen.findByRole("option", { name: "2 cases" }))

  expect(router.state.location.search).toMatchObject({ group: "fp-2", scope: "whole query" })
  expect(group).toHaveTextContent("2 cases")
  expect(screen.getByRole("combobox", { name: "Orders scope" })).toHaveTextContent("whole query")
  for (const key of ["a", "b", "case", "agg", "view"]) {
    expect(router.state.location.search).not.toHaveProperty(key)
  }
  expect(await screen.findByRole("radio", { name: "A v0" })).toBeChecked()
  expect(screen.getByRole("radio", { name: "B v39" })).toBeChecked()
  await userEvent.click(screen.getByRole("button", { name: "All cases · Sum" }))
  expect(screen.getByRole("radio", { name: /^All cases$/ })).toBeChecked()
})

test("hides one-option controls and gives each subject its own accessible selector names", async () => {
  const multiple = {
    ...sectionWithChoices,
    subjects: [
      sectionWithChoices.subjects[0]!,
      {
        ...sectionWithChoices.subjects[0]!, subject: "Customers", database: "crm",
        groups: [...sectionWithChoices.subjects[0]!.groups].reverse().map((item, index) => ({
          ...item, fingerprint: `customers-${index}`, subject: "Customers", database: "crm",
        })),
      },
      {
        ...section.subjects[0]!, subject: "Products", database: "catalog",
      },
    ],
  }
  stubSession({ "/api/sessions/7": sessionWithCompare(true), "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false }, "/api/sessions/7/optimization": multiple, "/api/sessions/7/optimization/compare": comparison })
  renderApp("/sessions/7?subject=Orders&db=shop&group=fp-2")

  expect(await screen.findByRole("combobox", { name: "Orders conditions group" })).toBeInTheDocument()
  expect(screen.getByRole("combobox", { name: "Orders scope" })).toBeInTheDocument()
  expect(screen.getByRole("combobox", { name: "Customers conditions group" })).toBeInTheDocument()
  expect(screen.getByRole("combobox", { name: "Customers scope" })).toBeInTheDocument()
  expect(screen.queryByRole("combobox", { name: "Products conditions group" })).not.toBeInTheDocument()
  expect(screen.queryByRole("combobox", { name: "Products scope" })).not.toBeInTheDocument()
})

test("keeps B unset when the selected scope has no qualified default", async () => {
  const scoped = { ...sectionWithChoices, subjects: [{ ...sectionWithChoices.subjects[0]!, groups: [{
    ...sectionWithChoices.subjects[0]!.groups[1]!,
    scopes: [{ scope: "fragment A", defaultA: "v4", defaultB: null, versions: versions.slice(4, 10) }, sectionWithChoices.subjects[0]!.groups[1]!.scopes[1]!],
  }] }] }
  const calls = stubSession({ "/api/sessions/7": sessionWithCompare(true), "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false }, "/api/sessions/7/optimization": scoped })
  renderApp("/sessions/7?subject=Orders&db=shop&group=fp-2&scope=fragment%20A")
  expect(await screen.findByRole("radio", { name: "A v4" })).toBeChecked()
  expect(screen.getByText("Select a version for B to compare it with v4.")).toBeInTheDocument()
  expect(screen.getByRole("radio", { name: "B v9" })).not.toBeChecked()
  expect(calls.some(call => call.includes("/optimization/compare"))).toBe(false)
})

test("renders a separate card for every subject and database", async () => {
  const multiple = { ...section, subjects: [section.subjects[0]!, { ...section.subjects[0]!, subject: "Customers", database: "crm", groups: [{
    ...section.subjects[0]!.groups[0]!, fingerprint: "fp-customers", subject: "Customers", database: "crm", defaultA: "v0", defaultB: "v1",
    scopes: [{ ...section.subjects[0]!.groups[0]!.scopes[0]!, versions: versions.slice(0, 2) }],
  }] }] }
  stubSession({
    "/api/sessions/7": sessionWithCompare(true), "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false }, "/api/sessions/7/optimization": multiple,
    "/api/sessions/7/optimization/compare": comparison,
  })
  const { router } = renderApp("/sessions/7?subject=Customers&db=crm&group=fp-customers&scope=whole%20query")
  expect(await screen.findByRole("heading", { name: "Orders" })).toBeInTheDocument()
  expect(screen.getByRole("heading", { name: "Customers" })).toBeInTheDocument()
  expect(screen.getByRole("table", { name: "Orders-shop versions" })).toBeInTheDocument()
  expect(screen.getByRole("table", { name: "Customers-crm versions" })).toBeInTheDocument()
  await userEvent.click(within(screen.getByRole("table", { name: "Orders-shop versions" })).getByRole("radio", { name: "A v1" }))
  expect(router.state.location.search).toMatchObject({ subject: "Orders", db: "shop", group: "fp-1", scope: "whole query" })
  expect(router.state.location.search).not.toMatchObject({ subject: "Customers", db: "crm" })
})

test("links every retained measurement operation with the current session return context", async () => {
  const measured = { ...section, subjects: [{ ...section.subjects[0]!, groups: [{ ...section.subjects[0]!.groups[0]!, scopes: [{
    ...section.subjects[0]!.groups[0]!.scopes[0]!, versions: [
      { ...versions[0]!, measuredIn: [201, 214], latestOperationId: 214 },
      { ...versions[1]!, measuredIn: [219], latestOperationId: 219 },
    ],
  }] }] }] }
  stubSession({ "/api/sessions/7": sessionWithCompare(true), "/api/sessions/7/sql": { hash: "", text: null, tooLarge: false }, "/api/sessions/7/optimization": measured, "/api/sessions/7/optimization/compare": comparison })
  renderApp("/sessions/7?subject=Orders&db=shop&group=fp-1&scope=whole%20query")
  await userEvent.click(await screen.findByRole("button", { name: "Details for v0" }))
  const one = await screen.findByRole("link", { name: "Operation #201" })
  const two = within(screen.getByRole("dialog")).getByRole("link", { name: "Operation #214" })
  const targets = [one.getAttribute("href")!, two.getAttribute("href")!]
  await userEvent.keyboard("{Escape}")
  await userEvent.click(screen.getByRole("button", { name: "Details for v1" }))
  const three = screen.getByRole("link", { name: "Operation #219" })
  targets.push(three.getAttribute("href")!)
  for (const href of targets) {
    const url = new URL(href, "http://dashboard")
    expect(url.pathname).toMatch(/^\/operations\/(201|214|219)$/)
    expect(Object.fromEntries(url.searchParams)).toMatchObject({ returnSession: "7", returnSubject: "Orders", returnDb: "shop", returnGroup: "fp-1", returnScope: "whole query" })
  }
})
