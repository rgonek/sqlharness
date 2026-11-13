import { act, screen, within, waitFor } from "@testing-library/react"
import { isInaccessible } from "@testing-library/dom"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { operation, session, sessionDetail } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

const outcomes = { resultsEquivalent: 2, resultsDifferent: 0, resultsUnavailable: 0, ioBetter: 2, ioSame: 0, ioWorse: 0, ioUnavailable: 0, policyPass: 1, policyFail: 1, policyUnavailable: 0 }
const versions = ["A", "B"].map(version => ({ version, hash: version, logicalReadsSum: 20, logicalReadsMean: 10, scansSum: 2, elapsedMedianMs: 4, caseOutcome: "ok", measuredIn: [], latestOperationId: 11, verdictBasis: "whole batch", metricCases: ["one", "two"].map(caseKey => ({ caseKey, caseId: caseKey, label: caseKey, logicalReads: 10, scans: 1, elapsedMs: 2, cpuMs: 1 })) }))
const section = { subjects: [{ subject: "Orders", database: "shop", engine: "SqlServer", groups: [{ fingerprint: "fp", label: "Two cases", subject: "Orders", database: "shop", defaultA: "A", defaultB: "B", scopes: [{ scope: "whole query", defaultA: "A", defaultB: "B", versions }] }] }], unassigned: [] }
const comparison = { a: "A", b: "B", basis: "MeasuredTogether", operationId: 10, equivalenceLabel: "equivalent", cases: ["one", "two"].map(id => ({ id, label: id, outcome: "better" })), selectedCase: "all", aggregate: "sum", coverageLabel: "2 of 2 cases", mixed: null, tables: [], collapsed: null, wholeBatch: { before: 100, after: 50, shared: null, label: "", sharedBefore: null, sharedAfter: null, changedBefore: null, changedAfter: null }, elapsed: { beforeMs: 10, afterMs: 20, policyVerdict: "fail", differentRun: false }, cpuBeforeMs: 8, cpuAfterMs: 16, scansBefore: 3, scansAfter: 2, outcomes, caseOutcomes: ["one", "two"].map(caseKey => ({ caseKey, label: caseKey, resultsEquivalent: true, ioOutcome: "better", policyVerdict: "fail", policyUnavailableReason: null, policyReasons: ["elapsed regressed"], readsBefore: 100, readsAfter: 50, elapsedBeforeMs: 10, elapsedAfterMs: 20, cpuBeforeMs: 8, cpuAfterMs: 16 })) }

test.each(["session", "agent-session"])("%s consumer hides placeholder pair evidence until the selected case response settles", async kind => {
  let resolveSingle!: (response: Response) => void
  const single = new Promise<Response>(resolve => { resolveSingle = resolve })
  let requestedSingle = false
  const path = kind === "session" ? "/api/sessions/7" : "/api/agent-sessions/codex/conversation-1"
  stubFetch({
    "/api/settings": { status: "valid", settings: { dashboard: { safetyNotifications: false, agentTranscripts: { enabled: true } } } },
    [path]: kind === "session" ? sessionDetail({ session: session({ id: 7, hasCompare: true }), operations: [operation({ operation: "compare" })] }) : { agentSession: { key: { agentKind: "codex", conversationId: "conversation-1" }, title: "Agent", children: [{ session: session({ id: 7, hasCompare: true }), exact: 1, inferred: 0, unassigned: 0 }], exact: 1, inferred: 0, unassigned: 0 }, note: null, operations: [] },
    [`${path}/operations`]: { items: [], nextCursor: null },
    [`${path}/optimization`]: section,
    [`${path}/sql`]: { hash: "", text: null, tooLarge: false },
    [`${path}/optimization/compare`]: (url: URL) => { if (url.searchParams.get("case") === "one") { requestedSingle = true; return single } return new Response(JSON.stringify(comparison), { status: 200 }) },
  })
  renderApp(kind === "session" ? "/sessions/7" : "/agent-sessions/codex/conversation-1")
  const table = await screen.findByRole("table", { name: "Orders-shop versions" })
  await within(table).findByRole("button", { name: /IO: 2 better/ })
  await userEvent.click(screen.getByRole("button", { name: "All cases · Sum" }))
  await userEvent.click(screen.getByRole("radio", { name: "one" }))
  await waitFor(() => expect(requestedSingle).toBe(true))
  expect(within(table).queryByRole("button", { name: /IO: 2 better/ })).not.toBeInTheDocument()
  expect(within(table).queryByRole("button", { name: /Policy: 1 pass, 1 fail/ })).not.toBeInTheDocument()
  const a = within(table).getByRole("radio", { name: "A A" }); a.focus()
  expect(a).toBeChecked()
  expect(a).toHaveFocus()
  await act(async () => resolveSingle(new Response(JSON.stringify({ ...comparison, selectedCase: "one", outcomes: { ...outcomes, resultsEquivalent: 1, ioBetter: 1, policyPass: 0 } }), { status: 200 })))
  expect(await within(table).findByRole("button", { name: /IO: 1 better/ })).toBeInTheDocument()
  expect(a).toHaveFocus()
})

function contextRoutes(kind: string, payload: unknown, compare: (url: URL) => Response | Promise<Response>) {
  const path = kind === "session" ? "/api/sessions/7" : "/api/agent-sessions/codex/conversation-1"
  return {
    "/api/settings": { status: "valid", settings: { dashboard: { safetyNotifications: false, agentTranscripts: { enabled: true } } } },
    [path]: kind === "session" ? sessionDetail({ session: session({ id: 7, hasCompare: true }) }) : { agentSession: { key: { agentKind: "codex", conversationId: "conversation-1" }, title: "Agent", children: [{ session: session({ id: 7, hasCompare: true }), exact: 1, inferred: 0, unassigned: 0 }], exact: 1, inferred: 0, unassigned: 0 }, note: null, operations: [] },
    [path + "/operations"]: { items: [], nextCursor: null },
    [path + "/optimization"]: payload,
    [path + "/sql"]: { hash: "", text: null, tooLarge: false },
    [path + "/optimization/compare"]: compare,
  }
}
const response = (data: unknown) => new Response(JSON.stringify(data), { status: 200 })

test.each(["session", "agent-session"])("%s reconciles a stale bookmarked case to the settled server selection", async kind => {
  stubFetch(contextRoutes(kind, section, () => response(comparison)))
  const route = kind === "session" ? "/sessions/7" : "/agent-sessions/codex/conversation-1"
  const { router } = renderApp(route + "?subject=Orders&db=shop&group=fp&a=A&b=B&case=removed&agg=sum&view=whole")
  await screen.findByRole("table", { name: "Orders-shop versions" })
  await waitFor(() => expect(router.state.location.search).toMatchObject({ case: "all", a: "A", b: "B", view: "whole" }))
  expect(await screen.findByRole("button", { name: "All cases · Sum" })).toBeInTheDocument()
  await waitFor(() => expect(screen.queryAllByText("Updating comparison…")).toHaveLength(0))
  expect(screen.getAllByRole("button", { name: /IO: 2 better/ }).length).toBeGreaterThan(0)
})

test.each(["session", "agent-session"])("%s clears a case invalidated by a delayed conditions-pair change", async kind => {
  const otherVersion = { ...versions[1]!, version: "C", hash: "C" }
  const firstGroup = section.subjects[0]!.groups[0]!
  const expanded = { ...section, subjects: [{ ...section.subjects[0]!, groups: [firstGroup, { ...firstGroup, fingerprint: "other", label: "Other conditions", defaultA: "C", defaultB: null, scopes: [{ scope: "whole query", defaultA: "C", defaultB: null, versions: [otherVersion] }] }] }] }
  const normalized = { ...comparison, a: "A", b: "C", groupA: "fp", groupB: "other", basis: "ApproximateAcrossConditions", selectedCase: "all", cases: [{ id: "varying", label: "Matched parameter", outcome: "unavailable" }] }
  let release!: (result: Response) => void
  const delayed = new Promise<Response>(resolve => { release = resolve })
  let requested = false
  stubFetch(contextRoutes(kind, expanded, url => {
    if (url.searchParams.get("groupB") === "other") { requested = true; return url.searchParams.get("case") === "one" ? delayed : response(normalized) }
    return response({ ...comparison, selectedCase: "one" })
  }))
  const route = kind === "session" ? "/sessions/7" : "/agent-sessions/codex/conversation-1"
  const { router } = renderApp(route + "?subject=Orders&db=shop&group=fp&a=A&b=B&case=one&agg=sum")
  const table = await screen.findByRole("table", { name: "Orders-shop versions" })
  await within(table).findByRole("button", { name: /IO: 2 better/ })
  await userEvent.click(within(table).getByRole("radio", { name: "B C" }))
  await waitFor(() => expect(requested).toBe(true))
  expect(within(table).queryByRole("button", { name: /IO: 2 better/ })).not.toBeInTheDocument()
  await act(async () => release(response(normalized)))
  await waitFor(() => expect(router.state.location.search).toMatchObject({ case: "all", a: "A", b: "C", groupB: "other" }))
  await waitFor(() => expect(screen.queryAllByText("Updating comparison…")).toHaveLength(0))
  expect(screen.getByRole("button", { name: "All cases · Sum" })).toBeInTheDocument()
})

test("PostgreSQL buffer evidence is inaccessible during a pending case transition", async () => {
  const pgSection = { ...section, subjects: [{ ...section.subjects[0]!, engine: "PostgreSql" }] }
  const buffers = { sharedHit: 999, sharedRead: 2, sharedDirtied: 0, sharedWritten: 0, tempRead: 0, tempWritten: 0 }
  const pg = { ...comparison, tableIoUnavailable: true, buffersBefore: buffers, buffersAfter: { ...buffers, sharedHit: 555 } }
  let release!: (result: Response) => void
  const delayed = new Promise<Response>(resolve => { release = resolve })
  let requested = false
  stubFetch(contextRoutes("session", pgSection, url => {
    if (url.searchParams.get("case") === "one") { requested = true; return delayed }
    return response(pg)
  }))
  renderApp("/sessions/7")
  await userEvent.click(await screen.findByRole("tab", { name: "IO" }))
  const bufferText = await screen.findByText(/shared hit 999/)
  expect(isInaccessible(bufferText)).toBe(false)
  await userEvent.click(screen.getByRole("button", { name: "All cases · Sum" }))
  await userEvent.click(screen.getByRole("radio", { name: "one" }))
  await waitFor(() => expect(requested).toBe(true))
  expect(isInaccessible(screen.getByText(/shared hit 999/))).toBe(true)
  await act(async () => release(response({ ...pg, selectedCase: "one", buffersBefore: { ...buffers, sharedHit: 1 }, buffersAfter: { ...buffers, sharedHit: 2 } })))
  expect(isInaccessible(await screen.findByText(/shared hit 1, shared read 2/))).toBe(false)
})
