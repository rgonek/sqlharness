import { LazyPlanSqlViewer } from "@/components/SqlEditors"
import type { SqlSourceSelection, SqlSourceSpan } from "@/lib/sqlDiff"
import { dialectOf } from "@/lib/sqlDiff"

export function PlanSourceView({
  executedText,
  engine,
  highlightedSpans,
  revealSpan,
  onSelectionChange,
}: {
  executedText: string | null
  engine: string | null
  highlightedSpans: SqlSourceSpan[]
  revealSpan: SqlSourceSpan | null
  onSelectionChange: (selection: SqlSourceSelection) => void
}) {
  if (executedText === null) return <section aria-label="Executed SQL" className="rounded border p-3 text-sm text-muted-foreground">The operation did not retain its executed SQL text.</section>
  return <LazyPlanSqlViewer
    sql={executedText}
    dialect={dialectOf(engine)}
    highlightedSpans={highlightedSpans}
    revealSpan={revealSpan}
    onSelectionChange={onSelectionChange}
  />
}
