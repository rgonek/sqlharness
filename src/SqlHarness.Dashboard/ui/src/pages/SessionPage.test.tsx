import { act, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { operation, session, sessionDetail } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

class SessionEventSource {
  static instances: SessionEventSource[] = []
  listeners = new Map<string, ((event: MessageEvent<string>) => void)[]>()
  readyState = 0
  onopen: (() => void) | null = null
  onerror: (() => void) | null = null
  readonly url: string
  constructor(url: string) { this.url = url; SessionEventSource.instances.push(this) }
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }
  close() { this.readyState = 2 }
  emit(type: string, data: unknown) {
    for (const listener of this.listeners.get(type) ?? []) listener(new MessageEvent(type, { data: JSON.stringify(data) }))
  }
}

function sessionRoutes(routes: Parameters<typeof stubFetch>[0]) {
  const operations = Object.fromEntries(Object.entries(routes)
    .map(([path, value]) => {
      const match = /^\/api\/sessions\/(\d+)$/.exec(path)
      if (!match) return null
      const items = typeof value === "object" && value !== null && "operations" in value && Array.isArray(value.operations)
        ? value.operations : []
      return [`/api/sessions/${match[1]}/operations`, { items, nextCursor: null }]
    })
    .filter((route): route is [string, { items: unknown[]; nextCursor: null }] => route !== null))
  return { ...operations, ...routes }
}

test("shows session facts and its operations", async () => {
  stubFetch(sessionRoutes({
    "/api/sessions/7": {
      session: session({ id: 7, sessionKey: "mcp:xyz", transport: "mcp", mcpMode: "fixed", clientName: "claude-code", clientVersion: "2.1.0" }),
      operations: [operation({ id: 70, operation: "compare" })],
    },
  }))

  renderApp("/sessions/7")

  expect(await screen.findByRole("heading", { level: 1, name: "mcp:xyz" })).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Session metadata" })).toHaveAttribute("aria-expanded", "false")
  await userEvent.click(await screen.findByRole("button", { name: "Session metadata" }))
  expect(screen.getByText("claude-code 2.1.0")).toBeInTheDocument()
  expect((await screen.findByRole("link", { name: "70" })).closest("tr")?.children[2]).toHaveTextContent("compare")
})

test("loads older operation pages, keeps rows through a retry, and disables Load more while pending", async () => {
  let settleOlder!: (response: Response) => void
  let olderAttempts = 0
  const pendingOlder = new Promise<Response>(resolve => { settleOlder = resolve })
  const calls = stubFetch(sessionRoutes({
    "/api/sessions/21": sessionDetail({ session: session({ id: 21, operations: 4 }) }),
    "/api/sessions/21/operations": (url: URL) => {
      const cursor = url.searchParams.get("cursor")
      if (!cursor) return Response.json({ items: [operation({ id: 40 }), operation({ id: 39 })], nextCursor: 39 })
      olderAttempts += 1
      return olderAttempts === 1 ? pendingOlder : Response.json({ items: [operation({ id: 2 })], nextCursor: null })
    },
  }))
  renderApp("/sessions/21")

  expect(await screen.findByRole("link", { name: "40" })).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "39" })).toBeInTheDocument()
  expect(screen.getByText(/Newest first/)).toHaveTextContent("Loaded 2 of 4 operations.")
  await userEvent.click(screen.getByRole("button", { name: "Load more" }))
  expect(screen.getByRole("button", { name: "Loading…" })).toBeDisabled()
  settleOlder(new Response("temporary failure", { status: 500 }))

  expect(await screen.findByRole("alert")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "40" })).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "39" })).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Retry" }))
  expect(await screen.findByRole("link", { name: "2" })).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "40" })).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument()
  expect(calls.filter(url => url.includes("/operations?")).map(url => new URL(url, "http://dashboard").searchParams.get("cursor"))).toEqual([null, "39", "39"])
})

test("continues past pages containing only hidden internal operations", async () => {
  const calls = stubFetch(sessionRoutes({
    "/api/sessions/22": sessionDetail({ session: session({ id: 22, operations: 3 }) }),
    "/api/sessions/22/operations": (url: URL) => url.searchParams.get("cursor")
      ? Response.json({ items: [operation({ id: 1, operation: "query" })], nextCursor: null })
      : Response.json({ items: [operation({ id: 3, operation: "artifact write" }), operation({ id: 2, operation: "artifact read" })], nextCursor: 2 }),
  }))
  renderApp("/sessions/22")

  expect(await screen.findByText(/Newest first/)).toHaveTextContent("Loaded 2 of 3 operations.")
  expect(screen.queryByRole("link", { name: "3" })).not.toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Load more" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("checkbox", { name: "Show internal operations" }))
  expect(screen.getByRole("link", { name: "3" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("checkbox", { name: "Show internal operations" }))
  expect(screen.queryByRole("link", { name: "3" })).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Load more" }))
  expect(await screen.findByRole("link", { name: "1" })).toBeInTheDocument()
  expect(calls.filter(url => url.includes("/operations?")).map(url => new URL(url, "http://dashboard").searchParams.get("cursor"))).toEqual([null, "2"])
})

test("switching session IDs shows only that session's cached operation pages", async () => {
  const calls = stubFetch(sessionRoutes({
    "/api/sessions/31": sessionDetail({ session: session({ id: 31 }) }),
    "/api/sessions/32": sessionDetail({ session: session({ id: 32 }) }),
    "/api/sessions/31/operations": { items: [operation({ id: 310 })], nextCursor: null },
    "/api/sessions/32/operations": { items: [operation({ id: 320 })], nextCursor: null },
  }))
  const app = renderApp("/sessions/31")

  expect(await screen.findByRole("link", { name: "310" })).toBeInTheDocument()
  await app.router.navigate({ to: "/sessions/$id", params: { id: "32" } })
  expect(await screen.findByRole("link", { name: "320" })).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "310" })).not.toBeInTheDocument()
  await app.router.navigate({ to: "/sessions/$id", params: { id: "31" } })
  expect(await screen.findByRole("link", { name: "310" })).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "320" })).not.toBeInTheDocument()
  expect(calls.filter(url => url.endsWith("/operations?limit=500"))).toHaveLength(2)
})

test("live operation events refresh paged history without losing rows or mixing detail cache", async () => {
  SessionEventSource.instances = []
  globalThis.EventSource = SessionEventSource as unknown as typeof EventSource
  let operationState = [operation({ id: 2 }), operation({ id: 1 })]
  let failNextPage = false
  stubFetch(sessionRoutes({
    "/api/sessions/41": sessionDetail({ session: session({ id: 41, operations: 4 }), operations: [operation({ id: 99 })] }),
    "/api/sessions/41/operations": (url: URL) => {
      const cursor = url.searchParams.get("cursor")
      if (!cursor) {
        if (failNextPage) {
          failNextPage = false
          return new Response("temporary refetch failure", { status: 503 })
        }
        const size = operationState.length > 2 ? 2 : 1
        return Response.json({ items: operationState.slice(0, size), nextCursor: operationState.length > size ? operationState[size - 1].id : null })
      }
      return Response.json({ items: operationState.filter(item => item.id < Number(cursor)), nextCursor: null })
    },
  }))
  renderApp("/sessions/41")

  expect(await screen.findByRole("link", { name: "2" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Load more" }))
  expect(await screen.findByRole("link", { name: "1" })).toBeInTheDocument()
  const table = screen.getAllByRole("table").at(-1)!
  expect(within(table).getAllByRole("row").slice(1).map(row => within(row).getAllByRole("link")[0].textContent)).toEqual(["2", "1"])

  const source = SessionEventSource.instances.find(item => item.url === "/api/live")!
  failNextPage = true
  operationState = [operation({ id: 3, status: "running" }), ...operationState]
  act(() => source.emit("operation", operation({ id: 3, sessionId: 41, status: "running" })))
  await waitFor(() => expect(screen.getByRole("alert")).toBeInTheDocument())
  expect(screen.getByRole("link", { name: "2" })).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "1" })).toBeInTheDocument()

  operationState = [operation({ id: 3, status: "succeeded" }), ...operationState.slice(1)]
  act(() => source.emit("operation", operation({ id: 3, sessionId: 41, status: "succeeded" })))
  await waitFor(() => expect(within(screen.getByRole("link", { name: "3" }).closest("tr")!).getByText("succeeded")).toBeInTheDocument())
  expect(screen.getByRole("link", { name: "3" })).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "2" })).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "1" })).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "99" })).not.toBeInTheDocument()

  operationState = [operation({ id: 4 }), ...operationState]
  act(() => source.emit("operation", operation({ id: 4, sessionId: 41 })))
  await waitFor(() => expect(screen.getByRole("link", { name: "4" })).toBeInTheDocument())
  const rowIds = within(screen.getAllByRole("table").at(-1)!).getAllByRole("row").slice(1)
    .map(row => within(row).getAllByRole("link")[0].textContent)
  expect(rowIds).toEqual(["4", "3", "2", "1"])
  expect(new Set(rowIds).size).toBe(rowIds.length)
})

test("shows the optimization section for sessions marked with a compare", async () => {
  stubFetch(sessionRoutes({
    "/api/sessions/7": sessionDetail({ session: session({ id: 7, hasCompare: true }) }),
    "/api/sessions/7/optimization": { subjects: [], unassigned: [] },
  }))
  renderApp("/sessions/7")
  expect(await screen.findByRole("heading", { name: "Optimization" })).toBeInTheDocument()
})

test("unknown session shows not found", async () => {
  stubFetch(sessionRoutes({ "/api/sessions/999": () => new Response("", { status: 404 }) }))
  renderApp("/sessions/999")
  expect(await screen.findByText("Not found")).toBeInTheDocument()
})

test("session detail has a heading", async () => {
  stubFetch(sessionRoutes({ "/api/sessions/7": sessionDetail({ session: session({ id: 7 }) }) }))
  renderApp("/sessions/7")
  expect(await screen.findByRole("heading", { level: 1, name: "cli:abc" })).toBeInTheDocument()
  expect(screen.getByRole("heading", { level: 1, name: "cli:abc" })).toBeVisible()
  expect(screen.getByText("cli:abc", { selector: "code" })).not.toBeVisible()
})

test("a titled session shows its key under the title", async () => {
  stubFetch(sessionRoutes({ "/api/sessions/7": { session: session({ id: 7, title: "BSI-1 orders" }), operations: [] } }))
  renderApp("/sessions/7")
  expect(await screen.findByRole("heading", { level: 1, name: "BSI-1 orders" })).toBeInTheDocument()
  expect(screen.getByText("cli:abc")).toBeInTheDocument()
})

test("shows the agent title and conversation list, and clears back to that title", async () => {
  let title: string | null = null
  stubFetch(sessionRoutes({
    "/api/sessions/1": () => new Response(JSON.stringify(sessionDetail({
      session: session({ id: 1, title, agentTitle: title ? null : "Optimize orders", agentKind: "claude" }),
      conversations: [{ agentKind: "claude", conversationId: "conv-1", title: "Optimize orders", operations: 2 }],
    })), { status: 200 }),
    "PATCH /api/sessions/1/annotation": (_url: URL, init?: RequestInit) => {
      title = (JSON.parse(String(init?.body)) as { title: string | null }).title
      return new Response(JSON.stringify({ title, note: null, starred: false, archived: false }), { status: 200 })
    },
  }))
  renderApp("/sessions/1")
  expect(await screen.findByRole("heading", { level: 1, name: "Optimize orders" })).toBeInTheDocument()
  expect(screen.getByText("from Claude")).toBeInTheDocument()
  expect(screen.getByText("Conversations")).not.toBeVisible()
  await userEvent.click(screen.getByRole("button", { name: "Session metadata" }))
  expect(screen.getByText("Conversations")).toBeVisible()
  await userEvent.click(screen.getByRole("button", { name: "Edit title" }))
  expect(screen.getByRole("textbox", { name: "Session title" })).toHaveAttribute("placeholder", "Optimize orders")
  expect(screen.getByText("Conversations")).toBeInTheDocument()
  expect(screen.getByText(/Claude Code.*2 operations/)).toBeInTheDocument()
  await userEvent.type(screen.getByRole("textbox", { name: "Session title" }), "Saved{Enter}")
  await waitFor(() => expect(screen.getByRole("heading", { level: 1, name: "Saved" })).toBeInTheDocument())
  await userEvent.click(screen.getByRole("button", { name: "Edit title" }))
  await userEvent.clear(screen.getByRole("textbox", { name: "Session title" }))
  await userEvent.type(screen.getByRole("textbox", { name: "Session title" }), "{Enter}")
  await waitFor(() => expect(screen.getByRole("heading", { level: 1, name: "Optimize orders" })).toBeInTheDocument())
})

test("a non-decimal session id shows not found without fetching session data", async () => {
  const calls = stubFetch(sessionRoutes({ "/api/sessions/16": { session: session({ id: 16 }), operations: [] } }))
  renderApp("/sessions/0x10")
  expect(await screen.findByText("Not found")).toBeInTheDocument()
  expect(calls).toContain("/api/profiles")
  expect(calls).toContain("/api/settings")
  expect(calls).not.toContain("/api/sessions/16")
})

test("shows the client title and workspace roots", async () => {
  stubFetch(sessionRoutes({
    "/api/sessions/8": {
      session: session({ id: 8, transport: "mcp", clientName: "claude-code", clientVersion: "2.1.0", clientTitle: "Claude Code",
        roots: [{ uri: "file:///D:/Dev/sqlharness", name: "sqlharness" }, { uri: "file:///D:/Dev/other", name: null }] }),
      operations: [],
    },
  }))
  renderApp("/sessions/8")
  await userEvent.click(await screen.findByRole("button", { name: "Session metadata" }))
  expect(await screen.findByText("Claude Code 2.1.0")).toBeInTheDocument()
  expect(screen.getByText("D:/Dev/sqlharness")).toBeInTheDocument()
  expect(screen.getByText("(sqlharness)")).toBeInTheDocument()
  expect(screen.getByText("D:/Dev/other")).toBeInTheDocument()
})

test("unknown roots show a dash with a hint", async () => {
  stubFetch(sessionRoutes({ "/api/sessions/9": { session: session({ id: 9, roots: null }), operations: [] } }))
  renderApp("/sessions/9")
  await userEvent.click(await screen.findByRole("button", { name: "Session metadata" }))
  const roots = await screen.findByLabelText("Workspace roots")
  expect(roots).toHaveTextContent("—")
  expect(roots).toHaveAttribute("title", expect.stringContaining("2026-07-28"))
})

test("an empty roots list shows none", async () => {
  stubFetch(sessionRoutes({ "/api/sessions/10": { session: session({ id: 10, roots: [] }), operations: [] } }))
  renderApp("/sessions/10")
  await userEvent.click(await screen.findByRole("button", { name: "Session metadata" }))
  expect(await screen.findByLabelText("Workspace roots")).toHaveTextContent("none")
})

test("shows the negotiated MCP protocol, or a dash when unknown", async () => {
  stubFetch(sessionRoutes({
    "/api/sessions/11": { session: session({ id: 11, transport: "mcp", protocolVersion: "2026-07-28" }), operations: [] },
    "/api/sessions/12": { session: session({ id: 12, transport: "mcp", protocolVersion: null }), operations: [] },
  }))
  const first = renderApp("/sessions/11")
  await userEvent.click(await screen.findByRole("button", { name: "Session metadata" }))
  const fact = (await screen.findByText("MCP protocol")).parentElement!
  expect(fact).toHaveTextContent("2026-07-28")
  first.unmount()

  renderApp("/sessions/12")
  await userEvent.click(await screen.findByRole("button", { name: "Session metadata" }))
  expect((await screen.findByText("MCP protocol")).parentElement!).toHaveTextContent("—")
})

test("edits the title inline: Enter saves, Escape cancels, empty clears", async () => {
  const bodies: unknown[] = []
  let title: string | null = null
  stubFetch(sessionRoutes({
    // The detail is refetched after each save, so it must reflect the saved state.
    "/api/sessions/1": () => new Response(JSON.stringify({ session: session({ id: 1, sessionKey: "cli:abc", title }), operations: [], note: null }), { status: 200 }),
    "PATCH /api/sessions/1/annotation": (_url: URL, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body)) as { title: string | null }
      bodies.push(body)
      title = body.title
      return new Response(JSON.stringify({ title, note: null, starred: false, archived: false }), { status: 200 })
    },
  }))
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Edit title" }))
  await userEvent.type(screen.getByRole("textbox", { name: "Session title" }), "draft{Escape}")
  expect(bodies).toEqual([])
  await userEvent.click(screen.getByRole("button", { name: "Edit title" }))
  await userEvent.type(screen.getByRole("textbox", { name: "Session title" }), "BSI-1{Enter}")
  expect(await screen.findByRole("heading", { level: 1, name: "BSI-1" })).toBeInTheDocument()
  await waitFor(() => expect(bodies).toEqual([{ title: "BSI-1" }]))
  await userEvent.click(screen.getByRole("button", { name: "Edit title" }))
  await userEvent.clear(screen.getByRole("textbox", { name: "Session title" }))
  await userEvent.keyboard("{Enter}")
  expect(await screen.findByRole("heading", { level: 1, name: "cli:abc" })).toBeInTheDocument()
  await waitFor(() => expect(bodies).toEqual([{ title: "BSI-1" }, { title: null }]))
})

test("clicking the title text starts editing", async () => {
  stubFetch(sessionRoutes({ "/api/sessions/1": { session: session({ id: 1, sessionKey: "cli:abc" }), operations: [], note: null } }))
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("heading", { level: 1, name: "cli:abc" }))
  expect(screen.getByRole("textbox", { name: "Session title" })).toBeInTheDocument()
})

test("a failed save rolls back and shows the server message", async () => {
  let fail!: () => void
  const pending = new Promise<Response>(resolve => {
    fail = () => resolve(new Response(JSON.stringify({ error: "The journal is busy; try again." }), { status: 503 }))
  })
  stubFetch(sessionRoutes({
    "/api/sessions/1": { session: session({ id: 1, sessionKey: "cli:abc", starred: false }), operations: [], note: null },
    "PATCH /api/sessions/1/annotation": () => pending,
  }))
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Star session" }))
  expect(await screen.findByRole("button", { name: "Unstar session" })).toHaveAttribute("aria-pressed", "true")
  fail()
  expect(await screen.findByText("The journal is busy; try again.")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Star session" })).toHaveAttribute("aria-pressed", "false")
})

test("a save for a session that no longer exists shows the server message", async () => {
  stubFetch(sessionRoutes({
    "/api/sessions/1": { session: session({ id: 1, starred: false }), operations: [], note: null },
    "PATCH /api/sessions/1/annotation": () => new Response(JSON.stringify({ error: "The session no longer exists." }), { status: 404 }),
  }))
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Star session" }))
  expect(await screen.findByText("The session no longer exists.")).toBeInTheDocument()
  expect(screen.queryByText(/\/annotation/)).not.toBeInTheDocument()
})

test("archive shows a badge and the button turns into Unarchive", async () => {
  let archived = false
  stubFetch(sessionRoutes({
    "/api/sessions/1": () => new Response(JSON.stringify({ session: session({ id: 1, archived }), operations: [], note: null }), { status: 200 }),
    "PATCH /api/sessions/1/annotation": () => {
      archived = true
      return new Response(JSON.stringify({ title: null, note: null, starred: false, archived: true }), { status: 200 })
    },
  }))
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Archive" }))
  expect(await screen.findByText("Archived")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Unarchive" })).toBeInTheDocument()
})

test("adds, counts and saves a note", async () => {
  const bodies: unknown[] = []
  let note: string | null = null
  stubFetch(sessionRoutes({
    "/api/sessions/1": () => new Response(JSON.stringify({ session: session({ id: 1 }), operations: [], note }), { status: 200 }),
    "PATCH /api/sessions/1/annotation": (_url: URL, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body)) as { note: string | null }
      bodies.push(body)
      note = body.note
      return new Response(JSON.stringify({ title: null, note, starred: false, archived: false }), { status: 200 })
    },
  }))
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Add note" }))
  await userEvent.type(screen.getByRole("textbox", { name: "Session note" }), "reads -40%")
  expect(screen.getByText("10 / 10000")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Save note" }))
  expect(await screen.findByText("Saved")).toBeInTheDocument()
  expect(bodies).toEqual([{ note: "reads -40%" }])
})

test("compact operations preserve detail return context, flags, target and owner keep workflow", async () => {
  const bodies: unknown[] = []
  stubFetch(sessionRoutes({
    "/api/sessions/7": sessionDetail({ session: session({ id: 7 }), operations: [operation({ id: 70, sessionId: 7, agentKeep: true, hasSpill: true, profile: "pg", sqlPreview: "select 1" })] }),
    "PATCH /api/operations/70/annotation": (_url: URL, init?: RequestInit) => {
      bodies.push(JSON.parse(String(init?.body)))
      return Response.json({ agentLabel: null, agentKeep: true, ownerStarred: true, ownerNote: null })
    },
  }))
  renderApp("/sessions/7")
  const link = await screen.findByRole("link", { name: "70" })
  expect(link).toHaveAttribute("href", "/operations/70?returnSession=7")
  const row = link.closest("tr")!
  expect(within(row).getByLabelText("Kept by agent")).toBeInTheDocument()
  expect(within(row).getByText("select 1")).toBeInTheDocument()
  expect(within(row).getByText("select 1")).toHaveAttribute("title", expect.stringContaining("pg"))
  expect(screen.queryByRole("columnheader", { name: "Owner" })).not.toBeInTheDocument()
  await userEvent.click(within(row).getByRole("button", { name: "Star operation" }))
  await waitFor(() => expect(bodies).toEqual([{ starred: true }]))
})


test("compact rows prefer labels while exposing SQL, subject, target and complete timestamp", async () => {
  const startedAt = "2026-10-28T12:34:56Z"
  stubFetch(sessionRoutes({ "/api/sessions/7": sessionDetail({ session: session({ id: 7 }), operations: [operation({ id: 71, sessionId: 7, startedAt, agentLabel: "Fast candidate", subject: "Orders", sqlPreview: "select * from Orders", profile: "production", status: "running", agentKeep: true })] }) }))
  renderApp("/sessions/7")
  const row = (await screen.findByRole("link", { name: "71" })).closest("tr")!
  const label = within(row).getByText("Fast candidate")
  expect(label).toHaveAttribute("title", expect.stringContaining("select * from Orders"))
  expect(label).toHaveAttribute("title", expect.stringContaining("production"))
  expect(within(row).queryByText("production")).not.toBeInTheDocument()
  expect(within(row).getByText("running")).toHaveAttribute("data-slot", "badge")
  const time = row.querySelector("time")!
  expect(time).toHaveAttribute("datetime", startedAt)
  expect(time.textContent).toMatch(/^\d{2}:\d{2}:\d{2}$/)
  expect(time).toHaveAttribute("title", expect.stringContaining("2026"))
})
