import { Button } from "@/components/ui/button"
import { mutationStore, usePendingMutations } from "./mutationStore"

export function ApprovalToast() {
  const pending = usePendingMutations()
  const terminal = mutationStore.getSnapshot().terminal
  return (
    <div aria-live="polite" className="fixed right-4 bottom-4 z-40 flex w-[min(24rem,calc(100vw-2rem))] flex-col gap-2">
      {pending.map(item => (
        <section key={item.requestId} className="rounded-xl border bg-card p-4 text-card-foreground shadow-lg" aria-label="Pending change approval">
          <div className="font-medium">A change needs your decision</div>
          <Button className="mt-3" size="sm" onClick={() => mutationStore.open(item.requestId)}>Review</Button>
        </section>
      ))}
      {terminal && <div role="status" className="rounded-xl border bg-card p-3 text-sm shadow-lg">Request {terminal.state}</div>}
    </div>
  )
}
