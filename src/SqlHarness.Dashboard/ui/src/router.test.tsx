import { screen } from "@testing-library/react"
import { expect, test } from "vitest"
import { renderApp } from "@/test/render"

test("unknown urls keep the navigation and show not found", async () => {
  renderApp("/nowhere")
  expect(await screen.findByText("Not found")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Live" })).toHaveAttribute("href", "/")
  expect(screen.getByRole("link", { name: "Sessions" })).toHaveAttribute("href", "/sessions")
  expect(screen.getByRole("link", { name: "Statistics" })).toHaveAttribute("href", "/stats")
})
