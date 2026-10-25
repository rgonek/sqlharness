import { screen } from "@testing-library/react"
import { expect, test } from "vitest"
import { renderApp, stubFetch } from "@/test/render"

test("unknown urls keep the navigation and show not found", async () => {
  stubFetch({
    "/api/profiles": { status: "missing", profiles: [], message: null },
    "/api/settings": { status: "valid", settings: { dashboard: { safetyNotifications: false } } },
  })
  renderApp("/nowhere")
  expect(await screen.findByText("Not found")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Activity" })).toHaveAttribute("href", "/")
  expect(screen.getByRole("link", { name: "Usage" })).toHaveAttribute("href", "/stats")
  expect(screen.getByRole("link", { name: "SQLHarness home" })).toHaveAttribute("href", "/")
  expect(screen.getByText("SQL")).toBeInTheDocument()
  expect(screen.getByText("Harness")).toHaveClass("text-[#10AC61]")
})
