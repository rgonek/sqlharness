import { expect, test } from "vitest"
import { stats } from "@/test/fixtures"
import { failureRate, pivotPerDay } from "./stats"

test("pivots per-day counts into one row per day with a column per agent", () => {
  const { rows, agents } = pivotPerDay(stats().operationsPerDay)
  expect(agents).toEqual(["claude", "codex"])
  expect(rows).toEqual([
    { day: "2026-10-06", claude: 2, codex: 0 },
    { day: "2026-10-07", claude: 3, codex: 1 },
  ])
})

test("failure rate includes rejected operations and has no zero-cohort percentage", () => {
  expect(failureRate([{ key: "failed", count: 1 }, { key: "rejected", count: 2 }], 4)).toBe(75)
  expect(failureRate([], 0)).toBeNull()
  expect(failureRate([], 2)).toBe(0)
})
