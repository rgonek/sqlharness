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
      optimization: [{ subject: "orders-by-customer", change: "−94%", warning: true }],
      running: 2, failed: 1, rejected: 1, abandoned: 1,
    })], nextCursor: null },
  })
  renderApp("/sessions")
  expect(await screen.findByText("sqlharness")).toHaveAttribute("title", "C:\\work\\sqlharness")
  expect(screen.getByText("orders-by-customer −94%")).toBeInTheDocument()
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
  await userEvent.click(await screen.findByRole("checkbox", { name: "Contains optimization" }))
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
    "/api/sessions/7/annotation": (url: URL, init?: RequestInit) => {
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
    "/api/sessions/9/annotation": () => new Response(JSON.stringify({ error: "The journal is busy; try again." }), { status: 503 }),
  })
  renderApp("/sessions")
  await userEvent.click(await screen.findByRole("button", { name: "Star session cli:busy" }))
  expect(await screen.findByRole("alert")).toHaveTextContent("The journal is busy; try again.")
  expect(screen.getByRole("button", { name: "Star session cli:busy" })).toBeEnabled()
})
