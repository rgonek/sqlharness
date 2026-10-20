import { screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { operation } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

test("named operations searches across sessions and separates owner stars from agent keep labels", async () => {
  const requests: URL[] = []
  stubFetch({ "/api/named-operations": (url: URL) => {
    requests.push(url)
    return jsonResponse({ items: [operation({ id: 41, sessionId: 5, agentLabel: "<b>weekly check</b>", agentKeep: true, ownerStarred: true })], nextCursor: null })
  } })
  renderApp("/named-operations")

  expect(await screen.findByText("<b>weekly check</b>")).toBeInTheDocument()
  expect(screen.getByText("Agent keep")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Unstar operation" })).toHaveAttribute("aria-pressed", "true")
  await userEvent.type(screen.getByRole("textbox", { name: "Search named operations" }), "weekly")
  expect(await screen.findByText("<b>weekly check</b>")).toBeInTheDocument()
  expect(requests.some(url => url.searchParams.get("q") === "weekly")).toBe(true)
})

function jsonResponse(value: unknown) {
  return new Response(JSON.stringify(value), { status: 200, headers: { "Content-Type": "application/json" } })
}
