import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { afterEach, expect, test } from "vitest"
import { ThemeMenu } from "@/components/ThemeMenu"

afterEach(() => {
  localStorage.clear()
  document.documentElement.classList.remove("dark")
})

test("choosing dark applies and remembers it", async () => {
  render(<ThemeMenu />)
  await userEvent.click(screen.getByRole("button", { name: "Theme: system" }))
  await userEvent.click(await screen.findByRole("menuitemradio", { name: "Dark" }))
  expect(document.documentElement).toHaveClass("dark")
  expect(localStorage.getItem("sqlharness.theme")).toBe("dark")
  expect(screen.getByRole("button", { name: "Theme: dark" })).toBeInTheDocument()
})
