import { useQueryClient } from "@tanstack/react-query"
import { useEffect, useState } from "react"
import { queryKeys } from "@/api/queries"
import type { OperationSummary, SessionSummary } from "@/api/types"
import { mergeOperation, mergeSession } from "./liveStore"

/**
 * Subscribes to /api/live (same-origin; the HttpOnly session cookie is sent automatically).
 * Events are merged into the Live view caches and invalidate the matching detail queries.
 * EventSource reconnects by itself; `connected` reflects the current state.
 */
export function useLiveFeed(): { connected: boolean } {
  const client = useQueryClient()
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    const source = new EventSource("/api/live")
    source.onopen = () => setConnected(true)
    source.onerror = () => setConnected(false)
    source.addEventListener("operation", event => {
      const operation = JSON.parse((event as MessageEvent<string>).data) as OperationSummary
      client.setQueryData<OperationSummary[]>(queryKeys.liveOperations, (current = []) => mergeOperation(current, operation))
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

  return { connected }
}
