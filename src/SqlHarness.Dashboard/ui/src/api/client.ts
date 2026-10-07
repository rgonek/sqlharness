export class UnauthorizedError extends Error {
  constructor() {
    super("The dashboard session is not valid.")
    this.name = "UnauthorizedError"
  }
}

export class NotFoundError extends Error {
  constructor(url: string) {
    super(`Not found: ${url}`)
    this.name = "NotFoundError"
  }
}

export type QueryParams = Record<string, string | number | null | undefined>

export function withQuery(path: string, params: QueryParams = {}): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== "") search.set(key, String(value))
  }
  const query = search.toString()
  return query ? `${path}?${query}` : path
}

export async function getJson<T>(path: string, params?: QueryParams): Promise<T> {
  const url = withQuery(path, params)
  const response = await fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" } })
  if (response.status === 401) throw new UnauthorizedError()
  if (response.status === 404) throw new NotFoundError(url)
  if (!response.ok) throw new Error(`Request failed with ${response.status}: ${url}`)
  return (await response.json()) as T
}
