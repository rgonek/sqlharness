import { expect, test } from "vitest"
import { operation } from "@/test/fixtures"
import { badgeTier, operationFlags, statusVariant } from "./flags"

test("badge tiers separate outcomes, warnings and informational labels", () => {
  for (const status of ["succeeded", "failed", "rejected"] as const) expect(badgeTier(status)).toBe("outcome")
  for (const kind of ["regression", "results-differ", "mixed"] as const) expect(badgeTier(kind)).toBe("warning")
  for (const kind of ["spill", "cold", "grant"] as const) expect(badgeTier(kind)).toBe("info")
})

test("flags list the notable conditions in a stable order", () => {
  const flags = operationFlags(operation({ mutationRequested: true, hasSpill: true, coldCache: true, overGranted: true }))
  expect(flags.map(flag => flag.key)).toEqual(["mutation", "spill", "cold", "grant"])
  expect(operationFlags(operation())).toEqual([])
})

test("status variants", () => {
  expect(statusVariant("succeeded")).toBe("secondary")
  expect(statusVariant("running")).toBe("default")
  expect(statusVariant("failed")).toBe("destructive")
  expect(statusVariant("rejected")).toBe("destructive")
  expect(statusVariant("abandoned")).toBe("outline")
})
