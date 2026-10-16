import { expect, test } from "vitest"
import { mutationStore, type PendingMutation } from "./mutationStore"

const pending = (requestId: string): PendingMutation => ({
  requestId,
  request: { profile: "db", server: "server", database: "database", engine: "postgres", tier: "dml", digest: "same-digest", grantees: [], sql: "update x", parameters: [] },
  environment: "dev", writerSameAsReader: false, expiresAt: new Date(Date.now() + 60_000).toISOString(),
})

test("a digest join keeps the original visible approval request", () => {
  mutationStore.reset()
  const original = pending("r-1")
  mutationStore.synchronize([original])
  mutationStore.add(pending("r-2"))
  expect(mutationStore.getSnapshot().pending).toEqual([original])
  mutationStore.remove("r-1", "denied")
  mutationStore.add(pending("r-2"))
  expect(mutationStore.getSnapshot().pending).toEqual([pending("r-2")])
  mutationStore.reset()
})
