import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { StatusBadge } from "@/components/StatusBadge"
import { operation } from "@/test/fixtures"

test("succeeded is a plain badge", () => {
  render(<StatusBadge operation={operation()} />)
  expect(screen.getByText("succeeded")).toBeInTheDocument()
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

test("rejected without a stored message explains storeSensitive", async () => {
  render(<StatusBadge operation={operation({ status: "rejected", exitCode: 2, errorKind: "safety_rejected" })} />)
  await userEvent.click(screen.getByRole("button", { name: /rejected/ }))
  expect(await screen.findByRole("dialog")).toHaveTextContent("journal.storeSensitive")
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
