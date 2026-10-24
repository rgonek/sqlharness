import { expect, test } from "vitest"
import type { ComparePair } from "@/lib/compare"
import type { DistilledPlan } from "@/api/types"
import { variant } from "@/test/fixtures"
import { planChanges } from "@/lib/planChanges"
import { buildPrMarkdown } from "./prMarkdown"

const pair: ComparePair = {
  label: "compare",
  baseline: variant({
    variant: "baseline", runs: 5, elapsedMs: { min: 110, median: 120, max: 130 },
    logicalReads: { min: 1000, median: 1000, max: 1000 },
    tableIo: [{ ...variant().tableIo[0], table: "Orders", logicalReads: 1000, totalLogicalReads: 1000 }],
  }),
  candidate: variant({
    variant: "candidate", runs: 5, elapsedMs: { min: 20, median: 30, max: 40 },
    logicalReads: { min: 100, median: 100, max: 100 },
    tableIo: [{ ...variant().tableIo[0], table: "Orders", logicalReads: 100, totalLogicalReads: 100 }],
  }),
}

const before: DistilledPlan = { statements: [{ root: { physicalOp: "Clustered Index Scan", objectName: "dbo.Orders", indexName: "PK_Orders" }, missingIndexes: [] }] }
const after: DistilledPlan = { statements: [{ root: { physicalOp: "Index Seek", objectName: "dbo.Orders", indexName: "PK_Orders" }, missingIndexes: [] }] }

test("builds readable PR Markdown with compare metrics, per-table IO, plans, and bounded SQL diff", () => {
  const markdown = buildPrMarkdown({
    subject: "orders-report", scope: "whole", baselineVersion: "v0", candidateVersion: "v3",
    engine: "sqlserver", status: "succeeded", regression: { verdict: "pass" },
    equivalence: { mode: "Ordered", equivalent: true }, isMatrix: false,
    cases: [{ label: "Compare", pair, regression: { verdict: "pass" }, equivalence: { mode: "Ordered", equivalent: true } }],
    plans: new Map([["compare", [{ ordinal: 0, changes: planChanges(before, after) }]]]),
    sqlDiff: "@@ -1,1 +1,1 @@\n-SELECT 1\n+SELECT 2",
  })

  expect(markdown.startsWith("### SQL compare: orders-report / whole (v0 → v3)")).toBe(true)
  expect(markdown).toContain("Engine: SQL Server")
  expect(markdown).toContain("Execution: succeeded")
  expect(markdown).toContain("Regression: No regression detected")
  expect(markdown).toContain("Equivalence: Results equivalent")
  expect(markdown).toContain("Runs: 5")
  expect(markdown).toContain("| Metric | Baseline | Candidate | Change |")
  expect(markdown).toContain("| Logical reads | 1,000 | 100 | -90% |")
  expect(markdown).toContain("| Table | Logical (baseline) | Logical (candidate) | LOB logical (baseline) | LOB logical (candidate) | Total (baseline) | Total (candidate) | Scans (baseline) | Scans (candidate) | Change |")
  expect(markdown).toContain("Scans (baseline)")
  expect(markdown).toContain("| Orders | 1,000 | 100 | 0 | 0 | 1,000 | 100 | 1 | 1 | -90% |")
  expect(markdown).toContain("median of run totals (data + LOB)")
  expect(markdown).toContain("Equivalence: Results equivalent")
  expect(markdown).toContain("**Plan changes**")
  expect(markdown).toContain("dbo.Orders.PK_Orders: Clustered Index Scan → Index Seek")
  expect(markdown).toContain("```diff\n@@ -1,1 +1,1 @@\n-SELECT 1\n+SELECT 2\n```")
})

test("preserves exact saved IO identities and reports data, LOB, total reads, and scans", () => {
  const tableVariable = (table: string) => ({ ...variant().tableIo[0], table, logicalReads: 10, lobLogicalReads: 4, totalLogicalReads: 14 })
  const markdown = buildPrMarkdown({
    subject: "orders", scope: "whole", baselineVersion: "v0", candidateVersion: "v1", engine: "sqlserver", status: "succeeded",
    regression: null, equivalence: { mode: "Ordered", equivalent: true }, isMatrix: false,
    cases: [{ label: "Compare", pair: { ...pair,
      baseline: { ...pair.baseline, tableIo: [tableVariable("#12345678"), tableVariable("#ABCDEF12")] },
      candidate: { ...pair.candidate, tableIo: [tableVariable("#87654321")] },
    }, regression: null, equivalence: { mode: "Ordered", equivalent: true } }], plans: new Map(), sqlDiff: null,
  })
  expect(markdown).toContain("| Table | Logical (baseline) | Logical (candidate) | LOB logical (baseline) | LOB logical (candidate) | Total (baseline) | Total (candidate) | Scans (baseline) | Scans (candidate) | Change |")
  expect(markdown).toContain("| #12345678 | 10 | — | 4 | — | 14 | — | 1 | — | — |")
  expect(markdown).toContain("| #ABCDEF12 | 10 | — | 4 | — | 14 | — | 1 | — | — |")
  expect(markdown).toContain("| #87654321 | — | 10 | — | 4 | — | 14 | — | 1 | — |")
  expect(markdown).toContain("| Total | 20 | 10 | 8 | 4 | 28 | 14 | 2 | 1 | -50% |")
})

test("includes plan deltas for every changed statement ordinal", () => {
  const beforeFirst: DistilledPlan = { statements: [{ root: { physicalOp: "Index Scan", objectName: "dbo.First" } }] }
  const afterFirst: DistilledPlan = { statements: [{ root: { physicalOp: "Index Seek", objectName: "dbo.First" } }] }
  const beforeSecond: DistilledPlan = { statements: [{ root: { physicalOp: "Table Scan", objectName: "dbo.Second" }, missingIndexes: [] }] }
  const afterSecond: DistilledPlan = { statements: [{ root: { physicalOp: "Index Seek", objectName: "dbo.Second" }, missingIndexes: [] }] }
  const markdown = buildPrMarkdown({
    subject: "batch", scope: "whole", baselineVersion: "v0", candidateVersion: "v1", engine: "sqlserver", status: "succeeded",
    regression: null, equivalence: null, isMatrix: false,
    cases: [{ label: "Compare", pair, regression: null, equivalence: null }],
    plans: new Map([["compare", [
      { ordinal: 1, changes: planChanges(beforeFirst, afterFirst) },
      { ordinal: 2, changes: planChanges(beforeSecond, afterSecond) },
    ]]]),
    sqlDiff: null,
  })
  expect(markdown).toContain("Statement 1\ndbo.First: Index Scan → Index Seek")
  expect(markdown).toContain("Statement 2\ndbo.Second: Table Scan → Index Seek")
})

test("shows every matrix cell's per-table regression and omits SQL diff when unavailable", () => {
  const markdown = buildPrMarkdown({
    subject: "orders", scope: "matrix", baselineVersion: "v0", candidateVersion: "v1",
    engine: "sqlserver", status: "succeeded", regression: null, equivalence: null, isMatrix: true,
    cases: [
      { label: "Cell 0", pair, regression: { verdict: "pass" }, equivalence: { mode: "Ordered", equivalent: true } },
      { label: "Cell 1", pair: { ...pair, label: "cell 1", baseline: { ...pair.baseline, tableIo: [{ ...pair.baseline.tableIo[0], table: "Lines", logicalReads: 100, totalLogicalReads: 100 }] }, candidate: { ...pair.candidate, tableIo: [{ ...pair.candidate.tableIo[0], table: "Lines", logicalReads: 500, totalLogicalReads: 500 }] } }, regression: { verdict: "fail" }, equivalence: { mode: "Ordered", equivalent: true } },
    ],
    plans: new Map(), sqlDiff: null,
  })

  expect(markdown).toContain("#### Cell 0")
  expect(markdown).toContain("#### Cell 1")
  expect(markdown).toContain("| Lines | 100 | 500 | 0 | 0 | 100 | 500 | 1 | 1 | +400% |")
  expect(markdown).toContain("SQL diff not included (SQL text not stored or over the size limit).")
  expect(markdown).not.toContain("```diff")
})

test("escapes table delimiters, inline code, and SQL diff fences from dynamic content", () => {
  const markdown = buildPrMarkdown({
    subject: "report|draft`tag", scope: "whole", baselineVersion: "v0", candidateVersion: "v1",
    engine: "sqlserver", status: "succeeded", regression: null, equivalence: null, isMatrix: false,
    cases: [{ label: "Compare", pair: { ...pair, baseline: { ...pair.baseline, tableIo: [{ ...pair.baseline.tableIo[0], table: "dbo.Orders|archive" }] } }, regression: null, equivalence: null }],
    plans: new Map(), sqlDiff: "@@\n-```danger\n+SELECT 1",
  })

  expect(markdown).toContain("### SQL compare: report\\|draft\\`tag / whole (v0 → v1)")
  expect(markdown).toContain("| dbo.Orders\\|archive |")
  expect(markdown).toContain("````diff\n@@\n-```danger\n+SELECT 1\n````")
  expect(markdown).toContain("Engine: SQL Server")
})
