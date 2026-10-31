import type { OperationStatus, OperationSummary } from "@/api/types"

export type Flag = { key: "mutation" | "spill" | "cold" | "grant"; label: string }

export function operationFlags(operation: OperationSummary): Flag[] {
  const flags: Flag[] = []
  if (operation.mutationRequested) flags.push({ key: "mutation", label: "mutation" })
  if (operation.hasSpill) flags.push({ key: "spill", label: "spill" })
  if (operation.coldCache) flags.push({ key: "cold", label: "cold cache" })
  if (operation.overGranted) flags.push({ key: "grant", label: "over-granted" })
  return flags
}

export type BadgeVariant = "default" | "secondary" | "destructive" | "outline"

export type BadgeKind = OperationStatus | Flag["key"] | "regression" | "results-differ" | "mixed"

export function badgeTier(kind: BadgeKind): "outcome" | "warning" | "info" {
  if (kind === "succeeded" || kind === "failed" || kind === "rejected" || kind === "cancelled") return "outcome"
  if (kind === "regression" || kind === "results-differ" || kind === "mixed" || kind === "mutation") return "warning"
  return "info"
}

export function statusVariant(status: OperationStatus): BadgeVariant {
  switch (status) {
    case "running":
      return "default"
    case "succeeded":
      return "secondary"
    case "failed":
    case "rejected":
    case "cancelled":
      return "destructive"
    default:
      return "outline"
  }
}
