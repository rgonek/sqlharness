import { act, fireEvent, render, screen } from "@testing-library/react"
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
  vi.useFakeTimers()
  try {
    render(<CopyButton label="Copy" getText={async () => "# Compare"} />)
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Copy" }))
      await Promise.resolve()
      await Promise.resolve()
    })
    expect(screen.getByRole("dialog", { name: "Copy manually" })).toBeInTheDocument()
    const text = screen.getByRole("textbox", { name: "Text to copy" }) as HTMLTextAreaElement
    expect(text).toHaveValue("# Compare")
    text.setSelectionRange(0, 0)
    await vi.advanceTimersByTimeAsync(0)
    expect(text.selectionStart).toBe(0)
    expect(text.selectionEnd).toBe("# Compare".length)
  } finally {
    vi.useRealTimers()
  }
})

test("reselects the full manual-copy text whenever the dialog focuses the textarea", async () => {
  setClipboard(vi.fn().mockRejectedValue(new Error("denied")))
  render(<CopyButton label="Copy" getText={async () => "# Compare"} />)
  await userEvent.click(screen.getByRole("button", { name: "Copy" }))
  const text = await screen.findByRole("textbox", { name: "Text to copy" }) as HTMLTextAreaElement
  text.setSelectionRange(0, 0)
  fireEvent.focus(text)
  expect(text.selectionStart).toBe(0)
  expect(text.selectionEnd).toBe("# Compare".length)
})
