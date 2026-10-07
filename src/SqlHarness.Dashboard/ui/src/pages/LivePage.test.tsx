import { act, screen, waitFor, within } from "@testing-library/react"
import { expect, test } from "vitest"
import type { OperationSummary } from "@/api/types"
import { operation, session } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

class FakeEventSource {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSED = 2
  static last: FakeEventSource | undefined
  listeners = new Map<string, ((event: MessageEvent<string>) => void)[]>()
  onopen: (() => void) | null = null
  onerror: (() => void) | null = null
  readyState = FakeEventSource.CONNECTING
  url: string
  constructor(url: string) {
    this.url = url
    FakeEventSource.last = this
  }
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }
  close() {
    this.readyState = FakeEventSource.CLOSED
  }
  open() {
    this.readyState = FakeEventSource.OPEN
    this.onopen?.()
  }
  fail(readyState: number) {
    this.readyState = readyState
    this.onerror?.()
  }
  emit(type: string, data: unknown) {
    for (const listener of this.listeners.get(type) ?? []) listener(new MessageEvent(type, { data: JSON.stringify(data) }))
  }
}

const RECENT = "/api/operations?limit=100"
const RUNNING = "/api/operations?status=running&limit=200"
const SESSIONS = "/api/sessions?limit=100"
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } })
const page = (items: unknown[]) => ({ items, nextCursor: null })
const count = (calls: string[], url: string) => calls.filter(call => call === url).length

function useFakeEventSource() {
  globalThis.EventSource = FakeEventSource as unknown as typeof EventSource
}

test("live page shows running, recent and pushed operations", async () => {
  useFakeEventSource()
  const watch = operation({ id: 2, status: "running", operation: "watch", progress: { polls: 4, changedPolls: 1, elapsedMs: 4000 } })
  stubFetch({
    [RECENT]: page([operation({ id: 1, operation: "ping" }), watch]),
    [RUNNING]: page([watch]),
    [SESSIONS]: page([session({ id: 1, running: 1, lastSeen: new Date().toISOString() })]),
  })

  renderApp("/")

  const running = await screen.findByRole("region", { name: "Running" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()
  expect(within(running).getByText("4 polls")).toBeInTheDocument()
  expect(await screen.findByText("cli:abc")).toBeInTheDocument()

  act(() => {
    FakeEventSource.last!.open()
    FakeEventSource.last!.emit("operation", operation({ id: 3, operation: "measure" }))
  })

  const recent = screen.getByRole("region", { name: "Recent operations" })
  expect(await within(recent).findByText("measure")).toBeInTheDocument()
  expect(screen.getByText("connected")).toBeInTheDocument()
})

test("the feed shows connecting until it first opens", async () => {
  useFakeEventSource()
  stubFetch({ [RECENT]: page([]), [RUNNING]: page([]), [SESSIONS]: page([]) })

  renderApp("/")

  expect(await screen.findByText("connecting")).toBeInTheDocument()
  act(() => FakeEventSource.last!.open())
  expect(screen.getByText("connected")).toBeInTheDocument()
  act(() => FakeEventSource.last!.fail(FakeEventSource.CONNECTING))
  expect(screen.getByText("disconnected")).toBeInTheDocument()
})

test("live lists are refetched on every (re)connect so changes made during a gap show", async () => {
  useFakeEventSource()
  let finished = false
  const watch = () => operation({ id: 2, operation: "watch", ...(finished
    ? { status: "succeeded" as const, updatedAt: "2026-10-07T09:00:05.000Z" }
    : { status: "running" as const, updatedAt: "2026-10-07T09:00:01.000Z" }) })
  const calls = stubFetch({
    [RECENT]: () => json(page([watch()])),
    [RUNNING]: () => json(page(finished ? [] : [watch()])),
    [SESSIONS]: () => json(page([session({ lastSeen: new Date().toISOString() })])),
  })

  renderApp("/")
  const running = await screen.findByRole("region", { name: "Running" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()

  act(() => FakeEventSource.last!.open())
  await waitFor(() => expect(count(calls, RECENT)).toBe(2))
  await waitFor(() => expect(count(calls, RUNNING)).toBe(2))
  expect(count(calls, SESSIONS)).toBe(2)

  act(() => FakeEventSource.last!.fail(FakeEventSource.CONNECTING))
  expect(screen.getByText("disconnected")).toBeInTheDocument()
  finished = true
  act(() => FakeEventSource.last!.open())

  await waitFor(() => expect(count(calls, RECENT)).toBe(3))
  await waitFor(() => expect(count(calls, RUNNING)).toBe(3))
  expect(count(calls, SESSIONS)).toBe(3)
  expect(await within(running).findByText("Nothing is running.")).toBeInTheDocument()
  const recent = screen.getByRole("region", { name: "Recent operations" })
  expect(await within(recent).findByText("succeeded")).toBeInTheDocument()
})

test("a long-running operation outside the recent page still shows in Running", async () => {
  useFakeEventSource()
  const recent = Array.from({ length: 100 }, (_, index) => operation({ id: 1000 - index, operation: "ping" }))
  stubFetch({
    [RECENT]: page(recent),
    [RUNNING]: page([operation({ id: 7, status: "running", operation: "watch" })]),
    [SESSIONS]: page([]),
  })

  renderApp("/")

  const running = await screen.findByRole("region", { name: "Running" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()
})

test("a running operation missing from the refetched running list leaves Running after reconnect", async () => {
  useFakeEventSource()
  let gone = false
  // Older than the recent page, so only the running list knows about it.
  const old: OperationSummary = operation({ id: 7, status: "running", operation: "watch" })
  const recent = Array.from({ length: 100 }, (_, index) => operation({ id: 1000 - index, operation: "ping" }))
  stubFetch({
    [RECENT]: () => json(page(recent)),
    [RUNNING]: () => json(page(gone ? [] : [old])),
    [SESSIONS]: () => json(page([])),
  })

  renderApp("/")
  const running = await screen.findByRole("region", { name: "Running" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()

  act(() => FakeEventSource.last!.open())
  act(() => FakeEventSource.last!.fail(FakeEventSource.CONNECTING))
  gone = true
  act(() => FakeEventSource.last!.open())

  expect(await within(running).findByText("Nothing is running.")).toBeInTheDocument()
})

test("pushed events add running rows and remove finished ones without the recent cap", async () => {
  useFakeEventSource()
  const recent = Array.from({ length: 100 }, (_, index) => operation({ id: 1000 - index, operation: "ping" }))
  stubFetch({ [RECENT]: page(recent), [RUNNING]: page([]), [SESSIONS]: page([]) })

  renderApp("/")
  const running = await screen.findByRole("region", { name: "Running" })
  expect(await within(running).findByText("Nothing is running.")).toBeInTheDocument()
  act(() => FakeEventSource.last!.open())

  const old = operation({ id: 5, status: "running", operation: "watch", updatedAt: "2026-10-07T09:00:01.000Z" })
  act(() => FakeEventSource.last!.emit("operation", old))
  expect(await within(running).findByText("watch")).toBeInTheDocument()

  act(() => FakeEventSource.last!.emit("operation", { ...old, status: "succeeded", updatedAt: "2026-10-07T09:00:02.000Z" }))
  expect(await within(running).findByText("Nothing is running.")).toBeInTheDocument()
})

test("a feed the server refuses (401) shows the reopen message without retrying", async () => {
  useFakeEventSource()
  let authorized = true
  const respond = (body: unknown) => () => (authorized ? json(body) : new Response("", { status: 401 }))
  const calls = stubFetch({ [RECENT]: respond(page([])), [RUNNING]: respond(page([])), [SESSIONS]: respond(page([])) })

  renderApp("/")
  await screen.findByRole("region", { name: "Running" })
  act(() => FakeEventSource.last!.open())
  await waitFor(() => expect(count(calls, RECENT)).toBe(2))

  authorized = false
  act(() => FakeEventSource.last!.fail(FakeEventSource.CLOSED))

  expect(await screen.findByText(/Run sqlharness dashboard again/)).toBeInTheDocument()
  const settled = calls.length
  await new Promise(resolve => setTimeout(resolve, 50))
  expect(calls.length).toBe(settled)
  expect(count(calls, RECENT)).toBe(3)
})
