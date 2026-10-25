import { useState } from "react"
import type { OptimizationActionReference } from "@/api/types"
import { useOperation } from "@/api/queries"
import { PlanWorkbench } from "@/components/plan/PlanWorkbench"
import { Button } from "@/components/ui/button"
import { ErrorState } from "@/components/ErrorState"

export function VersionPlan({ a, b, before, after, engine, selectedCase }: { a: string; b: string; before: OptimizationActionReference | null | undefined; after: OptimizationActionReference | null | undefined; engine: string | null; selectedCase: string }) {
  const [side, setSide] = useState<"a" | "b">("b")
  const action = side === "a" ? before : after
  return <section className="space-y-3" aria-label="Version plan">
    <div className="flex gap-2"><Button size="sm" variant={side === "a" ? "secondary" : "outline"} aria-pressed={side === "a"} onClick={() => setSide("a")}>{a} · A</Button><Button size="sm" variant={side === "b" ? "secondary" : "outline"} aria-pressed={side === "b"} onClick={() => setSide("b")}>{b} · B</Button></div>
    {action ? <ActionPlan key={`${action.operationId}:${action.side}:${selectedCase}`} action={action} engine={engine} selectedCase={selectedCase} /> : <p className="text-sm text-muted-foreground">No retained plan operation for this version.</p>}
  </section>
}

function ActionPlan({ action, engine, selectedCase }: { action: OptimizationActionReference; engine: string | null; selectedCase: string }) {
  const query = useOperation(action.operationId)
  if (query.isPending) return <p role="status">Loading plan…</p>
  if (query.error) return <ErrorState error={query.error} />
  const caseId = action.caseIdsByFingerprint?.[selectedCase] ?? (action.caseIds.includes(selectedCase) ? selectedCase : null)
  const variants = query.data.variants.filter(variant => variant.variant === action.side || (action.side === "measure" && variant.variant === "query"))
  const matches = caseId === null ? [] : variants.filter(item => item.caseId === caseId)
  const variant = matches.length === 1 ? matches[0] : undefined
  return variant ? <PlanWorkbench operationId={action.operationId} variant={variant} engine={engine} /> : <p className="text-sm text-muted-foreground">No retained plan variant for this selection.</p>
}
