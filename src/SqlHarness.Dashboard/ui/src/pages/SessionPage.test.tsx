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
