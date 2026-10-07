import { expect, test } from "vitest"
import { variant } from "@/test/fixtures"
import { comparePairs, compareRows, variantLabel } from "./compare"

test("labels include parameter set and matrix cell", () => {
  expect(variantLabel(variant())).toBe("measure")
  expect(variantLabel(variant({ variant: "baseline", matrixCell: 2 }))).toBe("baseline · cell 2")
  expect(variantLabel(variant({ parameterSet: "large" }))).toBe("measure · large")
})

test("pairs baseline and candidate per matrix cell", () => {
  const pairs = comparePairs([
    variant({ ordinal: 0, variant: "baseline", matrixCell: 0 }), variant({ ordinal: 1, variant: "candidate", matrixCell: 0 }),
    variant({ ordinal: 2, variant: "baseline", matrixCell: 1 }), variant({ ordinal: 3, variant: "candidate", matrixCell: 1 }),
  ])
  expect(pairs.map(pair => pair.label)).toEqual(["cell 0", "cell 1"])
  expect(comparePairs([variant()])).toEqual([])
})

test("rows report relative change of medians", () => {
  const rows = compareRows(
    variant({ elapsedMs: { min: 0, median: 100, max: 0 }, logicalReads: { min: 0, median: 50, max: 0 } }),
    variant({ elapsedMs: { min: 0, median: 25, max: 0 }, logicalReads: null }),
  )
  expect(rows.find(row => row.metric === "Elapsed (median ms)")).toEqual({ metric: "Elapsed (median ms)", baseline: 100, candidate: 25, delta: -0.75 })
  expect(rows.find(row => row.metric === "Logical reads (median)")?.delta).toBeNull()
})
