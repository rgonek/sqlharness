import { expect, test } from "vitest"
import { stubFetch } from "./render"

test("rejects an unexpected request with its method and path", async () => {
  stubFetch({})

  await expect(fetch("/api/unexpected?cursor=501", { method: "POST" }))
    .rejects.toThrow("Unexpected dashboard test request: POST /api/unexpected?cursor=501")
})

test("does not retain a previous test's request handlers", async () => {
  await expect(fetch("/api/profiles"))
    .rejects.toThrow("Unexpected dashboard test request: GET /api/profiles")
})
