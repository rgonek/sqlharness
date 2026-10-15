import { afterEach, expect, test } from "vitest"
import {
  dialectOf,
  diffTooLarge,
  normalizeForDiff,
  readPreference,
  writePreference,
} from "./sqlDiff"

afterEach(() => localStorage.clear())

test("enforces combined SQL byte and line limits", () => {
  expect(diffTooLarge("a", "b")).toBe(false)
  expect(diffTooLarge("x".repeat(200 * 1024), "y".repeat(57 * 1024))).toBe(true)
  expect(diffTooLarge("a\n".repeat(2000), "b\n".repeat(2001))).toBe(true)
})

test("chooses PostgreSQL only for the postgres engine", () => {
  expect(dialectOf("postgres")).toBe("postgres")
  expect(dialectOf("sqlserver")).toBe("tsql")
  expect(dialectOf(null)).toBe("tsql")
})

test("normalizes line endings and formats with the selected SQL dialect", () => {
  expect(normalizeForDiff("SELECT 1\r\nFROM t", "sqlserver", false)).toEqual({ text: "SELECT 1\nFROM t", formatFailed: false })
  expect(normalizeForDiff("select a,b from t where x=1", "sqlserver", true).formatFailed).toBe(false)
  expect(normalizeForDiff("select a,b from t where x=1", "sqlserver", true).text).toContain("\n")
})

test("preserves raw normalized SQL when formatting fails", () => {
  expect(normalizeForDiff("SELECT 'abc", "sqlserver", true)).toEqual({ text: "SELECT 'abc", formatFailed: true })
})

test("remembers only supported SQL diff preferences", () => {
  writePreference("sqlharness.sqlDiff.mode", "unified")
  expect(readPreference("sqlharness.sqlDiff.mode", ["side-by-side", "unified"] as const, "side-by-side")).toBe("unified")
  writePreference("sqlharness.sqlDiff.mode", "weird")
  expect(readPreference("sqlharness.sqlDiff.mode", ["side-by-side", "unified"] as const, "side-by-side")).toBe("side-by-side")
})
