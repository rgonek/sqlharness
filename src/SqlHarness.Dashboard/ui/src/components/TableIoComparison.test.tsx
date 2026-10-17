import { render, screen, within } from "@testing-library/react"
import { expect, test } from "vitest"
import type { ComparePair } from "@/lib/compare"
import { TableIoComparison } from "./TableIoComparison"
import { variant } from "@/test/fixtures"

test("labels each matrix cell and keeps a regression visible beside an improvement", () => {
  const pairs: ComparePair[] = [
    { label: "cell 0", baseline: variant({ variant: "baseline", matrixCell: 0, tableIo: [{ ...variant().tableIo[0], logicalReads: 100, totalLogicalReads: 100 }] }), candidate: variant({ variant: "candidate", matrixCell: 0, tableIo: [{ ...variant().tableIo[0], logicalReads: 400, totalLogicalReads: 400 }] }) },
    { label: "cell 1", baseline: variant({ variant: "baseline", matrixCell: 1, tableIo: [{ ...variant().tableIo[0], logicalReads: 100, totalLogicalReads: 100 }] }), candidate: variant({ variant: "candidate", matrixCell: 1, tableIo: [{ ...variant().tableIo[0], logicalReads: 50, totalLogicalReads: 50 }] }) },
  ]
  render(<TableIoComparison pairs={pairs} engine="sqlserver" />)
  const first = screen.getByRole("region", { name: "cell 0 table IO" })
  const second = screen.getByRole("region", { name: "cell 1 table IO" })
  const firstTable = within(first).getByRole("table", { name: /per-table IO/ })
  const secondTable = within(second).getByRole("table", { name: /per-table IO/ })
  expect(within(within(firstTable).getByRole("row", { name: /Orders/ })).getByText("+300%")).toBeInTheDocument()
  expect(within(within(secondTable).getByRole("row", { name: /Orders/ })).getByText("-50%")).toBeInTheDocument()
  expect(within(firstTable).getByText(/Logical B/)).toBeInTheDocument()
  expect(within(firstTable).getByText(/LOB logical B/)).toBeInTheDocument()
  expect(within(firstTable).getByText(/Total B/)).toBeInTheDocument()
  expect(screen.getAllByText(/median of run totals \(data \+ LOB\)/)).toHaveLength(2)
})

test("shows recorded PostgreSQL buffers without a per-table IO section", () => {
  const pairs: ComparePair[] = [{
    label: "compare",
    baseline: variant({ variant: "baseline", tableIo: [], postgres: { sharedHit: 10, sharedRead: 2, sharedDirtied: null, sharedWritten: null, tempRead: 4, tempWritten: 0 } }),
    candidate: variant({ variant: "candidate", tableIo: [], postgres: { sharedHit: 15, sharedRead: 3, sharedDirtied: null, sharedWritten: null, tempRead: 6, tempWritten: 1 } }),
  }]
  render(<TableIoComparison pairs={pairs} engine="postgres" />)
  expect(screen.getByRole("table", { name: "Buffers compare" })).toBeInTheDocument()
  expect(screen.getByText("Shared hit")).toBeInTheDocument()
  expect(screen.getByText("Shared read")).toBeInTheDocument()
  expect(screen.getByText("Temp read")).toBeInTheDocument()
  expect(screen.getByText("Temp written")).toBeInTheDocument()
  expect(screen.queryByRole("table", { name: /per-table IO/ })).not.toBeInTheDocument()
})

test("explains when neither side recorded table IO", () => {
  const pairs: ComparePair[] = [{ label: "compare", baseline: variant({ tableIo: [] }), candidate: variant({ variant: "candidate", tableIo: [] }) }]
  render(<TableIoComparison pairs={pairs} engine="sqlserver" />)
  expect(screen.getByText("No table IO recorded for this pair.")).toBeInTheDocument()
})
