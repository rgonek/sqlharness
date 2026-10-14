import { expect, test } from "vitest"
import { variant } from "@/test/fixtures"
import { comparePairs, compareRows, equivalenceLabel, formatChange, regressionLabel, variantLabel } from "./compare"

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
    "sqlserver",
  )
  expect(rows.find(row => row.metric === "Elapsed (median ms)")).toEqual({ metric: "Elapsed (median ms)", baseline: 100, candidate: 25, delta: -0.75 })
  expect(rows.find(row => row.metric === "Logical reads (median)")?.delta).toBeNull()
})

test("change shows a sign only when the rounded percent is not zero", () => {
  expect(formatChange(null)).toBe("—")
  expect(formatChange(0.5)).toBe("+50%")
  expect(formatChange(-0.5)).toBe("-50%")
  expect(formatChange(0.001)).toBe("0%")
  expect(formatChange(0)).toBe("0%")
})

test("verdict labels keep execution regression and equivalence independent", () => {
  expect(regressionLabel({ verdict: "fail" })).toBe("Regression detected")
  expect(regressionLabel(null)).toBe("No assessment")
  expect(equivalenceLabel({ mode: "Off", equivalent: null })).toBe("Equivalence check off")
  expect(equivalenceLabel({ mode: "Ordered", equivalent: false })).toBe("Results differ")
  expect(equivalenceLabel({ mode: "Ordered", equivalent: null })).toBe("Equivalence check incomplete")
  expect(equivalenceLabel(null)).toBe("Equivalence unavailable (legacy record)")
})

test("metric rows use only metrics for the recorded engine and never divide by zero", () => {
  const baseline = variant({ elapsedMs: { min: 1, median: 0, max: 2 }, logicalReads: null, postgres: null })
  const candidate = variant({ elapsedMs: { min: 1, median: 2, max: 3 }, logicalReads: null, postgres: null })
  const sqlServer = compareRows(baseline, candidate, "sqlserver")
  expect(sqlServer.find(row => row.metric === "Elapsed (median ms)")?.delta).toBeNull()
  expect(sqlServer.some(row => row.metric.includes("buffers"))).toBe(false)
  const postgres = compareRows(baseline, candidate, "postgres")
  expect(postgres.some(row => row.metric.startsWith("Logical reads"))).toBe(false)
  expect(postgres.some(row => row.metric.startsWith("Shared hits"))).toBe(true)
  expect(compareRows(baseline, candidate, null).map(row => row.metric)).toEqual(["Elapsed (median ms)", "CPU (median ms)"])
})

test("regression label names the policy version only for a positive integer", () => {
  expect(regressionLabel({ verdict: "fail", policyVersion: 2 })).toBe("Regression detected (policy v2)")
  expect(regressionLabel({ verdict: "inconclusive", policyVersion: 1 })).toBe("Inconclusive (policy v1)")
  expect(regressionLabel({ verdict: "pass" })).toBe("No regression detected")
  expect(regressionLabel({ verdict: "pass", policyVersion: "2" })).toBe("No regression detected")
  expect(regressionLabel({ policyVersion: 2 })).toBe("No assessment")
})
