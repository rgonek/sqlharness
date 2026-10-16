import { screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import type { CompareAttemptSummary, CompareSubjectSummary } from "@/api/types"
import { operation, operationDetail, session } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

vi.mock("@/components/SessionVersionDiff", () => ({
  SessionVersionDiff: ({ options }: { options: { version: string; hash: string; label?: string }[] }) => <div>
    <select aria-label="From version">{options.map(option => <option key={option.hash}>{option.label ?? option.version}</option>)}</select>
    <select aria-label="To version">{options.map(option => <option key={option.hash}>{option.label ?? option.version}</option>)}</select>
  </div>,
}))

const attempt = (over: Partial<CompareAttemptSummary> = {}): CompareAttemptSummary => ({
  operationId: 70, subject: "orders", scope: "whole", baselineHash: "a", candidateHash: "b",
  baselineVersion: "v0", candidateVersion: "v1", status: "succeeded", startedAt: "2026-10-12T10:00:00Z",
  regression: { verdict: "pass" }, cases: null, conditions: null, summary: null, ...over,
})

const subject = (over: Partial<CompareSubjectSummary> = {}): CompareSubjectSummary => ({
  subject: "orders", scopes: ["whole", "history"], latestWhole: attempt(), latestCompletedWhole: attempt(),
  startToLatestWhole: attempt({ baselineVersion: "v0", candidateVersion: "v2", startedAt: "2026-10-12T12:00:00Z" }),
  scopeVersions: [],
  versionsFromRetainedHistory: true, ...over,
})

function stubSession(subjects: CompareSubjectSummary[], items: CompareAttemptSummary[], nextCursor: number | null = null) {
  stubFetch({
    "/api/sessions/7": { session: { id: 7, sessionKey: "mcp:test", transport: "mcp", source: "process-tree", agentKind: "codex", clientName: null, clientVersion: null, mcpMode: "fixed", cwd: null, firstSeen: "2026-10-12T10:00:00Z", lastSeen: "2026-10-12T12:00:00Z", operations: 2, failed: 1, rejected: 0, running: 0, abandoned: 0, clientTitle: null, roots: null, protocolVersion: null }, operations: [] , compareSubjects: subjects, unassignedCompareCount: 0 },
    "/api/sessions/7/compares": { items, nextCursor },
  })
}

test("groups subjects, scopes and chronological attempts while linking back to the same group", async () => {
  stubSession([subject(), subject({ subject: "invoices", scopes: ["whole"], latestWhole: null, latestCompletedWhole: null, startToLatestWhole: null })], [
    attempt({ operationId: 72, scope: "history", startedAt: "2026-10-12T12:00:00Z" }),
    attempt({ operationId: 71, scope: "whole", startedAt: "2026-10-12T11:00:00Z" }),
    attempt({ operationId: 70, scope: "whole", startedAt: "2026-10-12T10:00:00Z" }),
    attempt({ operationId: 73, subject: "invoices", scope: "whole", startedAt: "2026-10-12T09:00:00Z" }),
  ])

  renderApp("/sessions/7")

  const group = (await screen.findByText("orders", { selector: "summary" })).closest("details")!
  await userEvent.click(within(group).getByText("orders", { selector: "summary" }))
  expect(screen.getByText("invoices", { selector: "summary" })).toBeInTheDocument()
  expect(within(group).getByText(/Direct whole compare from retained-history v0: v0 → v2/)).toBeInTheDocument()
  await userEvent.click(within(group).getByText("whole", { selector: "summary" }))
  const wholeAttempts = within(group).getByRole("list", { name: "orders whole compare attempts" })
  expect(within(wholeAttempts).getAllByRole("link").map(link => link.textContent)).toEqual(["Compare #70", "Compare #71"])
  await userEvent.click(within(group).getByText("history", { selector: "summary" }))
  const link = await screen.findByRole("link", { name: "Compare #72" })
  const target = new URL(link.getAttribute("href")!, "http://dashboard")
  expect(target.pathname).toBe("/operations/72")
  expect(target.searchParams.get("returnSession")).toBe("7")
  expect(target.searchParams.get("returnSubject")).toBe("orders")
  expect(target.searchParams.get("returnScope")).toBe("history")
  expect(within(group).getByText("Versions use retained history; labels can change after retention.")).toBeInTheDocument()
  expect(within(group).getByText("SQL hashes define versions; no winning version is selected.")).toBeInTheDocument()
})

test("offers a session SQL diff only when a scope has at least two distinct versions", async () => {
  const oneVersion = attempt({ operationId: 90, scope: "single", baselineVersion: "v4", candidateVersion: "v4", baselineHash: "same", candidateHash: "same" })
  stubSession([subject({ scopes: ["whole", "single"], scopeVersions: [
    { scope: "whole", versions: [{ version: "v0", hash: "hash0" }, { version: "v1", hash: "hash1" }, { version: "v3", hash: "hash3" }] },
    { scope: "single", versions: [{ version: "v4", hash: "same" }] },
  ] })], [
    attempt({ operationId: 91, scope: "whole", baselineVersion: "v0", candidateVersion: "v1", baselineHash: "hash0", candidateHash: "hash1" }),
    attempt({ operationId: 92, scope: "whole", baselineVersion: "v1", candidateVersion: "v3", baselineHash: "hash1", candidateHash: "hash3" }),
    oneVersion,
  ])
  renderApp("/sessions/7")

  const group = (await screen.findByText("orders", { selector: "summary" })).closest("details")!
  await userEvent.click(within(group).getByText("orders", { selector: "summary" }))
  const whole = within(group).getByText("whole", { selector: "summary" }).closest("details")!
  await userEvent.click(within(whole).getByText("whole", { selector: "summary" }))
  expect(within(whole).getByRole("button", { name: "Diff versions" })).toBeInTheDocument()

  const single = within(group).getByText("single", { selector: "summary" }).closest("details")!
  await userEvent.click(within(single).getByText("single", { selector: "summary" }))
  expect(within(single).queryByRole("button", { name: "Diff versions" })).not.toBeInTheDocument()
})

test("uses complete per-scope version inventory before compare-attempt pagination is loaded", async () => {
  const loadedHistory = Array.from({ length: 100 }, (_, index) => attempt({
    operationId: 100 - index, scope: "history", baselineVersion: "v0", candidateVersion: "v1",
    baselineHash: "sha256:aaaa", candidateHash: "sha256:bbbb",
  }))
  stubFetch({
    "/api/sessions/7": {
      session: session({ id: 7, sessionKey: "mcp:test" }), operations: [],
      compareSubjects: [subject({ scopes: ["whole", "history"], scopeVersions: [
        { scope: "whole", versions: [
          { version: "v0", hash: "sha256:00000000" }, { version: "v1", hash: "sha256:11111111" },
          { version: "v2", hash: "sha256:22222222" }, { version: "v3", hash: "sha256:33333333" },
        ] },
        { scope: "history", versions: [{ version: "v0", hash: "sha256:aaaa" }, { version: "v1", hash: "sha256:bbbb" }] },
      ] })], unassignedCompareCount: 0,
    },
    "/api/sessions/7/compares": { items: loadedHistory, nextCursor: 1 },
    "/api/sessions/7/sql": (url: URL) => {
      const hash = url.searchParams.get("hash")
      return new Response(JSON.stringify({ hash, text: `SELECT '${hash}'`, tooLarge: false }), { status: 200 })
    },
  })
  renderApp("/sessions/7")

  const subjectGroup = (await screen.findByText("orders", { selector: "summary" })).closest("details")!
  await userEvent.click(within(subjectGroup).getByText("orders", { selector: "summary" }))
  const whole = within(subjectGroup).getByText("whole", { selector: "summary" }).closest("details")!
  await userEvent.click(within(whole).getByText("whole", { selector: "summary" }))
  await userEvent.click(within(whole).getByRole("button", { name: "Diff versions" }))

  const from = within(whole).getByRole("combobox", { name: "From version" })
  const to = within(whole).getByRole("combobox", { name: "To version" })
  expect(from.querySelectorAll("option")).toHaveLength(4)
  expect(Array.from((from as HTMLSelectElement).options, option => option.textContent)).toEqual(["v0", "v1", "v2", "v3"])
  expect(Array.from((to as HTMLSelectElement).options, option => option.textContent)).toEqual(["v0", "v1", "v2", "v3"])
  expect(screen.getByRole("button", { name: "Load more compare attempts" })).toBeInTheDocument()
  expect(within(whole).getByText("No attempts loaded for this scope yet.")).toBeInTheDocument()
})

test("a failed latest completed whole compare never borrows an earlier pass", async () => {
  const failed = attempt({ operationId: 71, status: "failed", regression: null, startedAt: "2026-10-12T11:00:00Z" })
  stubSession([subject({ latestWhole: attempt({ status: "running" }), latestCompletedWhole: failed, startToLatestWhole: null })], [
    failed,
    attempt({ operationId: 70, regression: { verdict: "pass" }, startedAt: "2026-10-12T10:00:00Z" }),
  ])
  renderApp("/sessions/7")

  const group = (await screen.findByText("orders", { selector: "summary" })).closest("details")!
  await userEvent.click(within(group).getByText("orders", { selector: "summary" }))
  expect(within(group).getByText(/Latest completed whole compare: failed;/)).toBeInTheDocument()
  expect(within(group).getByText(/No assessment/)).toBeInTheDocument()
  expect(within(group).queryByText(/No regression detected/)).not.toBeInTheDocument()
})

test("a subject with fragments only says there is no final whole compare and legacy attempts stay separate", async () => {
  stubSession([subject({ scopes: ["history"], latestWhole: null, latestCompletedWhole: null, startToLatestWhole: null })], [
    attempt({ operationId: 80, scope: null, subject: null, baselineVersion: null, candidateVersion: null }),
  ])
  // The page aggregate, rather than the page contents, owns the legacy group count.
  const calls = stubFetch({
    "/api/sessions/7": { session: session({ id: 7, sessionKey: "mcp:test" }), operations: [], compareSubjects: [subject({ scopes: ["history"], latestWhole: null, latestCompletedWhole: null, startToLatestWhole: null })], unassignedCompareCount: 3 },
    "/api/sessions/7/compares": { items: [attempt({ operationId: 80, scope: null, subject: null, baselineVersion: null, candidateVersion: null })], nextCursor: null },
    "/api/operations/80": operationDetail({ operation: operation({ id: 80, sessionId: 7, operation: "compare" }), session: session({ id: 7, sessionKey: "mcp:test" }) }),
  })
  renderApp("/sessions/7")

  await userEvent.click(await screen.findByText("orders", { selector: "summary" }))
  expect(await screen.findByText("No final whole compare")).toBeInTheDocument()
  const unassignedSummary = screen.getByText("Unassigned context (3)", { selector: "summary" })
  await userEvent.click(unassignedSummary)
  const unassignedGroup = unassignedSummary.closest("details")!
  expect(unassignedGroup).toHaveAttribute("open")
  const compareLink = await within(unassignedGroup).findByRole("link", { name: "Compare #80" })
  const operationUrl = new URL(compareLink.getAttribute("href")!, "http://dashboard")
  expect(operationUrl.searchParams.get("returnUnassigned")).toBe("true")
  expect(operationUrl.searchParams.has("returnSubject")).toBe(false)
  expect(operationUrl.searchParams.has("returnScope")).toBe(false)
  await userEvent.click(compareLink)
  await userEvent.click(await screen.findByRole("link", { name: "mcp:test" }))
  const returnedSummary = await screen.findByText("Unassigned context (3)", { selector: "summary" })
  expect(returnedSummary.closest("details")).toHaveAttribute("open")
  expect(calls.some(call => call.startsWith("/api/sessions/7/compares"))).toBe(true)
})

test("shows running and abandoned attempts without deriving an assessment for abandoned work", async () => {
  const running = attempt({ operationId: 74, status: "running", regression: { verdict: "pass" } })
  const abandoned = attempt({ operationId: 73, status: "abandoned", regression: null })
  stubSession([subject({ latestWhole: running, latestCompletedWhole: abandoned, startToLatestWhole: abandoned })], [running, abandoned])
  renderApp("/sessions/7")

  const group = (await screen.findByText("orders", { selector: "summary" })).closest("details")!
  await userEvent.click(within(group).getByText("orders", { selector: "summary" }))
  expect(within(group).getByText(/Latest completed whole compare: abandoned/)).toBeInTheDocument()
  expect(within(group).getByText(/Direct whole compare from retained-history v0: v0 → v1; No assessment/)).toBeInTheDocument()
  await userEvent.click(within(group).getByText("whole", { selector: "summary" }))
  const history = within(group).getByRole("list", { name: "orders whole compare attempts" })
  expect(within(history).getByText("running")).toBeInTheDocument()
  expect(within(history).getByText("abandoned")).toBeInTheDocument()
})

test("retries a failed page request and then shows the empty state", async () => {
  let fail = true
  stubFetch({
    "/api/sessions/7": { session: { id: 7, sessionKey: "mcp:test", transport: "mcp", source: "process-tree", agentKind: "codex", clientName: null, clientVersion: null, mcpMode: "fixed", cwd: null, firstSeen: "2026-10-12T10:00:00Z", lastSeen: "2026-10-12T12:00:00Z", operations: 2, failed: 1, rejected: 0, running: 0, abandoned: 0, clientTitle: null, roots: null, protocolVersion: null }, operations: [], compareSubjects: [], unassignedCompareCount: 0 },
    "/api/sessions/7/compares": () => fail
      ? new Response(JSON.stringify({ error: "temporarily unavailable" }), { status: 503 })
      : new Response(JSON.stringify({ items: [], nextCursor: null }), { status: 200 }),
  })
  renderApp("/sessions/7")

  expect(await screen.findByRole("alert")).toHaveTextContent("Could not load compare attempts")
  fail = false
  await userEvent.click(screen.getByRole("button", { name: "Retry" }))
  expect(await screen.findByText("No compare attempts in this session.")).toBeInTheDocument()
})

test("shows loading state while the session aggregate remains visible", async () => {
  let resolveCompare: ((response: Response) => void) | undefined
  const sessionData = { session: { id: 7, sessionKey: "mcp:test", transport: "mcp", source: "process-tree", agentKind: "codex", clientName: null, clientVersion: null, mcpMode: "fixed", cwd: null, firstSeen: "2026-10-12T10:00:00Z", lastSeen: "2026-10-12T12:00:00Z", operations: 2, failed: 1, rejected: 0, running: 0, abandoned: 0, clientTitle: null, roots: null, protocolVersion: null }, operations: [], compareSubjects: [subject()], unassignedCompareCount: 0 }
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://127.0.0.1")
    if (url.pathname === "/api/sessions/7") return new Response(JSON.stringify(sessionData), { status: 200 })
    return await new Promise<Response>(resolve => { resolveCompare = resolve })
  }) as typeof fetch
  renderApp("/sessions/7")

  expect(await screen.findByRole("status")).toHaveTextContent("Loading compare attempts")
  expect(screen.getByText("orders", { selector: "summary" })).toBeInTheDocument()
  resolveCompare?.(new Response(JSON.stringify({ items: [], nextCursor: null }), { status: 200 }))
  expect(await screen.findAllByText("No attempts loaded for this scope yet.")).toHaveLength(2)
})

test("loads more than 500 retained attempts without losing session context", async () => {
  let request = 0
  stubFetch({
    "/api/sessions/7": { session: { id: 7, sessionKey: "mcp:test", transport: "mcp", source: "process-tree", agentKind: "codex", clientName: null, clientVersion: null, mcpMode: "fixed", cwd: null, firstSeen: "2026-10-12T10:00:00Z", lastSeen: "2026-10-12T12:00:00Z", operations: 2, failed: 1, rejected: 0, running: 0, abandoned: 0, clientTitle: null, roots: null, protocolVersion: null }, operations: [], compareSubjects: [subject()], unassignedCompareCount: 0 },
    "/api/sessions/7/compares": () => {
      const page = request++
      const length = page === 5 ? 1 : 100
      const items = Array.from({ length }, (_, index) => attempt({ operationId: 1000 - page * 100 - index, startedAt: new Date(Date.UTC(2026, 9, 1, 0, page * 100 + index)).toISOString() }))
      return new Response(JSON.stringify({ items, nextCursor: page < 5 ? page + 1 : null }), { status: 200 })
    },
  })
  renderApp("/sessions/7")

  expect(await screen.findByText("orders", { selector: "summary" })).toBeInTheDocument()
  for (let page = 0; page < 5; page++) {
    await userEvent.click(await screen.findByRole("button", { name: "Load more compare attempts" }))
  }
  expect(await screen.findByRole("link", { name: "Compare #500" })).toBeInTheDocument()
  expect(screen.getAllByRole("link", { name: /^Compare #/ })).toHaveLength(501)
  expect(screen.getByRole("heading", { level: 1, name: "mcp:test" })).toBeInTheDocument()
  expect(request).toBe(6)
})

test("the whole compare assessment names the regression policy version", async () => {
  const versioned = attempt({ regression: { verdict: "pass", policyVersion: 2 } })
  stubSession([subject({ latestWhole: versioned, latestCompletedWhole: versioned, startToLatestWhole: null })], [versioned])
  renderApp("/sessions/7")

  const group = (await screen.findByText("orders", { selector: "summary" })).closest("details")!
  expect(within(group).getByText(/No regression detected \(policy v2\)/)).toBeInTheDocument()
})
