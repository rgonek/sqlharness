import type { PlanOccurrenceManifest } from "@/api/types"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"

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
    <Select items={options.map(occurrence => ({
      value: occurrence.statementIndex,
      label: `Statement ${occurrence.statementIndex + 1}${occurrence.matchKind === "unmatched" ? " · unmatched" : ""}`,
    }))} value={selectedIndex ?? options[0].statementIndex} onValueChange={value => {
      if (typeof value === "number") onChange(value)
    }}>
      <SelectTrigger aria-label="Statement"><SelectValue /></SelectTrigger>
      <SelectContent>{options.map(occurrence => <SelectItem key={occurrence.statementIndex} value={occurrence.statementIndex}>
        Statement {occurrence.statementIndex + 1}{occurrence.matchKind === "unmatched" ? " · unmatched" : ""}
      </SelectItem>)}</SelectContent>
    </Select>
  </label>
}
