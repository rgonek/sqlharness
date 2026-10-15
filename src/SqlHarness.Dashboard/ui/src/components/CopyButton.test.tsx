import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import { CopyButton } from "./CopyButton"

function setClipboard(writeText: ReturnType<typeof vi.fn>) {
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } })
}

test("copies text and confirms", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  setClipboard(writeText)
  render(<CopyButton label="Copy baseline SQL" getText={() => "SELECT 1"} />)
  await userEvent.click(screen.getByRole("button", { name: "Copy baseline SQL" }))
  expect(writeText).toHaveBeenCalledWith("SELECT 1")
  expect(await screen.findByRole("status")).toHaveTextContent("Copied")
})

test("falls back to a manual copy dialog", async () => {
  setClipboard(vi.fn().mockRejectedValue(new Error("denied")))
  render(<CopyButton label="Copy" getText={async () => "# Compare"} />)
  await userEvent.click(screen.getByRole("button", { name: "Copy" }))
  expect(await screen.findByRole("dialog", { name: "Copy manually" })).toBeInTheDocument()
  expect(screen.getByRole("textbox", { name: "Text to copy" })).toHaveValue("# Compare")
})
