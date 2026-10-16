import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { createMemoryHistory, RouterProvider } from "@tanstack/react-router"
import { render } from "@testing-library/react"
import { createAppRouter } from "@/router"

class EventSourceStub {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSED = 2
  readyState = EventSourceStub.CONNECTING
  onopen: (() => void) | null = null
  onerror: (() => void) | null = null
  constructor(_url: string) {}
  addEventListener(_type: string, _listener: (event: MessageEvent<string>) => void) {}
  close() { this.readyState = EventSourceStub.CLOSED }
}

/** Renders the whole app at `url` on a memory history; assert with findBy* because routes resolve asynchronously. */
export function renderApp(url = "/") {
  globalThis.EventSource ??= EventSourceStub as unknown as typeof EventSource
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } })
  const router = createAppRouter(createMemoryHistory({ initialEntries: [url] }))
  return {
    client,
    router,
    ...render(
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    ),
  }
}

/** Replaces fetch with a route table: exact path+query match first, then path only. */
export function stubFetch(routes: Record<string, unknown | ((url: URL, init?: RequestInit) => Response)>) {
  const calls: string[] = []
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://127.0.0.1")
    calls.push(url.pathname + url.search)
    const handler = routes[url.pathname + url.search] ?? routes[url.pathname]
    if (handler === undefined) return new Response("not found", { status: 404 })
    if (typeof handler === "function") return (handler as (u: URL, i?: RequestInit) => Response)(url, init)
    return new Response(JSON.stringify(handler), { status: 200, headers: { "Content-Type": "application/json" } })
  }) as typeof fetch
  return calls
}
