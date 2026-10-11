import { expect, test } from "vitest"
import { createSaveQueue } from "@/lib/saveQueue"

test("Queued_saves_apply_in_order", async () => {
  const applied: number[] = []
  const queue = createSaveQueue<number>(async value => {
    await new Promise(r => setTimeout(r, value === 1 ? 30 : 0))
    applied.push(value)
  })
  await Promise.all([queue.enqueue(1), queue.enqueue(2)])
  expect(applied).toEqual([1, 2])
})

test("a failed save does not block later saves", async () => {
  const applied: number[] = []
  const queue = createSaveQueue<number>(async value => { if (value === 1) throw new Error("x"); applied.push(value) })
  await expect(queue.enqueue(1)).rejects.toThrow("x")
  await queue.enqueue(2)
  expect(applied).toEqual([2])
})
