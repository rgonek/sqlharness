import { describe, expect, test } from "vitest"
import { stubFetch } from "@/test/render"
import { ConflictError, FieldErrorsError, getJson, HttpStatusError, NotFoundError, putJson, updateProfile, UnauthorizedError, withQuery } from "./client"

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
    stubFetch({ "/api/stats": { workflowCosts: [] } })
    expect(await getJson("/api/stats")).toEqual({ workflowCosts: [] })
  })

  test("preserves bounded plan API status and error codes", async () => {
    stubFetch({
      "/api/plan-documents/abc/graph": () => new Response(JSON.stringify({ error: { code: "plans_not_stored", message: "Sensitive plan storage is disabled." } }), { status: 409 }),
      "/api/plan-documents/def/graph": () => new Response(JSON.stringify({ error: { code: "graph_response_size_limit_exceeded", message: "The complete graph exceeds the response limit." } }), { status: 413 }),
      "/api/plan-documents/ghi/graph": () => new Response(JSON.stringify({ error: { code: "invalid_plan_graph", message: "The stored graph is invalid." } }), { status: 422 }),
    })
    for (const [sha, status, code] of [["abc", 409, "plans_not_stored"], ["def", 413, "graph_response_size_limit_exceeded"], ["ghi", 422, "invalid_plan_graph"]] as const) {
      const error = await getJson(`/api/plan-documents/${sha}/graph`).catch(value => value)
      expect(error).toBeInstanceOf(HttpStatusError)
      expect(error).toMatchObject({ status, code })
    }
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

test("updateProfile sends a guarded revision and editable profile", async () => {
  let seen: RequestInit | undefined
  globalThis.fetch = (async (_: RequestInfo | URL, init?: RequestInit) => { seen = init; return new Response(JSON.stringify({ status: "valid" })) }) as typeof fetch
  await updateProfile({ name: "pg", expectedRevision: "rev", profile: { server: "host", database: "db", vars: {}, auth: "sql" } })
  expect(seen?.method).toBe("PUT")
  expect(new Headers(seen?.headers).get("X-SqlHarness-Dashboard")).toBe("1")
  expect(JSON.parse(seen!.body as string)).toMatchObject({ name: "pg", expectedRevision: "rev", profile: { server: "host" } })
})
