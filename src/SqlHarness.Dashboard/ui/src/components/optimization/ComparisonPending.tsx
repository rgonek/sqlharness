import type { ReactNode } from "react"
import { cn } from "cn"

/** Retain comparison geometry while new evidence loads, without exposing stale evidence. */
export function ComparisonPending({ pending, children, announce = false, className }: { pending: boolean; children: ReactNode; announce?: boolean; className?: string }) {
  return <div className={cn("relative", className)} aria-busy={pending}>
    <div inert={pending} aria-hidden={pending || undefined} className={cn(pending && "invisible")}>{children}</div>
    {pending && announce && <div role="status" className="absolute inset-0 flex items-center justify-center">Updating comparison…</div>}
  </div>
}
