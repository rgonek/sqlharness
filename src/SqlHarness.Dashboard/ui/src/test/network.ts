import type { QueryClient } from "@tanstack/react-query"
import { expect } from "vitest"

type FetchHandler = (url: URL, init?: RequestInit) => Response | Promise<Response>
type FetchRoute = unknown | FetchHandler
type UnexpectedRequest = { method: string; path: string; testName: string }
const appLayoutRoutes: Record<string, FetchRoute> = {
  "/api/profiles": { status: "missing", profiles: [], message: null },
  "/api/settings": { status: "valid", path: "config.json", settings: { dashboard: { safetyNotifications: false, agentTranscripts: { enabled: false } } } },
}

let originalFetch: typeof fetch | undefined
let activeRoutes: Record<string, FetchRoute> | undefined
let activeCalls: string[] | undefined
let unexpectedRequests: UnexpectedRequest[] | undefined
const activeQueryClients = new Set<QueryClient>()

function formatUnexpectedRequest({ method, path, testName }: UnexpectedRequest) {
  return `Unexpected dashboard test request in "${testName}": ${method} ${path}`
}

const isolatedFetch: typeof fetch = async (input, init) => {
  const request = input instanceof Request ? input : undefined
  const url = new URL(request?.url ?? String(input), window.location.href)
  const method = (init?.method ?? request?.method ?? "GET").toUpperCase()
  const path = url.pathname + url.search
  activeCalls?.push(path)
  const handler = activeRoutes?.[path] ?? activeRoutes?.[url.pathname]

  if (!activeRoutes || handler === undefined) {
    const unexpected = { method, path, testName: expect.getState().currentTestName ?? "<unknown test>" }
    unexpectedRequests?.push(unexpected)
    throw new Error(formatUnexpectedRequest(unexpected))
  }

  if (typeof handler === "function") return handler(url, init ?? request)
  return new Response(JSON.stringify(handler), { status: 200, headers: { "Content-Type": "application/json" } })
}

/** Installs the per-test request guard and clears any routes left by an earlier test. */
export function installNetworkIsolation() {
  if (originalFetch) throw new Error("Dashboard test network isolation is already installed")
  originalFetch = globalThis.fetch
  activeRoutes = undefined
  activeCalls = undefined
  unexpectedRequests = []
  globalThis.fetch = isolatedFetch
}

/** Restores the environment's fetch and drops every fixture when the test ends. */
export function restoreNetworkIsolation() {
  if (!originalFetch) return
  globalThis.fetch = originalFetch
  originalFetch = undefined
  activeRoutes = undefined
  activeCalls = undefined
  unexpectedRequests = undefined
  activeQueryClients.clear()
}

/** Fails the owning test for any request the API client may have converted to query state. */
export function assertNoUnexpectedRequests() {
  const pending = unexpectedRequests ?? []
  unexpectedRequests = []
  if (pending.length > 0) throw new Error(pending.map(formatUnexpectedRequest).join("\n"))
}

/** Registers renderApp clients so test teardown can cancel retries after unmounting the DOM. */
export function trackQueryClient(client: QueryClient) {
  if (!originalFetch) throw new Error("Dashboard test network isolation is not installed")
  activeQueryClients.add(client)
}

/** Cancels in-flight and scheduled query work owned by the renderApp instances in this test. */
export async function cancelTestQueries() {
  await Promise.all([...activeQueryClients].map(client => client.cancelQueries()))
  activeQueryClients.clear()
}

/** Registers the current test's explicit response routes. Exact path+query wins over path-only. */
export function stubFetch(routes: Record<string, FetchRoute>) {
  if (!originalFetch) throw new Error("Dashboard test network isolation is not installed")

  const calls: string[] = []
  activeRoutes = { ...appLayoutRoutes, ...routes }
  activeCalls = calls
  return calls
}
