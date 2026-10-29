import { act, fireEvent, screen, waitFor, within } from "@testing-library/react"
import { expect, test, vi } from "vitest"
import { renderApp, stubFetch } from "@/test/render"
import * as sound from "./sound"

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
  constructor(url: string) { this.url = url; FakeEventSource.instances.push(this) }
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]) }
  close() { this.readyState = FakeEventSource.CLOSED }
  open() { this.readyState = FakeEventSource.OPEN; this.onopen?.() }
  emit(type: string, data: unknown) { for (const listener of this.listeners.get(type) ?? []) listener(new MessageEvent(type, { data: JSON.stringify(data) })) }
}

const request = {
  requestId: "r-1",
  request: { profile: "prod-db", server: "db.example", database: "payments", engine: "postgres", tier: "dml", digest: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", grantees: [], sql: "<script>alert(1)</script>", parameters: [
    { name: "p", type: "text", value: "<img src=x onerror=1>" },
    { name: "optional", type: null, value: null },
    { name: "long_parameter_name_that_must_remain_selectable", type: "text", value: "0123456789abcdef".repeat(40) },
  ] },
  environment: "production", writerSameAsReader: false, expiresAt: new Date(Date.now() + 60_000).toISOString(),
}
const profiles = { status: "valid", message: null, profiles: [{ name: "prod-db", environment: "production", mutationMode: "yolo", acknowledgeProductionYolo: true }] }

/** LivePage always reads these lists; approval scenarios provide the event and mutation behavior separately. */
function stubApprovalPageFetch(routes: Record<string, unknown | ((url: URL, init?: RequestInit) => Response)>) {
  return stubFetch({
    "/api/profiles": profiles,
    "/api/operations?limit=100": { items: [], nextCursor: null },
    "/api/operations?status=running&limit=200": { items: [], nextCursor: null },
    "/api/sessions?archived=any&starred=true&limit=100": { items: [], nextCursor: null },
    "/api/sessions?archived=any&starred=false&limit=100": { items: [], nextCursor: null },
    ...routes,
  })
}

async function setup() {
  FakeEventSource.instances = []
  globalThis.EventSource = FakeEventSource as unknown as typeof EventSource
  const play = vi.spyOn(sound, "playApprovalSound").mockResolvedValue(undefined)
  stubApprovalPageFetch({})
  renderApp("/")
  await screen.findByRole("main")
  const source = () => FakeEventSource.instances.find(item => item.url === "/api/mutations/events")!
  expect(source()).toBeDefined()
  expect(FakeEventSource.instances.map(item => item.url)).toContain("/api/live")
  return { play, source }
}

test("live added event shows a safe toast and the default sound; snapshots and digest joins stay silent", async () => {
  const { play, source } = await setup()
  expect(source().url).toBe("/api/mutations/events")
  act(() => source().emit("snapshot", [request]))
  expect(play).not.toHaveBeenCalled()
  act(() => source().emit("snapshot", [{ ...request, requestId: "r-reconnect", request: { ...request.request, digest: "c".repeat(64) } }]))
  expect(play).not.toHaveBeenCalled()
  expect(await screen.findByText("YOLO is on for production profile prod-db")).toBeInTheDocument()
  expect(screen.getByText("production")).toBeInTheDocument()
  expect(screen.getByText(request.request.sql)).toBeInTheDocument()
  act(() => source().emit("added", request))
  expect(play).not.toHaveBeenCalled()
  act(() => source().emit("added", { ...request, requestId: "r-2" }))
  expect(play).not.toHaveBeenCalled()
  act(() => source().emit("added", { ...request, requestId: "r-3", request: { ...request.request, digest: "b".repeat(64) } }))
  await waitFor(() => expect(play).toHaveBeenCalledTimes(1))
  act(() => source().emit("added", { ...request, requestId: "r-3", request: { ...request.request, digest: "b".repeat(64) } }))
  expect(play).toHaveBeenCalledTimes(1)
})

test("approval dialog renders SQL and parameter values as literal text and waits before approval", async () => {
  const { source } = await setup()
  vi.useFakeTimers()
  act(() => source().emit("snapshot", [request]))
  fireEvent.click(screen.getByRole("button", { name: "Review" }))
  expect(screen.getByText("production")).toBeInTheDocument()
  expect(screen.getByText("Requested by unknown requester")).toBeInTheDocument()
  expect(screen.getByText(/Digest 0123456789ab/)).toBeInTheDocument()
  expect(screen.getByTestId("sql-code").textContent).toBe("<script>alert(1)</script>")
  expect(screen.getByText("<img src=x onerror=1>")).toBeInTheDocument()
  const parameters = screen.getByRole("table")
  expect(within(parameters).getByRole("rowheader", { name: "p" })).toBeInTheDocument()
  expect(within(parameters).getByText("NULL")).toBeInTheDocument()
  expect(within(parameters).getByText("0123456789abcdef".repeat(40))).toBeInTheDocument()
  expect(within(parameters).getByText("long_parameter_name_that_must_remain_selectable")).toBeInTheDocument()
  expect(document.querySelector("script")).toBeNull()
  expect(screen.getByRole("button", { name: "Approve" })).toBeDisabled()
  expect(screen.getByRole("button", { name: "Deny" })).toBeEnabled()
  const decisionFetch = vi.spyOn(globalThis, "fetch")
  expect(decisionFetch.mock.calls.some(([input]) => String(input).includes("/approve") || String(input).includes("/deny"))).toBe(false)
  decisionFetch.mockRestore()
  act(() => vi.advanceTimersByTime(1_000))
  expect(screen.getByRole("button", { name: "Approve" })).toBeEnabled()
  const fetchSpy = vi.spyOn(globalThis, "fetch")
  fireEvent.keyDown(screen.getByRole("dialog"), { key: "Enter" })
  expect(fetchSpy).not.toHaveBeenCalled()
  fetchSpy.mockRestore()
  vi.useRealTimers()
})

test("a 409 approve shows the pending-state message and removed closes the dialog", async () => {
  const { source } = await setup()
  stubApprovalPageFetch({ "POST /api/mutations/r-1/approve": () => new Response(JSON.stringify({ error: "request_not_pending" }), { status: 409 }) })
  act(() => source().emit("added", request))
  vi.useFakeTimers()
  fireEvent.click(screen.getByRole("button", { name: "Review" }))
  act(() => vi.advanceTimersByTime(1_000))
  fireEvent.click(screen.getByRole("button", { name: "Approve" }))
  await act(async () => { await Promise.resolve(); await Promise.resolve() })
  expect(screen.getByText("This request is no longer pending")).toBeInTheDocument()
  act(() => source().emit("removed", { requestId: "r-1", state: "denied" }))
  expect(screen.getByRole("dialog", { name: "Request no longer pending" })).toHaveAttribute("data-closed", "")
  vi.useRealTimers()
  vi.restoreAllMocks()
})

test("a profile execution conflict explains that the approved request did not run", async () => {
  const { source } = await setup()
  stubApprovalPageFetch({ "POST /api/mutations/r-1/approve": () => new Response(JSON.stringify({ status: "busy", error: "profile_execution_busy" }), { status: 409 }) })
  act(() => source().emit("added", request))
  vi.useFakeTimers()
  fireEvent.click(screen.getByRole("button", { name: "Review" }))
  act(() => vi.advanceTimersByTime(1_000))
  fireEvent.click(screen.getByRole("button", { name: "Approve" }))
  await act(async () => { await Promise.resolve(); await Promise.resolve() })
  expect(screen.getByText("Another change is already running for this profile. This request was not executed.")).toBeInTheDocument()
  vi.useRealTimers()
  vi.restoreAllMocks()
})

test("an autoplay rejection does not hide the toast or its review action", async () => {
  const { source, play } = await setup()
  play.mockRejectedValueOnce(new Error("autoplay blocked"))
  act(() => source().emit("added", request))
  expect(await screen.findByRole("button", { name: "Review" })).toBeInTheDocument()
  await waitFor(() => expect(play).toHaveBeenCalledTimes(1))
  expect(screen.getByRole("button", { name: "Review" })).toBeEnabled()
})

test("a completed request releases its digest so a later request can notify", async () => {
  const { source, play } = await setup()
  const first = { ...request, requestId: "r-first" }
  const later = { ...request, requestId: "r-later" }
  act(() => source().emit("added", first))
  await waitFor(() => expect(play).toHaveBeenCalledTimes(1))
  act(() => source().emit("removed", { requestId: first.requestId, state: "denied" }))
  act(() => source().emit("added", later))
  await waitFor(() => expect(play).toHaveBeenCalledTimes(2))
})

test("a hidden tab receives only a safe notification whose click opens the approval", async () => {
  class FakeNotification {
    static permission = "granted"
    static last: FakeNotification | undefined
    onclick: (() => void) | null = null
    title: string
    constructor(title: string) { this.title = title; FakeNotification.last = this }
    close() {}
  }
  const previousNotification = Object.getOwnPropertyDescriptor(window, "Notification")
  const previousVisibility = Object.getOwnPropertyDescriptor(document, "visibilityState")
  Object.defineProperty(window, "Notification", { configurable: true, value: FakeNotification })
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" })
  const focus = vi.spyOn(window, "focus").mockImplementation(() => {})
  const { source } = await setup()
  const hiddenSnapshot = { ...request, requestId: "r-hidden", request: { ...request.request, digest: "d".repeat(64) } }
  act(() => source().emit("snapshot", [hiddenSnapshot]))
  act(() => source().emit("snapshot", [hiddenSnapshot]))
  expect(FakeNotification.last).toBeUndefined()
  const liveRequest = { ...request, requestId: "r-live", request: { ...request.request, digest: "e".repeat(64) } }
  act(() => source().emit("added", liveRequest))
  expect(FakeNotification.last?.title).toBe("SQLHarness: a change needs your decision")
  expect(FakeNotification.last?.title).not.toContain(liveRequest.request.sql)
  act(() => FakeNotification.last?.onclick?.())
  expect(focus).toHaveBeenCalledOnce()
  expect(screen.getByRole("dialog")).toBeInTheDocument()
  if (previousNotification) Object.defineProperty(window, "Notification", previousNotification)
  else delete (window as Window & { Notification?: typeof Notification }).Notification
  if (previousVisibility) Object.defineProperty(document, "visibilityState", previousVisibility)
  vi.restoreAllMocks()
})


test("toast Deny keeps failed requests visible and uses the decision endpoint", async () => {
  const { source } = await setup()
  const fetch = stubApprovalPageFetch({ "POST /api/mutations/r-1/deny": () => new Response("", { status: 500 }) })
  act(() => source().emit("added", request))
  fireEvent.click(await screen.findByRole("button", { name: "Deny" }))
  expect(await screen.findByText("The decision could not be submitted. Try again.")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Review" })).toBeEnabled()
  expect(fetch).toContain("/api/mutations/r-1/deny")
})

test("requester context and display modes retain the original request", async () => {
  const { source } = await setup()
  const original = { ...request, requester: { sessionId: 42, agentLabel: "codex" }, request: { ...request.request, sql: "UPDATE t SET x=@a", parameters: [{ name: "a", type: null, value: "123" }] } }
  const before = JSON.stringify(original)
  act(() => source().emit("added", original))
  fireEvent.click(await screen.findByRole("button", { name: "Review" }))
  expect(screen.getByText(/Requested by/)).toHaveTextContent("Codex")
  expect(screen.getByRole("link", { name: "session 42" })).toHaveAttribute("href", "/sessions/42")
  expect(screen.queryByText("Review the requested database change before deciding.")).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole("radio", { name: "Values inline" }))
  expect(screen.getByTestId("sql-code").textContent).toContain("'123'")
  fireEvent.click(screen.getByRole("radio", { name: "As sent" }))
  expect(screen.getByTestId("sql-code").textContent).toBe(original.request.sql)
  expect(JSON.stringify(original)).toBe(before)
})


test("toast Deny disables while submitting and retains busy and expired requests", async () => {
  const { source } = await setup()
  let finish!: (response: Response) => void
  const fetch = vi.spyOn(globalThis, "fetch").mockImplementation(input => String(input).endsWith("/deny") ? new Promise<Response>(resolve => { finish = resolve }) : Promise.resolve(new Response("{}")))
  act(() => source().emit("added", request))
  fireEvent.click(await screen.findByRole("button", { name: "Deny" }))
  expect(screen.getByRole("button", { name: "Denying..." })).toBeDisabled()
  await act(async () => { finish(new Response(JSON.stringify({ status: "busy", error: "profile_execution_busy" }), { status: 409 })) })
  expect(await screen.findByText("Another change is already running for this profile. Try again.")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Deny" })).toBeEnabled()
  act(() => source().emit("snapshot", [{ ...request, expiresAt: new Date(Date.now() - 1000).toISOString() }]))
  expect(screen.getByRole("button", { name: "Deny" })).toBeDisabled()
  fetch.mockRestore()
})
