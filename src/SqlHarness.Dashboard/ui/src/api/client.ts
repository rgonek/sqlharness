import type { FieldError } from "./types"

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

export class FieldErrorsError extends Error {
  readonly errors: FieldError[]
  constructor(errors: FieldError[]) {
    super("The settings are not valid.")
    this.errors = errors
    this.name = "FieldErrorsError"
  }
}

export class ConflictError extends Error {
  constructor(message: string) {
    super(message)
    this.name = "ConflictError"
  }
}

/** The dashboard's only write; the custom header is what the server's write guard requires. */
export async function putJson<T>(path: string, body: unknown, params?: QueryParams): Promise<T> {
  const url = withQuery(path, params)
  const response = await fetch(url, {
    method: "PUT",
    credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json", "X-SqlHarness-Dashboard": "1" },
    body: JSON.stringify(body),
  })
  if (response.status === 401) throw new UnauthorizedError()
  if (response.status === 400) throw new FieldErrorsError(((await response.json()) as { errors: FieldError[] }).errors)
  if (response.status === 409) throw new ConflictError(((await response.json()) as { error: string }).error)
  if (!response.ok) throw new Error(`Request failed with ${response.status}: ${url}`)
  return (await response.json()) as T
}
