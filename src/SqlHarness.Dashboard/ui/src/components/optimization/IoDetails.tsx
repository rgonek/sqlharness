import { useQueries } from "@tanstack/react-query"
import type { OperationDetail, OptimizationActionReference, OptimizationComparison } from "@/api/types"
import { getJson } from "@/api/client"
import { queryKeys } from "@/api/queries"
import { aggregate, selectedVariants, tableExtras } from "@/lib/ioComparison"
import { IoComparison } from "./IoComparison"

export function IoDetails({ comparison, byStatement, onViewChange }: { comparison: OptimizationComparison; byStatement: boolean; onViewChange: (view: "whole" | "statements") => void }) {
  const ids = [...new Set([comparison.beforeAction?.operationId, comparison.afterAction?.operationId].filter((id): id is number => id !== undefined))]
  const queries = useQueries({ queries: ids.map(id => ({ queryKey: queryKeys.operation(id), queryFn: () => getJson<OperationDetail>(`/api/operations/${id}`) })) })
  const detail = (action: OptimizationActionReference | null | undefined) => queries[ids.indexOf(action?.operationId ?? -1)]?.data
  const before = selectedVariants(detail(comparison.beforeAction), comparison.beforeAction, comparison)
  const after = selectedVariants(detail(comparison.afterAction), comparison.afterAction, comparison)
  const mean = comparison.aggregate === "mean"
  const memory = ([ ["Grant", "grantGrantedKb"], ["Max used", "grantMaxUsedKb"] ] as const).map(([label, key]) => ({ label, before: aggregate(before.map(variant => variant[key]), mean), after: aggregate(after.map(variant => variant[key]), mean) }))
  return <div className="space-y-3">
    {queries.some(query => query.isPending) && <p role="status" className="text-xs text-muted-foreground">Loading additional IO counters…</p>}
    {queries.some(query => query.error) && <p role="alert" className="text-sm text-destructive">Additional IO counters could not be loaded. The recorded comparison remains available.</p>}
    <IoComparison comparison={comparison} byStatement={byStatement} onViewChange={onViewChange} extras={{ before: tableExtras(before, mean), after: tableExtras(after, mean) }} />
    <div className="flex flex-wrap gap-x-5 gap-y-2 rounded-md bg-muted/40 p-3 text-xs tabular-nums" aria-label="Batch memory">
      <span className="font-medium">Memory ({mean ? "mean" : "sum"} of selected case medians)</span>{memory.map(metric => <span key={metric.label}><span className="text-muted-foreground">{metric.label} </span>{metric.before?.toLocaleString() ?? "—"} → {metric.after?.toLocaleString() ?? "—"} KB</span>)}
      <span className="text-muted-foreground">Statement memory is not retained in the journal.</span>
    </div>
  </div>
}
