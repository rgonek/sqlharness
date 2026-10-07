import type { OperationSummary } from "@/api/types"
import { Badge } from "@/components/ui/badge"
import { operationFlags } from "@/lib/flags"

export function FlagBadges({ operation }: { operation: OperationSummary }) {
  const flags = operationFlags(operation)
  if (flags.length === 0) return null
  return (
    <div className="flex flex-wrap gap-1">
      {flags.map(flag => (
        <Badge key={flag.key} variant={flag.key === "mutation" ? "destructive" : "outline"}>
          {flag.label}
        </Badge>
      ))}
    </div>
  )
}
