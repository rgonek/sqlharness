import { useId, useState } from "react"
import type { OptimizationActionReference } from "@/api/types"
import { useOperation } from "@/api/queries"
import { PlanWorkbench } from "@/components/plan/PlanWorkbench"
import { Badge } from "@/components/ui/badge"
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group"
import { Label } from "@/components/ui/label"
import { ErrorState } from "@/components/ErrorState"

export function VersionPlan({ a, b, before, after, engine, selectedCase }: { a: string; b: string; before: OptimizationActionReference | null | undefined; after: OptimizationActionReference | null | undefined; engine: string | null; selectedCase: string }) {
  const [side, setSide] = useState<"a" | "b">("b")
  const id = useId()
  const [toolbarTarget, setToolbarTarget] = useState<HTMLDivElement | null>(null)
  const action = side === "a" ? before : after
  const plan = action ? <ActionPlan key={`${action.operationId}:${action.side}:${selectedCase}`} toolbarTarget={toolbarTarget} action={action} engine={engine} selectedCase={selectedCase} /> : <p className="text-sm text-muted-foreground">No retained plan operation for this version.</p>
  return <section className="version-plan flex flex-col gap-3" aria-label="Version plan">
    <div className="plan-version-toolbar flex flex-wrap items-center gap-3">
      <RadioGroup aria-label="Plan version" value={side} onValueChange={value => { if (value === "a" || value === "b") setSide(value) }} className="flex flex-wrap gap-3">
        <div className="flex items-center gap-2"><RadioGroupItem id={`${id}-a`} value="a" aria-label={`${a} A`} /><Label htmlFor={`${id}-a`}>{a}<Badge variant="secondary" className="h-3.5 rounded-sm bg-border px-1 py-0 text-[10.5px]">A</Badge></Label></div>
        <div className="flex items-center gap-2"><RadioGroupItem id={`${id}-b`} value="b" aria-label={`${b} B`} /><Label htmlFor={`${id}-b`}>{b}<Badge className="h-3.5 rounded-sm px-1 py-0 text-[10.5px]">B</Badge></Label></div>
      </RadioGroup>
      <div ref={setToolbarTarget} className="min-w-0 flex-1 basis-full sm:basis-auto" />
    </div>
    {plan}
  </section>
}

function ActionPlan({ action, engine, selectedCase, toolbarTarget }: { action: OptimizationActionReference; engine: string | null; selectedCase: string; toolbarTarget: HTMLElement | null }) {
  const query = useOperation(action.operationId)
  if (query.isPending) return <p role="status">Loading plan…</p>
  if (query.error) return <ErrorState error={query.error} />
  const caseId = action.caseIdsByFingerprint?.[selectedCase] ?? (action.caseIds.includes(selectedCase) ? selectedCase : null)
  const variants = query.data.variants.filter(variant => variant.variant === action.side || (action.side === "measure" && variant.variant === "query"))
  const matches = caseId === null ? [] : variants.filter(item => item.caseId === caseId)
  const variant = matches.length === 1 ? matches[0] : undefined
  return variant ? <PlanWorkbench toolbarTarget={toolbarTarget} operationId={action.operationId} variant={variant} engine={engine} /> : <p className="text-sm text-muted-foreground">No retained plan variant for this selection.</p>
}
