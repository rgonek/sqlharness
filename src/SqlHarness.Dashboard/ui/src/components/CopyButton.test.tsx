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

test("keyboard copy retains focus while busy and after success without submitting twice", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  setClipboard(writeText)
  let finish!: (text: string) => void
  const getText = vi.fn(() => new Promise<string>(resolve => { finish = resolve }))
  render(<><CopyButton label="Copy baseline SQL" getText={getText} /><button>Next action</button></>)
  const button = screen.getByRole("button", { name: "Copy baseline SQL" })
  button.focus()
  await userEvent.keyboard("{Enter}")
  expect(button).toHaveFocus()
  expect(button).not.toBeDisabled()
  expect(button).toHaveAttribute("aria-disabled", "true")
  await userEvent.keyboard("{Enter}")
  expect(getText).toHaveBeenCalledTimes(1)
  await act(async () => finish("SELECT 1"))
  expect(writeText).toHaveBeenCalledWith("SELECT 1")
  expect(button).toHaveFocus()
  await userEvent.tab()
  expect(screen.getByRole("button", { name: "Next action" })).toHaveFocus()
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

test("names visible copy actions and announces the copied payload", async () => {
  setClipboard(vi.fn().mockResolvedValue(undefined))
  render(<CopyButton label="Copy candidate SQL" showLabel getText={() => "SELECT 2"} />)
  const button = screen.getByRole("button", { name: "Copy candidate SQL" })
  expect(button).toHaveTextContent("Copy candidate SQL")
  await userEvent.click(button)
  expect(screen.getByRole("status")).toHaveTextContent("Copied candidate SQL")
})

test("preparation failure is actionable and does not show an empty manual-copy dialog", async () => {
  const getText = vi.fn().mockRejectedValue(new Error("unavailable"))
  render(<CopyButton label="Copy diff" getText={getText} />)
  await userEvent.click(screen.getByRole("button", { name: "Copy diff" }))
  expect(await screen.findByRole("alert")).toHaveTextContent("Could not prepare diff. Try again.")
  expect(getText).toHaveBeenCalledTimes(1)
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument()
})

test("manual copy explains the payload and returns focus to its action", async () => {
  setClipboard(vi.fn().mockRejectedValue(new Error("denied")))
  render(<CopyButton label="Copy baseline SQL" getText={() => "SELECT 1"} />)
  const button = screen.getByRole("button", { name: "Copy baseline SQL" })
  await userEvent.click(button)
  expect(await screen.findByRole("dialog")).toHaveAccessibleDescription(/baseline SQL/)
  await userEvent.click(screen.getByRole("button", { name: "Close" }))
  expect(button).toHaveFocus()
})

test("changing the copy payload clears confirmation from the previous selection", async () => {
  setClipboard(vi.fn().mockResolvedValue(undefined))
  const { rerender } = render(<CopyButton label="Copy candidate SQL" scopeKey="SELECT 1" getText={() => "SELECT 1"} />)
  await userEvent.click(screen.getByRole("button", { name: "Copy candidate SQL" }))
  expect(screen.getByRole("status")).toHaveTextContent("Copied candidate SQL")
  rerender(<CopyButton label="Copy candidate SQL" scopeKey="SELECT 2" getText={() => "SELECT 2"} />)
  expect(screen.getByRole("status")).toHaveTextContent(/^$/)
})
