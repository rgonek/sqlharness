import type { OperationSummary, SessionSummary } from "@/api/types"

export const LIVE_CAP = 200
export const ACTIVE_WINDOW_MS = 15 * 60_000

/**
 * Upsert by id, newest id first. A repeated or older event (same id, earlier updatedAt)
 * never replaces a newer row; an equal timestamp is accepted because liveness changes
 * (running -> abandoned) arrive without a journal write.
 */
export function mergeOperation(list: OperationSummary[], incoming: OperationSummary, cap = LIVE_CAP): OperationSummary[] {
  return upsertOperation(list, incoming, false, cap)
}

/**
 * Whether `incoming` replaces `existing`. A later updatedAt wins. At an equal updatedAt a pushed
 * event wins (liveness changes carry no journal write), but a fetched row never turns a
 * non-running status back into `running`: the push that made it abandoned is at least as new.
 */
function replaces(existing: OperationSummary | undefined, incoming: OperationSummary, fetched: boolean): boolean {
  if (!existing) return true
  if (existing.updatedAt !== incoming.updatedAt) return incoming.updatedAt > existing.updatedAt
  return !(fetched && existing.status !== "running" && incoming.status === "running")
}

function upsertOperation(list: OperationSummary[], incoming: OperationSummary, fetched: boolean, cap: number): OperationSummary[] {
  const existing = list.find(item => item.id === incoming.id)
  if (!replaces(existing, incoming, fetched)) return list
  const next = [incoming, ...list.filter(item => item.id !== incoming.id)]
  next.sort((a, b) => b.id - a.id)
  return next.slice(0, cap)
}

export function mergeSession(list: SessionSummary[], incoming: SessionSummary, cap = LIVE_CAP): SessionSummary[] {
  const existing = list.find(item => item.id === incoming.id)
  if (existing && existing.lastSeen > incoming.lastSeen) return list
  const next = [incoming, ...list.filter(item => item.id !== incoming.id)]
  next.sort((a, b) => (a.lastSeen < b.lastSeen ? 1 : a.lastSeen > b.lastSeen ? -1 : b.id - a.id))
  return next.slice(0, cap)
}

/**
 * Folds a fetched page into the live list with the same rules as pushed events, so a refetch
 * whose response is older than an event that arrived meanwhile cannot roll that row back.
 */
export function mergeOperations(list: OperationSummary[], fetched: OperationSummary[], cap = LIVE_CAP): OperationSummary[] {
  return fetched.reduce((current, item) => upsertOperation(current, item, true, cap), list)
}

export function mergeSessions(list: SessionSummary[], fetched: SessionSummary[], cap = LIVE_CAP): SessionSummary[] {
  return fetched.reduce((current, item) => mergeSession(current, item, cap), list)
}

export function isActiveSession(session: SessionSummary, now: number, windowMs = ACTIVE_WINDOW_MS): boolean {
  return session.running > 0 || now - Date.parse(session.lastSeen) <= windowMs
}

/** How many finished ids the running list remembers so a stale fetch cannot resurrect them. */
export const FINISHED_MEMORY = 500

/**
 * The running list's cache holds every running row (never capped) plus the latest status of
 * recently finished ids. Only running rows are shown; the finished ones guard against stale
 * running rows from a refetch that was in flight when the finish was pushed.
 */
export function runningOnly(tracked: OperationSummary[]): OperationSummary[] {
  return tracked.filter(item => item.status === "running")
}

function trim(tracked: OperationSummary[]): OperationSummary[] {
  const sorted = [...tracked].sort((a, b) => b.id - a.id)
  const finished = sorted.filter(item => item.status !== "running")
  if (finished.length <= FINISHED_MEMORY) return sorted
  const keep = new Set(
    [...finished].sort((a, b) => (a.updatedAt < b.updatedAt ? 1 : a.updatedAt > b.updatedAt ? -1 : 0)).slice(0, FINISHED_MEMORY),
  )
  return sorted.filter(item => item.status === "running" || keep.has(item))
}

/** A pushed event: a running row is added or updated, a finished one leaves the running list. */
export function pushRunning(tracked: OperationSummary[], incoming: OperationSummary): OperationSummary[] {
  const existing = tracked.find(item => item.id === incoming.id)
  if (!replaces(existing, incoming, false)) return tracked
  return trim([incoming, ...tracked.filter(item => item.id !== incoming.id)])
}

/**
 * Applies a fetched `status=running` list. `before` is the cache when the request was sent and
 * `current` the cache now. The server reads its snapshot after the request is sent, so a row
 * that was running in `before` and is missing from `fetched` has stopped running and is dropped.
 * Rows pushed while the request was in flight are kept, and a fetched row never undoes a newer
 * (or, for a finish, equal) pushed status.
 */
export function reconcileRunning(before: OperationSummary[], current: OperationSummary[], fetched: OperationSummary[]): OperationSummary[] {
  const fetchedById = new Map(fetched.map(item => [item.id, item]))
  const wasRunning = new Set(runningOnly(before).map(item => item.id))
  const next: OperationSummary[] = []
  for (const item of current) {
    const server = fetchedById.get(item.id)
    fetchedById.delete(item.id)
    if (server) next.push(replaces(item, server, true) ? server : item)
    else if (!(item.status === "running" && wasRunning.has(item.id))) next.push(item)
  }
  next.push(...fetchedById.values())
  return trim(next)
}
