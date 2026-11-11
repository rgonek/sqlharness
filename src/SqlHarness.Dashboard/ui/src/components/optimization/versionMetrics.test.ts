import { test, expect } from "vitest"
import { versionMetrics } from "./versionMetrics"
import type { OptimizationVersion } from "@/api/types"

test("ambiguous varying fingerprints cannot become a paired timing total", () => {
  const version: OptimizationVersion = { version: "v0", hash: "hash", logicalReadsSum: 2, logicalReadsMean: 1, scansSum: 2, elapsedMedianMs: 20, caseOutcome: "measured", measuredIn: [], latestOperationId: 1, verdictBasis: "whole batch", metricCases: [
    { caseKey: "a", caseId: "a", label: "A", varyingFingerprint: "duplicate", logicalReads: 1, scans: 1, elapsedMs: 12, cpuMs: 2 },
    { caseKey: "b", caseId: "b", label: "B", varyingFingerprint: "duplicate", logicalReads: 1, scans: 1, elapsedMs: 28, cpuMs: 4 },
  ] }
  expect(versionMetrics(version, "all", "sum", ["duplicate"], true).elapsed).toBeNull()
})
