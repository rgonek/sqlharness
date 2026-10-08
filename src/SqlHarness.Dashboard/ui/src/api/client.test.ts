import { describe, expect, test } from "vitest"
import { stubFetch } from "@/test/render"
import { ConflictError, FieldErrorsError, getJson, NotFoundError, putJson, UnauthorizedError, withQuery } from "./client"

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

  test("putJson sends the write header and JSON, and maps 400 and 409", async () => {
    let seen: RequestInit | undefined
    globalThis.fetch = (async (_: RequestInfo | URL, init?: RequestInit) => {
      seen = init
      return new Response(JSON.stringify({ errors: [{ field: "journal.retention.maxAgeDays", message: "Must be between 1 and 3650." }] }), { status: 400 })
    }) as typeof fetch
    const error = await putJson("/api/settings", { a: 1 }).catch(e => e)
    expect(error).toBeInstanceOf(FieldErrorsError)
    expect((error as FieldErrorsError).errors[0].field).toBe("journal.retention.maxAgeDays")
    expect(seen?.method).toBe("PUT")
    expect(new Headers(seen?.headers).get("X-SqlHarness-Dashboard")).toBe("1")
    expect(new Headers(seen?.headers).get("Content-Type")).toBe("application/json")

    globalThis.fetch = (async () => new Response(JSON.stringify({ error: "invalid" }), { status: 409 })) as typeof fetch
    expect(await putJson("/api/settings", {}).catch(e => e)).toBeInstanceOf(ConflictError)
  })
})
