import { screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { session } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

test("lists sessions, filters by agent and loads more", async () => {
  const calls = stubFetch({
    "/api/sessions?limit=50": { items: [session({ id: 2, sessionKey: "cli:two" })], nextCursor: 2 },
    "/api/sessions?cursor=2&limit=50": { items: [session({ id: 1, sessionKey: "cli:one" })], nextCursor: null },
    "/api/sessions?agent=codex&limit=50": { items: [session({ id: 3, sessionKey: "mcp:codex", agentKind: "codex" })], nextCursor: null },
  })

  renderApp("/sessions")
  expect(await screen.findByText("cli:two")).toBeInTheDocument()

  await userEvent.click(screen.getByRole("button", { name: "Load more" }))
  expect(await screen.findByText("cli:one")).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument()

  await userEvent.click(screen.getByRole("tab", { name: "Codex" }))
  expect(await screen.findByText("mcp:codex")).toBeInTheDocument()
  expect(calls).toContain("/api/sessions?agent=codex&limit=50")
})
