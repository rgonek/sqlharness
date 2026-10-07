import type { OperationSummary, SessionSummary } from "@/api/types"

export const LIVE_CAP = 200
export const ACTIVE_WINDOW_MS = 15 * 60_000

/**
 * Upsert by id, newest id first. A repeated or older event (same id, earlier updatedAt)
 * never replaces a newer row; an equal timestamp is accepted because liveness changes
 * (running -> abandoned) arrive without a journal write.
 */
export function mergeOperation(list: OperationSummary[], incoming: OperationSummary, cap = LIVE_CAP): OperationSummary[] {
  const existing = list.find(item => item.id === incoming.id)
  if (existing && existing.updatedAt > incoming.updatedAt) return list
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
  return fetched.reduce((current, item) => mergeOperation(current, item, cap), list)
}

export function mergeSessions(list: SessionSummary[], fetched: SessionSummary[], cap = LIVE_CAP): SessionSummary[] {
  return fetched.reduce((current, item) => mergeSession(current, item, cap), list)
}

export function isActiveSession(session: SessionSummary, now: number, windowMs = ACTIVE_WINDOW_MS): boolean {
  return session.running > 0 || now - Date.parse(session.lastSeen) <= windowMs
}
