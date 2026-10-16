import { screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { renderApp, stubFetch } from "@/test/render"

const row = {
  id: 1, requestId: "0123456789abcdef0123456789abcdef", sessionKey: "mcp:session", profile: "payments",
  engine: "postgres", server: "db.example", database: "payments", tier: "dml", mode: "yolo",
  digest: "a".repeat(64), sql: "<script>delete from payments</script>", parametersJson: null,
  state: "succeeded", status: "success", verification: null, createdAt: "2026-10-16T10:00:00Z",
  decidedAt: null, finishedAt: "2026-10-16T10:00:01Z", environment: "production",
}

test("history shows the stored snapshot, omitted SQL state, and warns for an unasked production YOLO execution", async () => {
  stubFetch({ "/api/mutations?limit=50": { items: [row, { ...row, id: 2, sql: null, mode: "operator", environment: "test", verification: "click" }], nextCursor: null }, "/api/profiles": { status: "valid", profiles: [], message: null } })
  renderApp("/mutations")

  expect(await screen.findAllByRole("cell", { name: "payments" })).toHaveLength(4)
  expect(screen.getAllByText("dml")).toHaveLength(2)
  expect(screen.getByText("yolo")).toBeInTheDocument()
  expect(screen.getAllByText("success")).toHaveLength(2)
  expect(screen.getByText("production")).toBeInTheDocument()
  expect(screen.getByText("Not stored")).toBeInTheDocument()
  expect(screen.getByText("ran without approval on production")).toBeInTheDocument()
  expect(screen.getByText(row.sql).tagName).toBe("PRE")
  expect(document.querySelector("script")).toBeNull()
})

test("history requests the next snapshot page with the returned cursor", async () => {
  const calls = stubFetch({
    "/api/mutations?limit=50": { items: [{ ...row, sql: null, mode: "operator", environment: "test", verification: "click" }], nextCursor: 9 },
    "/api/mutations?cursor=9&limit=50": { items: [{ ...row, id: 8, profile: "reporting", sql: null, mode: "never" }], nextCursor: null },
    "/api/profiles": { status: "valid", profiles: [], message: null },
  })
  renderApp("/mutations")
  expect(await screen.findAllByRole("cell", { name: "payments" })).toHaveLength(2)
  await userEvent.click(screen.getByRole("button", { name: "Load more" }))
  expect(await screen.findByRole("cell", { name: "reporting" })).toBeInTheDocument()
  expect(calls).toContain("/api/mutations?cursor=9&limit=50")
})
