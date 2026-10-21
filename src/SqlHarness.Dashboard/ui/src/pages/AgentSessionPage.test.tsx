import { renderHook, screen, waitFor, within } from "@testing-library/react"
import { QueryClientProvider } from "@tanstack/react-query"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { createQueryClient, useSessionSqlText } from "@/api/queries"
import { operation, session } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

const summary = {
  key: { agentKind: "codex", conversationId: "conversation-1" }, title: "Optimize orders", conversationTitle: "Optimize orders", ownerTitle: null,
  notePreview: "first note line", starred: false, archived: false, exact: 1, inferred: 1, unassigned: 1,
  lastSeen: "2026-10-07T09:05:00.000Z", children: [{ session: session({ id: 7, sessionKey: "mcp:child", hasCompare: true }), exact: 1, inferred: 1, unassigned: 1 }],
}
const settings = { status: "valid", path: "config.json", settings: { journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } }, dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: true } }, compare: { requireContext: true }, diagnostics: { enabled: false }, mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 }, connection: { timeoutSeconds: 30 } } }

test("agent detail shows membership counts, markers, annotations, and child optimization links", async () => {
  const patches: unknown[] = []
  const calls = stubFetch({
    "/api/settings": settings,
    "/api/agent-sessions/codex/conversation-1": { agentSession: summary, note: "full owner note", operations: [
      { operation: operation({ id: 11 }), membership: "exact" },
      { operation: operation({ id: 12 }), membership: "inferred" },
      { operation: operation({ id: 13 }), membership: "unassigned" },
    ] },
    "/api/agent-sessions/codex/conversation-1/operations": { items: [
      { operation: operation({ id: 11 }), membership: "exact" },
      { operation: operation({ id: 12 }), membership: "inferred" },
      { operation: operation({ id: 13 }), membership: "unassigned" },
    ], nextCursor: null },
    "PATCH /api/agent-sessions/codex/conversation-1/annotation": (_url: URL, init?: RequestInit) => {
      patches.push(JSON.parse(String(init?.body)))
      return new Response(JSON.stringify({ title: null, note: "full owner note", starred: true, archived: false }), { status: 200 })
    },
    "/api/agent-sessions/codex/conversation-1/optimization": { subjects: [], unassigned: [], versionLabelsProvisional: true, versionLabelNote: "versions may renumber while matching catches up" },
  })
  renderApp("/agent-sessions/codex/conversation-1")
  expect(await screen.findByRole("heading", { name: "Optimize orders" })).toBeInTheDocument()
  expect(screen.getByText("1 exact · 1 inferred · 1 unassigned")).toBeInTheDocument()
  expect(screen.getByText("exact")).toBeInTheDocument()
  expect(screen.getByText("inferred")).toBeInTheDocument()
  expect(screen.getAllByText("unassigned").length).toBeGreaterThan(0)
  expect(calls).toContain("/api/agent-sessions/codex/conversation-1/optimization")
  expect(await screen.findByRole("heading", { name: "Optimization" })).toBeInTheDocument()
  expect(screen.getByText("versions may renumber while matching catches up")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Optimization history" })).toHaveAttribute("href", "/sessions/7")
  await userEvent.click(screen.getByRole("button", { name: "Edit note" }))
  expect(screen.getByRole("textbox", { name: "Agent session note" })).toHaveValue("full owner note")
  await userEvent.click(screen.getByRole("button", { name: "Star" }))
  expect(patches).toContainEqual({ starred: true })
})

test("agent operation pages load older rows, retain rows on retry, and filter internal rows with membership", async () => {
  let olderPageAttempts = 0
  const calls = stubFetch({
    "/api/settings": settings,
    "/api/agent-sessions/codex/conversation-1": { agentSession: summary, note: null, operations: [] },
    "/api/agent-sessions/codex/conversation-1/optimization": { subjects: [], unassigned: [] },
    "/api/agent-sessions/codex/conversation-1/operations": (url: URL) => {
      const cursor = url.searchParams.get("cursor")
      if (!cursor) return new Response(JSON.stringify({ items: [
        { operation: operation({ id: 503 }), membership: "exact" },
        { operation: operation({ id: 502, operation: "artifact write" }), membership: "inferred" },
        { operation: operation({ id: 501 }), membership: "unassigned" },
      ], nextCursor: 501 }), { status: 200, headers: { "Content-Type": "application/json" } })
      olderPageAttempts += 1
      if (olderPageAttempts === 1) return new Response("temporary failure", { status: 500 })
      return new Response(JSON.stringify({ items: [
        { operation: operation({ id: 2 }), membership: "inferred" },
        { operation: operation({ id: 1 }), membership: "unassigned" },
      ], nextCursor: null }), { status: 200, headers: { "Content-Type": "application/json" } })
    },
  })
  renderApp("/agent-sessions/codex/conversation-1")

  expect(await screen.findByRole("link", { name: "503" })).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "502" })).not.toBeInTheDocument()
  expect(screen.getByText("exact")).toBeInTheDocument()
  expect(screen.getAllByText("unassigned").length).toBeGreaterThan(0)
  expect(screen.getAllByRole("row").slice(1).map(row => within(row).getAllByRole("link")[0]?.textContent)).toEqual(["503", "501"])
  expect(screen.getByText(/Loaded 3 of/)).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Load more" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("checkbox", { name: "Show internal operations" }))
  expect(await screen.findByRole("link", { name: "502" })).toBeInTheDocument()
  expect(screen.getAllByRole("row").slice(1).map(row => within(row).getAllByRole("link")[0]?.textContent)).toEqual(["503", "502", "501"])
  await userEvent.click(screen.getByRole("checkbox", { name: "Show internal operations" }))
  expect(screen.queryByRole("link", { name: "502" })).not.toBeInTheDocument()

  await userEvent.click(screen.getByRole("button", { name: "Load more" }))
  expect(await screen.findByRole("alert")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "503" })).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Retry" }))
  expect(await screen.findByRole("link", { name: "1" })).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "503" })).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument()
  expect(screen.getByText("exact")).toBeInTheDocument()
  expect(screen.getByText("inferred")).toBeInTheDocument()
  expect(screen.getAllByRole("row").slice(1).map(row => within(row).getAllByRole("link")[0]?.textContent)).toEqual(["503", "501", "2", "1"])
  expect(screen.getAllByText("unassigned").length).toBeGreaterThan(0)
  expect(calls.filter(url => url.includes("/operations?")).map(url => new URL(url, "http://dashboard").searchParams.get("cursor"))).toEqual([null, "501", "501"])
})

test("an exact operation after 500 newer unassigned rows remains reachable", async () => {
  const agentSession = { ...summary, exact: 1, inferred: 0, unassigned: 500, children: summary.children.map(child => ({ ...child, exact: 1, inferred: 0, unassigned: 500 })) }
  const firstPage = Array.from({ length: 500 }, (_, index) => ({
    operation: operation({ id: 1000 - index }),
    membership: "unassigned" as const,
  }))
  stubFetch({
    "/api/settings": settings,
    "/api/agent-sessions/codex/conversation-1": { agentSession, note: null, operations: firstPage },
    "/api/agent-sessions/codex/conversation-1/optimization": { subjects: [], unassigned: [] },
    "/api/agent-sessions/codex/conversation-1/operations": (url: URL) => {
      const cursor = url.searchParams.get("cursor")
      const page = cursor === null
        ? { items: firstPage, nextCursor: 501 }
        : { items: [{ operation: operation({ id: 500 }), membership: "exact" }], nextCursor: null }
      return new Response(JSON.stringify(page), { status: 200, headers: { "Content-Type": "application/json" } })
    },
  })
  renderApp("/agent-sessions/codex/conversation-1")

  expect(await screen.findByRole("link", { name: "1000" })).toBeInTheDocument()
  expect(screen.getByText(/Loaded 500 of 501 operations\./)).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Load more" }))

  const olderExactLink = await screen.findByRole("link", { name: /^500$/ })
  expect(within(olderExactLink.closest("tr")!).getByText("exact")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "1000" })).toBeInTheDocument()
  expect(screen.getByText(/Loaded 501 of 501 operations\./)).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument()
}, 60_000)

test("agent session note drafts cancel cleanly and save the current value after a retry", async () => {
  let savedNote = "full owner note"
  const patches: unknown[] = []
  const calls = stubFetch({
    "/api/settings": settings,
    "/api/agent-sessions/codex/conversation-1": { agentSession: summary, get note() { return savedNote }, operations: [] },
    "/api/agent-sessions/codex/conversation-1/operations": { items: [], nextCursor: null },
    "PATCH /api/agent-sessions/codex/conversation-1/annotation": (_url: URL, init?: RequestInit) => {
      const patch = JSON.parse(String(init?.body)) as { note?: string | null }
      patches.push(patch)
      if (patches.length === 1) return new Response("save failed", { status: 500 })
      savedNote = patch.note ?? ""
      return new Response(JSON.stringify({ title: null, note: savedNote, starred: false, archived: false }), { status: 200 })
    },
    "/api/agent-sessions/codex/conversation-1/optimization": { subjects: [], unassigned: [] },
  })
  renderApp("/agent-sessions/codex/conversation-1")

  await screen.findByText("full owner note")
  await userEvent.click(screen.getByRole("button", { name: "Edit note" }))
  const note = screen.getByRole("textbox", { name: "Agent session note" })
  expect(note).toHaveValue("full owner note")
  expect(note).toHaveAttribute("maxlength", "10000")
  expect(note).toHaveAttribute("rows", "5")
  await userEvent.clear(note)
  await userEvent.type(note, "discarded draft")
  await userEvent.click(screen.getByRole("button", { name: "Cancel" }))

  expect(screen.queryByRole("textbox", { name: "Agent session note" })).not.toBeInTheDocument()
  expect(screen.getByText("full owner note")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Edit note" }))
  const reopenedNote = screen.getByRole("textbox", { name: "Agent session note" })
  expect(reopenedNote).toHaveValue("full owner note")
  await userEvent.clear(reopenedNote)
  await userEvent.type(reopenedNote, "saved owner note")
  await userEvent.click(screen.getByRole("button", { name: "Save note" }))

  expect(await screen.findByRole("alert")).toBeInTheDocument()
  expect(screen.getByRole("textbox", { name: "Agent session note" })).toHaveValue("saved owner note")
  expect(patches).toEqual([{ note: "saved owner note" }])
  await userEvent.click(screen.getByRole("button", { name: "Save note" }))

  expect(await screen.findByText("saved owner note")).toBeInTheDocument()
  expect(screen.queryByRole("textbox", { name: "Agent session note" })).not.toBeInTheDocument()
  expect(patches).toEqual([{ note: "saved owner note" }, { note: "saved owner note" }])
  expect(calls).toContain("/api/agent-sessions/codex/conversation-1/annotation")
})

test("subagent fallback label is visible", async () => {
  stubFetch({
    "/api/settings": settings,
    "/api/agent-sessions/codex/subagent": { agentSession: { ...summary, key: { agentKind: "codex", conversationId: "subagent" }, title: "subagent of unknown parent", children: [] }, operations: [] },
    "/api/agent-sessions/codex/subagent/operations": { items: [], nextCursor: null },
    "/api/agent-sessions/codex/subagent/optimization": { subjects: [], unassigned: [] },
  })
  renderApp("/agent-sessions/codex/subagent")
  expect(await screen.findByRole("heading", { name: "subagent of unknown parent" })).toBeInTheDocument()
})

test("matching off redirects away from cached agent scope", async () => {
  const calls = stubFetch({
    "/api/settings": { ...settings, settings: { ...settings.settings, dashboard: { ...settings.settings.dashboard, agentTranscripts: { enabled: false } } } },
    "/api/agent-sessions/codex/subagent": { agentSession: { ...summary, key: { agentKind: "codex", conversationId: "subagent" }, title: "subagent of unknown parent", children: [] }, operations: [] },
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [], nextCursor: null },
  })
  const { router } = renderApp("/agent-sessions/codex/subagent")
  await waitFor(() => expect(router.state.location.pathname).toBe("/sessions"))
  expect(calls.some(url => url.startsWith("/api/agent-sessions/codex/subagent"))).toBe(false)
  expect(calls.some(url => url.includes("/operations"))).toBe(false)
})

test("agent comparison SQL versions use the agent-session route", async () => {
  const calls = stubFetch({ "/api/agent-sessions/codex/conversation-1/sql": { hash: "hash-1", text: "SELECT 1", tooLarge: false } })
  const client = createQueryClient()
  const { result } = renderHook(() => useSessionSqlText(7, "hash-1", { agentKind: "codex", conversationId: "conversation-1" }), {
    wrapper: ({ children }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
  })
  await waitFor(() => expect(result.current.isSuccess).toBe(true))
  expect(result.current.data?.text).toBe("SELECT 1")
  expect(calls).toContain("/api/agent-sessions/codex/conversation-1/sql?hash=hash-1")
  expect(calls.some(url => url.startsWith("/api/sessions/7/sql"))).toBe(false)
})

test("aggregate measure from a second MCP child keeps the agent-session return route", async () => {
  const calls = stubFetch({
    "/api/settings": settings,
    "/api/operations/21/repro": { text: null, completeness: "Unavailable", label: "Script not available — enable storeSensitive" },
    "/api/operations/22/repro": { text: null, completeness: "Unavailable", label: "Script not available — enable storeSensitive" },
    "/api/agent-sessions/claude/conversation-1": { agentSession: { ...summary, children: [
      { session: session({ id: 7, sessionKey: "mcp:first-child", hasCompare: true }), exact: 1, inferred: 0, unassigned: 0 },
      { session: session({ id: 8, sessionKey: "mcp:second-child", hasCompare: true }), exact: 1, inferred: 0, unassigned: 0 },
    ] }, operations: [] },
    "/api/agent-sessions/claude/conversation-1/operations": { items: [], nextCursor: null },
    "/api/agent-sessions/claude/conversation-1/optimization": { subjects: [{
      subject: "orders", database: "db", engine: "sqlserver", groups: [{ fingerprint: "fp", label: "default", subject: "orders", database: "db", defaultA: "v0", defaultB: "v1", scopes: [{
        scope: "whole", defaultA: "v0", defaultB: "v1", versions: [
          { version: "v0", hash: "sha256:0000000000000000", logicalReadsSum: 10, logicalReadsMean: 10, scansSum: 1, elapsedMedianMs: 10, caseOutcome: "same", measuredIn: [21], latestOperationId: 21, verdictBasis: "whole batch" },
          { version: "v1", hash: "sha256:1111111111111111", logicalReadsSum: 5, logicalReadsMean: 5, scansSum: 1, elapsedMedianMs: 8, caseOutcome: "same", measuredIn: [22], latestOperationId: 22, verdictBasis: "whole batch" },
        ],
      }] }],
    }], unassigned: [] },
    "/api/agent-sessions/claude/conversation-1/optimization/compare": {
      a: "v0", b: "v1", basis: "DifferentRunsSameConditions", operationId: null, equivalenceLabel: "equivalent", cases: [{ id: "0", label: "Case 1", outcome: "same" }],
      selectedCase: "all", aggregate: "sum", coverageLabel: "1 of 1 cases", mixed: null, tables: [], collapsed: null,
      elapsed: { beforeMs: 10, afterMs: 8, policyVerdict: null, differentRun: true },
      beforeAction: { operationId: 21, side: "baseline", caseIds: ["0"] }, afterAction: { operationId: 22, side: "measure", caseIds: ["0"] },
    },
  })
  renderApp("/agent-sessions/claude/conversation-1")
  const measure = await screen.findByRole("link", { name: "Measure for v1" })
  const target = new URL(measure.getAttribute("href")!, "http://dashboard")
  expect(target.pathname).toBe("/operations/22")
  expect(target.searchParams.get("returnSession")).toBe("7")
  expect(target.searchParams.get("returnAgentKind")).toBe("claude")
  expect(target.searchParams.get("returnAgentId")).toBe("conversation-1")
  expect(calls.some(url => url.startsWith("/api/sessions/7/optimization/compare"))).toBe(false)
})
