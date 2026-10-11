import { expect, test } from "vitest"
import { NetworkError, UnauthorizedError } from "@/api/client"
import { retryDelay, shouldRetry } from "@/api/queries"

test("network errors retry without limit, others as before", () => {
  expect(shouldRetry(10, new NetworkError("down"))).toBe(true)
  expect(shouldRetry(0, new UnauthorizedError())).toBe(false)
  expect(shouldRetry(1, new Error("500"))).toBe(true)
  expect(shouldRetry(2, new Error("500"))).toBe(false)
})

test("retry delay grows and caps at 30 s", () => {
  expect(retryDelay(0)).toBe(1000)
  expect(retryDelay(3)).toBe(8000)
  expect(retryDelay(10)).toBe(30000)
})
