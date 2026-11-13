import { act, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { renderApp, stubFetch } from "@/test/render"
import { session, stats } from "@/test/fixtures"

test("session history retains its search and optimization filter after a detail visit", async () => {
  stubFetch({
    "/api/sessions": { items: [session({ id: 1, title: "Orders" })], nextCursor: null },
    "/api/agents": [],
  })
  const { router } = renderApp("/sessions")
  const user = userEvent.setup()
  await user.type(await screen.findByRole("searchbox", { name: "Search sessions" }), "Orders")
  await user.click(screen.getByRole("checkbox", { name: "Contains optimization" }))
  await act(() => router.navigate({ to: "/nowhere" as never }))
  await screen.findByText("Not found")
  await act(() => router.navigate({ to: "/sessions" }))
  expect(await screen.findByRole("searchbox", { name: "Search sessions" })).toHaveValue("Orders")
  expect(screen.getByRole("checkbox", { name: "Contains optimization" })).toBeChecked()
})

test("Usage retains its range and SQL ordering across a drill-down round trip", async () => {
  stubFetch({ "/api/stats": stats() })
  const user = userEvent.setup()
  const { router } = renderApp("/stats")
  await screen.findByRole("region", { name: "Top SQL" })
  await user.click(screen.getByRole("tab", { name: "All time" }))
  await user.click(await screen.findByRole("radio", { name: "Logical reads" }))
  await act(() => router.navigate({ to: "/nowhere" as never }))
  await screen.findByText("Not found")
  await user.click(screen.getByRole("link", { name: "Usage" }))
  await screen.findByRole("region", { name: "Top SQL" })
  expect(screen.getByRole("tab", { name: "All time" })).toHaveAttribute("aria-selected", "true")
  expect(screen.getByRole("radio", { name: "Logical reads" })).toBeChecked()
})
