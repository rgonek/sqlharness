import { screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { operation, session } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

test("shows session facts and its operations", async () => {
  stubFetch({
    "/api/sessions/7": {
      session: session({ id: 7, sessionKey: "mcp:xyz", transport: "mcp", mcpMode: "fixed", clientName: "claude-code", clientVersion: "2.1.0" }),
      operations: [operation({ id: 70, operation: "compare" })],
    },
  })

  renderApp("/sessions/7")

  expect(await screen.findByRole("heading", { level: 1, name: "mcp:xyz" })).toBeInTheDocument()
  await userEvent.click(await screen.findByText("Session metadata"))
  expect(screen.getByText("claude-code 2.1.0")).toBeInTheDocument()
  expect(screen.getByText("compare")).toBeInTheDocument()
})

test("unknown session shows not found", async () => {
  stubFetch({})
  renderApp("/sessions/999")
  expect(await screen.findByText("Not found")).toBeInTheDocument()
})

test("session detail has a heading", async () => {
  stubFetch({ "/api/sessions/7": { session: session({ id: 7 }), operations: [] } })
  renderApp("/sessions/7")
  expect(await screen.findByRole("heading", { level: 1, name: "cli:abc" })).toBeInTheDocument()
})

test("a non-decimal session id shows not found without calling the api", async () => {
  const calls = stubFetch({ "/api/sessions/16": { session: session({ id: 16 }), operations: [] } })
  renderApp("/sessions/0x10")
  expect(await screen.findByText("Not found")).toBeInTheDocument()
  expect(calls).toEqual([])
})

test("shows the client title and workspace roots", async () => {
  stubFetch({
    "/api/sessions/8": {
      session: session({ id: 8, transport: "mcp", clientName: "claude-code", clientVersion: "2.1.0", clientTitle: "Claude Code",
        roots: [{ uri: "file:///D:/Dev/sqlharness", name: "sqlharness" }, { uri: "file:///D:/Dev/other", name: null }] }),
      operations: [],
    },
  })
  renderApp("/sessions/8")
  await userEvent.click(await screen.findByText("Session metadata"))
  expect(await screen.findByText("Claude Code 2.1.0")).toBeInTheDocument()
  expect(screen.getByText("D:/Dev/sqlharness")).toBeInTheDocument()
  expect(screen.getByText("(sqlharness)")).toBeInTheDocument()
  expect(screen.getByText("D:/Dev/other")).toBeInTheDocument()
})

test("unknown roots show a dash with a hint", async () => {
  stubFetch({ "/api/sessions/9": { session: session({ id: 9, roots: null }), operations: [] } })
  renderApp("/sessions/9")
  await userEvent.click(await screen.findByText("Session metadata"))
  const roots = await screen.findByLabelText("Workspace roots")
  expect(roots).toHaveTextContent("—")
  expect(roots).toHaveAttribute("title", expect.stringContaining("2026-07-28"))
})

test("an empty roots list shows none", async () => {
  stubFetch({ "/api/sessions/10": { session: session({ id: 10, roots: [] }), operations: [] } })
  renderApp("/sessions/10")
  await userEvent.click(await screen.findByText("Session metadata"))
  expect(await screen.findByLabelText("Workspace roots")).toHaveTextContent("none")
})

test("shows the negotiated MCP protocol, or a dash when unknown", async () => {
  stubFetch({
    "/api/sessions/11": { session: session({ id: 11, transport: "mcp", protocolVersion: "2026-07-28" }), operations: [] },
    "/api/sessions/12": { session: session({ id: 12, transport: "mcp", protocolVersion: null }), operations: [] },
  })
  const first = renderApp("/sessions/11")
  await userEvent.click(await screen.findByText("Session metadata"))
  const fact = (await screen.findByText("MCP protocol")).parentElement!
  expect(fact).toHaveTextContent("2026-07-28")
  first.unmount()

  renderApp("/sessions/12")
  await userEvent.click(await screen.findByText("Session metadata"))
  expect((await screen.findByText("MCP protocol")).parentElement!).toHaveTextContent("—")
})

test("edits the title inline: Enter saves, Escape cancels, empty clears", async () => {
  const bodies: unknown[] = []
  let title: string | null = null
  stubFetch({
    // The detail is refetched after each save, so it must reflect the saved state.
    "/api/sessions/1": () => new Response(JSON.stringify({ session: session({ id: 1, sessionKey: "cli:abc", title }), operations: [], note: null }), { status: 200 }),
    "/api/sessions/1/annotation": (_url: URL, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body)) as { title: string | null }
      bodies.push(body)
      title = body.title
      return new Response(JSON.stringify({ title, note: null, starred: false, archived: false }), { status: 200 })
    },
  })
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
  stubFetch({ "/api/sessions/1": { session: session({ id: 1, sessionKey: "cli:abc" }), operations: [], note: null } })
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("heading", { level: 1, name: "cli:abc" }))
  expect(screen.getByRole("textbox", { name: "Session title" })).toBeInTheDocument()
})

test("a failed save rolls back and shows the server message", async () => {
  let fail!: () => void
  const pending = new Promise<Response>(resolve => {
    fail = () => resolve(new Response(JSON.stringify({ error: "The journal is busy; try again." }), { status: 503 }))
  })
  stubFetch({
    "/api/sessions/1": { session: session({ id: 1, sessionKey: "cli:abc", starred: false }), operations: [], note: null },
    "/api/sessions/1/annotation": () => pending,
  })
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Star session" }))
  expect(await screen.findByRole("button", { name: "Unstar session" })).toHaveAttribute("aria-pressed", "true")
  fail()
  expect(await screen.findByText("The journal is busy; try again.")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Star session" })).toHaveAttribute("aria-pressed", "false")
})

test("archive shows a badge and the button turns into Unarchive", async () => {
  let archived = false
  stubFetch({
    "/api/sessions/1": () => new Response(JSON.stringify({ session: session({ id: 1, archived }), operations: [], note: null }), { status: 200 }),
    "/api/sessions/1/annotation": () => {
      archived = true
      return new Response(JSON.stringify({ title: null, note: null, starred: false, archived: true }), { status: 200 })
    },
  })
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Archive" }))
  expect(await screen.findByText("Archived")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Unarchive" })).toBeInTheDocument()
})

test("adds, counts and saves a note", async () => {
  const bodies: unknown[] = []
  let note: string | null = null
  stubFetch({
    "/api/sessions/1": () => new Response(JSON.stringify({ session: session({ id: 1 }), operations: [], note }), { status: 200 }),
    "/api/sessions/1/annotation": (_url: URL, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body)) as { note: string | null }
      bodies.push(body)
      note = body.note
      return new Response(JSON.stringify({ title: null, note, starred: false, archived: false }), { status: 200 })
    },
  })
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Add note" }))
  await userEvent.type(screen.getByRole("textbox", { name: "Session note" }), "reads -40%")
  expect(screen.getByText("10 / 10000")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Save note" }))
  expect(await screen.findByText("Saved")).toBeInTheDocument()
  expect(bodies).toEqual([{ note: "reads -40%" }])
})
