import { describe, expect, test } from "vitest"
import { operation, session } from "@/test/fixtures"
import { isActiveSession, mergeOperation, mergeSession } from "./liveStore"

describe("liveStore", () => {
  test("Merging_is_idempotent_and_newer_updates_win", () => {
    const running = operation({ id: 5, status: "running", updatedAt: "2026-10-07T09:00:01.000Z" })
    const done = operation({ id: 5, status: "succeeded", updatedAt: "2026-10-07T09:00:02.000Z" })

    let list = mergeOperation([], running)
    list = mergeOperation(list, done)
    list = mergeOperation(list, running)
    list = mergeOperation(list, done)

    expect(list).toHaveLength(1)
    expect(list[0].status).toBe("succeeded")
  })

  test("abandoned is accepted even with an unchanged timestamp", () => {
    const running = operation({ id: 5, status: "running" })
    const abandoned = { ...running, status: "abandoned" as const }
    expect(mergeOperation([running], abandoned)[0].status).toBe("abandoned")
  })

  test("operations are newest first and capped", () => {
    let list = mergeOperation([], operation({ id: 1 }))
    list = mergeOperation(list, operation({ id: 3 }))
    list = mergeOperation(list, operation({ id: 2 }), 2)
    expect(list.map(o => o.id)).toEqual([3, 2])
  })

  test("sessions merge by id with the latest lastSeen", () => {
    const old = session({ id: 1, lastSeen: "2026-10-07T09:00:00.000Z" })
    const fresh = session({ id: 1, lastSeen: "2026-10-07T09:10:00.000Z", running: 1 })
    expect(mergeSession(mergeSession([fresh], old), fresh)).toEqual([fresh])
  })

  test("active sessions are running or recently seen", () => {
    const now = Date.parse("2026-10-07T10:00:00Z")
    expect(isActiveSession(session({ running: 1, lastSeen: "2026-10-01T00:00:00Z" }), now)).toBe(true)
    expect(isActiveSession(session({ lastSeen: "2026-10-07T09:50:00Z" }), now)).toBe(true)
    expect(isActiveSession(session({ lastSeen: "2026-10-07T09:40:00Z" }), now)).toBe(false)
  })
})
