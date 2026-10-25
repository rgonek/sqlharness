import { screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { session } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

test("lists sessions, filters by agent and loads more", async () => {
  const calls = stubFetch({
    "/api/agents": [{ key: "codex", count: 1 }],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 2, sessionKey: "cli:two" })], nextCursor: 2 },
    "/api/sessions?archived=false&starred=false&cursor=2&limit=50": { items: [session({ id: 1, sessionKey: "cli:one" })], nextCursor: null },
    "/api/sessions?agent=codex&archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?agent=codex&archived=false&starred=false&limit=50": { items: [session({ id: 3, sessionKey: "mcp:codex", agentKind: "codex" })], nextCursor: null },
  })

  renderApp("/sessions")
  expect(await screen.findByText("cli:two")).toBeInTheDocument()

  await userEvent.click(screen.getByRole("button", { name: "Load more" }))
  expect(await screen.findByText("cli:one")).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument()

  await userEvent.click(screen.getByRole("tab", { name: "Codex" }))
  expect(await screen.findByText("mcp:codex")).toBeInTheDocument()
  expect(calls).toContain("/api/sessions?agent=codex&archived=false&starred=false&limit=50")
})

test("agent tabs follow the kinds present in the journal", async () => {
  stubFetch({
    "/api/agents": [{ key: "copilot", count: 1 }, { key: "claude", count: 2 }],
    "/api/sessions": { items: [], nextCursor: null },
  })
  renderApp("/sessions")
  expect(await screen.findByRole("tab", { name: "Claude Code" })).toBeInTheDocument()
  expect(screen.getByRole("tab", { name: "Copilot" })).toBeInTheDocument()
  expect(screen.queryByRole("tab", { name: "Codex" })).not.toBeInTheDocument()
  expect(screen.queryByRole("tab", { name: "Unknown" })).not.toBeInTheDocument()
})

test("starred sessions are pinned above the others and show titles", async () => {
  stubFetch({
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [session({ id: 5, sessionKey: "cli:pin", title: "BSI-1 orders", starred: true })], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 6, sessionKey: "cli:plain" })], nextCursor: null },
  })
  renderApp("/sessions")
  const pinned = await screen.findByText("BSI-1 orders")
  const plain = await screen.findByText("cli:plain")
  expect(pinned.compareDocumentPosition(plain) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(screen.getByText("cli:pin")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Unstar session cli:pin" })).toBeInTheDocument()
})

test("shows concise working directories and optimization badges without clipping state", async () => {
  stubFetch({
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({
      cwd: "C:\\work\\sqlharness", hasCompare: true,
      optimization: [{ subject: "orders-by-customer", change: "−94%", warning: true, verdictBasis: "changed statements" }],
      running: 2, failed: 1, rejected: 1, abandoned: 1,
    })], nextCursor: null },
  })
  renderApp("/sessions")
  expect(await screen.findByText("sqlharness")).toHaveAttribute("title", "C:\\work\\sqlharness")
  expect(screen.getByText("orders-by-customer −94%")).toBeInTheDocument()
  expect(screen.getByText("orders-by-customer −94%")).toHaveAttribute("title", "Verdict basis: changed statements")
  expect(screen.getByLabelText("Warning")).toBeInTheDocument()
  const states = screen.getByText("2 running").parentElement!
  expect(states).toHaveTextContent("1 failed")
  expect(states).toHaveTextContent("1 rejected")
  expect(states).toHaveTextContent("1 abandoned")
  expect(states).not.toHaveClass("truncate")
})

test("archived view and search change the query", async () => {
  const calls = stubFetch({
    "/api/agents": [],
    "/api/sessions": { items: [], nextCursor: null },
  })
  renderApp("/sessions")
  await userEvent.click(await screen.findByRole("tab", { name: "Archived" }))
  expect(calls).toContain("/api/sessions?archived=true&starred=false&limit=50")
  await userEvent.type(screen.getByRole("searchbox", { name: "Search sessions" }), "BSI_1")
  await waitFor(() => expect(calls).toContain("/api/sessions?archived=true&starred=false&q=BSI_1&limit=50"))
})

test("contains optimization filters both starred and unstarred session queries", async () => {
  const calls = stubFetch({
    "/api/agents": [],
    "/api/sessions": { items: [], nextCursor: null },
  })
  renderApp("/sessions")
  await userEvent.click(await screen.findByText("Contains optimization"))
  expect(screen.getByRole("checkbox", { name: "Contains optimization" })).toBeChecked()
  await waitFor(() => {
    expect(calls).toContain("/api/sessions?archived=false&starred=true&hasCompare=true&limit=50")
    expect(calls).toContain("/api/sessions?archived=false&starred=false&hasCompare=true&limit=50")
  })
})

test("star and archive buttons patch the annotation and refetch", async () => {
  const patches: { url: string; body: unknown }[] = []
  stubFetch({
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 7, sessionKey: "cli:x" })], nextCursor: null },
    "PATCH /api/sessions/7/annotation": (url: URL, init?: RequestInit) => {
      patches.push({ url: url.pathname, body: JSON.parse(String(init?.body)) })
      expect(init?.method).toBe("PATCH")
      expect(new Headers(init?.headers).get("X-SqlHarness-Dashboard")).toBe("1")
      return new Response(JSON.stringify({ title: null, note: null, starred: true, archived: false }), { status: 200 })
    },
  })
  renderApp("/sessions")
  await userEvent.click(await screen.findByRole("button", { name: "Star session cli:x" }))
  // One mutation per row: its buttons are disabled while a save is pending.
  await waitFor(() => expect(patches).toHaveLength(1))
  await waitFor(() => expect(screen.getByRole("button", { name: "Archive session cli:x" })).toBeEnabled())
  await userEvent.click(screen.getByRole("button", { name: "Archive session cli:x" }))
  await waitFor(() => expect(patches.map(p => p.body)).toEqual([{ starred: true }, { archived: true }]))
})

test("a note shows an icon with its first line", async () => {
  stubFetch({
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 8, sessionKey: "cli:n", notePreview: "reads down 40%" })], nextCursor: null },
  })
  renderApp("/sessions")
  expect(await screen.findByLabelText("Note: reads down 40%")).toBeInTheDocument()
})

test("uses an agent conversation title as the session link", async () => {
  stubFetch({
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ agentTitle: "Optimize orders" })], nextCursor: null },
  })
  renderApp("/sessions")
  expect(await screen.findByRole("link", { name: "Optimize orders" })).toBeInTheDocument()
})

test("a failed save shows the server message and re-enables the button", async () => {
  stubFetch({
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 9, sessionKey: "cli:busy" })], nextCursor: null },
    "PATCH /api/sessions/9/annotation": () => new Response(JSON.stringify({ error: "The journal is busy; try again." }), { status: 503 }),
  })
  renderApp("/sessions")
  await userEvent.click(await screen.findByRole("button", { name: "Star session cli:busy" }))
  expect(await screen.findByRole("alert")).toHaveTextContent("The journal is busy; try again.")
  expect(screen.getByRole("button", { name: "Star session cli:busy" })).toBeEnabled()
})

test("matching on lists expandable agent rows and leaves unmatched sessions at the top level", async () => {
  stubFetch({
    "/api/settings": { status: "valid", settings: { dashboard: { agentTranscripts: { enabled: true } } } },
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null, matching: true },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 9, sessionKey: "cli:unmatched" })], nextCursor: null, matching: true },
    "/api/agent-sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/agent-sessions?archived=false&starred=false&limit=50": { items: [{
      key: { agentKind: "claude", conversationId: "conversation-1" }, title: "Optimize orders", conversationTitle: "Optimize orders",
      ownerTitle: null, notePreview: null, starred: false, archived: false, exact: 2, inferred: 1, unassigned: 0,
      lastSeen: "2026-10-07T09:05:00.000Z", children: [{ session: session({ id: 8, sessionKey: "mcp:child", transport: "mcp" }), exact: 2, inferred: 1, unassigned: 0 }],
    }], nextCursor: null },
    "/api/agent-sessions/unmatched?archived=false&starred=true&limit=50": { items: [], nextCursor: null, matching: true },
    "/api/agent-sessions/unmatched?archived=false&starred=false&limit=50": { items: [session({ id: 9, sessionKey: "cli:unmatched" })], nextCursor: null, matching: true },
  })
  renderApp("/sessions")
  expect(await screen.findByRole("link", { name: "Optimize orders" })).toBeInTheDocument()
  expect(screen.getByText("not matched")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: /Expand.*Optimize orders/i }))
  expect(await screen.findByRole("link", { name: "mcp:child" })).toBeInTheDocument()
})

test("agent search preserves matches on child session titles", async () => {
  stubFetch({
    "/api/settings": { status: "valid", settings: { dashboard: { agentTranscripts: { enabled: true } } } },
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null, matching: true },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [], nextCursor: null, matching: true },
    "/api/agent-sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/agent-sessions?archived=false&starred=false&limit=50": { items: [{
      key: { agentKind: "claude", conversationId: "child-title-search" }, title: "Conversation", conversationTitle: null,
      ownerTitle: null, notePreview: null, starred: false, archived: false, exact: 1, inferred: 0, unassigned: 0,
      lastSeen: "2026-10-07T09:05:00.000Z", children: [{ session: session({ id: 21, sessionKey: "mcp:child-title-search", title: "Distinct child title" }), exact: 1, inferred: 0, unassigned: 0 }],
    }], nextCursor: null },
    "/api/agent-sessions/unmatched?archived=false&starred=true&limit=50": { items: [], nextCursor: null, matching: true },
    "/api/agent-sessions/unmatched?archived=false&starred=false&limit=50": { items: [], nextCursor: null, matching: true },
  })
  renderApp("/sessions")
  await userEvent.type(await screen.findByRole("searchbox", { name: "Search sessions" }), "Distinct child title")
  expect(await screen.findByRole("link", { name: "Conversation" })).toBeInTheDocument()
})

test("does not label a matched child not matched when its agent row is on a later page", async () => {
  stubFetch({
    "/api/settings": { status: "valid", settings: { dashboard: { agentTranscripts: { enabled: true } } } },
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null, matching: true },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 22, sessionKey: "mcp:later-page-child" })], nextCursor: 22, matching: true },
    "/api/agent-sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/agent-sessions?archived=false&starred=false&limit=50": { items: [{
      key: { agentKind: "claude", conversationId: "first-page" }, title: "First conversation", conversationTitle: null,
      ownerTitle: null, notePreview: null, starred: false, archived: false, exact: 1, inferred: 0, unassigned: 0,
      lastSeen: "2026-10-07T09:05:00.000Z", children: [],
    }], nextCursor: 1 },
    "/api/agent-sessions/unmatched?archived=false&starred=true&limit=50": { items: [], nextCursor: null, matching: true },
    "/api/agent-sessions/unmatched?archived=false&starred=false&limit=50": { items: [], nextCursor: null, matching: true },
  })
  renderApp("/sessions")
  expect(await screen.findByRole("link", { name: "First conversation" })).toBeInTheDocument()
  expect(screen.queryByText("not matched")).not.toBeInTheDocument()
})

test("matching off returns immediately to per-session rows even when agent rows are cached", async () => {
  let matching = true
  let enabled = true
  const calls = stubFetch({
    "/api/operations": { items: [], nextCursor: null },
    "/api/agents": [],
    "/api/sessions": (url: URL) => new Response(JSON.stringify({ items: url.searchParams.get("starred") === "true" ? [] : [session({ id: 2, sessionKey: "mcp:plain" })], nextCursor: null, matching }), { status: 200 }),
    "/api/agent-sessions": (url: URL) => new Response(JSON.stringify({ items: url.searchParams.get("starred") === "true" ? [] : [{
      key: { agentKind: "codex", conversationId: "cached" }, title: "stale agent row", conversationTitle: null, ownerTitle: null, notePreview: null,
      starred: false, archived: false, exact: 0, inferred: 0, unassigned: 0, lastSeen: "2026-10-07T09:05:00.000Z", children: [],
    }], nextCursor: null }), { status: 200 }),
    "/api/agent-sessions/unmatched": { items: [], nextCursor: null, matching: true },
    "/api/prices": { source: "embedded", fetchedAt: "2026-10-07T09:00:00Z", origin: "embedded" },
    "/api/transcripts": { enabled: false, matched: 0, unmatched: 0, readErrors: 0, lastCycleAt: null },
    "/api/settings": () => {
      return new Response(JSON.stringify({ status: "valid", path: "config.json", settings: {
        journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
        dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled } },
        compare: { requireContext: true }, diagnostics: { enabled: false }, mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 }, connection: { timeoutSeconds: 30 }, execution: { defaultTimeoutSeconds: 30 },
      } }), { status: 200 })
    },
    "PUT /api/settings": () => {
      enabled = false
      matching = false
      return new Response(JSON.stringify({ status: "valid", path: "config.json", settings: {
        journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
        dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled } },
        compare: { requireContext: true }, diagnostics: { enabled: false }, mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 }, connection: { timeoutSeconds: 30 }, execution: { defaultTimeoutSeconds: 30 },
      } }), { status: 200 })
    },
  })
  renderApp("/sessions")
  expect(await screen.findByText("stale agent row")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("link", { name: "Settings" }))
  await userEvent.click(await screen.findByRole("switch", { name: "Read agent transcripts" }))
  await waitFor(() => expect(enabled).toBe(false))
  await userEvent.click(screen.getByRole("link", { name: "Activity" }))
  await userEvent.click(await screen.findByRole("link", { name: "Browse all sessions" }))
  expect((await screen.findAllByText("mcp:plain")).length).toBe(1)
  expect(screen.queryByText("stale agent row")).not.toBeInTheDocument()
  expect(calls).toContain("/api/sessions?archived=false&starred=false&limit=50")
})
