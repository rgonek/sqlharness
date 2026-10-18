import { render, screen } from "@testing-library/react"
import { expect, test } from "vitest"
import { SafetyNoticeToast } from "./SafetyNoticeToast"

test("shows a notice with an operation link only when enabled", async () => {
  const notices = [{ operationId: 42, notices: ["open transaction rolled back"] }]
  const { rerender } = render(<SafetyNoticeToast enabled notices={notices} />)

  expect(await screen.findByText("open transaction rolled back")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "View operation" })).toHaveAttribute("href", "/operations/42")
  rerender(<SafetyNoticeToast enabled={false} notices={notices} />)
  expect(screen.queryByText("open transaction rolled back")).not.toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "View operation" })).not.toBeInTheDocument()
})
