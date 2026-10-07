/** Replaces fetch with a route table: exact path+query match first, then path only. */
export function stubFetch(routes: Record<string, unknown | ((url: URL) => Response)>) {
  const calls: string[] = []
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://127.0.0.1")
    calls.push(url.pathname + url.search)
    const handler = routes[url.pathname + url.search] ?? routes[url.pathname]
    if (handler === undefined) return new Response("not found", { status: 404 })
    if (typeof handler === "function") return (handler as (u: URL) => Response)(url)
    return new Response(JSON.stringify(handler), { status: 200, headers: { "Content-Type": "application/json" } })
  }) as typeof fetch
  return calls
}
