import { render, screen, within } from "@testing-library/react"
import { expect, test, vi } from "vitest"
import { ActivityOperations } from "@/components/ActivityOperations"
import { operation } from "@/test/fixtures"

vi.mock("@tanstack/react-router", () => ({ Link: ({ children, ...props }: React.PropsWithChildren<Record<string, unknown>>) => <a href={`/operations/${(props.params as { id: string }).id}`}>{children}</a> }))

const now = Date.parse("2026-10-26T12:00:00Z")

test("shows the supplied SQL preview alongside the label and marks truncation", () => {
  const sql = "😀".repeat(240)
  render(<ActivityOperations now={now} operations={[{ ...operation({ id: 2, operation: "compare", agentLabel: "Baseline comparison" }), sqlPreview: sql, sqlPreviewTruncated: true }]} />)
  expect(screen.getByText("Baseline comparison")).toBeInTheDocument()
  expect(screen.getByText(sql)).toBeInTheDocument()
  expect(screen.getByLabelText("SQL preview truncated")).toHaveTextContent("…")
  expect(screen.getByRole("link", { name: "2" })).toHaveAttribute("href", "/operations/2")
})

test("handles old, null and empty previews while preserving row order", () => {
  render(<ActivityOperations now={now} operations={[
    operation({ id: 1, subject: "old subject" }),
    { ...operation({ id: 2, subject: "null subject" }), sqlPreview: null, sqlPreviewTruncated: false },
    { ...operation({ id: 3, subject: "empty subject" }), sqlPreview: "", sqlPreviewTruncated: false },
    { ...operation({ id: 4, subject: "new subject" }), sqlPreview: "SELECT 0", sqlPreviewTruncated: false },
  ]} />)
  const rows = screen.getByLabelText("Session operations").children
  expect(Array.from(rows).map(row => within(row as HTMLElement).getByRole("link").textContent)).toEqual(["4", "3", "2", "1"])
  expect(screen.getByText("SELECT 0")).toBeInTheDocument()
  expect(screen.queryByLabelText("SQL preview truncated")).not.toBeInTheDocument()
  expect(screen.getByText("old subject")).toBeInTheDocument()
  expect(screen.getByText("null subject")).toBeInTheDocument()
  expect(screen.getByText("empty subject")).toBeInTheDocument()
})
