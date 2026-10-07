import { act, screen, within } from "@testing-library/react"
import { expect, test } from "vitest"
import { operation, session } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

class FakeEventSource {
  static last: FakeEventSource | undefined
  listeners = new Map<string, ((event: MessageEvent<string>) => void)[]>()
  onopen: (() => void) | null = null
  onerror: (() => void) | null = null
  url: string
  constructor(url: string) {
    this.url = url
    FakeEventSource.last = this
  }
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }
  close() {}
  emit(type: string, data: unknown) {
    for (const listener of this.listeners.get(type) ?? []) listener(new MessageEvent(type, { data: JSON.stringify(data) }))
  }
}

test("live page shows running, recent and pushed operations", async () => {
  globalThis.EventSource = FakeEventSource as unknown as typeof EventSource
  stubFetch({
    "/api/operations": { items: [operation({ id: 1, operation: "ping" }), operation({ id: 2, status: "running", operation: "watch", progress: { polls: 4, changedPolls: 1, elapsedMs: 4000 } })], nextCursor: null },
    "/api/sessions": { items: [session({ id: 1, running: 1, lastSeen: new Date().toISOString() })], nextCursor: null },
  })

  renderApp("/")

  const running = await screen.findByRole("region", { name: "Running" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()
  expect(within(running).getByText("4 polls")).toBeInTheDocument()
  expect(await screen.findByText("cli:abc")).toBeInTheDocument()

  act(() => {
    FakeEventSource.last!.onopen?.()
    FakeEventSource.last!.emit("operation", operation({ id: 3, operation: "measure" }))
  })

  const recent = screen.getByRole("region", { name: "Recent operations" })
  expect(await within(recent).findByText("measure")).toBeInTheDocument()
  expect(screen.getByText("connected")).toBeInTheDocument()
})
