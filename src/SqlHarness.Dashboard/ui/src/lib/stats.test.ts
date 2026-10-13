import { expect, test } from "vitest"
import { stats } from "@/test/fixtures"
import { pivotPerDay } from "./stats"

test("pivots per-day counts into one row per day with a column per agent", () => {
  const { rows, agents } = pivotPerDay(stats().operationsPerDay)
  expect(agents).toEqual(["claude", "codex"])
  expect(rows).toEqual([
    { day: "2026-10-06", claude: 2, codex: 0 },
    { day: "2026-10-07", claude: 3, codex: 1 },
  ])
})
