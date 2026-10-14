import { render, screen } from "@testing-library/react"
import { expect, test } from "vitest"
import { SqlDiff } from "./SqlDiff"

test("shows a deterministic line diff and leaves complete SQL available on demand", async () => {
  render(<SqlDiff baseline="SELECT 1\nFROM Orders" candidate="SELECT 2\nFROM Orders" />)
  expect(screen.getByLabelText("SQL diff")).toHaveTextContent("- SELECT 1")
  expect(screen.getByLabelText("SQL diff")).toHaveTextContent("+ SELECT 2")
  expect(screen.getByRole("button", { name: "Show full SQL versions" })).toBeInTheDocument()
})

test("falls back to bounded previews when SQL exceeds the diff limits", () => {
  const baseline = "a".repeat(132_000)
  const candidate = "b".repeat(132_000)
  render(<SqlDiff baseline={baseline} candidate={candidate} />)
  expect(screen.getByText("Diff unavailable: SQL exceeds the 256 KiB or 4,000 line limit.")).toBeInTheDocument()
  expect(screen.getAllByText(/Preview limited to 8 KiB/)).toHaveLength(2)
})
