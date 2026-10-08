import { expect, test } from "vitest"
import { describeError } from "@/lib/errors"

test("known kinds have fixed sentences", () => {
  expect(describeError(2, "safety_rejected")).toMatch(/rejected the request before running it/)
  expect(describeError(5, "sql_execution_failed")).toMatch(/database returned an error/)
  expect(describeError(-1, "cancelled")).toMatch(/cancelled/)
})

test("unknown kinds fall back to the exit code, then to a generic sentence", () => {
  expect(describeError(3, "something_new")).toBe(describeError(3, "authentication_failed"))
  expect(describeError(99, null)).toBe("The operation failed.")
})
