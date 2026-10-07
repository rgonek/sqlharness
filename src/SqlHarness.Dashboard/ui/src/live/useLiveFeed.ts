import { useQueryClient } from "@tanstack/react-query"
import { useEffect, useState } from "react"
import { queryKeys } from "@/api/queries"
import type { OperationSummary, SessionSummary } from "@/api/types"
import { mergeOperation, mergeSession, pushRunning } from "./liveStore"

export type LiveFeedState = "connecting" | "connected" | "disconnected"

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
 * When EventSource gives up (readyState CLOSED, e.g. a 401 after the dashboard restarted with a
 * new token), the live lists are refetched once so the reason surfaces through the queries'
 * error (UnauthorizedError is not retried).
 */
export function useLiveFeed(): LiveFeedState {
  const client = useQueryClient()
  const [state, setState] = useState<LiveFeedState>("connecting")

  useEffect(() => {
    const refetchLists = () => {
      for (const queryKey of liveLists) void client.invalidateQueries({ queryKey })
    }
    const source = new EventSource("/api/live")
    source.onopen = () => {
      setState("connected")
      refetchLists()
    }
    source.onerror = () => {
      setState("disconnected")
      if (source.readyState === EventSource.CLOSED) refetchLists()
    }
    source.addEventListener("operation", event => {
      const operation = JSON.parse((event as MessageEvent<string>).data) as OperationSummary
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

  return state
}
