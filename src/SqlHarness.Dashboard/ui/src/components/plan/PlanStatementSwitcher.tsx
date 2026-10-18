import type { PlanOccurrenceManifest } from "@/api/types"

export function PlanStatementSwitcher({
  occurrences,
  selectedIndex,
  onChange,
}: {
  occurrences: PlanOccurrenceManifest[]
  selectedIndex: number | null
  onChange: (statementIndex: number) => void
}) {
  const options = [...new Map(occurrences.map(occurrence => [occurrence.statementIndex, occurrence])).values()]
    .sort((left, right) => left.statementIndex - right.statementIndex)
  if (options.length <= 1) return <p className="text-sm text-muted-foreground">Batch SQL stays visible while its linked statement is selected.</p>
  return <label className="grid min-w-48 gap-1 text-sm">
    Statement
    <select aria-label="Statement" className="rounded-md border bg-background px-2 py-1" value={selectedIndex ?? options[0].statementIndex} onChange={event => onChange(Number(event.target.value))}>
      {options.map(occurrence => <option key={occurrence.statementIndex} value={occurrence.statementIndex}>
        Statement {occurrence.statementIndex + 1}{occurrence.matchKind === "unmatched" ? " · unmatched" : ""}
      </option>)}
    </select>
  </label>
}
