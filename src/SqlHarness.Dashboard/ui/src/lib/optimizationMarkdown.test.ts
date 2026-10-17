import { expect, test } from "vitest"
import type { OptimizationComparison } from "@/api/types"
import { optimizationMarkdown } from "./optimizationMarkdown"

const comparison: OptimizationComparison = {
  a: "v0", b: "v3", basis: "MeasuredTogether", operationId: 214,
  equivalenceLabel: "results equivalent (direct)", cases: [{ id: "case-1", label: "@CustomerId = 9001", outcome: "worse" }],
  selectedCase: "all", aggregate: "sum", coverageLabel: "", mixed: { better: 2, worse: 1, worstCaseLabel: "@CustomerId = 9001", worstChangePercent: 35 },
  tables: [{ table: "dbo.Orders", scansBefore: 4, scansAfter: 2, readsBefore: 100, readsAfter: 135, change: "+35%" }],
  collapsed: { count: 2, label: "2 smaller tables" }, elapsed: { beforeMs: 10, afterMs: 11, policyVerdict: null, differentRun: false },
}

test("Markdown matches the selected comparison rows and labels", () => {
  expect(optimizationMarkdown(comparison, "Orders", "whole query")).toBe([
    "# Orders — whole query — v0 → v3",
    "Case: all cases (Sum)",
    "Basis: measured together in #214",
    "Equivalence: results equivalent (direct)",
    "",
    "Mixed: 2 better, 1 worse — @CustomerId = 9001: logical reads +35%",
    "",
    "| Table | Scans before | Scans after | Logical reads before | Logical reads after | Change |",
    "| --- | ---: | ---: | ---: | ---: | --- |",
    "| dbo.Orders | 4 | 2 | 100 | 135 | +35% |",
    "| 2 smaller tables | — | — | — | — | collapsed |",
  ].join("\n"))
})

test("Markdown labels different-run comparisons and unavailable table IO", () => {
  expect(optimizationMarkdown({ ...comparison, basis: "DifferentRunsSameConditions", operationId: null, tableIoUnavailable: true, tables: [], collapsed: null }, "Orders", "whole query"))
    .toContain("Basis: different runs, same conditions")
  expect(optimizationMarkdown({ ...comparison, tableIoUnavailable: true, tables: [], collapsed: null }, "Orders", "whole query"))
    .toContain("per-table IO not available for this engine")
  const conditionsDiffer = optimizationMarkdown({ ...comparison, basis: "ConditionsDiffer", operationId: null, tables: [], collapsed: null }, "Orders", "whole query")
  expect(conditionsDiffer).toContain("conditions differ; SQL diff is the available comparison")
  expect(conditionsDiffer).not.toContain("| Table |")
})
