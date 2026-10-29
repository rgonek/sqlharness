import { useState } from "react"
import type { OptimizationActionReference } from "@/api/types"
import { useOperation } from "@/api/queries"
import { PlanWorkbench } from "@/components/plan/PlanWorkbench"
import { Badge } from "@/components/ui/badge"
import { SegmentedRadioGroup } from "@/components/SegmentedRadioGroup"
import { ErrorState } from "@/components/ErrorState"

export function VersionPlan({ a, b, before, after, engine, selectedCase }: { a: string; b: string; before: OptimizationActionReference | null | undefined; after: OptimizationActionReference | null | undefined; engine: string | null; selectedCase: string }) {
  const [side, setSide] = useState<"a" | "b">("b")
  const [toolbarTarget, setToolbarTarget] = useState<HTMLDivElement | null>(null)
  const action = side === "a" ? before : after
  const plan = action ? <ActionPlan key={`${action.operationId}:${action.side}:${selectedCase}`} toolbarTarget={toolbarTarget} action={action} engine={engine} selectedCase={selectedCase} /> : <p className="text-sm text-muted-foreground">No retained plan operation for this version.</p>
  return <section className="version-plan flex flex-col gap-3" aria-label="Version plan">
    <div className="plan-version-toolbar flex flex-wrap items-center gap-3">
      <SegmentedRadioGroup className="[&_[data-slot=label]]:min-w-20 [&_[data-slot=label]]:justify-between [&_[data-slot=label]]:px-3" label="Plan version" value={side} onValueChange={value => { if (value === "a" || value === "b") setSide(value) }} options={[
        { value: "a", ariaLabel: `${a} A`, label: <>{a}<Badge variant="secondary" className="size-4 rounded-full bg-border p-0 text-[11px] leading-none">A</Badge></> },
        { value: "b", ariaLabel: `${b} B`, label: <>{b}<Badge className="size-4 rounded-full p-0 text-[11px] leading-none">B</Badge></> },
      ]} />
      <div ref={setToolbarTarget} className="min-w-0 flex-1 basis-full sm:basis-auto" />
    </div>
    {plan}
  </section>
}

function ActionPlan({ action, engine, selectedCase, toolbarTarget }: { action: OptimizationActionReference; engine: string | null; selectedCase: string; toolbarTarget: HTMLElement | null }) {
  const query = useOperation(action.operationId)
  if (query.isPending) return <p role="status">Loading plan…</p>
  if (query.error) return <ErrorState error={query.error} />
  const caseId = action.caseIdsByFingerprint?.[selectedCase] ?? (action.caseIds.includes(selectedCase) ? selectedCase : selectedCase === "all" ? action.caseIds[0] ?? null : null)
  const variants = query.data.variants.filter(variant => variant.variant === action.side || (action.side === "measure" && variant.variant === "query"))
  const matches = caseId === null ? [] : variants.filter(item => item.caseId === caseId)
  const variant = matches.length === 1 ? matches[0] : undefined
  return variant ? <PlanWorkbench toolbarTarget={toolbarTarget} operationId={action.operationId} variant={variant} engine={engine} /> : <p className="text-sm text-muted-foreground">No retained plan variant for this selection.</p>
}
