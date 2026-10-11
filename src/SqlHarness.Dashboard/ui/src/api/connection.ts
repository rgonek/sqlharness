import { useSyncExternalStore } from "react"

type State = "online" | "reconnecting"
let state: State = "online"
const listeners = new Set<() => void>()
const set = (next: State) => { if (state !== next) { state = next; listeners.forEach(l => l()) } }

export const connection = {
  reportFailure: () => set("reconnecting"),
  reportSuccess: () => set("online"),
  subscribe: (listener: () => void) => { listeners.add(listener); return () => listeners.delete(listener) },
  get: () => state,
}

export function useConnectionState(): State {
  return useSyncExternalStore(connection.subscribe, connection.get, connection.get)
}
