import { act, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import type { OperationSummary, SessionSummary } from "@/api/types"
import { operation as fixtureOperation, session as fixtureSession } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

const operation = (over: Partial<OperationSummary> = {}) => fixtureOperation({ startedAt: new Date().toISOString(), updatedAt: new Date().toISOString(), ...over })
const session = (over: Partial<SessionSummary> = {}) => fixtureSession({ firstSeen: new Date().toISOString(), lastSeen: new Date().toISOString(), ...over })

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

  const running = await screen.findByRole("region", { name: "Activity feed" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()
  expect(within(running).getByText("4 polls")).toBeInTheDocument()
  expect(await screen.findByText("cli:abc")).toBeInTheDocument()

  act(() => {
    liveFeed().open()
    liveFeed().emit("operation", operation({ id: 3, operation: "measure" }))
  })

  const recent = screen.getByRole("region", { name: "Activity feed" })
  expect(await within(recent).findByText("measure")).toBeInTheDocument()
  expect(screen.queryByText("connected")).not.toBeInTheDocument()
})

test("an enabled safety notice from the live feed links to its operation", async () => {
  useFakeEventSource()
  stubFetch({
    "/api/settings": { status: "valid", path: "config.json", settings: { connection: { timeoutSeconds: 30 }, dashboard: { safetyNotifications: true } } },
    [RECENT]: page([]), [RUNNING]: page([]), [STARRED]: page([]), [SESSIONS]: page([]),
  })
  const { client } = renderApp("/")
  await screen.findByRole("region", { name: "Activity feed" })
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
  const active = await screen.findByRole("region", { name: "Activity feed" })
  await userEvent.click(screen.getByRole("button", { name: "Include archived" }))
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
  const active = await screen.findByRole("region", { name: "Activity feed" })
  await userEvent.click(await within(active).findByRole("button", { name: "Star session cli:live" }))

  expect(await within(active).findByRole("button", { name: "Unstar session cli:live" })).toHaveAttribute("aria-pressed", "true")
})

test("the feed keeps connection state out of the Activity content", async () => {
  useFakeEventSource()
  stubFetch({ [RECENT]: page([]), [RUNNING]: page([]), [STARRED]: page([]), [SESSIONS]: page([]) })

  renderApp("/")

  await screen.findByRole("region", { name: "Activity feed" })
  expect(screen.queryByText("connecting")).not.toBeInTheDocument()
  act(() => liveFeed().open())
  expect(screen.queryByText("connected")).not.toBeInTheDocument()
  act(() => liveFeed().fail(FakeEventSource.CONNECTING))
  expect(screen.queryByText("disconnected")).not.toBeInTheDocument()
})

test("empty running and active sections share one compact status line", async () => {
  useFakeEventSource()
  stubFetch({ [RECENT]: page([]), [RUNNING]: page([]), [STARRED]: page([]), [SESSIONS]: page([]) })
  renderApp("/")
  expect(await screen.findByText("Nothing matches these filters.")).toBeInTheDocument()
})

test("artifact-only running rows stay hidden without an internal toggle on Activity", async () => {
  useFakeEventSource()
  localStorage.removeItem("sqlharness.showInternalOperations")
  stubFetch({
    [RECENT]: page([]),
    [RUNNING]: page([operation({ id: 41, status: "running", operation: "sqlharness_artifact" })]),
    [STARRED]: page([]),
    [SESSIONS]: page([]),
  })
  renderApp("/")
  const running = await screen.findByRole("region", { name: "Activity feed" })
  expect(await screen.findByText("Nothing matches these filters.")).toBeInTheDocument()
  expect(within(running).queryByText("sqlharness_artifact")).not.toBeInTheDocument()
  expect(screen.queryByRole("checkbox", { name: "Show internal operations" })).not.toBeInTheDocument()
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
  const running = await screen.findByRole("region", { name: "Activity feed" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()

  act(() => liveFeed().open())
  await waitFor(() => expect(count(calls, RECENT)).toBe(2))
  await waitFor(() => expect(count(calls, RUNNING)).toBe(2))
  expect(count(calls, SESSIONS)).toBe(2)

  act(() => liveFeed().fail(FakeEventSource.CONNECTING))
  expect(screen.queryByText("disconnected")).not.toBeInTheDocument()
  finished = true
  act(() => liveFeed().open())

  await waitFor(() => expect(count(calls, RECENT)).toBe(3))
  await waitFor(() => expect(count(calls, RUNNING)).toBe(3))
  expect(count(calls, SESSIONS)).toBe(3)
  await waitFor(() => expect(within(running).queryByText("watch")).not.toBeInTheDocument())
  await userEvent.click(within(running).getByRole("button", { name: /^Expand (?!all$)/ }))
  const recent = screen.getByRole("region", { name: "Activity feed" })
  expect(await within(recent).findByText("succeeded")).toBeInTheDocument()
})

test("clearing transcript data removes stale titles and models from the live caches", async () => {
  useFakeEventSource()
  let cleared = false
  const liveOperation = () => operation({ id: 12, status: "running", operation: "watch", agentModel: cleared ? null : "claude-opus-5-5",
    agentModelSource: cleared ? null : "transcript", updatedAt: "2026-10-07T09:00:01.000Z" })
  const liveSession = () => session({ id: 14, sessionKey: "cli:live", running: 1, agentTitle: cleared ? null : "Optimize orders",
    lastSeen: new Date().toISOString() })
  const calls = stubFetch({
    [RECENT]: () => json(page([liveOperation()])),
    [RUNNING]: () => json(page([liveOperation()])),
    [STARRED]: page([]),
    [SESSIONS]: () => json(page([liveSession()])),
    "/api/settings": { status: "valid", path: "settings.json", settings: {
      journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
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
  const running = await screen.findByRole("region", { name: "Activity feed" })
  expect(await screen.findByRole("link", { name: "Optimize orders" })).toBeInTheDocument()
  expect(within(running).getByText("Claude Code", { selector: "span[title]" })).toHaveAttribute("title", "claude-opus-5-5 (transcript)")
  expect(count(calls, RECENT)).toBe(1)
  expect(count(calls, RUNNING)).toBe(1)
  expect(count(calls, SESSIONS)).toBe(1)

  await userEvent.click(screen.getByRole("link", { name: "Settings" }))
  const clear = await screen.findByRole("button", { name: "Clear matched data" })
  await userEvent.click(clear)
  const dialog = await screen.findByRole("dialog")
  await userEvent.click(within(dialog).getByRole("button", { name: "Clear matched data" }))
  await waitFor(() => expect(calls).toContain("/api/transcripts/clear"))

  await userEvent.click(screen.getByRole("link", { name: "Activity" }))
  await screen.findByRole("region", { name: "Activity feed" })
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

  const running = await screen.findByRole("region", { name: "Activity feed" })
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
  const running = await screen.findByRole("region", { name: "Activity feed" })
  expect(await within(running).findByText("watch")).toBeInTheDocument()

  act(() => liveFeed().open())
  act(() => liveFeed().fail(FakeEventSource.CONNECTING))
  gone = true
  act(() => liveFeed().open())

  await waitFor(() => expect(within(running).queryByText("watch")).not.toBeInTheDocument())
})

test("pushed events add running rows and remove finished ones without the recent cap", async () => {
  useFakeEventSource()
  const recent = Array.from({ length: 100 }, (_, index) => operation({ id: 1000 - index, operation: "ping" }))
  stubFetch({ [RECENT]: page(recent), [RUNNING]: page([]), [STARRED]: page([]), [SESSIONS]: page([]) })

  renderApp("/")
  const running = await screen.findByRole("region", { name: "Activity feed" })
  expect(within(running).queryByText("running")).not.toBeInTheDocument()
  act(() => liveFeed().open())

  const old = operation({ id: 5, status: "running", operation: "watch", updatedAt: "2026-10-07T09:00:01.000Z" })
  act(() => liveFeed().emit("operation", old))
  expect(await within(running).findByText("watch")).toBeInTheDocument()

  act(() => liveFeed().emit("operation", { ...old, status: "succeeded", updatedAt: "2026-10-07T09:00:02.000Z" }))
  await waitFor(() => expect(within(running).queryByText("running")).not.toBeInTheDocument())
})

test("a feed the server refuses (401) shows the reopen message without retrying", async () => {
  useFakeEventSource()
  let authorized = true
  const respond = (body: unknown) => () => (authorized ? json(body) : new Response("", { status: 401 }))
  const calls = stubFetch({ [RECENT]: respond(page([])), [RUNNING]: respond(page([])), [STARRED]: respond(page([])), [SESSIONS]: respond(page([])) })

  renderApp("/")
  await screen.findByRole("region", { name: "Activity feed" })
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

test("the grouped feed retains every running page beyond the recent-operation cap", async () => {
  useFakeEventSource()
  const running = Array.from({ length: 200 }, (_, index) => operation({ id: index + 2, status: "running", operation: "watch" }))
  stubFetch({
    [RECENT]: page([]),
    [RUNNING]: { items: running, nextCursor: 2 },
    "/api/operations?status=running&limit=200&cursor=2": page([operation({ id: 1, status: "running", operation: "watch" })]),
    [STARRED]: page([]), [SESSIONS]: page([]),
  })
  renderApp("/")
  const feed = await screen.findByRole("region", { name: "Activity feed" })
  expect(within(feed).getByRole("button", { name: "Running 201" })).toBeInTheDocument()
  expect(within(feed).getByRole("link", { name: "1" })).toHaveAttribute("href", expect.stringContaining("/operations/1"))
})

test("Activity filters hide empty session groups and can be reset", async () => {
  useFakeEventSource()
  stubFetch({
    [RECENT]: page([operation({ id: 1, sessionId: 1, operation: "measure", status: "running", agentKind: "codex", agentLabel: "Orders tuning" }), operation({ id: 2, sessionId: 2, operation: "query", agentKind: "claude" })]),
    [RUNNING]: page([operation({ id: 1, sessionId: 1, operation: "measure", status: "running", agentKind: "codex", agentLabel: "Orders tuning" })]),
    [STARRED]: page([]),
    [SESSIONS]: page([session({ id: 1, title: "Orders session", agentKind: "codex" }), session({ id: 2, title: "Billing session", agentKind: "claude" })]),
  })
  renderApp("/")
  await screen.findByRole("region", { name: "Activity feed" })
  await userEvent.type(screen.getByRole("textbox", { name: "Search activity" }), "Orders tuning")
  expect(screen.getByRole("link", { name: "Orders session" })).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "Billing session" })).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Reset filters" }))
  expect(screen.getByRole("link", { name: "Billing session" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Agent" }))
  await userEvent.click(screen.getByRole("checkbox", { name: "claude" }))
  expect(screen.queryByRole("link", { name: "Orders session" })).not.toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Billing session" })).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Running 0" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("checkbox", { name: "codex" }))
  expect(screen.getByRole("link", { name: "Orders session" })).toBeInTheDocument()
  expect(screen.getByRole("link", { name: "Billing session" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Named 1" }))
  expect(screen.queryByRole("link", { name: "Billing session" })).not.toBeInTheDocument()
})

test("clicking session metadata toggles its operations and the disclosure toggles only once", async () => {
  useFakeEventSource()
  stubFetch({ [RECENT]: page([operation({ id: 17, sessionId: 1 })]), [RUNNING]: page([]), [STARRED]: page([]),
    [SESSIONS]: page([session({ id: 1, sessionKey: "cli:row", cwd: "D:\\Dev\\shop", running: 0 })]) })
  renderApp("/")
  const group = await screen.findByRole("region", { name: "cli:row" })
  expect(within(group).queryByRole("link", { name: "17" })).not.toBeInTheDocument()
  await userEvent.click(within(group).getByTitle("D:\\Dev\\shop"))
  expect(within(group).getByRole("link", { name: "17" })).toBeInTheDocument()
  await userEvent.click(within(group).getByRole("button", { name: "Collapse cli:row" }))
  expect(within(group).queryByRole("link", { name: "17" })).not.toBeInTheDocument()
  expect(within(group).getByRole("button", { name: "Expand cli:row" })).toHaveAttribute("aria-expanded", "false")
})
