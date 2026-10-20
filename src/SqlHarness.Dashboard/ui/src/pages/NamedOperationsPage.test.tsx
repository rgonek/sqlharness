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
  await userEvent.type(screen.getByRole("searchbox", { name: "Search named operations" }), "weekly")
  expect(await screen.findByText("<b>weekly check</b>")).toBeInTheDocument()
  expect(requests.some(url => url.searchParams.get("q") === "weekly")).toBe(true)
})

test("failed next-page fetch keeps loaded operations visible and can be retried", async () => {
  let laterPageCalls = 0
  stubFetch({ "/api/named-operations": (url: URL) => {
    if (!url.searchParams.has("cursor")) return jsonResponse({ items: [operation({ id: 41, agentLabel: "first page" })], nextCursor: 40 })
    laterPageCalls += 1
    return laterPageCalls === 1
      ? new Response(JSON.stringify({ error: "temporary failure" }), { status: 503, headers: { "Content-Type": "application/json" } })
      : jsonResponse({ items: [operation({ id: 39, agentLabel: "second page" })], nextCursor: null })
  } })
  renderApp("/named-operations")

  expect(await screen.findByText("first page")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Load more operations" }))
  expect(await screen.findByRole("alert")).toHaveTextContent("Request failed")
  expect(screen.getByText("first page")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Retry loading more" }))
  expect(await screen.findByText("second page")).toBeInTheDocument()
})

function jsonResponse(value: unknown) {
  return new Response(JSON.stringify(value), { status: 200, headers: { "Content-Type": "application/json" } })
}
