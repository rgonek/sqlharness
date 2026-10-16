import { connection } from "./connection"
import type { FieldError } from "./types"

export class NetworkError extends Error {
  constructor(message: string) {
    super(message)
    this.name = "NetworkError"
  }
}

/** The one place fetches go through: reports connection state and turns transport failures into NetworkError. */
async function send(url: string, init?: RequestInit): Promise<Response> {
  try {
    const response = await fetch(url, init)
    connection.reportSuccess()
    return response
  } catch {
    connection.reportFailure()
    throw new NetworkError(`Dashboard unreachable: ${url}`)
  }
}

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
  const response = await send(url, { credentials: "same-origin", headers: { Accept: "application/json" } })
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

/** A settings write; the custom header is what the server's write guard requires. */
export async function putJson<T>(path: string, body: unknown, params?: QueryParams): Promise<T> {
  const url = withQuery(path, params)
  const response = await send(url, {
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

/** The price refresh write; a 502 carries a fixed message that is safe to show. */
export async function postJson<T>(path: string, body: unknown): Promise<T> {
  const response = await send(path, {
    method: "POST",
    credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json", "X-SqlHarness-Dashboard": "1" },
    body: JSON.stringify(body),
  })
  if (response.status === 401) throw new UnauthorizedError()
  if (response.status === 502 || response.status === 503) {
    const error = await response.json().then((data: { error?: string }) => data.error, () => undefined)
    throw new Error(error ?? `Request failed with ${response.status}: ${path}`)
  }
  if (!response.ok) throw new Error(`Request failed with ${response.status}: ${path}`)
  return (await response.json()) as T
}

/** Sends an operator decision and returns the server's fixed pending-state conflict without leaking request data. */
export async function postMutationDecision(requestId: string, decision: "approve" | "deny"): Promise<"ok" | "not-pending"> {
  const path = `/api/mutations/${encodeURIComponent(requestId)}/${decision}`
  const response = await send(path, { method: "POST", credentials: "same-origin", headers: { Accept: "application/json", "X-SqlHarness-Dashboard": "1" } })
  if (response.status === 401) throw new UnauthorizedError()
  if (response.status === 409) return "not-pending"
  if (!response.ok) throw new Error(`Request failed with ${response.status}: ${path}`)
  return "ok"
}

/** A write whose failure carries the server's `error` text, shown to the operator as is. */
export class RequestError extends Error {
  constructor(message: string) {
    super(message)
    this.name = "RequestError"
  }
}

export async function patchJson<T>(path: string, body: unknown): Promise<T> {
  const response = await send(path, {
    method: "PATCH",
    credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json", "X-SqlHarness-Dashboard": "1" },
    body: JSON.stringify(body),
  })
  if (response.status === 401) throw new UnauthorizedError()
  // Any other failure, 404 included, shows the server's message (e.g. "The session no longer exists.").
  if (!response.ok) {
    const error = await response.json().then((data: { error?: string }) => data.error, () => undefined)
    throw new RequestError(error ?? `Request failed with ${response.status}: ${path}`)
  }
  return (await response.json()) as T
}
