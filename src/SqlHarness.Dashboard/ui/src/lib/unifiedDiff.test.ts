import { expect, test } from "vitest"
import { unifiedDiffText } from "./unifiedDiff"

test("formats a contextual unified diff", () => {
  expect(unifiedDiffText("SELECT a\nFROM t\nWHERE x = 1\nORDER BY a", "SELECT a\nFROM t\nWHERE x = 2\nORDER BY a", 1)).toBe(
    ["@@ -2,3 +2,3 @@", " FROM t", "-WHERE x = 1", "+WHERE x = 2", " ORDER BY a"].join("\n"),
  )
})

test("returns empty text for equal inputs", () => {
  expect(unifiedDiffText("SELECT 1", "SELECT 1")).toBe("")
})

test("includes the complete short file when context exceeds its length", () => {
  expect(unifiedDiffText("SELECT 1", "SELECT 1\nUNION ALL SELECT 2", 3)).toBe(
    ["@@ -1,1 +1,2 @@", " SELECT 1", "+UNION ALL SELECT 2"].join("\n"),
  )
})

test("represents deleting the final line", () => {
  expect(unifiedDiffText("SELECT 1\nFROM t\n", "SELECT 1\n", 1)).toBe(
    ["@@ -1,2 +1,1 @@", " SELECT 1", "-FROM t"].join("\n"),
  )
})

test("keeps distant changes in separate contextual hunks", () => {
  expect(unifiedDiffText("a\nb\nc\nd\ne\nf\ng", "A\nb\nc\nd\ne\nf\nG", 1)).toBe(
    [
      "@@ -1,2 +1,2 @@",
      "-a",
      "+A",
      " b",
      "@@ -6,2 +6,2 @@",
      " f",
      "-g",
      "+G",
    ].join("\n"),
  )
})

test("rejects oversized inputs before asking the diff package to process them", () => {
  expect(() => unifiedDiffText("x".repeat(200 * 1024), "y".repeat(57 * 1024))).toThrow(RangeError)
  expect(() => unifiedDiffText("a\n".repeat(2000), "b\n".repeat(2001))).toThrow(RangeError)
})
