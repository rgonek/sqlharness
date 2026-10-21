import type { QueryClient } from "@tanstack/react-query"

type FetchHandler = (url: URL, init?: RequestInit) => Response
type FetchRoute = unknown | FetchHandler
const appLayoutRoutes: Record<string, FetchRoute> = {
  "/api/profiles": { status: "missing", profiles: [], message: null },
  "/api/settings": { status: "valid", path: "config.json", settings: { dashboard: { safetyNotifications: false, agentTranscripts: { enabled: false } } } },
}

let originalFetch: typeof fetch | undefined
let activeRoutes: Record<string, FetchRoute> | undefined
let activeCalls: string[] | undefined
const activeQueryClients = new Set<QueryClient>()

const isolatedFetch: typeof fetch = async (input, init) => {
  const request = input instanceof Request ? input : undefined
  const url = new URL(request?.url ?? String(input), window.location.href)
  const method = (init?.method ?? request?.method ?? "GET").toUpperCase()
  const path = url.pathname + url.search
  activeCalls?.push(path)
  const handler = activeRoutes?.[path] ?? activeRoutes?.[url.pathname]

  if (!activeRoutes || handler === undefined) {
    throw new Error(`Unexpected dashboard test request: ${method} ${path}`)
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
  globalThis.fetch = isolatedFetch
}

/** Restores the environment's fetch and drops every fixture when the test ends. */
export function restoreNetworkIsolation() {
  if (!originalFetch) return
  globalThis.fetch = originalFetch
  originalFetch = undefined
  activeRoutes = undefined
  activeCalls = undefined
  activeQueryClients.clear()
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
