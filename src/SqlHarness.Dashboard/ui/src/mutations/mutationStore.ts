import { useSyncExternalStore } from "react"

export type MutationParameter = { name: string; type: string | null; value: string | null }
export type MutationRequest = {
  profile: string; server: string; database: string; engine: string; tier: string; sql: string
  parameters: MutationParameter[]; digest: string; grantees: string[]
}
export type PendingMutation = {
  requestId: string; request: MutationRequest; environment: string; writerSameAsReader: boolean; expiresAt: string
}
export type MutationTerminal = { requestId: string; state: string }

type MutationState = { pending: PendingMutation[]; openRequestId: string | null; terminal: MutationTerminal | null }
let state: MutationState = { pending: [], openRequestId: null, terminal: null }
const listeners = new Set<() => void>()
const publish = (next: MutationState) => { state = next; listeners.forEach(listener => listener()) }

export const mutationStore = {
  getSnapshot: () => state,
  subscribe(listener: () => void) { listeners.add(listener); return () => listeners.delete(listener) },
  synchronize(requests: PendingMutation[]) { publish({ ...state, pending: dedupe(requests) }) },
  add(request: PendingMutation) {
    const existingIndex = state.pending.findIndex(item => item.requestId === request.requestId)
    if (existingIndex < 0 && state.pending.some(item => item.request.digest === request.request.digest)) return
    const pending = existingIndex < 0 ? [...state.pending, request] : state.pending.map((item, index) => index === existingIndex ? request : item)
    publish({ ...state, pending })
  },
  remove(requestId: string, terminalState: string) {
    const pending = state.pending.filter(item => item.requestId !== requestId)
    publish({ pending, openRequestId: state.openRequestId === requestId ? null : state.openRequestId, terminal: { requestId, state: terminalState } })
    window.setTimeout(() => { if (state.terminal?.requestId === requestId) publish({ ...state, terminal: null }) }, 3_000)
  },
  open(requestId: string) { publish({ ...state, openRequestId: requestId }) },
  close() { publish({ ...state, openRequestId: null }) },
  reset() { publish({ pending: [], openRequestId: null, terminal: null }) },
}

function dedupe(requests: PendingMutation[]): PendingMutation[] {
  const seen = new Set<string>()
  return requests.filter(item => {
    if (seen.has(item.request.digest)) return false
    seen.add(item.request.digest)
    return true
  })
}

export function usePendingMutations(): PendingMutation[] {
  return useSyncExternalStore(mutationStore.subscribe, () => mutationStore.getSnapshot().pending, () => [])
}
