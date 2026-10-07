import { screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { stats } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

test("shows KPIs, charts and top lists, and switches range", async () => {
  const calls = stubFetch({ "/api/stats": stats() })
  renderApp("/stats")

  expect(await screen.findByText("90%")).toBeInTheDocument()
  expect(screen.getByText("Operations per day")).toBeInTheDocument()
  expect(screen.getByText("aaaaaaaaaaaa")).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "Orders" })).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "PAGEIOLATCH_SH" })).toBeInTheDocument()

  await userEvent.click(screen.getByRole("tab", { name: "All time" }))
  await screen.findByText("90%")
  expect(calls.some(call => call === "/api/stats")).toBe(true)
  expect(calls.some(call => call.startsWith("/api/stats?from="))).toBe(true)
})
