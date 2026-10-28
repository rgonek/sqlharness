import { useEffect, useRef, useSyncExternalStore } from "react"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Toast, ToastContent, ToastDescription, ToastPortal, ToastProvider, ToastTitle, ToastViewport, useToastManager } from "@/components/ui/toast"
import { useNow } from "@/lib/useNow"
import { mutationStore, type PendingMutation } from "./mutationStore"

function ApprovalNotifications({ pending, hidden }: { pending: PendingMutation[]; hidden: boolean }) {
  const { toasts, add, close } = useToastManager<{ request: PendingMutation }>()
  const previous = useRef(new Set<string>())
  const now = useNow()
  useEffect(() => {
    const ids = new Set(pending.map(item => item.requestId))
    for (const id of previous.current) if (!ids.has(id)) close(id)
    for (const request of pending) add({ id: request.requestId, title: "A change needs your decision", timeout: 0, data: { request } })
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
          <div className="flex flex-wrap items-center gap-2"><ToastTitle /><Badge variant="outline">{item.environment || "unknown"}</Badge></div>
          <ToastDescription>{item.request.profile} · {item.request.database} · {item.request.tier}</ToastDescription>
          <div className="flex items-center justify-between gap-3"><span className="text-xs text-muted-foreground">Expires in {Math.floor(remaining / 60)}:{String(remaining % 60).padStart(2, "0")}{pending.length > 1 ? ` · ${pending.length} pending` : ""}</span><Button size="sm" onClick={() => mutationStore.open(item.requestId)}>Review</Button></div>
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
