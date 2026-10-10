import { screen } from "@testing-library/react"
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

  expect(await screen.findByText("mcp:xyz")).toBeInTheDocument()
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
  expect(await screen.findByRole("heading", { level: 1, name: "Session 7" })).toBeInTheDocument()
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
  expect(await screen.findByText("Claude Code 2.1.0")).toBeInTheDocument()
  expect(screen.getByText("D:/Dev/sqlharness")).toBeInTheDocument()
  expect(screen.getByText("(sqlharness)")).toBeInTheDocument()
  expect(screen.getByText("D:/Dev/other")).toBeInTheDocument()
})

test("unknown roots show a dash with a hint", async () => {
  stubFetch({ "/api/sessions/9": { session: session({ id: 9, roots: null }), operations: [] } })
  renderApp("/sessions/9")
  const roots = await screen.findByLabelText("Workspace roots")
  expect(roots).toHaveTextContent("—")
  expect(roots).toHaveAttribute("title", expect.stringContaining("2026-07-28"))
})

test("an empty roots list shows none", async () => {
  stubFetch({ "/api/sessions/10": { session: session({ id: 10, roots: [] }), operations: [] } })
  renderApp("/sessions/10")
  expect(await screen.findByLabelText("Workspace roots")).toHaveTextContent("none")
})
