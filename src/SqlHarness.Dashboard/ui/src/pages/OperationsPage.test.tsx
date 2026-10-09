import { screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { operation } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

test("lists dimension-scoped operations with exact window and cursor paging", async () => {
  const first = operation({ id: 12, profile: "app", database: "alpha.api" })
  const second = operation({ id: 11, profile: "app", database: "alpha.api" })
  const requests: URL[] = []
  const calls = stubFetch({
    "/api/operations": (url: URL) => {
      requests.push(url)
      return jsonResponse(url.searchParams.has("cursor") ? { items: [second], nextCursor: null } : { items: [first], nextCursor: 11 })
    },
  })
  const { router } = renderApp("/operations?from=2026-10-01T00%3A00%3A00.000Z&to=2026-10-08T12%3A34%3A00.000Z&profile=app&dimensions=%7B%22region%22%3A%22west%22%2C%22component%22%3A%22api%22%2C%22project%22%3A%22alpha%22%7D")
  // RouterProvider starts the initial memory-history navigation asynchronously. Wait for route loading
  // explicitly before asserting on data fetched by that route, especially under the full Vitest suite.
  await router.load()

  expect(await screen.findByRole("link", { name: "12" })).toBeInTheDocument()
  expect(requests[0].searchParams.get("from")).toBe("2026-10-01T00:00:00.000Z")
  expect(requests[0].searchParams.get("to")).toBe("2026-10-08T12:34:00.000Z")
  expect(requests[0].searchParams.get("profile")).toBe("app")
  expect(JSON.parse(requests[0].searchParams.get("dimensions") ?? "{}")).toEqual({ region: "west", component: "api", project: "alpha" })
  expect(screen.getByText(/region = west · component = api · project = alpha/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Load more operations" }))
  expect(await screen.findByRole("link", { name: "11" })).toBeInTheDocument()
  expect(calls.filter(call => call.startsWith("/api/operations?")).length).toBeGreaterThanOrEqual(2)
})

function jsonResponse(value: unknown) {
  return new Response(JSON.stringify(value), { status: 200, headers: { "Content-Type": "application/json" } })
}
