import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { StatusBadge } from "@/components/StatusBadge"
import { operation } from "@/test/fixtures"

test("succeeded is a plain badge", () => {
  render(<StatusBadge operation={operation()} />)
  expect(screen.getByText("succeeded").closest("[data-slot='badge']")).toHaveAttribute("data-tier", "outcome")
  expect(screen.queryByRole("button")).not.toBeInTheDocument()
})

test("running is an informational badge without an error action", () => {
  render(<StatusBadge operation={operation({ status: "running" })} />)
  expect(screen.getByText("running").closest("[data-slot='badge']")).toHaveAttribute("data-tier", "info")
  expect(screen.queryByRole("button")).not.toBeInTheDocument()
})

test("failed opens a dialog with the stored message", async () => {
  render(<StatusBadge operation={operation({ status: "failed", exitCode: 5, errorKind: "sql_execution_failed", errorMessage: "Invalid column name 'x'." })} />)
  await userEvent.click(screen.getByRole("button", { name: /failed/ }))
  const dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent("sql_execution_failed")
  expect(dialog).toHaveTextContent("exit 5")
  expect(dialog).toHaveTextContent("Invalid column name 'x'.")
})

test("a legacy operation without a stored message explains that details are unavailable", async () => {
  render(<StatusBadge operation={operation({ status: "rejected", exitCode: 2, errorKind: "safety_rejected" })} />)
  await userEvent.click(screen.getByRole("button", { name: /rejected/ }))
  const dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent("No error message is available for this operation.")
  expect(dialog).not.toHaveTextContent("journal.storeSensitive")
})

test("renders error message as text", async () => {
  render(<StatusBadge operation={operation({ status: "failed", exitCode: 5, errorKind: "sql_execution_failed", errorMessage: "<img src=x onerror=alert(1)>" })} />)
  await userEvent.click(screen.getByRole("button", { name: /failed/ }))
  const dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent("<img src=x onerror=alert(1)>")
  expect(dialog.querySelector("img")).toBeNull()
})

test("hover shows the error kind in a tooltip", async () => {
  render(<StatusBadge operation={operation({ status: "failed", exitCode: 5, errorKind: "sql_execution_failed" })} />)
  await userEvent.hover(screen.getByRole("button", { name: /failed/ }))
  expect(await screen.findByText(/database returned an error/)).toBeInTheDocument()
})

test("focus shows the tooltip and Enter opens the dialog, Escape returns focus", async () => {
  const user = userEvent.setup()
  render(<StatusBadge operation={operation({ status: "failed", exitCode: 5, errorKind: "sql_execution_failed" })} />)
  const trigger = screen.getByRole("button", { name: /failed/ })

  await user.tab()
  expect(trigger).toHaveFocus()
  expect(await screen.findByText(/database returned an error/)).toBeInTheDocument()

  await user.keyboard("{Enter}")
  const dialog = await screen.findByRole("dialog")
  expect(trigger).toHaveAttribute("aria-controls", dialog.id)
  await user.keyboard("{Escape}")

  expect(screen.queryByRole("dialog")).not.toBeInTheDocument()
  expect(trigger).toHaveFocus()
})

test("each error badge opens its own dialog from one effective button", async () => {
  const user = userEvent.setup()
  render(
    <>
      <StatusBadge operation={operation({ status: "failed", errorMessage: "first failure" })} />
      <StatusBadge operation={operation({ status: "rejected", errorMessage: "second rejection" })} />
    </>
  )

  const triggers = screen.getAllByRole("button")
  expect(triggers).toHaveLength(2)
  expect(triggers.every((trigger) => trigger.querySelector("button") === null)).toBe(true)

  await user.click(screen.getByRole("button", { name: /rejected/ }))
  expect(await screen.findByRole("dialog")).toHaveTextContent("second rejection")
  await user.keyboard("{Escape}")

  await user.click(screen.getByRole("button", { name: /failed/ }))
  expect(await screen.findByRole("dialog")).toHaveTextContent("first failure")
})

test("a cancellation names who cancelled", async () => {
  render(<StatusBadge operation={operation({ status: "failed", exitCode: -1, errorKind: "cancelled", cancelReason: "client" })} />)
  await userEvent.click(screen.getByRole("button", { name: /failed/ }))
  expect(await screen.findByRole("dialog")).toHaveTextContent("Cancelled by the client.")
})
