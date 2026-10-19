import { fireEvent, render, screen, within } from "@testing-library/react"
import { expect, test } from "vitest"
import type { StatementRow } from "@/api/types"
import { StatementRows } from "./StatementRows"

const row: StatementRow = {
  key: "select-orders", occurrence: 0, text: "SELECT * FROM Orders", status: "different-work",
  readsBefore: 120, readsAfter: 240, scansBefore: 2, scansAfter: 4, executions: 3, change: "+100%",
  attribution: "confirmed", hiddenWork: [{ kind: "scalar-udf", name: "dbo.Price", executions: null, sharePercent: 15 }],
  objects: [{ table: "dbo.Orders", scansBefore: 2, scansAfter: 4, readsBefore: 120, readsAfter: 240, change: "+100%" }],
  planHashBefore: "shape-before", planHashAfter: "shape-after", planLinkBefore: "/operations/11?planStatement=0", planLinkAfter: null,
  udfElapsedMilliseconds: 4,
}

test("renders statement status, counters, expandable objects, and only the resolved plan destination", () => {
  render(<StatementRows rows={[row]} />)

  expect(screen.getByText("different work")).toBeInTheDocument()
  expect(screen.getByText("3 executions")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Plan before" })).toHaveAttribute("href", "/operations/11?planStatement=0")
  expect(screen.getByText("Plan unavailable")).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "Plan after" })).not.toBeInTheDocument()
  const objects = screen.getByText("Objects (1)").closest("details")!
  fireEvent.click(within(objects).getByText("Objects (1)"))
  expect(within(objects).getByRole("rowheader", { name: "dbo.Orders" })).toBeInTheDocument()
})

test("renders one joined object row with its before and after counters", () => {
  render(<StatementRows rows={[{
    ...row,
    objects: [{ table: "dbo.T", scansBefore: 2, scansAfter: 1, readsBefore: 20, readsAfter: 10, change: "−50%" }],
  }]} />)

  const objects = screen.getByText("Objects (1)").closest("details")!
  fireEvent.click(within(objects).getByText("Objects (1)"))
  const objectRow = within(objects).getByRole("row", { name: "dbo.T 2 1 20 10 −50%" })
  expect(objectRow).toBeInTheDocument()
})

test("renders both hidden-work flag forms", () => {
  render(<StatementRows rows={[{
    ...row,
    hiddenWork: [
      { kind: "scalar-udf", name: "dbo.Price", executions: null, sharePercent: 15 },
      { kind: "multi-statement-tvf", name: "dbo.Expand", executions: 7, sharePercent: null },
    ],
  }]} />)

  expect(screen.getByText("UDF time 15% of statement")).toBeInTheDocument()
  expect(screen.getByText("multi-statement function: internal reads not visible (7 executions)")).toBeInTheDocument()
})

test("sorts comparison rows by absolute read change", () => {
  render(<StatementRows rows={[row, {
    ...row, key: "larger", text: "SELECT larger change", readsBefore: 10, readsAfter: 510, change: "+5000%",
  }]} />)

  const rows = within(screen.getByRole("table", { name: "Statement IO rows" })).getAllByRole("row")
  const largerIndex = rows.findIndex(item => item.textContent?.includes("SELECT larger change"))
  const smallerIndex = rows.findIndex(item => item.textContent?.includes("SELECT * FROM Orders"))
  expect(largerIndex).toBeGreaterThan(0)
  expect(smallerIndex).toBeGreaterThan(largerIndex)
})
