import { useQueryClient } from "@tanstack/react-query"
import { useEffect, useSyncExternalStore } from "react"
import { connection } from "@/api/connection"
import { queryKeys } from "@/api/queries"
import type { OperationSummary, SessionSummary } from "@/api/types"
import { mergeOperation, mergeSession, pushRunning } from "./liveStore"

export type LiveFeedState = "connecting" | "connected" | "disconnected"

let feedState: LiveFeedState = "connecting"
const stateListeners = new Set<() => void>()
function setFeedState(next: LiveFeedState) {
  if (feedState === next) return
  feedState = next
  stateListeners.forEach(listener => listener())
}
export function useLiveFeedState(): LiveFeedState {
  return useSyncExternalStore(listener => { stateListeners.add(listener); return () => stateListeners.delete(listener) }, () => feedState, () => feedState)
}

type SafetyNoticeEvent = { operationId: number; notices: string[] }
const noticeListeners = new Set<(item: SafetyNoticeEvent) => void>()
export function subscribeSafetyNotices(listener: (item: SafetyNoticeEvent) => void) {
  noticeListeners.add(listener)
  return () => { noticeListeners.delete(listener) }
}

const liveLists = [queryKeys.liveOperations, queryKeys.liveRunning, queryKeys.liveSessions]

/**
 * Subscribes to /api/live (same-origin; the HttpOnly session cookie is sent automatically).
 * Events are merged into the Live view caches and invalidate the matching detail queries.
 * EventSource reconnects by itself; `state` reflects the current connection.
 *
 * The server sends nothing on connect: it takes the journal's current state as its starting
 * point and streams only later changes, so anything that changed before an open (the initial
 * fetch race, or a gap while disconnected) is never pushed. Every open therefore refetches the
 * live lists. `open` fires once per (re)connection and a refetch does not touch the
 * EventSource, so this cannot loop.
 *
 * When EventSource gives up (readyState CLOSED, e.g. a 401: the dashboard token is persistent, so
 * that now means it was rotated), the live lists are refetched once so the reason surfaces through the queries'
 * error (UnauthorizedError is not retried).
 */
export function useLiveFeed(): LiveFeedState {
  const client = useQueryClient()

  useEffect(() => {
    setFeedState("connecting")
    const refetchLists = () => {
      for (const queryKey of liveLists) void client.invalidateQueries({ queryKey })
    }
    const source = new EventSource("/api/live")
    source.onopen = () => {
      setFeedState("connected")
      connection.reportSuccess()
      refetchLists()
    }
    source.onerror = () => {
      setFeedState("disconnected")
      if (source.readyState !== EventSource.OPEN) connection.reportFailure()
      if (source.readyState === EventSource.CLOSED) refetchLists()
    }
    source.addEventListener("operation", event => {
      const operation = JSON.parse((event as MessageEvent<string>).data) as OperationSummary
      if (operation.safetyNotices?.length)
        noticeListeners.forEach(listener => listener({ operationId: operation.id, notices: operation.safetyNotices }))
      client.setQueryData<OperationSummary[]>(queryKeys.liveOperations, (current = []) => mergeOperation(current, operation))
      client.setQueryData<OperationSummary[]>(queryKeys.liveRunning, (current = []) => pushRunning(current, operation))
      void client.invalidateQueries({ queryKey: queryKeys.operation(operation.id) })
      void client.invalidateQueries({ queryKey: queryKeys.session(operation.sessionId) })
    })
    source.addEventListener("session", event => {
      const session = JSON.parse((event as MessageEvent<string>).data) as SessionSummary
      client.setQueryData<SessionSummary[]>(queryKeys.liveSessions, (current = []) => mergeSession(current, session))
      void client.invalidateQueries({ queryKey: ["sessions"] })
    })
    return () => source.close()
  }, [client])

  return useLiveFeedState()
}
