import { useEffect } from "react"
import type { MutationTerminal, PendingMutation } from "./mutationStore"
import { mutationStore } from "./mutationStore"
import { playApprovalSound } from "./sound"

export function useMutationEvents(): void {
  useEffect(() => {
    const observedIds = new Set<string>()
    const activeDigests = new Map<string, string>()
    const source = new EventSource("/api/mutations/events")
    const markObserved = (request: PendingMutation) => {
      observedIds.add(request.requestId)
      activeDigests.set(request.request.digest, request.requestId)
    }
    source.addEventListener("snapshot", event => {
      const requests = JSON.parse((event as MessageEvent<string>).data) as PendingMutation[]
      activeDigests.clear()
      requests.forEach(markObserved)
      mutationStore.synchronize(requests)
    })
    source.addEventListener("added", event => {
      const request = JSON.parse((event as MessageEvent<string>).data) as PendingMutation
      const seenRequest = observedIds.has(request.requestId)
      const activeRequestId = activeDigests.get(request.request.digest)
      const digestJoin = activeRequestId !== undefined && activeRequestId !== request.requestId
      observedIds.add(request.requestId)
      if (!digestJoin) activeDigests.set(request.request.digest, request.requestId)
      mutationStore.add(request)
      if (seenRequest || digestJoin) return
      void playApprovalSound().catch(() => {})
      if (document.visibilityState === "hidden" && "Notification" in window && Notification.permission === "granted") {
        try {
          const notification = new Notification("SQLHarness: a change needs your decision")
          notification.onclick = () => {
            window.focus()
            mutationStore.open(request.requestId)
            notification.close()
          }
        } catch { /* Notification support is optional. */ }
      }
    })
    source.addEventListener("removed", event => {
      const removed = JSON.parse((event as MessageEvent<string>).data) as MutationTerminal
      const digest = [...activeDigests.entries()].find(([, activeRequestId]) => activeRequestId === removed.requestId)?.[0]
      if (digest) activeDigests.delete(digest)
      mutationStore.remove(removed.requestId, removed.state)
    })
    return () => { source.close(); mutationStore.reset() }
  }, [])
}
