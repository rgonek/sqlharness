import { expect, test } from "vitest"
import type { OptimizationComparison } from "@/api/types"
import { operationDetail, variant } from "@/test/fixtures"
import { aggregate, selectedVariants, tableExtras } from "./ioComparison"

const comparison: OptimizationComparison = {
  a: "v0", b: "v1", basis: "MeasuredTogether", operationId: 10, equivalenceLabel: "equivalent",
  cases: [{ id: "fingerprint-1", label: "first", outcome: "same" }, { id: "fingerprint-2", label: "second", outcome: "same" }],
  selectedCase: "all", aggregate: "sum", coverageLabel: "", mixed: null, tables: [], collapsed: null,
  elapsed: { beforeMs: null, afterMs: null, differentRun: false, policyVerdict: null },
}

test("IO maps fingerprints separately for each side and excludes unrelated cases", () => {
  const detail = operationDetail({ variants: [variant({ variant: "baseline", caseId: "left-1" }), variant({ variant: "baseline", caseId: "left-2" }), variant({ variant: "baseline", caseId: "other" }), variant({ variant: "candidate", caseId: "left-1" })] })
  const action = { operationId: 10, side: "baseline" as const, caseIds: ["left-1", "left-2", "other"], caseIdsByFingerprint: { "fingerprint-1": "left-1", "fingerprint-2": "left-2" } }
  expect(selectedVariants(detail, action, comparison).map(item => item.caseId)).toEqual(["left-1", "left-2"])
  expect(selectedVariants(detail, action, { ...comparison, selectedCase: "fingerprint-2" }).map(item => item.caseId)).toEqual(["left-2"])
})

test("incomplete case mappings and missing or ambiguous case metrics never produce partial totals", () => {
  const action = { operationId: 10, side: "baseline" as const, caseIds: ["first"], caseIdsByFingerprint: { "fingerprint-1": "first" } }
  const detail = operationDetail({ variants: [variant({ variant: "baseline", caseId: "first" })] })
  expect(selectedVariants(detail, action, comparison)).toEqual([])
  const completeAction = { ...action, caseIdsByFingerprint: { ...action.caseIdsByFingerprint, "fingerprint-2": "second" } }
  expect(selectedVariants(detail, completeAction, comparison)).toEqual([])
  expect(selectedVariants({ ...detail, variants: [...detail.variants, ...detail.variants] }, action, { ...comparison, selectedCase: "fingerprint-1" })).toEqual([])
})

test("additional IO preserves unknown counters, real zeroes and sum versus mean", () => {
  const row = variant().tableIo[0]!
  const first = variant({ tableIo: [{ ...row, table: "Orders", physicalReads: 0, readAheadReads: null }] })
  const second = variant({ tableIo: [{ ...row, table: "Orders", physicalReads: 10, readAheadReads: 20 }] })
  expect(tableExtras([first, second], false).Orders).toMatchObject({ physicalReads: 10, readAheadReads: null })
  expect(tableExtras([first, second], true).Orders?.physicalReads).toBe(5)
  expect(tableExtras([first, variant({ tableIo: [] })], false).Orders?.physicalReads).toBeNull()
  expect(tableExtras([first, variant({ tableIo: [{ ...row, table: "Other", physicalReads: 3 }] })], false).Orders?.physicalReads).toBeNull()
  expect(aggregate([], false)).toBeNull()
  expect(aggregate([0, 0], true)).toBe(0)
})
