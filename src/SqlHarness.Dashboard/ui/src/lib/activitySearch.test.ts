import { expect, test } from "vitest"
import { readActivityExpansion, rememberActivityExpansion, validateActivitySearch } from "./activitySearch"

test("Activity bounds facet arrays and ignores unknown or malformed search values", () => {
  expect(validateActivitySearch({ text: "x".repeat(400), status: ["failed", "fake", "failed"], time: "forever", agent: Array.from({ length: 100 }, (_, i) => "a" + i), named: {}, secrets: "not retained" }))
    .toEqual({ text: "x".repeat(256), status: ["failed"], agent: Array.from({ length: 20 }, (_, i) => "a" + i) })
})

test("Activity keeps the most recent expansion change even for an older session id", () => {
  const older = Object.fromEntries(Array.from({ length: 200 }, (_, i) => [i + 2, true]))
  rememberActivityExpansion(older)
  rememberActivityExpansion({ ...older, 1: false })
  expect(readActivityExpansion()[1]).toBe(false)
  expect(Object.keys(readActivityExpansion())).toHaveLength(200)
})
