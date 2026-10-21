import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { createMemoryHistory, RouterProvider } from "@tanstack/react-router"
import { render } from "@testing-library/react"
import { createAppRouter } from "@/router"
import { trackQueryClient } from "./network"
export { stubFetch } from "./network"

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
  trackQueryClient(client)
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
