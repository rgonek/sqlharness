import { expect, test } from "vitest"
import type { OptimizationComparison } from "@/api/types"
import { optimizationMarkdown } from "./optimizationMarkdown"

const comparison: OptimizationComparison = {
  a: "v0", b: "v3", basis: "MeasuredTogether", operationId: 214,
  equivalenceLabel: "results equivalent (direct)", cases: [{ id: "case-1", label: "@CustomerId = 9001", outcome: "worse" }],
  selectedCase: "all", aggregate: "sum", coverageLabel: "", mixed: { better: 2, worse: 1, worstCaseLabel: "@CustomerId = 9001", worstChangePercent: 35 },
  tables: [{ table: "dbo.Orders", scansBefore: 4, scansAfter: 2, readsBefore: 100, readsAfter: 135, change: "+35%" }],
  collapsed: { count: 2, label: "2 smaller tables" }, elapsed: { beforeMs: 10, afterMs: 11, policyVerdict: null, differentRun: false },
  view: "whole", verdictBasis: "whole batch",
}

test("Markdown matches the selected comparison rows and labels", () => {
  expect(optimizationMarkdown(comparison, "Orders", "whole query")).toBe([
    "# Orders — whole query — v0 → v3",
    "Case: all cases (Sum)",
    "Basis: measured together in #214",
    "View: Whole batch",
    "basis: whole batch",
    "Equivalence: results equivalent (direct)",
    "",
    "Mixed: 2 better, 1 worse — @CustomerId = 9001: logical reads +35%",
    "",
    "| Table | Scans before | Scans after | Logical reads before | Logical reads after | Change |",
    "| --- | ---: | ---: | ---: | ---: | --- |",
    "| dbo.Orders | 4 | 2 | 100 | 135 | +35% |",
    "| 2 smaller tables |",
  ].join("\n"))
})

test("Markdown labels different-run comparisons and unavailable table IO", () => {
  expect(optimizationMarkdown({ ...comparison, basis: "DifferentRunsSameConditions", operationId: null, tableIoUnavailable: true, tables: [], collapsed: null }, "Orders", "whole query"))
    .toContain("Basis: different runs, same conditions")
  expect(optimizationMarkdown({ ...comparison, tableIoUnavailable: true, tables: [], collapsed: null }, "Orders", "whole query"))
    .toContain("per-table IO not available for this engine")
  const conditionsDiffer = optimizationMarkdown({ ...comparison, basis: "ConditionsDiffer", operationId: null, tables: [], collapsed: null }, "Orders", "whole query")
  expect(conditionsDiffer).toContain("conditions differ; SQL diff is the available comparison.")
  expect(conditionsDiffer).not.toContain("| Table |")
})

test("measure-only Markdown states the different-run basis and unverified results", () => {
  const markdown = optimizationMarkdown({ ...comparison, basis: "DifferentRunsSameConditions", operationId: null,
    equivalenceLabel: "results not compared", mixed: null, beforeAction: null, afterAction: null }, "Orders", "whole query")

  expect(markdown).toContain("Basis: different runs, same conditions")
  expect(markdown).toContain("Equivalence: results not compared")
})

test("Markdown records the selected statement view, verdict basis, batch headline, hidden work, and omissions", () => {
  const markdown = optimizationMarkdown({ ...comparison,
    view: "statements", verdictBasis: "changed statements",
    wholeBatch: { before: 500, after: 650, shared: 100,
      label: "whole batch: 500 → 650 reads (shared statements: 100)",
      sharedBefore: 100, sharedAfter: 100, changedBefore: 400, changedAfter: 550 },
    omittedStatementsBefore: 1, omittedStatementsAfter: 2,
    statements: [{ key: "q", occurrence: 0, text: "SELECT 1", status: "different-work",
      readsBefore: 20, readsAfter: 40, scansBefore: 1, scansAfter: 2, executions: 3, change: "+100%", attribution: "confirmed",
      hiddenWork: [{ kind: "scalar-udf", name: "dbo.Price", executions: null, sharePercent: 15 }], objects: [],
      planHashBefore: null, planHashAfter: null, planLinkBefore: null, planLinkAfter: null, udfElapsedMilliseconds: 4 }],
  }, "Orders", "whole query")

  expect(markdown).toContain("View: By statement")
  expect(markdown).toContain("basis: changed statements")
  expect(markdown).toContain("whole batch: 500 → 650 reads (shared statements: 100)")
  expect(markdown).toContain("UDF time 15% of statement")
  expect(markdown).toContain("omitted statements: 1 (their reads remain in the batch total)")
  expect(markdown).toContain("omitted statements: 2 (their reads remain in the batch total)")
})
