import { act, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import type { OperationSummary } from "@/api/types"
import { operation, session } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

class FakeEventSource {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSED = 2
  static instances: FakeEventSource[] = []
  listeners = new Map<string, ((event: MessageEvent<string>) => void)[]>()
  onopen: (() => void) | null = null
  onerror: (() => void) | null = null
  readyState = FakeEventSource.CONNECTING
  url: string
  constructor(url: string) {
    this.url = url
    FakeEventSource.instances.push(this)
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
const SESSIONS = "/api/sessions?archived=any&starred=false&limit=100"
const STARRED = "/api/sessions?archived=any&starred=true&limit=100"
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } })
const page = (items: unknown[]) => ({ items, nextCursor: null })
const count = (calls: string[], url: string) => calls.filter(call => call === url).length

function useFakeEventSource() {
  FakeEventSource.instances = []
  globalThis.EventSource = FakeEventSource as unknown as typeof EventSource
}

const liveFeed = () => FakeEventSource.instances.findLast(source => source.url === "/api/live")!

test("live page shows running, recent and pushed operations", async () => {
  useFakeEventSource()
  const watch = operation({ id: 2, status: "running", operation: "watch", progress: { polls: 4, changedPolls: 1, elapsedMs: 4000 } })
  stubFetch({
    [RECENT]: page([operation({ id: 1, operation: "ping" }), watch]),
    [RUNNING]: page([watch]),
    [STARRED]: page([]),
    [SESSIONS]: page([session({ id: 1, running: 1, lastSeen: new Date().toISOString() })]),
  })

  renderApp("/")
  await waitFor(() => expect(FakeEventSource.instances.filter(source => source.url === "/api/live")).toHaveLength(1))

  const running = await screen.findByRole("region", { name: "Running" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()
  expect(within(running).getByText("4 polls")).toBeInTheDocument()
  expect(await screen.findByText("cli:abc")).toBeInTheDocument()

  act(() => {
    liveFeed().open()
    liveFeed().emit("operation", operation({ id: 3, operation: "measure" }))
  })

  const recent = screen.getByRole("region", { name: "Recent operations" })
  expect(await within(recent).findByText("measure")).toBeInTheDocument()
  expect(screen.getByText("connected")).toBeInTheDocument()
})

test("an enabled safety notice from the live feed links to its operation", async () => {
  useFakeEventSource()
  stubFetch({
    "/api/settings": { status: "valid", path: "config.json", settings: { connection: { timeoutSeconds: 30 }, dashboard: { safetyNotifications: true } } },
    [RECENT]: page([]), [RUNNING]: page([]), [STARRED]: page([]), [SESSIONS]: page([]),
  })
  const { client } = renderApp("/")
  await screen.findByRole("region", { name: "Running" })
  await waitFor(() => expect(client.getQueryData(["settings"])).toBeDefined())

  act(() => {
    liveFeed().open()
    liveFeed().emit("operation", operation({ id: 31, safetyNotices: ["open transaction rolled back"] }))
  })

  expect(await screen.findByText("open transaction rolled back")).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "View operation" })).toHaveAttribute("href", "/operations/31")
})

test("an archived session that is still active stays on the live page", async () => {
  useFakeEventSource()
  stubFetch({
    [RECENT]: page([]),
    [RUNNING]: page([]),
    [STARRED]: page([]),
    [SESSIONS]: page([session({ id: 9, sessionKey: "cli:archived", archived: true, lastSeen: new Date().toISOString() })]),
  })
  renderApp("/")
  const active = await screen.findByRole("region", { name: "Active sessions" })
  expect(await within(active).findByText("cli:archived")).toBeInTheDocument()
})

test("starring a live session updates its row", async () => {
  useFakeEventSource()
  const lastSeen = new Date().toISOString()
  let starred = false
  const row = () => session({ id: 4, sessionKey: "cli:live", starred, lastSeen })
  stubFetch({
    [RECENT]: page([]),
    [RUNNING]: page([]),
    [STARRED]: () => json(page(starred ? [row()] : [])),
    [SESSIONS]: () => json(page(starred ? [] : [row()])),
    "PATCH /api/sessions/4/annotation": () => {
      starred = true
      return json({ title: null, note: null, starred: true, archived: false })
    },
  })

  renderApp("/")
  const active = await screen.findByRole("region", { name: "Active sessions" })
  await userEvent.click(await within(active).findByRole("button", { name: "Star session cli:live" }))

  expect(await within(active).findByRole("button", { name: "Unstar session cli:live" })).toHaveAttribute("aria-pressed", "true")
})

test("the feed shows connecting until it first opens", async () => {
  useFakeEventSource()
  stubFetch({ [RECENT]: page([]), [RUNNING]: page([]), [STARRED]: page([]), [SESSIONS]: page([]) })

  renderApp("/")

  expect(await screen.findByText("connecting")).toBeInTheDocument()
  act(() => liveFeed().open())
  expect(screen.getByText("connected")).toBeInTheDocument()
  act(() => liveFeed().fail(FakeEventSource.CONNECTING))
  expect(screen.getByText("disconnected")).toBeInTheDocument()
})

test("empty running and active sections share one compact status line", async () => {
  useFakeEventSource()
  stubFetch({ [RECENT]: page([]), [RUNNING]: page([]), [STARRED]: page([]), [SESSIONS]: page([]) })
  renderApp("/")
  expect(await screen.findByText("Nothing running · no active sessions")).toBeInTheDocument()
})

test("artifact-only running rows share the empty state and remain revealable", async () => {
  useFakeEventSource()
  localStorage.removeItem("sqlharness.showInternalOperations")
  stubFetch({
    [RECENT]: page([]),
    [RUNNING]: page([operation({ id: 41, status: "running", operation: "sqlharness_artifact" })]),
    [STARRED]: page([]),
    [SESSIONS]: page([]),
  })
  renderApp("/")
  const running = await screen.findByRole("region", { name: "Running" })
  expect(await screen.findByText("Nothing running · no active sessions")).toBeInTheDocument()
  expect(within(running).queryByText("sqlharness_artifact")).not.toBeInTheDocument()
  await userEvent.click(within(running).getByText("Show internal operations"))
  expect(await within(running).findByText("sqlharness_artifact")).toBeInTheDocument()
  expect(screen.queryByText("Nothing running · no active sessions")).not.toBeInTheDocument()
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
    [STARRED]: page([]),
    [SESSIONS]: () => json(page([session({ lastSeen: new Date().toISOString() })])),
  })

  renderApp("/")
  const running = await screen.findByRole("region", { name: "Running" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()

  act(() => liveFeed().open())
  await waitFor(() => expect(count(calls, RECENT)).toBe(2))
  await waitFor(() => expect(count(calls, RUNNING)).toBe(2))
  expect(count(calls, SESSIONS)).toBe(2)

  act(() => liveFeed().fail(FakeEventSource.CONNECTING))
  expect(screen.getByText("disconnected")).toBeInTheDocument()
  finished = true
  act(() => liveFeed().open())

  await waitFor(() => expect(count(calls, RECENT)).toBe(3))
  await waitFor(() => expect(count(calls, RUNNING)).toBe(3))
  expect(count(calls, SESSIONS)).toBe(3)
  expect(await within(running).findByText("Nothing is running.")).toBeInTheDocument()
  const recent = screen.getByRole("region", { name: "Recent operations" })
  expect(await within(recent).findByText("succeeded")).toBeInTheDocument()
})

test("clearing transcript data removes stale titles and models from the live caches", async () => {
  useFakeEventSource()
  let cleared = false
  const liveOperation = () => operation({ id: 12, status: "running", operation: "watch", agentModel: cleared ? null : "claude-opus-5-5",
    agentModelSource: cleared ? null : "transcript", updatedAt: "2026-10-07T09:00:01.000Z" })
  const liveSession = () => session({ id: 14, sessionKey: "cli:live", running: 1, agentTitle: cleared ? null : "Optimize orders",
    lastSeen: "2026-10-07T09:00:01.000Z" })
  const calls = stubFetch({
    [RECENT]: () => json(page([liveOperation()])),
    [RUNNING]: () => json(page([liveOperation()])),
    [STARRED]: page([]),
    [SESSIONS]: () => json(page([liveSession()])),
    "/api/settings": { status: "valid", path: "settings.json", settings: {
      journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
      dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: false } }, compare: { requireContext: true },
      connection: { timeoutSeconds: 30 },
      execution: { defaultTimeoutSeconds: 30 },
      diagnostics: { enabled: false },
      mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 },
    } },
    "/api/prices": { source: "embedded", fetchedAt: "2026-10-07T09:00:00Z", origin: "embedded" },
    "/api/transcripts": { enabled: false, matched: 1, unmatched: 0, readErrors: 0, lastCycleAt: null },
    "POST /api/transcripts/clear": () => {
      cleared = true
      return json({ enabled: false, matched: 0, unmatched: 0, readErrors: 0, lastCycleAt: null })
    },
  })

  renderApp("/")
  const running = await screen.findByRole("region", { name: "Running" })
  expect(await screen.findByRole("link", { name: "Optimize orders" })).toBeInTheDocument()
  expect(within(running).getByText("Claude Code")).toHaveAttribute("title", "claude-opus-5-5 (transcript)")
  expect(count(calls, RECENT)).toBe(1)
  expect(count(calls, RUNNING)).toBe(1)
  expect(count(calls, SESSIONS)).toBe(1)

  await userEvent.click(screen.getByRole("link", { name: "Settings" }))
  const clear = await screen.findByRole("button", { name: "Clear matched data" })
  await userEvent.click(clear)
  const dialog = await screen.findByRole("dialog")
  await userEvent.click(within(dialog).getByRole("button", { name: "Clear matched data" }))
  await waitFor(() => expect(calls).toContain("/api/transcripts/clear"))

  await userEvent.click(screen.getByRole("link", { name: "Live" }))
  await screen.findByRole("region", { name: "Running" })
  await waitFor(() => {
    expect(count(calls, RECENT)).toBe(2)
    expect(count(calls, RUNNING)).toBe(2)
    expect(count(calls, SESSIONS)).toBe(2)
  })
  expect(await screen.findByRole("link", { name: "cli:live" })).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "Optimize orders" })).not.toBeInTheDocument()
  expect(screen.queryByText("Claude Code", { selector: "span[title='claude-opus-5-5 (transcript)']" })).not.toBeInTheDocument()
})

test("a long-running operation outside the recent page still shows in Running", async () => {
  useFakeEventSource()
  const recent = Array.from({ length: 100 }, (_, index) => operation({ id: 1000 - index, operation: "ping" }))
  stubFetch({
    [RECENT]: page(recent),
    [RUNNING]: page([operation({ id: 7, status: "running", operation: "watch" })]),
    [STARRED]: page([]),
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
    [STARRED]: page([]),
    [SESSIONS]: () => json(page([])),
  })

  renderApp("/")
  const running = await screen.findByRole("region", { name: "Running" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()

  act(() => liveFeed().open())
  act(() => liveFeed().fail(FakeEventSource.CONNECTING))
  gone = true
  act(() => liveFeed().open())

  expect(await screen.findByText("Nothing running · no active sessions")).toBeInTheDocument()
})

test("pushed events add running rows and remove finished ones without the recent cap", async () => {
  useFakeEventSource()
  const recent = Array.from({ length: 100 }, (_, index) => operation({ id: 1000 - index, operation: "ping" }))
  stubFetch({ [RECENT]: page(recent), [RUNNING]: page([]), [STARRED]: page([]), [SESSIONS]: page([]) })

  renderApp("/")
  const running = await screen.findByRole("region", { name: "Running" })
  expect(await screen.findByText("Nothing running · no active sessions")).toBeInTheDocument()
  act(() => liveFeed().open())

  const old = operation({ id: 5, status: "running", operation: "watch", updatedAt: "2026-10-07T09:00:01.000Z" })
  act(() => liveFeed().emit("operation", old))
  expect(await within(running).findByText("watch")).toBeInTheDocument()

  act(() => liveFeed().emit("operation", { ...old, status: "succeeded", updatedAt: "2026-10-07T09:00:02.000Z" }))
  expect(await screen.findByText("Nothing running · no active sessions")).toBeInTheDocument()
})

test("a feed the server refuses (401) shows the reopen message without retrying", async () => {
  useFakeEventSource()
  let authorized = true
  const respond = (body: unknown) => () => (authorized ? json(body) : new Response("", { status: 401 }))
  const calls = stubFetch({ [RECENT]: respond(page([])), [RUNNING]: respond(page([])), [STARRED]: respond(page([])), [SESSIONS]: respond(page([])) })

  renderApp("/")
  await screen.findByRole("region", { name: "Running" })
  act(() => liveFeed().open())
  await waitFor(() => expect(count(calls, RECENT)).toBe(2))

  authorized = false
  act(() => liveFeed().fail(FakeEventSource.CLOSED))

  expect(await screen.findByText(/Run sqlharness dashboard again/)).toBeInTheDocument()
  const settled = calls.length
  await new Promise(resolve => setTimeout(resolve, 50))
  expect(calls.length).toBe(settled)
  expect(count(calls, RECENT)).toBe(3)
})
