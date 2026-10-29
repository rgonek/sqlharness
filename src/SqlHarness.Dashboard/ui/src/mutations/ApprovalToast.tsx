import { useEffect, useRef, useState, useSyncExternalStore } from "react"
import { Button } from "@/components/ui/button"
import { postMutationDecision } from "@/api/client"
import { Toast, ToastContent, ToastDescription, ToastPortal, ToastProvider, ToastTitle, ToastViewport, useToastManager } from "@/components/ui/toast"
import { useNow } from "@/lib/useNow"
import { mutationStore, type PendingMutation } from "./mutationStore"

function ToastDecision({ item, expired }: { item: PendingMutation; expired: boolean }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState("")
  const deny = async () => {
    if (busy || expired) return
    setBusy(true)
    setError("")
    try {
      const result = await postMutationDecision(item.requestId, "deny")
      if (result === "not-pending") setError("This request is no longer pending")
      else if (result === "busy") setError("Another change is already running for this profile. Try again.")
      // The authoritative removed event dismisses the toast.
    } catch { setError("The decision could not be submitted. Try again.") }
    finally { setBusy(false) }
  }
  return <div className="flex flex-col gap-2">
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    <div className="flex justify-end gap-2"><Button size="sm" variant="outline" disabled={busy || expired} onClick={() => void deny()}>{busy ? "Denying..." : "Deny"}</Button><Button size="sm" onClick={() => mutationStore.open(item.requestId)}>Review</Button></div>
  </div>
}

function ApprovalNotifications({ pending, hidden }: { pending: PendingMutation[]; hidden: boolean }) {
  const { toasts, add, close } = useToastManager<{ request: PendingMutation }>()
  const previous = useRef(new Set<string>())
  const now = useNow()
  useEffect(() => {
    const ids = new Set(pending.map(item => item.requestId))
    for (const id of previous.current) if (!ids.has(id)) close(id)
    for (const request of pending) add({ id: request.requestId, title: request.environment || "unknown", timeout: 0, data: { request } })
    previous.current = ids
  }, [pending, add, close])
  if (hidden) return null
  return <ToastPortal><ToastViewport>
    {toasts.map(notification => {
      const item = notification.data?.request
      if (!item) return null
      const remaining = Math.max(0, Math.ceil((Date.parse(item.expiresAt) - now) / 1000))
      return <Toast key={notification.id} toast={notification} swipeDirection={[]}>
        <ToastContent><section className="flex min-w-0 flex-1 flex-col gap-2" aria-label="Pending change approval">
          <ToastTitle className="text-base font-semibold" />
          <ToastDescription>{item.request.profile} | {item.request.database} | {item.request.tier}</ToastDescription>
          <pre className="line-clamp-2 whitespace-pre-wrap break-all font-mono text-xs">{item.request.sql.slice(0, 240)}{item.request.sql.length > 240 ? "..." : ""}</pre>
          <div className="flex items-center justify-between gap-3"><span className="text-xs text-muted-foreground">Expires in {Math.floor(remaining / 60)}:{String(remaining % 60).padStart(2, "0")}{pending.length > 1 ? ` | ${pending.length} pending` : ""}</span></div><ToastDecision item={item} expired={remaining === 0} />
        </section></ToastContent>
      </Toast>
    })}
  </ToastViewport></ToastPortal>
}

export function ApprovalToast() {
  const state = useSyncExternalStore(mutationStore.subscribe, mutationStore.getSnapshot, mutationStore.getSnapshot)
  return <>
    <ToastProvider timeout={0}><ApprovalNotifications pending={state.pending} hidden={state.openRequestId !== null} /></ToastProvider>
    {state.terminal && <div role="status" className="fixed right-4 bottom-4 z-40 rounded-md border bg-popover p-3 text-sm text-popover-foreground shadow-lg">Request {state.terminal.state}</div>}
  </>
}
