import { describe, expect, test } from "vitest"
import { stubFetch } from "@/test/render"
import { getJson, NotFoundError, UnauthorizedError, withQuery } from "./client"

describe("client", () => {
  test("withQuery skips empty values", () => {
    expect(withQuery("/api/sessions", { agent: "claude", cursor: undefined, limit: 50, transport: "" }))
      .toBe("/api/sessions?agent=claude&limit=50")
    expect(withQuery("/api/stats")).toBe("/api/stats")
  })

  test("maps 401 and 404 to typed errors", async () => {
    stubFetch({
      "/api/sessions": () => new Response("no", { status: 401 }),
      "/api/operations/9": () => new Response("no", { status: 404 }),
    })
    await expect(getJson("/api/sessions")).rejects.toBeInstanceOf(UnauthorizedError)
    await expect(getJson("/api/operations/9")).rejects.toBeInstanceOf(NotFoundError)
  })

  test("returns parsed json", async () => {
    stubFetch({ "/api/stats": { tokens: { raw: 1, emitted: 1 } } })
    expect(await getJson("/api/stats")).toEqual({ tokens: { raw: 1, emitted: 1 } })
  })
})
