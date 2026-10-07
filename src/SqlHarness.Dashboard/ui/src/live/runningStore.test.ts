import { describe, expect, test } from "vitest"
import { operation } from "@/test/fixtures"
import { pushRunning, reconcileRunning, runningOnly } from "./liveStore"

const at = (second: number) => `2026-10-07T09:00:${String(second).padStart(2, "0")}.000Z`

describe("running list", () => {
  test("pushed running rows are added and are not capped", () => {
    let tracked = pushRunning([], operation({ id: 1, status: "running" }))
    for (let id = 2; id <= 300; id++) tracked = pushRunning(tracked, operation({ id, status: "running" }))
    expect(runningOnly(tracked)).toHaveLength(300)
  })

  test("a pushed finish removes the row and a stale running event cannot bring it back", () => {
    const running = operation({ id: 5, status: "running", updatedAt: at(1) })
    let tracked = pushRunning([], running)
    tracked = pushRunning(tracked, { ...running, status: "succeeded", updatedAt: at(2) })
    tracked = pushRunning(tracked, running)
    expect(runningOnly(tracked)).toEqual([])
  })

  test("a refetch is authoritative for rows that were running before it started", () => {
    const before = [operation({ id: 5, status: "running", updatedAt: at(1) })]
    const tracked = reconcileRunning(before, before, [])
    expect(runningOnly(tracked)).toEqual([])
  })

  test("a refetch keeps rows pushed while it was in flight", () => {
    const pushed = operation({ id: 9, status: "running", updatedAt: at(3) })
    const tracked = reconcileRunning([], [pushed], [])
    expect(runningOnly(tracked)).toEqual([pushed])
  })

  test("a stale refetch does not undo a newer or equal pushed finish", () => {
    const stale = operation({ id: 5, status: "running", updatedAt: at(1) })
    const finished = pushRunning([], { ...stale, status: "failed", updatedAt: at(2) })
    expect(runningOnly(reconcileRunning([], finished, [stale]))).toEqual([])

    const abandoned = pushRunning([], { ...stale, status: "abandoned" })
    expect(runningOnly(reconcileRunning([], abandoned, [stale]))).toEqual([])
  })

  test("a refetch adds running rows the cache did not know and updates known ones", () => {
    const known = operation({ id: 4, status: "running", updatedAt: at(1), progress: { polls: 1, changedPolls: 0, elapsedMs: 1 } })
    const newer = { ...known, updatedAt: at(4), progress: { polls: 9, changedPolls: 0, elapsedMs: 9 } }
    const fresh = operation({ id: 8, status: "running" })
    const tracked = reconcileRunning([known], [known], [fresh, newer])
    expect(runningOnly(tracked).map(o => [o.id, o.progress?.polls ?? null])).toEqual([[8, null], [4, 9]])
  })
})
